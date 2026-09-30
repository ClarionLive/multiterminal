using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.API.Controllers;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using MultiTerminal.Services.Startup;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Ticket 9a731cda items 2, 3 and 4: the credentials endpoint checks the poster's origin against
    /// the connected row holding the name; the disconnect endpoint no longer clears credentials on its
    /// own; and <c>GET /api/health</c> advertises <c>ca-embedded-v1</c> only while the behaviours
    /// that token promises are in force.
    ///
    /// Real <see cref="MessageBroker"/> over temp databases, driven through the real
    /// <see cref="MessagingController"/> and <see cref="HealthController"/> actions. Ownership uses
    /// this test process's own pid, which the broker binds together with its real start time, so
    /// pid-held rows are genuinely Alive rather than simulated.
    /// </summary>
    public sealed class CredentialPostProofTests : IDisposable
    {
        private const string SocketA = @"\\.\pipe\LOCAL\cc-msg-aaaaaaaaaaaaaaaa";
        private const string SocketB = @"\\.\pipe\LOCAL\cc-msg-bbbbbbbbbbbbbbbb";
        private const string Token = "token-0123456789abcdef";

        private static int OwnPid => Environment.ProcessId;

        /// <summary>A pid that is not the owner's. Only equality is compared, so it need not be alive.</summary>
        private static int StrangerPid => Environment.ProcessId + 4;

        private readonly string _dbPath;
        private readonly string _msgDbPath;
        private readonly string _reaperEnvBefore;

        public CredentialPostProofTests()
        {
            var stamp = Guid.NewGuid().ToString("N");
            _dbPath = Path.Combine(Path.GetTempPath(), $"mt_credproof_{stamp}.db");
            _msgDbPath = Path.Combine(Path.GetTempPath(), $"mt_credproof_msg_{stamp}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _dbPath);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", _msgDbPath);

            _reaperEnvBefore = Environment.GetEnvironmentVariable("MULTITERMINAL_TERMINAL_REAPER");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TERMINAL_REAPER", null);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("MULTITERMINAL_TERMINAL_REAPER", _reaperEnvBefore);
            SQLiteConnection.ClearAllPools();
            foreach (var basePath in new[] { _dbPath, _msgDbPath })
            {
                foreach (var f in new[] { basePath, basePath + "-wal", basePath + "-shm" })
                {
                    if (File.Exists(f)) File.Delete(f);
                }
            }
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", null);
            GC.SuppressFinalize(this);
        }

        // ─── helpers ────────────────────────────────────────────────────────────────────────────

        private static int StatusOf(IActionResult result) => result switch
        {
            ObjectResult o => o.StatusCode ?? 200,
            StatusCodeResult s => s.StatusCode,
            _ => throw new InvalidOperationException($"Unexpected result {result?.GetType().Name}"),
        };

        private static IActionResult Post(MessagingController controller, string name, string socket = SocketA, int? ownerPid = null, string nonce = null)
            => controller.StoreMessagingCredentials(new StoreMessagingCredentialsRequest
            {
                Name = name,
                SessionId = "session",
                Socket = socket,
                Token = Token,
                OwnerPid = ownerPid,
                Nonce = nonce,
            });

        /// <summary>A CA-shaped registration: name + owner pid, no docId, no nonce.</summary>
        private static TerminalInfo RegisterPidHeld(MessageBroker broker, string name)
        {
            broker.RegisterTerminal(name, docId: null, ownerPid: OwnPid);
            var row = Assert.Single(broker.GetAllConnectedTerminals(), t => t.Name == name);
            // PRECONDITION: pid-held exactly as the broker binds it, or these facts test another shape.
            Assert.Equal(OwnPid, row.OwnerPid);
            Assert.NotNull(row.OwnerStartTime);
            Assert.True(string.IsNullOrEmpty(row.LaunchNonce));
            return row;
        }

        private static TerminalInfo RegisterNonceHeld(MessageBroker broker, string name, string nonce)
        {
            broker.RegisterTerminal(name, docId: "doc-" + name, nonce: nonce);
            var row = Assert.Single(broker.GetAllConnectedTerminals(), t => t.Name == name);
            Assert.Equal(nonce, row.LaunchNonce);   // PRECONDITION: nonce-held
            return row;
        }

        private static TerminalInfo RegisterUnowned(MessageBroker broker, string name, string docId = null)
        {
            broker.RegisterTerminal(name, docId: docId);
            var row = Assert.Single(broker.GetAllConnectedTerminals(), t => t.Name == name && t.DocId == docId);
            Assert.Null(row.OwnerPid);                        // PRECONDITION: unowned
            Assert.True(string.IsNullOrEmpty(row.LaunchNonce));
            return row;
        }

        // ─── Item 3: who may post credentials ───────────────────────────────────────────────────

        /// <summary>
        /// Falsified: with the "no live holder" return removed from the broker's authorization, this
        /// went red.
        /// </summary>
        [Fact]
        public void A_name_no_terminal_holds_is_refused_and_nothing_is_stored()
        {
            using var broker = new MessageBroker();
            var controller = new MessagingController(broker, broker.MessagingCredentials);

            Assert.Equal(409, StatusOf(Post(controller, "Nobody")));
            Assert.Equal(0, broker.MessagingCredentials.Count);
        }

        /// <summary>
        /// A row that exists but is disconnected holds nothing. Falsified alongside the fact above,
        /// by the same mutation.
        /// </summary>
        [Fact]
        public void A_name_held_only_by_a_disconnected_row_is_refused()
        {
            using var broker = new MessageBroker();
            var controller = new MessagingController(broker, broker.MessagingCredentials);
            var row = RegisterUnowned(broker, "Gone");
            broker.DisconnectTerminalByName("Gone");
            Assert.False(row.IsConnected);   // precondition: the row still exists, disconnected

            Assert.Equal(409, StatusOf(Post(controller, "Gone")));
            Assert.False(broker.MessagingCredentials.Has("Gone"));
        }

        [Fact]
        public void The_owning_process_may_post_for_its_pid_held_name()
        {
            using var broker = new MessageBroker();
            var controller = new MessagingController(broker, broker.MessagingCredentials);
            RegisterPidHeld(broker, "Cara");

            Assert.Equal(200, StatusOf(Post(controller, "Cara", ownerPid: OwnPid)));
            Assert.True(broker.MessagingCredentials.TryGet("Cara", out var stored));
            Assert.Equal(SocketA, stored.Socket);
        }

        /// <summary>
        /// Falsified: with the pid comparison removed (any presented pid admitted), this went red.
        /// </summary>
        [Fact]
        public void A_different_pid_is_refused_and_the_stored_credential_is_untouched()
        {
            using var broker = new MessageBroker();
            var controller = new MessagingController(broker, broker.MessagingCredentials);
            RegisterPidHeld(broker, "Cara");
            Assert.Equal(200, StatusOf(Post(controller, "Cara", ownerPid: OwnPid)));
            Assert.True(broker.MessagingCredentials.TryGet("Cara", out var before));

            Assert.Equal(409, StatusOf(Post(controller, "Cara", socket: SocketB, ownerPid: StrangerPid)));

            Assert.True(broker.MessagingCredentials.TryGet("Cara", out var after));
            Assert.Same(before, after);
        }

        /// <summary>
        /// Falsified: with the "owner pid must be present" half removed (a missing pid admitted),
        /// this went red.
        /// </summary>
        [Fact]
        public void A_pid_held_name_refuses_a_post_that_omits_the_pid()
        {
            using var broker = new MessageBroker();
            var controller = new MessagingController(broker, broker.MessagingCredentials);
            RegisterPidHeld(broker, "Cara");

            Assert.Equal(409, StatusOf(Post(controller, "Cara")));
            Assert.False(broker.MessagingCredentials.Has("Cara"));
        }

        /// <summary>
        /// The discriminator. A name-only check admits every poster, so the LAST post wins and the
        /// stored socket would be the stranger's. Only a check that compares the owner pid leaves the
        /// owner's socket in place. The stranger posts second so that last-writer-wins cannot pass
        /// this by accident. Falsified by the pid-comparison mutation above: went red.
        /// </summary>
        [Fact]
        public void Of_two_posters_for_one_pid_held_name_only_the_owner_changes_the_stored_socket()
        {
            using var broker = new MessageBroker();
            var controller = new MessagingController(broker, broker.MessagingCredentials);
            RegisterPidHeld(broker, "Cara");

            Post(controller, "Cara", socket: SocketA, ownerPid: OwnPid);
            Post(controller, "Cara", socket: SocketB, ownerPid: StrangerPid);

            Assert.True(broker.MessagingCredentials.TryGet("Cara", out var stored));
            Assert.Equal(SocketA, stored.Socket);
        }

        [Fact]
        public void A_nonce_held_name_accepts_its_own_nonce()
        {
            using var broker = new MessageBroker();
            var controller = new MessagingController(broker, broker.MessagingCredentials);
            RegisterNonceHeld(broker, "Nora", "nonce-nora");

            Assert.Equal(200, StatusOf(Post(controller, "Nora", nonce: "nonce-nora")));
            Assert.True(broker.MessagingCredentials.Has("Nora"));
        }

        /// <summary>
        /// Falsified: with the nonce comparison removed, this went red (200).
        /// </summary>
        [Fact]
        public void A_nonce_held_name_refuses_a_wrong_nonce()
        {
            using var broker = new MessageBroker();
            var controller = new MessagingController(broker, broker.MessagingCredentials);
            RegisterNonceHeld(broker, "Nora", "nonce-nora");

            Assert.Equal(409, StatusOf(Post(controller, "Nora", nonce: "nonce-other")));
            Assert.False(broker.MessagingCredentials.Has("Nora"));
        }

        /// <summary>
        /// Compatibility, not security: the deployed SessionStart hook sends no nonce, and refusing it
        /// would cut native delivery for every MT pane. Ticket c032a177 section D closes this. Not
        /// falsified separately.
        /// </summary>
        [Fact]
        public void A_nonce_held_name_still_accepts_a_post_with_no_nonce()
        {
            using var broker = new MessageBroker();
            var controller = new MessagingController(broker, broker.MessagingCredentials);
            RegisterNonceHeld(broker, "Nora", "nonce-nora");

            Assert.Equal(200, StatusOf(Post(controller, "Nora")));
            Assert.True(broker.MessagingCredentials.Has("Nora"));
        }

        [Fact]
        public void An_unowned_row_without_a_nonce_accepts_as_before()
        {
            using var broker = new MessageBroker();
            var controller = new MessagingController(broker, broker.MessagingCredentials);
            RegisterUnowned(broker, "Uma");

            Assert.Equal(200, StatusOf(Post(controller, "Uma")));
            Assert.True(broker.MessagingCredentials.Has("Uma"));
        }

        // ─── Run 1: panes MT launches register nonce-held, so self-registration cannot pid-lock them ──

        /// <summary>
        /// What the MCP server's startup self-registration sends for a pane with MULTITERMINAL_DOC_ID
        /// (<c>buildRegisterPayload</c>): the launch name and docId, the env's launch nonce when one was
        /// injected, and ownerPid = the claude.exe that spawned it (this test process stands in).
        /// </summary>
        private static void McpSelfRegister(MessageBroker broker, string name, string docId, string envNonce, int ownerPid)
        {
            var result = broker.RegisterTerminal(name, docId, nonce: envNonce, ownerPid: ownerPid);
            Assert.True(result.Success, $"precondition: the self-registration succeeds ({result.Error})");
            var row = Assert.Single(broker.GetAllConnectedTerminals(), t => t.Name == name);
            Assert.Equal(ownerPid, row.OwnerPid);   // PRECONDITION: the pid really was bound onto the row
        }

        /// <summary>
        /// Oracle, through the registration MainForm now makes (<see cref="OracleService.RegisterWithBroker"/>).
        /// After her MCP server binds claude.exe's pid, the deployed plugin's post (no ownerPid, no
        /// nonce) is still accepted, because the row is nonce-held.
        /// </summary>
        [Fact]
        public void Oracle_registers_nonce_held_and_accepts_the_deployed_plugin_post_after_self_registration()
        {
            using var broker = new MessageBroker();
            using var oracle = new OracleService();
            var controller = new MessagingController(broker, broker.MessagingCredentials);

            Assert.True(oracle.RegisterWithBroker(broker).Success);
            var row = Assert.Single(broker.GetAllConnectedTerminals(), t => t.Name == OracleService.OracleName);
            Assert.Equal(oracle.DocId, row.DocId);
            Assert.False(string.IsNullOrEmpty(row.LaunchNonce), "Oracle's row must carry a launch nonce");
            Assert.Equal(oracle.LaunchNonce, row.LaunchNonce);

            // OracleService passes the same field to StartTerminal, so the child's env nonce, and
            // therefore the self-registration's echo, is oracle.LaunchNonce.
            McpSelfRegister(broker, OracleService.OracleName, oracle.DocId, oracle.LaunchNonce, OwnPid);

            Assert.Equal(200, StatusOf(Post(controller, OracleService.OracleName)));
            Assert.True(broker.MessagingCredentials.Has(OracleService.OracleName));
        }

        /// <summary>
        /// The discriminator for the fact above: Oracle's registration as it was before run 1 (no
        /// nonce, so no nonce in her env either). The self-registration makes the row pid-held and the
        /// deployed plugin's post is refused. This is the failure the fix removes, so the fact above
        /// cannot be passing for a reason the fix did not supply.
        /// </summary>
        [Fact]
        public void Without_the_nonce_Oracle_is_pid_locked_and_the_deployed_plugin_post_is_refused()
        {
            using var broker = new MessageBroker();
            using var oracle = new OracleService();
            var controller = new MessagingController(broker, broker.MessagingCredentials);

            broker.RegisterTerminal(OracleService.OracleName, oracle.DocId);   // the pre-run-1 call
            McpSelfRegister(broker, OracleService.OracleName, oracle.DocId, envNonce: null, OwnPid);

            Assert.Equal(409, StatusOf(Post(controller, OracleService.OracleName)));
            Assert.False(broker.MessagingCredentials.Has(OracleService.OracleName));
        }

        /// <summary>
        /// Crash restart. The first claude.exe self-registers and dies; the restarted one presents the
        /// SAME nonce (OracleService keeps it for its lifetime), rebinds the row, and the deployed
        /// plugin's post is accepted. A rotated nonce is refused while the old row still holds the
        /// name, which is why the nonce is not regenerated on restart.
        /// </summary>
        [Fact]
        public void An_Oracle_crash_restart_with_the_same_nonce_rebinds_and_a_rotated_nonce_would_not()
        {
            using var broker = new MessageBroker();
            using var oracle = new OracleService();
            var controller = new MessagingController(broker, broker.MessagingCredentials);
            Assert.True(oracle.RegisterWithBroker(broker).Success);

            RegisterWithDeadOwner(broker, OracleService.OracleName, oracle.DocId, oracle.LaunchNonce);

            var rotated = broker.RegisterTerminal(OracleService.OracleName, oracle.DocId, nonce: Guid.NewGuid().ToString("N"), ownerPid: OwnPid);
            Assert.False(rotated.Success, "a restart presenting a new nonce must be refused, or keeping the nonce is not load-bearing");

            McpSelfRegister(broker, OracleService.OracleName, oracle.DocId, oracle.LaunchNonce, OwnPid);
            Assert.Equal(200, StatusOf(Post(controller, OracleService.OracleName)));
        }

        /// <summary>
        /// "Launch as…", at the broker level: MainForm.OnLaunchAsIdentityRequested is a private UI
        /// handler with no seam, so this replays the registrations it makes: unregister the pane's
        /// docId, pre-register the new identity with <c>doc.LaunchNonce</c> (run 1), then the child's
        /// self-registration, which echoes that nonce because TerminalDocument always injects it.
        /// The MainForm call site itself is not executed by any test.
        /// </summary>
        [Fact]
        public void Launch_as_with_the_doc_nonce_accepts_the_deployed_plugin_post_after_self_registration()
        {
            using var broker = new MessageBroker();
            var controller = new MessagingController(broker, broker.MessagingCredentials);
            const string docId = "D7";
            const string nonce = "nonce-d7";
            broker.RegisterTerminal("Wes", docId, nonce: nonce);
            broker.UnregisterTerminal(docId);

            Assert.True(broker.RegisterTerminal("Ivy", docId, nonce: nonce).Success);
            McpSelfRegister(broker, "Ivy", docId, nonce, OwnPid);

            Assert.Equal(200, StatusOf(Post(controller, "Ivy")));
        }

        /// <summary>
        /// The pre-run-1 Launch-as registration (no nonce). The child still echoes the doc's nonce, but
        /// the broker refuses to seed a nonce from a caller that proved nothing about the row, so the
        /// row ends up pid-held and the deployed plugin's post is refused.
        /// </summary>
        [Fact]
        public void Launch_as_without_the_doc_nonce_is_pid_locked_and_the_deployed_plugin_post_is_refused()
        {
            using var broker = new MessageBroker();
            var controller = new MessagingController(broker, broker.MessagingCredentials);
            const string docId = "D7";
            const string nonce = "nonce-d7";
            broker.RegisterTerminal("Wes", docId, nonce: nonce);
            broker.UnregisterTerminal(docId);

            Assert.True(broker.RegisterTerminal("Ivy", docId).Success);
            McpSelfRegister(broker, "Ivy", docId, nonce, OwnPid);

            Assert.Equal(409, StatusOf(Post(controller, "Ivy")));
        }

        // ─── The two switches really gate their behaviours ──────────────────────────────────────

        /// <summary>
        /// Registers <paramref name="name"/> owned by a real child process, then kills it, so the row's
        /// owner is genuinely Dead (the construction TerminalLivenessReaperTests uses).
        /// </summary>
        private static void RegisterWithDeadOwner(MessageBroker broker, string name, string docId = null, string nonce = null)
        {
            var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c pause",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });
            Assert.NotNull(child);

            try
            {
                Assert.True(MessageBroker.TryGetProcessStartTime(child.Id, out _));
                Assert.True(broker.RegisterTerminal(name, docId, nonce: nonce, ownerPid: child.Id).Success);
                var row = Assert.Single(broker.GetAllConnectedTerminals(), t => t.Name == name);
                Assert.Equal(child.Id, row.OwnerPid);   // PRECONDITION: owned by the child

                child.Kill();
                Assert.True(child.WaitForExit(10000), "The owner did not die, so this proves nothing.");
            }
            finally
            {
                try { if (!child.HasExited) child.Kill(); } catch { }
                child.Dispose();
            }
        }

        /// <summary>
        /// With <see cref="MessageBroker.CredentialPostProofEnforced"/> off, a mismatched owner pid is
        /// admitted; the same post is refused with it on (A_different_pid_is_refused_and_the_stored_credential_is_untouched).
        /// </summary>
        [Fact]
        public void Proof_switched_off_admits_a_mismatched_owner_pid()
        {
            using var broker = new MessageBroker();
            var controller = new MessagingController(broker, broker.MessagingCredentials);
            RegisterPidHeld(broker, "Cara");
            broker.CredentialPostProofEnforced = false;

            Assert.Equal(200, StatusOf(Post(controller, "Cara", ownerPid: StrangerPid)));
        }

        /// <summary>
        /// With <see cref="MessageBroker.ReapClearsMessagingCredentials"/> off, a reap leaves the
        /// credentials; with it on they are cleared
        /// (MessagingCredentialTeardownTests.A_reaped_terminal_loses_its_credentials_and_nobody_else_does).
        /// </summary>
        [Fact]
        public void Reap_clearing_switched_off_leaves_the_credentials()
        {
            using var broker = new MessageBroker();
            RegisterWithDeadOwner(broker, "Diana");
            broker.MessagingCredentials.Store("Diana", "s", SocketA, Token);
            broker.ReapClearsMessagingCredentials = false;

            Assert.Equal(new[] { "Diana" }, broker.ReapDeadOwnerTerminals());
            Assert.True(broker.MessagingCredentials.Has("Diana"));
        }

        // ─── Item 4: disconnect clears only through the guarded broker teardown ─────────────────

        [Fact]
        public void Disconnecting_the_only_row_clears_its_credentials()
        {
            using var broker = new MessageBroker();
            var controller = new MessagingController(broker, broker.MessagingCredentials);
            RegisterUnowned(broker, "Alice");
            broker.MessagingCredentials.Store("Alice", "s", SocketA, Token);

            Assert.Equal(200, StatusOf(controller.DisconnectTerminal(new DisconnectTerminalRequest { Name = "Alice" })));

            Assert.False(broker.MessagingCredentials.Has("Alice"));
        }

        /// <summary>
        /// Two connected rows can carry one name through the pane-rename path (an Unassigned pane
        /// renamed to a name an unowned row already holds; the same construction as
        /// TerminalLivenessReaperTests' two-row facts, with both rows live). A disconnect by name
        /// tears down the first; the other still holds the name, so the credential is its session's.
        /// Falsified: with the controller's unconditional <c>_credentials.Clear(request.Name)</c>
        /// restored, this went red.
        /// </summary>
        [Fact]
        public void Disconnecting_one_of_two_live_same_name_rows_keeps_the_credentials()
        {
            using var broker = new MessageBroker();
            var controller = new MessagingController(broker, broker.MessagingCredentials);
            RegisterUnowned(broker, "Diana", docId: "D1");
            broker.RegisterTerminal("Unassigned", docId: "D2", nonce: "N2");
            broker.RegisterTerminal("Diana", docId: "D2", nonce: "N2");
            Assert.True(broker.GetAllConnectedTerminals().Count(t => t.Name == "Diana") == 2,
                "precondition: two connected 'Diana' rows, or this fact exercises nothing");
            broker.MessagingCredentials.Store("Diana", "s", SocketA, Token);

            controller.DisconnectTerminal(new DisconnectTerminalRequest { Name = "Diana" });

            Assert.Single(broker.GetAllConnectedTerminals(), t => t.Name == "Diana");   // one row went
            Assert.True(broker.MessagingCredentials.Has("Diana"),
                "a live same-name session lost its ingress to another row's disconnect");
        }

        /// <summary>
        /// The endpoint path inherits the broker's compare-and-clear: a same-name session storing its
        /// credential inside the teardown (between the decision and the clear, reached
        /// deterministically from a <see cref="MessageBroker.TerminalDisconnected"/> subscriber) keeps
        /// it. Falsified by replacing <c>ClearIfCurrent</c> with a plain <c>Clear</c> in
        /// <c>ClearTornDownMessagingCredential</c>: went red. Restoring the controller's unconditional
        /// clear does NOT turn this red, because that clear ran before the teardown; it is the fact
        /// above that pins item 4.
        /// </summary>
        [Fact]
        public void A_credential_stored_during_the_disconnect_teardown_survives_it()
        {
            using var broker = new MessageBroker();
            var controller = new MessagingController(broker, broker.MessagingCredentials);
            RegisterUnowned(broker, "Eve");
            broker.MessagingCredentials.Store("Eve", "old", SocketA, Token);
            broker.TerminalDisconnected += (_, t) => broker.MessagingCredentials.Store(t.Name, "new", SocketB, Token);

            controller.DisconnectTerminal(new DisconnectTerminalRequest { Name = "Eve" });

            Assert.True(broker.MessagingCredentials.TryGet("Eve", out var survivor));
            Assert.Equal("new", survivor.SessionId);
        }

        // ─── Item 2: GET /api/health capabilities ───────────────────────────────────────────────

        private static IReadOnlyList<string> CapabilitiesOf(MessageBroker broker)
        {
            var controller = new HealthController(new ICapabilityProvider[] { new CaEmbeddedCapabilityProvider(broker) });
            var ok = Assert.IsType<OkObjectResult>(controller.Health());
            var identity = Assert.IsType<HealthIdentity>(ok.Value);
            Assert.Equal(HealthIdentity.ServiceMarker, identity.Service);
            return identity.Capabilities;
        }

        private static TerminalLivenessReaper StartReaper(MessageBroker broker)
        {
            // A no-op sweep: these facts concern whether the reaper runs, not what it reaps.
            var reaper = new TerminalLivenessReaper(() => Array.Empty<string>());
            reaper.Start();
            broker.LivenessReaper = reaper;
            return reaper;
        }

        /// <summary>
        /// Falsified by hard-coding the provider to always return the token: the reaper-absent facts
        /// below went red, this one stayed green (as it must).
        /// </summary>
        [Fact]
        public void Default_runtime_advertises_ca_embedded_v1()
        {
            using var broker = new MessageBroker();
            using var reaper = StartReaper(broker);
            Assert.True(reaper.IsRunning);   // precondition

            Assert.Contains(CaEmbeddedCapabilityProvider.Token, CapabilitiesOf(broker));
            Assert.Equal("ca-embedded-v1", CaEmbeddedCapabilityProvider.Token);
        }

        /// <summary>Falsified by the hard-coded-token mutation: went red.</summary>
        [Fact]
        public void No_reaper_means_no_capability()
        {
            using var broker = new MessageBroker();
            Assert.DoesNotContain(CaEmbeddedCapabilityProvider.Token, CapabilitiesOf(broker));
        }

        /// <summary>Falsified by the hard-coded-token mutation: went red.</summary>
        [Fact]
        public void A_reaper_disabled_by_env_means_no_capability()
        {
            using var broker = new MessageBroker();
            Environment.SetEnvironmentVariable("MULTITERMINAL_TERMINAL_REAPER", "off");
            using var reaper = StartReaper(broker);
            Assert.False(reaper.IsRunning);   // precondition: Start was refused

            Assert.DoesNotContain(CaEmbeddedCapabilityProvider.Token, CapabilitiesOf(broker));
        }

        /// <summary>Falsified by the hard-coded-token mutation: went red.</summary>
        [Fact]
        public void A_disposed_reaper_means_no_capability()
        {
            using var broker = new MessageBroker();
            var reaper = StartReaper(broker);
            reaper.Dispose();

            Assert.DoesNotContain(CaEmbeddedCapabilityProvider.Token, CapabilitiesOf(broker));
        }

        /// <summary>
        /// The capability reads the same flags the behaviours branch on, so turning either behaviour
        /// off withdraws it. Falsified by the hard-coded-token mutation: went red.
        /// </summary>
        [Fact]
        public void Either_behaviour_switched_off_withdraws_the_capability()
        {
            using var broker = new MessageBroker();
            using var reaper = StartReaper(broker);

            broker.ReapClearsMessagingCredentials = false;
            Assert.DoesNotContain(CaEmbeddedCapabilityProvider.Token, CapabilitiesOf(broker));

            broker.ReapClearsMessagingCredentials = true;
            broker.CredentialPostProofEnforced = false;
            Assert.DoesNotContain(CaEmbeddedCapabilityProvider.Token, CapabilitiesOf(broker));
        }

        [Fact]
        public void The_startup_probe_still_recognises_a_health_body_carrying_capabilities()
        {
            var identity = HealthIdentity.Current(5050);
            identity.Capabilities = new[] { CaEmbeddedCapabilityProvider.Token };
            string body = System.Text.Json.JsonSerializer.Serialize(identity,
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
            Assert.Contains("\"capabilities\":[\"ca-embedded-v1\"]", body, StringComparison.Ordinal);

            Assert.True(StartupHealthProbe.Parse(body).IsMultiTerminal);
        }
    }
}
