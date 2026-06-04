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
    }
}
