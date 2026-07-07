using System;
using System.IO;
using System.Net.Sockets;
using MultiTerminal.Services.Startup;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Unit coverage for the startup port-contention decision logic (task 4fec40e2).
    /// This pure surface carries the load because the end-to-end flow (a second instance
    /// colliding on :5050) can only be exercised by the Owner — an agent inside
    /// MultiTerminal cannot launch a second process to trigger it. So the classification,
    /// exception-shape detection, and user-message wording are all asserted here.
    /// </summary>
    public class StartupPortContentionClassifierTests
    {
        // ---- IsAddressInUse: recognizing a "port already bound" failure in any shape ----

        [Fact]
        public void IsAddressInUse_true_for_direct_socket_addr_in_use()
        {
            var ex = new SocketException((int)SocketError.AddressAlreadyInUse);
            Assert.True(StartupPortContentionClassifier.IsAddressInUse(ex));
        }

        [Fact]
        public void IsAddressInUse_true_for_kestrel_shaped_io_wrapping_socket()
        {
            // Kestrel throws IOException("Failed to bind to address ...") wrapping a SocketException.
            var inner = new SocketException((int)SocketError.AddressAlreadyInUse);
            var ex = new IOException("Failed to bind to address http://127.0.0.1:5050: address already in use.", inner);
            Assert.True(StartupPortContentionClassifier.IsAddressInUse(ex));
        }

        [Fact]
        public void IsAddressInUse_true_for_message_only_match_no_socket_type()
        {
            // A future runtime could reshape the exception; fall back to the message text.
            var ex = new InvalidOperationException("Only one usage of each socket address is normally permitted");
            Assert.True(StartupPortContentionClassifier.IsAddressInUse(ex));
        }

        [Fact]
        public void IsAddressInUse_true_when_nested_deeply()
        {
            var deep = new SocketException((int)SocketError.AddressAlreadyInUse);
            var ex = new AggregateException("outer", new InvalidOperationException("middle", deep));
            Assert.True(StartupPortContentionClassifier.IsAddressInUse(ex));
        }

        [Fact]
        public void IsAddressInUse_false_for_null()
        {
            Assert.False(StartupPortContentionClassifier.IsAddressInUse(null));
        }

        [Fact]
        public void IsAddressInUse_false_for_unrelated_exception()
        {
            Assert.False(StartupPortContentionClassifier.IsAddressInUse(new InvalidOperationException("db locked")));
        }

        [Fact]
        public void IsAddressInUse_false_for_other_socket_error()
        {
            var ex = new SocketException((int)SocketError.ConnectionRefused);
            Assert.False(StartupPortContentionClassifier.IsAddressInUse(ex));
        }

        // ---- Classify: probe result → verdict ----

        [Fact]
        public void Classify_multiterminal_when_probe_identifies_MT()
        {
            var probe = new HealthProbeResult { Reached = true, IsMultiTerminal = true };
            Assert.Equal(PortContentionVerdict.MultiTerminalAlreadyRunning, StartupPortContentionClassifier.Classify(probe));
        }

        [Fact]
        public void Classify_foreign_when_reached_but_not_MT()
        {
            var probe = new HealthProbeResult { Reached = true, IsMultiTerminal = false };
            Assert.Equal(PortContentionVerdict.ForeignHolder, StartupPortContentionClassifier.Classify(probe));
        }

        [Fact]
        public void Classify_foreign_when_probe_not_reached()
        {
            // Probe timeout / connection reset → unknown holder → foreign (fall through to PID id).
            Assert.Equal(PortContentionVerdict.ForeignHolder, StartupPortContentionClassifier.Classify(HealthProbeResult.NotReached()));
        }

        [Fact]
        public void Classify_foreign_when_probe_null()
        {
            Assert.Equal(PortContentionVerdict.ForeignHolder, StartupPortContentionClassifier.Classify(null));
        }

        // ---- BuildMessage: user-facing wording ----

        [Fact]
        public void BuildMessage_MT_with_identity_names_pid_and_port()
        {
            var probe = new HealthProbeResult
            {
                Reached = true,
                IsMultiTerminal = true,
                Identity = new HealthIdentity { Pid = 4321, User = "alice", SessionId = 3, Port = 5050 },
            };
            string msg = StartupPortContentionClassifier.BuildMessage(
                PortContentionVerdict.MultiTerminalAlreadyRunning, 5050, probe, PortHolderInfo.Unknown);

            Assert.Contains("already running", msg, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("4321", msg);
            Assert.Contains("alice", msg);
            Assert.Contains("5050", msg);
            Assert.Contains("Retry", msg);
        }

        [Fact]
        public void BuildMessage_MT_without_identity_does_not_crash()
        {
            string msg = StartupPortContentionClassifier.BuildMessage(
                PortContentionVerdict.MultiTerminalAlreadyRunning, 5050, HealthProbeResult.NotReached(), PortHolderInfo.Unknown);

            Assert.Contains("already running", msg, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("5050", msg);
        }

        [Fact]
        public void BuildMessage_foreign_with_holder_names_process_and_pid()
        {
            var holder = new PortHolderInfo { Pid = 9090, ProcessName = "node" };
            string msg = StartupPortContentionClassifier.BuildMessage(
                PortContentionVerdict.ForeignHolder, 5050, new HealthProbeResult { Reached = true, IsMultiTerminal = false }, holder);

            Assert.Contains("node", msg);
            Assert.Contains("9090", msg);
            Assert.Contains("5050", msg);
            Assert.Contains("does not", msg, StringComparison.OrdinalIgnoreCase); // "does not fall back to another port"
        }

        [Fact]
        public void BuildMessage_foreign_without_holder_says_unidentified()
        {
            string msg = StartupPortContentionClassifier.BuildMessage(
                PortContentionVerdict.ForeignHolder, 5050, HealthProbeResult.NotReached(), PortHolderInfo.Unknown);

            Assert.Contains("could not be identified", msg, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("5050", msg);
        }
    }
}
