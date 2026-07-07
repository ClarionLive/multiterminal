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
        public void Derivation_ExtractsOwnershipSafeIdentity_FromFirstPrompt()
        {
            // The only trusted transcript marker — MT's system-generated SessionStart
            // hook injection — is what the fallback extracts. NOT free-form prose and
            // NOT the "[Name]:" sent-not-is pattern.
            WriteSession("MULTITERMINAL: You are registered as Alice");
            WriteSession("MULTITERMINAL: You are registered as Alice (second session)");
            WriteSession("MULTITERMINAL: You are registered as Bob");

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
        public void FreshIndex_IsUsed_AsFastPath_WhenExactAndFresh()
        {
            // One JSONL on disk whose CONTENT says Alice...
            var path = WriteSession("[Alice]: from jsonl content");
            var uuid = Path.GetFileNameWithoutExtension(path);

            // ...and a FRESH index that EXACTLY covers it (same id set on disk, no
            // phantom) but records a DIFFERENT firstPrompt. If the fast-path serves
            // the index, the returned session carries the index's firstPrompt rather
            // than the derived one — that is how we prove the index was used and not
            // re-derived (the counts would otherwise be identical).
            var indexPath = Path.Combine(_projectFolder, "sessions-index.json");
            File.WriteAllText(indexPath, JsonSerializer.Serialize(new
            {
                version = 1,
                entries = new[] { new { sessionId = uuid, firstPrompt = "[Zeta]: served from the fresh index" } }
            }));
            File.SetLastWriteTimeUtc(indexPath, DateTime.UtcNow.AddHours(1));

            var sessions = new SessionDiscovery(_projectsRoot).DiscoverAllSessionsInProject(_projectPath);
            var only = Assert.Single(sessions);
            Assert.Contains("served from the fresh index", only.FirstPrompt);
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

        [Fact]
        public void FreshButSupersetIndex_DerivesFromJsonl()
        {
            // One real JSONL on disk...
            var p1 = WriteSession("[Alice]: real");
            var uuid1 = Path.GetFileNameWithoutExtension(p1);

            // ...and a FRESH index that COVERS it but ALSO lists a phantom session
            // whose JSONL no longer exists. Trust must be EXACT, not just a superset:
            // an indexed session with no transcript on disk would be a fabricated row,
            // so the index is rejected and derivation wins (1), not the 2-entry index.
            var indexPath = Path.Combine(_projectFolder, "sessions-index.json");
            File.WriteAllText(indexPath, JsonSerializer.Serialize(new
            {
                version = 1,
                entries = new[]
                {
                    new { sessionId = uuid1, firstPrompt = "[Alice]: real" },
                    new { sessionId = "55555555-5555-5555-5555-555555555555", firstPrompt = "[Ghost]: deleted transcript" }
                }
            }));
            File.SetLastWriteTimeUtc(indexPath, DateTime.UtcNow.AddHours(1));

            var sessions = new SessionDiscovery(_projectsRoot).DiscoverAllSessionsInProject(_projectPath);
            Assert.Single(sessions);
        }

        [Fact]
        public void Identity_ResolverWins_OverTranscript_AndFallsBackWhenUnknown()
        {
            // Session A: the transcript starts "[Zeta]:" (a message Zeta SENT here —
            // NOT ownership) but the authoritative resolver knows it was owned by Bob.
            var pathA = WriteSession("[Zeta]: a message Zeta sent to this terminal");
            var uuidA = Path.GetFileNameWithoutExtension(pathA);
            // Session B: the resolver doesn't know it → falls back to the transcript's
            // trusted system-hook marker.
            WriteSession("MULTITERMINAL: You are registered as Alice");

            var lineage = new System.Collections.Generic.Dictionary<string, string> { [uuidA] = "Bob" };
            var discovery = new SessionDiscovery(_projectsRoot,
                sid => lineage.TryGetValue(sid, out var n) ? n : null);

            var identities = discovery.DiscoverIdentitiesInProject(_projectPath);

            Assert.Contains("Bob", identities.Keys);         // resolver won over the transcript
            Assert.DoesNotContain("Zeta", identities.Keys);  // "[Zeta]:" is sent-not-is, never an owner
            Assert.Contains("Alice", identities.Keys);       // ownership-safe fallback for the unknown session
        }

        [Fact]
        public void SentNotIs_ReceivedMessageMarker_NotAttributedAsOwner_WhenResolverUnknown()
        {
            // "[Alice]:" means Alice SENT a message to this terminal — NOT that the
            // terminal IS Alice. With no authoritative mapping, the session must NOT be
            // attributed to Alice, or a foreign/crafted "[Alice]: ..." transcript could
            // spoof identity (security, task 4558fa6b).
            WriteSession("[Alice]: hello from Alice");

            // Resolver is wired but returns null (session unknown) → ownership-safe fallback.
            var discovery = new SessionDiscovery(_projectsRoot, sid => null);
            var identities = discovery.DiscoverIdentitiesInProject(_projectPath);

            Assert.DoesNotContain("Alice", identities.Keys);
            Assert.Empty(identities);
        }

        [Fact]
        public void ResolverThrows_DegradesToTranscriptFallback_WithoutBlankingAll()
        {
            // A session with the trusted hook marker...
            WriteSession("MULTITERMINAL: You are registered as Alice");

            // ...and a resolver that throws for EVERY session (e.g. transient DB
            // failure). It must NOT abort the whole pass and blank every identity —
            // each session degrades to its transcript fallback.
            var discovery = new SessionDiscovery(_projectsRoot,
                sid => throw new InvalidOperationException("transient DB failure"));

            var identities = discovery.DiscoverIdentitiesInProject(_projectPath);
            Assert.Contains("Alice", identities.Keys);
        }

        [Fact]
        public void FreeFormRegisterText_NotAttributedAsOwner_WhenResolverUnknown()
        {
            // Incidental free-form prose that merely MENTIONS registering must NOT
            // attribute ownership — otherwise a foreign/crafted transcript could spoof
            // a team identity for an unmapped session. Only the resolver
            // (session_agent_map) or the system hook marker determine ownership; this
            // session has neither, so it stays Unknown / out of the dropdown
            // (task 4558fa6b, security — Run 3).
            WriteSession("please register as Alice before you start, and also register as Bob");

            var discovery = new SessionDiscovery(_projectsRoot, sid => null);
            var identities = discovery.DiscoverIdentitiesInProject(_projectPath);

            Assert.DoesNotContain("Alice", identities.Keys);
            Assert.DoesNotContain("Bob", identities.Keys);
            Assert.Empty(identities);
        }
    }
}
