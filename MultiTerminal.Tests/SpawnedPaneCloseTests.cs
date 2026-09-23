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
        public void A_recorded_helper_is_found_by_its_spawner_with_the_brokers_name_comparison()
        {
            var reg = new SpawnedPaneRegistry();
            reg.Record(HelperDocId, "Bob", PmDocId);

            var found = Assert.Single(reg.FindForSpawner(PmDocId, "bob"));
            Assert.Equal(HelperDocId, found.HelperDocId);
            Assert.Equal("Bob", found.HelperName);
        }

        [Fact]
        public void Another_pane_cannot_see_a_helper_it_did_not_spawn()
        {
            var reg = new SpawnedPaneRegistry();
            reg.Record(HelperDocId, "Bob", PmDocId);

            Assert.Empty(reg.FindForSpawner(OtherDocId, "Bob"));
        }

        /// <summary>
        /// The lookup is exact apart from case; trimming is the controller's job at the boundary. A trim
        /// in here would be a permissive comparison in front of an exact one.
        /// </summary>
        [Fact]
        public void The_lookup_does_not_trim()
        {
            var reg = new SpawnedPaneRegistry();
            reg.Record(HelperDocId, "Bob", PmDocId);

            Assert.Empty(reg.FindForSpawner(PmDocId, " Bob"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void An_unproven_spawner_records_nothing(string? spawnerDocId)
        {
            var reg = new SpawnedPaneRegistry();
            reg.Record(HelperDocId, "Bob", spawnerDocId);

            Assert.Equal(0, reg.Count);
        }

        [Fact]
        public void A_forgotten_pane_can_never_be_matched_again()
        {
            var reg = new SpawnedPaneRegistry();
            reg.Record(HelperDocId, "Bob", PmDocId);

            Assert.True(reg.Forget(HelperDocId));
            Assert.False(reg.Forget(HelperDocId));
            Assert.Empty(reg.FindForSpawner(PmDocId, "Bob"));
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

        [Fact]
        public async Task Two_helpers_matching_one_name_close_neither()
        {
            using var h = new Harness();
            h.Service.Panes.Record("doc-1", "Bob", PmDocId);
            h.Service.Panes.Record("doc-2", "BOB", PmDocId);

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
