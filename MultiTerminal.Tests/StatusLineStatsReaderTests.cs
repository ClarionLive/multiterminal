using System;
using System.IO;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Tests for <see cref="StatusLineStatsReader"/> (task e855c051) — parsing the
    /// per-terminal + shared-quota temp files statusline.js writes, with graceful
    /// degradation on missing/corrupt/stale files. Each test uses an isolated temp
    /// dir and a fixed clock so staleness math is deterministic.
    /// </summary>
    public sealed class StatusLineStatsReaderTests : IDisposable
    {
        private readonly string _dir;
        private const long NowMs = 1_700_000_000_000;
        private readonly StatusLineStatsReader _reader;

        public StatusLineStatsReaderTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"mt_statsreader_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _reader = new StatusLineStatsReader(
                tempDir: _dir,
                clock: () => DateTimeOffset.FromUnixTimeMilliseconds(NowMs),
                staleThresholdSeconds: 60.0);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            GC.SuppressFinalize(this);
        }

        private void WritePerTerminal(string name, string docId, string json) =>
            File.WriteAllText(Path.Combine(_dir, $"mt-statusline-{name}-{docId}.json"), json);

        private void WriteShared(string json) =>
            File.WriteAllText(Path.Combine(_dir, "mt-statusline-quota.json"), json);

        [Fact]
        public void Fresh_PerTerminalAndShared_PopulatesAllFields()
        {
            WritePerTerminal("Alice", "doc1",
                $"{{\"model\":\"opus-4-8\",\"contextPct\":43,\"quota5h\":10,\"quota7d\":20,\"timestamp\":{NowMs - 5000}}}");
            WriteShared("{\"quota5h\":55,\"quota7d\":66,\"pace5h\":3,\"pace7d\":-2,\"resetIn5h\":\"2h 15m\",\"isOffPeak\":true,\"timestamp\":" + (NowMs - 1000) + "}");

            var s = _reader.ReadFor("Alice", "doc1");

            Assert.True(s.Available);
            Assert.Equal("opus-4-8", s.Model);
            Assert.Equal(43, s.ContextPercent);
            // Shared quota wins over the per-terminal copy.
            Assert.Equal("shared", s.QuotaSource);
            Assert.Equal(55, s.FiveHourPercent);
            Assert.Equal(66, s.SevenDayPercent);
            Assert.Equal(3, s.FiveHourPace);
            Assert.Equal("2h 15m", s.FiveHourResetIn);
            Assert.True(s.IsOffPeak);
            Assert.Equal(5.0, s.AgeSeconds);
            Assert.False(s.Stale);
        }

        [Fact]
        public void MissingPerTerminalFile_ReportsUnavailable()
        {
            var s = _reader.ReadFor("Ghost", "nope");
            Assert.False(s.Available);
            Assert.Null(s.ContextPercent);
        }

        [Fact]
        public void CorruptPerTerminalJson_TreatedAsUnavailable_DoesNotThrow()
        {
            WritePerTerminal("Alice", "doc1", "{ this is not valid json ");
            var s = _reader.ReadFor("Alice", "doc1");
            Assert.False(s.Available);
        }

        [Fact]
        public void OldTimestamp_FlaggedStale()
        {
            WritePerTerminal("Alice", "doc1", $"{{\"contextPct\":50,\"timestamp\":{NowMs - 120_000}}}");
            var s = _reader.ReadFor("Alice", "doc1");
            Assert.True(s.Available);
            Assert.Equal(120.0, s.AgeSeconds);
            Assert.True(s.Stale);
        }

        [Fact]
        public void NoDocId_SelectsNewestByTimestamp()
        {
            WritePerTerminal("Alice", "old", $"{{\"contextPct\":11,\"timestamp\":{NowMs - 60_000}}}");
            WritePerTerminal("Alice", "new", $"{{\"contextPct\":77,\"timestamp\":{NowMs - 2000}}}");

            var s = _reader.ReadFor("Alice"); // no docId → newest wins

            Assert.True(s.Available);
            Assert.Equal(77, s.ContextPercent);
        }

        [Fact]
        public void SharedQuotaMissing_FallsBackToPerTerminalQuota()
        {
            WritePerTerminal("Alice", "doc1",
                $"{{\"contextPct\":40,\"quota5h\":12,\"quota7d\":34,\"timestamp\":{NowMs - 1000}}}");
            // No shared quota file written.

            var s = _reader.ReadFor("Alice", "doc1");

            Assert.True(s.Available);
            Assert.Equal("per-terminal", s.QuotaSource);
            Assert.Equal(12, s.FiveHourPercent);
            Assert.Equal(34, s.SevenDayPercent);
        }

        [Fact]
        public void CorruptSharedQuota_FallsBackToPerTerminalQuota()
        {
            WritePerTerminal("Alice", "doc1",
                $"{{\"contextPct\":40,\"quota5h\":12,\"quota7d\":34,\"timestamp\":{NowMs - 1000}}}");
            WriteShared("}{ torn");

            var s = _reader.ReadFor("Alice", "doc1");

            Assert.True(s.Available);
            Assert.Equal("per-terminal", s.QuotaSource);
            Assert.Equal(12, s.FiveHourPercent);
        }

        [Theory]
        [InlineData("../evil")]
        [InlineData("..\\evil")]
        [InlineData("a/b")]
        [InlineData("a\\b")]
        [InlineData("foo:bar")]
        [InlineData("..")]
        public void UnsafeName_RejectedAsUnavailable(string badName)
        {
            // Path-injection guard: a name with separators / dot-segments must never
            // be turned into a file path — returns unavailable, no traversal.
            var s = _reader.ReadFor(badName, "doc1");
            Assert.False(s.Available);
        }

        [Fact]
        public void UnsafeDocId_RejectedAsUnavailable()
        {
            WritePerTerminal("Alice", "doc1", $"{{\"contextPct\":40,\"timestamp\":{NowMs - 1000}}}");
            var s = _reader.ReadFor("Alice", "../../etc/passwd");
            Assert.False(s.Available);
        }

        [Fact]
        public void NonClaudeTerminal_NoContextOrQuota_StillAvailable()
        {
            // A plain shell terminal: statusline.js still writes a file but with null
            // contextPct / quota (no Claude Code rate-limit data).
            WritePerTerminal("Bob", "doc9", $"{{\"model\":\"pwsh\",\"contextPct\":null,\"timestamp\":{NowMs - 1000}}}");
            var s = _reader.ReadFor("Bob", "doc9");
            Assert.True(s.Available);
            Assert.Null(s.ContextPercent);
            Assert.Null(s.FiveHourPercent);
        }

        [Theory]
        [InlineData("43.7", 44)]
        [InlineData("43.4", 43)]
        [InlineData("43.0", 43)] // strict TryGetInt32 rejects even an integer-valued decimal → would have dropped to null
        [InlineData("0.5", 1)]
        public void FractionalContextPct_RoundedNotDropped(string raw, int expected)
        {
            // statusline.js writes contextPct straight from used_percentage without a
            // floor, so the reader must tolerate a fractional value instead of nulling it.
            WritePerTerminal("Alice", "doc1", $"{{\"contextPct\":{raw},\"timestamp\":{NowMs - 1000}}}");
            var s = _reader.ReadFor("Alice", "doc1");
            Assert.True(s.Available);
            Assert.Equal(expected, s.ContextPercent);
        }

        [Theory]
        [InlineData("1e30")]
        [InlineData("-1e30")]
        public void OutOfRangeContextPct_TreatedAsNull(string raw)
        {
            // A planted/corrupt temp file with an out-of-range number must yield null,
            // not a garbage int from an unchecked double→int cast. [debugger LOW]
            WritePerTerminal("Alice", "doc1", $"{{\"contextPct\":{raw},\"timestamp\":{NowMs - 1000}}}");
            var s = _reader.ReadFor("Alice", "doc1");
            Assert.True(s.Available);
            Assert.Null(s.ContextPercent);
        }

        [Fact]
        public void FractionalSharedQuota_Rounded()
        {
            WritePerTerminal("Alice", "doc1", $"{{\"contextPct\":40,\"timestamp\":{NowMs - 1000}}}");
            WriteShared($"{{\"quota5h\":55.9,\"quota7d\":66.1,\"timestamp\":{NowMs - 1000}}}");
            var s = _reader.ReadFor("Alice", "doc1");
            Assert.Equal("shared", s.QuotaSource);
            Assert.Equal(56, s.FiveHourPercent);
            Assert.Equal(66, s.SevenDayPercent);
        }

        [Fact]
        public void StaleSharedQuota_FallsBackToFreshPerTerminalQuota()
        {
            // Per-terminal is fresh (1s) with its own quota copy; the shared account
            // file is hours old. The stale shared numbers must NOT be presented as the
            // live rate-cap — fall back to the fresh per-terminal copy. [adversary HIGH]
            WritePerTerminal("Alice", "doc1",
                $"{{\"contextPct\":43,\"quota5h\":12,\"quota7d\":34,\"timestamp\":{NowMs - 1000}}}");
            WriteShared($"{{\"quota5h\":99,\"quota7d\":99,\"timestamp\":{NowMs - 120_000}}}");

            var s = _reader.ReadFor("Alice", "doc1");

            Assert.Equal("per-terminal", s.QuotaSource);
            Assert.Equal(12, s.FiveHourPercent); // per-terminal value, not the stale 99
            Assert.Equal(34, s.SevenDayPercent);
            Assert.False(s.QuotaStale);          // per-terminal copy is itself fresh
            Assert.False(s.Stale);               // context reading is fresh
        }

        [Fact]
        public void FreshSharedQuota_NullFields_DoesNotEraseValidPerTerminalQuota()
        {
            // statusline.js writes the shared file whenever rate_limits exists, even
            // with null subfields. A fresh-but-empty shared file must not overwrite
            // usable per-terminal quota numbers with nulls. [adversary medium, run 2]
            WritePerTerminal("Alice", "doc1",
                $"{{\"contextPct\":43,\"quota5h\":12,\"quota7d\":34,\"timestamp\":{NowMs - 1000}}}");
            WriteShared($"{{\"quota5h\":null,\"quota7d\":null,\"timestamp\":{NowMs - 1000}}}");

            var s = _reader.ReadFor("Alice", "doc1");

            Assert.Equal("per-terminal", s.QuotaSource);
            Assert.Equal(12, s.FiveHourPercent);
            Assert.Equal(34, s.SevenDayPercent);
        }

        [Fact]
        public void FutureDatedSharedQuota_NotTrusted_FallsBackToPerTerminal()
        {
            WritePerTerminal("Alice", "doc1",
                $"{{\"contextPct\":43,\"quota5h\":12,\"quota7d\":34,\"timestamp\":{NowMs - 1000}}}");
            WriteShared($"{{\"quota5h\":99,\"quota7d\":99,\"timestamp\":{NowMs + 600_000}}}"); // 10 min ahead

            var s = _reader.ReadFor("Alice", "doc1");

            Assert.Equal("per-terminal", s.QuotaSource);
            Assert.Equal(12, s.FiveHourPercent);
        }

        [Fact]
        public void FreshSharedQuota_PopulatesQuotaStalenessFields()
        {
            WritePerTerminal("Alice", "doc1", $"{{\"contextPct\":43,\"timestamp\":{NowMs - 5000}}}");
            WriteShared($"{{\"quota5h\":55,\"quota7d\":66,\"timestamp\":{NowMs - 2000}}}");

            var s = _reader.ReadFor("Alice", "doc1");

            Assert.Equal("shared", s.QuotaSource);
            Assert.Equal(NowMs - 2000, s.QuotaSourceTimestampMs);
            Assert.Equal(2.0, s.QuotaAgeSeconds);
            Assert.False(s.QuotaStale);
        }

        [Fact]
        public void FutureDatedPerTerminal_FlaggedStale()
        {
            // A legit file is always written before we read it, so a future timestamp
            // means clock skew or a planted file — flag stale, don't report it "fresh".
            WritePerTerminal("Alice", "doc1", $"{{\"contextPct\":50,\"timestamp\":{NowMs + 600_000}}}");
            var s = _reader.ReadFor("Alice", "doc1");
            Assert.True(s.Available);
            Assert.True(s.Stale);
        }

        [Fact]
        public void NoDocId_IgnoresFutureDatedSibling()
        {
            // A planted/zombie file with a far-future timestamp must not pin selection
            // ahead of the genuine newest file. [security A04]
            WritePerTerminal("Alice", "real", $"{{\"contextPct\":77,\"timestamp\":{NowMs - 2000}}}");
            WritePerTerminal("Alice", "zombie", $"{{\"contextPct\":11,\"timestamp\":{NowMs + 9_000_000}}}");

            var s = _reader.ReadFor("Alice"); // no docId → newest non-future wins

            Assert.True(s.Available);
            Assert.Equal(77, s.ContextPercent);
        }

        // ───────── the freshness floor (task 85af4635, pipeline run 1) ─────────

        /// <summary>
        /// A file older than the caller's floor yields NO reading, not a stale one.
        /// </summary>
        /// <remarks>
        /// <see cref="TerminalUsageStats.Stale"/> answers "is this old?" and is advisory. The floor
        /// answers "could this possibly describe a terminal running now?" and is disqualifying.
        /// Measured on a live machine before this existed: readings 40 and 58 DAYS old came back
        /// available, with real percentages, for agents whose current run had written nothing. A
        /// consumer that renders a percentage cannot be handed that and be expected to do the right
        /// thing with a boolean beside it — the percentage is what gets read and acted on.
        /// </remarks>
        [Fact]
        public void A_reading_older_than_the_floor_is_not_available()
        {
            long weekOld = NowMs - (7L * 24 * 60 * 60 * 1000);
            WritePerTerminal("Alice", "doc1", $"{{\"contextPct\":44,\"timestamp\":{weekOld}}}");

            var s = _reader.ReadFor("Alice", "doc1", notBeforeUnixMs: NowMs - 60_000);

            Assert.False(s.Available);
            Assert.Null(s.ContextPercent);
        }

        /// <summary>
        /// COUNTERWEIGHT: a reading ON or after the floor still comes through. Without this the
        /// floor is indistinguishable from "never report anything".
        /// </summary>
        [Fact]
        public void A_reading_at_or_after_the_floor_is_still_available()
        {
            WritePerTerminal("Alice", "doc1", $"{{\"contextPct\":44,\"timestamp\":{NowMs - 5_000}}}");

            var s = _reader.ReadFor("Alice", "doc1", notBeforeUnixMs: NowMs - 60_000);

            Assert.True(s.Available);
            Assert.Equal(44, s.ContextPercent);
        }

        /// <summary>
        /// A file with NO timestamp cannot prove it is recent, and a caller that passed a floor
        /// asked for proof. Rejected rather than assumed fresh.
        /// </summary>
        [Fact]
        public void An_untimestamped_reading_is_rejected_when_a_floor_is_in_force()
        {
            WritePerTerminal("Alice", "doc1", "{\"contextPct\":44}");

            Assert.False(_reader.ReadFor("Alice", "doc1", notBeforeUnixMs: NowMs - 60_000).Available);

            // ...but is still returned when no floor was asked for, so existing callers are
            // untouched. The floor is opt-in; this is the proof it did not change the default.
            Assert.True(_reader.ReadFor("Alice", "doc1").Available);
        }

        // ───────── the account quota, standalone ─────────

        /// <summary>
        /// The account numbers must be reachable WITHOUT a per-terminal file.
        /// </summary>
        /// <remarks>
        /// <c>ReadFor</c> returns early when the caller's own file is missing — before the
        /// shared-quota block — so an account-scoped number that was present and fresh on disk was
        /// being suppressed by the absence of one terminal's private file, which has nothing to do
        /// with it.
        /// </remarks>
        [Fact]
        public void The_account_quota_is_readable_with_no_per_terminal_file_present()
        {
            WriteShared($"{{\"quota5h\":30,\"quota7d\":82,\"timestamp\":{NowMs - 5_000}}}");

            var q = _reader.ReadAccountQuota();

            Assert.True(q.Available);
            Assert.Equal(30, q.FiveHourPercent);
            Assert.Equal(82, q.SevenDayPercent);
            Assert.Equal(TerminalUsageStats.QuotaSourceShared, q.QuotaSource);
            Assert.False(q.QuotaStale);
        }

        /// <summary>An old shared file is reported, and flagged — shown-but-marked, not hidden.</summary>
        [Fact]
        public void A_stale_account_quota_is_returned_and_flagged()
        {
            WriteShared($"{{\"quota5h\":30,\"timestamp\":{NowMs - 600_000}}}");

            var q = _reader.ReadAccountQuota();

            Assert.True(q.Available);
            Assert.True(q.QuotaStale);
            Assert.Equal(30, q.FiveHourPercent);
        }

        /// <summary>
        /// A future-dated shared file is stale, never "extra fresh" — same polarity as
        /// <see cref="ReadFor"/>, because a legitimate writer always writes before we read.
        /// </summary>
        [Fact]
        public void A_future_dated_account_quota_is_stale_not_fresh()
        {
            WriteShared($"{{\"quota5h\":30,\"timestamp\":{NowMs + 600_000}}}");

            Assert.True(_reader.ReadAccountQuota().QuotaStale);
        }

        /// <summary>No shared file, and an untimestamped one, both mean no account reading.</summary>
        [Fact]
        public void A_missing_or_untimestamped_account_quota_is_unavailable()
        {
            Assert.False(_reader.ReadAccountQuota().Available);

            WriteShared("{\"quota5h\":30}");
            Assert.False(_reader.ReadAccountQuota().Available);
        }
    }
}
