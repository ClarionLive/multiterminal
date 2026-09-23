#nullable enable
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.API.Controllers;
using MultiTerminal.MCPServer.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task <c>7f389704</c>: a PM closes a helper it spawned with <c>close_helper</c>. The property that
    /// matters is WHO may close: only the pane that spawned the helper, proven by its launch nonce, never
    /// by a name the caller typed. Behavioural facts against the real <see cref="SpawnedPaneRegistry"/>,
    /// the real <see cref="SpawnController"/> and a real <see cref="MessageBroker"/>; MainForm's close
    /// callback is a recording stub, because the pane teardown it triggers is the existing tab-✕ path.
    /// </summary>
    public sealed class SpawnedPaneCloseTests : IDisposable
    {
        private const string PmDocId = "aa11bb22";
        private const string PmNonce = "NONCE-PM";
        private const string OtherDocId = "cc33dd44";
        private const string OtherNonce = "NONCE-OTHER";
        private const string HelperDocId = "ee55ff66";

        private readonly string _testDbPath;
        private readonly string _testMsgDbPath;

        /// <summary>
        /// A real <see cref="MessageBroker"/> opens both of MT's databases, so both are pointed at temp
        /// files first. Same isolation idiom as <c>SpawnJobStoreTests</c> (task 2ddfc32f).
        /// </summary>
        public SpawnedPaneCloseTests()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_spawnclose_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _testDbPath);
            _testMsgDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_spawnclose_msg_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", _testMsgDbPath);
        }

        public void Dispose()
        {
            SQLiteConnection.ClearAllPools();
            foreach (var p in new[]
            {
                _testDbPath, _testDbPath + "-wal", _testDbPath + "-shm",
                _testMsgDbPath, _testMsgDbPath + "-wal", _testMsgDbPath + "-shm",
            })
            {
                // Best-effort, as in SpawnJobStoreTests: a leftover temp file must not fail a test.
                try
                {
                    if (File.Exists(p)) File.Delete(p);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", null);
        }

        // ---- registry ----------------------------------------------------------------------------

        [Fact]
        public void A_recorded_helper_is_found_by_its_spawner_case_insensitively()
        {
            var reg = new SpawnedPaneRegistry();
            reg.Record(HelperDocId, "Bob", PmDocId, PmNonce);

            var found = Assert.Single(reg.FindForSpawner(PmDocId, PmNonce, "bob"));
            Assert.Equal(HelperDocId, found.HelperDocId);
            Assert.Equal("Bob", found.HelperName);
        }

        [Fact]
        public void Another_pane_cannot_see_a_helper_it_did_not_spawn()
        {
            var reg = new SpawnedPaneRegistry();
            reg.Record(HelperDocId, "Bob", PmDocId, PmNonce);

            Assert.Empty(reg.FindForSpawner(OtherDocId, OtherNonce, "Bob"));
        }

        /// <summary>
        /// The lookup is exact apart from case; trimming is the controller's job at the boundary. A trim
        /// in here would be a permissive comparison in front of an exact one.
        /// </summary>
        [Fact]
        public void The_lookup_does_not_trim()
        {
            var reg = new SpawnedPaneRegistry();
            reg.Record(HelperDocId, "Bob", PmDocId, PmNonce);

            Assert.Empty(reg.FindForSpawner(PmDocId, PmNonce, " Bob"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void An_unproven_spawner_records_nothing(string? spawnerDocId)
        {
            var reg = new SpawnedPaneRegistry();
            reg.Record(HelperDocId, "Bob", spawnerDocId, PmNonce);

            Assert.Equal(0, reg.Count);
        }

        [Fact]
        public void A_forgotten_pane_can_never_be_matched_again()
        {
            var reg = new SpawnedPaneRegistry();
            reg.Record(HelperDocId, "Bob", PmDocId, PmNonce);

            Assert.True(reg.Forget(HelperDocId));
            Assert.False(reg.Forget(HelperDocId));
            Assert.Empty(reg.FindForSpawner(PmDocId, PmNonce, "Bob"));
        }

        /// <summary>
        /// The spawner's DocId is not enough on its own: the entry answers only to the nonce the spawner
        /// proved itself with.
        /// </summary>
        [Fact]
        public void The_spawners_DocId_with_another_nonce_finds_nothing()
        {
            var reg = new SpawnedPaneRegistry();
            reg.Record(HelperDocId, "Bob", PmDocId, PmNonce);

            Assert.Empty(reg.FindForSpawner(PmDocId, "NONCE-ATTACKER", "Bob"));
        }

        [Fact]
        public void A_spawner_that_leaves_takes_its_entries_with_it()
        {
            var reg = new SpawnedPaneRegistry();
            reg.Record(HelperDocId, "Bob", PmDocId, PmNonce);
            reg.Record("other-helper", "Dan", OtherDocId, OtherNonce);

            Assert.Equal(1, reg.ForgetSpawner(PmDocId));
            Assert.Empty(reg.FindForSpawner(PmDocId, PmNonce, "Bob"));
            Assert.Single(reg.FindForSpawner(OtherDocId, OtherNonce, "Dan"));
        }

        // ---- controller --------------------------------------------------------------------------

        /// <summary>
        /// The whole path: the PM spawns with its nonce, then closes with its nonce, and exactly the
        /// helper's pane is handed to the close callback.
        /// </summary>
        [Fact]
        public async Task The_spawning_pane_closes_its_helper()
        {
            using var h = new Harness();
            await h.Spawn("Bob", spawnerName: "Alice", nonce: PmNonce);

            var ok = Assert.IsType<OkObjectResult>(await h.Close("Bob", PmNonce));

            Assert.Equal(new[] { HelperDocId }, h.Closed);
            Assert.Equal(true, Prop(ok.Value, "closed"));
            Assert.Equal("Bob", Prop(ok.Value, "terminalName"));
        }

        /// <summary>
        /// No nonce, an empty one, or one no pane holds: 401, and nothing is closed.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("NONCE-WRONG")]
        public async Task A_caller_that_proves_no_pane_is_refused(string? nonce)
        {
            using var h = new Harness();
            await h.Spawn("Bob", spawnerName: "Alice", nonce: PmNonce);

            var refused = Assert.IsType<ObjectResult>(await h.Close("Bob", nonce));

            Assert.Equal(401, refused.StatusCode);
            Assert.Empty(h.Closed);
        }

        /// <summary>
        /// THE DISCRIMINATOR. Another live pane presents its own valid nonce. A check that only asks "is
        /// this some connected pane" accepts it; only comparing that pane with the recorded spawner
        /// refuses it. Answered 404, the same as a name that does not exist, so it learns nothing.
        /// </summary>
        [Fact]
        public async Task Another_live_pane_cannot_close_the_helper()
        {
            using var h = new Harness();
            await h.Spawn("Bob", spawnerName: "Alice", nonce: PmNonce);

            var refused = Assert.IsType<ObjectResult>(await h.Close("Bob", OtherNonce));

            Assert.Equal(404, refused.StatusCode);
            Assert.Empty(h.Closed);
            Assert.Equal(1, h.Service.Panes.Count);
        }

        /// <summary>
        /// The spawner is the pane the nonce proves, not the name the caller typed. Spawned by the other
        /// pane while CLAIMING to be Alice, the helper belongs to the other pane: Alice's pane cannot close
        /// it, and the pane that really asked can.
        /// </summary>
        [Fact]
        public async Task The_spawner_is_the_proven_pane_not_the_claimed_name()
        {
            using var h = new Harness();
            await h.Spawn("Bob", spawnerName: "Alice", nonce: OtherNonce);

            Assert.Equal(404, Assert.IsType<ObjectResult>(await h.Close("Bob", PmNonce)).StatusCode);
            Assert.Empty(h.Closed);

            Assert.IsType<OkObjectResult>(await h.Close("Bob", OtherNonce));
            Assert.Equal(new[] { HelperDocId }, h.Closed);
        }

        /// <summary>
        /// A spawn with no nonce (the phone app, an MCP server older than this ticket) says so in its
        /// result and records nothing, so no agent can close that pane.
        /// </summary>
        [Fact]
        public async Task A_spawn_without_a_nonce_is_not_closable_by_any_agent()
        {
            using var h = new Harness();
            var ok = Assert.IsType<OkObjectResult>(await h.Spawn("Bob", spawnerName: "Alice", nonce: null));

            Assert.Equal(false, Prop(ok.Value, "closableBySpawner"));
            Assert.Equal(0, h.Service.Panes.Count);
            Assert.Equal(404, Assert.IsType<ObjectResult>(await h.Close("Bob", PmNonce)).StatusCode);
            Assert.Empty(h.Closed);
        }

        /// <summary>
        /// The pane was already gone when the close arrived: 404, and its stale entry is dropped.
        /// </summary>
        [Fact]
        public async Task A_pane_that_is_already_gone_is_reported_and_forgotten()
        {
            using var h = new Harness { PaneIsOpen = false };
            await h.Spawn("Bob", spawnerName: "Alice", nonce: PmNonce);

            Assert.Equal(404, Assert.IsType<ObjectResult>(await h.Close("Bob", PmNonce)).StatusCode);
            Assert.Equal(0, h.Service.Panes.Count);
        }

        /// <summary>
        /// Pipeline debugger (MEDIUM). Something else in the PM's pane, such as a subagent, shares its env
        /// and registers under another name presenting the pane's DocId and nonce. The broker rejects the
        /// DocId claim and keeps a row with no DocId but the SAME nonce. With the pane's own row also
        /// disconnected, that row is the only one holding the nonce: the old lookup returned it, and
        /// callers took its missing DocId as "no proven pane". This is the DETERMINISTIC form. With both
        /// rows connected, the old lookup was wrong only when dictionary order happened to put the
        /// DocId-less row first, so that case alone could pass by luck.
        /// </summary>
        [Fact]
        public void A_row_without_a_DocId_is_never_the_pane_a_nonce_resolves_to()
        {
            using var broker = new MessageBroker();
            broker.RegisterTerminal("Alice", docId: PmDocId, nonce: PmNonce);
            broker.RegisterTerminal("Agent Explore", docId: PmDocId, nonce: PmNonce);

            // Precondition: the fixture really produced the DocId-less row sharing the pane's nonce.
            // Without this the facts below would pass vacuously if the broker ever stopped doing so.
            broker.UnregisterTerminal(PmDocId);
            var onlyRow = broker.GetConnectedTerminalByLaunchNonce(PmNonce);
            Assert.NotNull(onlyRow);
            Assert.True(string.IsNullOrEmpty(onlyRow!.DocId), "fixture did not produce a DocId-less row holding the nonce");

            Assert.Null(broker.GetConnectedPaneByLaunchNonce(PmNonce));
        }

        /// <summary>
        /// The positive half: with the pane's row and the DocId-less row both connected, the pane lookup
        /// returns the pane, so the PM can close its helper. NOT a discriminator on its own (see above).
        /// </summary>
        [Fact]
        public async Task A_subagent_row_in_the_pms_pane_does_not_stop_the_close()
        {
            using var h = new Harness();
            h.Broker.RegisterTerminal("Agent Explore", docId: PmDocId, nonce: PmNonce);

            var ok = Assert.IsType<OkObjectResult>(await h.Spawn("Bob", spawnerName: "Alice", nonce: PmNonce));
            Assert.Equal(true, Prop(ok.Value, "closableBySpawner"));
            Assert.IsType<OkObjectResult>(await h.Close("Bob", PmNonce));
            Assert.Equal(new[] { HelperDocId }, h.Closed);
        }

        /// <summary>
        /// Pipeline Run 1, Codex security HIGH. The spawner's pane is gone and something registers a
        /// fresh connected row holding its OLD DocId (DocIds are visible in listings) with a nonce of
        /// its own. Keyed on DocId alone, the close route accepted that row as the spawner and killed a
        /// helper it did not spawn. The precondition proves the broker really hands the attacker the
        /// old DocId; without it this fact would pass vacuously if the broker ever refused.
        /// </summary>
        [Fact]
        public async Task A_new_row_holding_a_gone_spawners_DocId_cannot_close_its_helper()
        {
            using var h = new Harness();
            await h.Spawn("Bob", spawnerName: "Alice", nonce: PmNonce);
            h.Broker.UnregisterTerminal(PmDocId);
            h.Broker.RegisterTerminal("Mallory", docId: PmDocId, nonce: "NONCE-ATTACKER");
            Assert.Equal(PmDocId, h.Broker.GetConnectedPaneByLaunchNonce("NONCE-ATTACKER")?.DocId);

            Assert.Equal(404, Assert.IsType<ObjectResult>(await h.Close("Bob", "NONCE-ATTACKER")).StatusCode);
            Assert.Empty(h.Closed);
        }

        [Fact]
        public async Task Two_helpers_matching_one_name_close_neither()
        {
            using var h = new Harness();
            h.Service.Panes.Record("doc-1", "Bob", PmDocId, PmNonce);
            h.Service.Panes.Record("doc-2", "BOB", PmDocId, PmNonce);

            Assert.Equal(409, Assert.IsType<ObjectResult>(await h.Close("bob", PmNonce)).StatusCode);
            Assert.Empty(h.Closed);
        }

        private static object? Prop(object? anonymous, string name)
            => anonymous?.GetType().GetProperty(name)?.GetValue(anonymous);

        private sealed class Harness : IDisposable
        {
            public Harness()
            {
                Broker.RegisterTerminal("Alice", docId: PmDocId, nonce: PmNonce);
                Broker.RegisterTerminal("Carol", docId: OtherDocId, nonce: OtherNonce);
                Service = new SpawnService
                {
                    OnSpawnRequested = (agentName, agentType, workingDir, initialPrompt, spawnerName)
                        => Task.FromResult((true, HelperDocId, (string)null!, agentName)),
                    OnCloseRequested = docId =>
                    {
                        if (PaneIsOpen)
                        {
                            Closed.Add(docId);
                        }

                        return Task.FromResult(PaneIsOpen);
                    },
                };
                Controller = new SpawnController(Service, projectDatabase: null, Broker);
            }

            public MessageBroker Broker { get; } = new MessageBroker();

            public SpawnService Service { get; }

            public SpawnController Controller { get; }

            public List<string> Closed { get; } = new();

            public bool PaneIsOpen { get; set; } = true;

            public Task<IActionResult> Spawn(string name, string spawnerName, string? nonce)
                => Controller.SpawnTerminal(new SpawnTerminalRequest { AgentName = name, SpawnerName = spawnerName }, nonce!);

            public Task<IActionResult> Close(string name, string? nonce)
                => Controller.CloseSpawnedTerminal(new CloseSpawnedTerminalRequest { TerminalName = name }, nonce!);

            public void Dispose() => Broker.Dispose();
        }
    }
}
