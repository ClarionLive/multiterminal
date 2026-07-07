using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MultiTerminal.MCPServer.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Falsifiable coverage for <see cref="SessionDiscovery"/>'s JSONL-derived
    /// index (Eval P5 repair A). The load-bearing invariant: discovery no longer
    /// depends on <c>sessions-index.json</c> (which the Claude CLI stopped writing
    /// in early 2026) — it derives one row per valid session JSONL, tolerates a
    /// torn last line, and only honors the index when it is genuinely fresh.
    ///
    /// <para>Each test builds a throwaway <c>~/.claude/projects</c> tree on disk
    /// and points a <see cref="SessionDiscovery"/> at it via the test constructor.</para>
    /// </summary>
    public sealed class SessionDiscoveryTests : IDisposable
    {
        private readonly string _projectsRoot;
        private readonly string _projectPath = @"C:\Fake\P5Proj";
        private readonly string _projectFolder;

        public SessionDiscoveryTests()
        {
            _projectsRoot = Path.Combine(Path.GetTempPath(), $"mt_sd_test_{Guid.NewGuid():N}");
            _projectFolder = Path.Combine(_projectsRoot, SessionDiscovery.GetProjectFolderName(_projectPath));
            Directory.CreateDirectory(_projectFolder);
        }

        public void Dispose()
        {
            try { Directory.Delete(_projectsRoot, recursive: true); } catch { /* best effort */ }
        }

        private static string UserLine(string content)
            => JsonSerializer.Serialize(new
            {
                type = "user",
                uuid = Guid.NewGuid().ToString(),
                timestamp = "2026-07-07T10:00:00Z",
                message = new { role = "user", content }
            });

        private string WriteSession(string content, params string[] extraRawLines)
        {
            var path = Path.Combine(_projectFolder, $"{Guid.NewGuid()}.jsonl");
            var lines = new List<string> { UserLine(content) };
            lines.AddRange(extraRawLines);
            File.WriteAllLines(path, lines);
            return path;
        }

        [Fact]
        public void RowCount_EqualsValidJsonlFileCount_Torn_And_NonSession_Excluded()
        {
            // 3 clean sessions.
            WriteSession("[Alice]: hello one");
            WriteSession("[Bob]: hello two");
            WriteSession("register as Carol");

            // 1 session whose LAST line is torn (truncated JSON). ParseSessionFile
            // skips the unparseable line but the earlier good line still yields a
            // message -> this file MUST still count as one derived row.
            WriteSession("[Dave]: good line", "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"truncated");

            // NOT a session: filename is not a UUID -> misclassification guard drops it.
            File.WriteAllText(Path.Combine(_projectFolder, "notes.jsonl"), UserLine("[Zoe]: not a session"));

            // NOT a JSONL at all -> ignored by the *.jsonl enumeration.
            File.WriteAllText(Path.Combine(_projectFolder, "readme.txt"), "hello");

            var sessions = new SessionDiscovery(_projectsRoot).DiscoverAllSessionsInProject(_projectPath);

            // 4 valid JSONL session files (3 clean + 1 torn) == 4 derived rows.
            Assert.Equal(4, sessions.Count);
        }

        [Fact]
        public void Derivation_ExtractsIdentity_FromFirstPrompt()
        {
            WriteSession("[Alice]: working on the thing");
            WriteSession("[Alice]: another Alice session");
            WriteSession("[Bob]: a Bob session");

            var discovery = new SessionDiscovery(_projectsRoot);

            Assert.Equal(2, discovery.DiscoverSessionsForIdentity("Alice", _projectPath).Count);
            var identities = discovery.DiscoverIdentitiesInProject(_projectPath);
            Assert.Contains("Alice", identities.Keys);
            Assert.Contains("Bob", identities.Keys);
        }

        [Fact]
        public void StaleIndex_IsIgnored_DerivationWins()
        {
            // Two JSONL sessions on disk...
            WriteSession("[Alice]: s1");
            WriteSession("[Bob]: s2");

            // ...but a sessions-index.json that is OLDER than them and claims only
            // ONE (fabricated) session. Because it is stale, derivation must win:
            // the result reflects the 2 real JSONL files, not the 1 stale row.
            var indexPath = Path.Combine(_projectFolder, "sessions-index.json");
            File.WriteAllText(indexPath, JsonSerializer.Serialize(new
            {
                version = 1,
                entries = new[] { new { sessionId = "11111111-1111-1111-1111-111111111111", firstPrompt = "[Ghost]: stale" } }
            }));
            File.SetLastWriteTimeUtc(indexPath, DateTime.UtcNow.AddDays(-30));

            var sessions = new SessionDiscovery(_projectsRoot).DiscoverAllSessionsInProject(_projectPath);
            Assert.Equal(2, sessions.Count);
        }

        [Fact]
        public void FreshIndex_IsUsed_AsFastPath_WhenComplete()
        {
            // One real JSONL on disk...
            var path = WriteSession("[Alice]: real session");
            var uuid = Path.GetFileNameWithoutExtension(path);

            // ...and a FRESH sessions-index.json that COVERS that JSONL (its uuid is
            // present) plus one index-only extra. Fresh AND complete => the fast-path
            // serves the index verbatim (2), not the single derived JSONL.
            var indexPath = Path.Combine(_projectFolder, "sessions-index.json");
            File.WriteAllText(indexPath, JsonSerializer.Serialize(new
            {
                version = 1,
                entries = new[]
                {
                    new { sessionId = uuid, firstPrompt = "[Alice]: idx covers the on-disk session" },
                    new { sessionId = "33333333-3333-3333-3333-333333333333", firstPrompt = "[Bob]: index-only extra" }
                }
            }));
            File.SetLastWriteTimeUtc(indexPath, DateTime.UtcNow.AddHours(1));

            var sessions = new SessionDiscovery(_projectsRoot).DiscoverAllSessionsInProject(_projectPath);
            Assert.Equal(2, sessions.Count);
        }

        [Fact]
        public void FreshButIncompleteIndex_DerivesFromJsonl()
        {
            // Two JSONL sessions on disk...
            var p1 = WriteSession("[Alice]: s1");
            WriteSession("[Bob]: s2");
            var uuid1 = Path.GetFileNameWithoutExtension(p1);

            // ...but a FRESH index (newer than both) that lists only ONE of them.
            // Freshness alone is NOT coverage: an incomplete index would silently
            // hide the second session, so it must be rejected and derivation win (2).
            var indexPath = Path.Combine(_projectFolder, "sessions-index.json");
            File.WriteAllText(indexPath, JsonSerializer.Serialize(new
            {
                version = 1,
                entries = new[] { new { sessionId = uuid1, firstPrompt = "[Alice]: s1" } }
            }));
            File.SetLastWriteTimeUtc(indexPath, DateTime.UtcNow.AddHours(1));

            var sessions = new SessionDiscovery(_projectsRoot).DiscoverAllSessionsInProject(_projectPath);
            Assert.Equal(2, sessions.Count);
        }

        [Fact]
        public void MtimeTie_IsTreatedAsStale_DerivesFromJsonl()
        {
            // One JSONL, and an index that COVERS it (+ an extra) but shares its
            // EXACT mtime. A tie is treated as stale — a JSONL written in the same
            // clock tick as the index may not be reflected in it — so derivation
            // wins (1), not the index (2).
            var p1 = WriteSession("[Alice]: s1");
            var uuid1 = Path.GetFileNameWithoutExtension(p1);
            var indexPath = Path.Combine(_projectFolder, "sessions-index.json");
            File.WriteAllText(indexPath, JsonSerializer.Serialize(new
            {
                version = 1,
                entries = new[]
                {
                    new { sessionId = uuid1, firstPrompt = "[Alice]: s1" },
                    new { sessionId = "44444444-4444-4444-4444-444444444444", firstPrompt = "[X]: extra" }
                }
            }));
            var tie = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(p1, tie);
            File.SetLastWriteTimeUtc(indexPath, tie);

            var sessions = new SessionDiscovery(_projectsRoot).DiscoverAllSessionsInProject(_projectPath);
            Assert.Single(sessions);
        }
    }
}
