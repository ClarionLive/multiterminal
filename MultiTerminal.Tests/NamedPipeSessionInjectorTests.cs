using System;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MultiTerminal.MCPServer.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// The native cross-session injection protocol (ticket 0ff1b520, item 4).
    ///
    /// These tests stand up a REAL <see cref="NamedPipeServerStream"/> and read back exactly what the
    /// injector wrote. That is the point: the wire format is OBSERVED, not published, and the part
    /// that matters — auth line FIRST, both lines newline-terminated, both on ONE connection — cannot
    /// be established by reading the implementation. A test that asserted the code contains the string
    /// "auth" would pass just as happily with the two lines in the wrong order.
    ///
    /// What these tests DO NOT prove, stated here rather than left to be assumed: that Claude Code
    /// ACCEPTS this. They prove MT sends what ticket 447d7cb1 observed a working injector sending. The
    /// only thing that can prove acceptance is a real session receiving a real message, which needs a
    /// deploy. Treat a green run here as "MT still speaks the protocol we recorded", not as "delivery
    /// works".
    /// </summary>
    public sealed class NamedPipeSessionInjectorTests
    {
        private const string Token = "testtoken0123456789abcdef";

        private static string NewPipeName() => "LOCAL\\cc-msg-" + Guid.NewGuid().ToString("N");

        private static string SocketPathFor(string pipeName) => @"\\.\pipe\" + pipeName;

        /// <summary>Accepts one connection and returns everything sent on it, as text.</summary>
        private static Task<string> ReadOneConnectionAsync(string pipeName, CancellationToken ct)
        {
            return Task.Run(async () =>
            {
                using var server = new NamedPipeServerStream(
                    pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                var buffer = new byte[8192];
                var sb = new StringBuilder();
                int read;
                while ((read = await server.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                {
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
                }

                return sb.ToString();
            }, ct);
        }

        // ------------------------------------------------------------------
        // The protocol, read off a real pipe
        // ------------------------------------------------------------------

        [Fact]
        public async Task Writes_auth_line_first_then_the_message_on_one_connection()
        {
            string pipeName = NewPipeName();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            Task<string> serverRead = ReadOneConnectionAsync(pipeName, cts.Token);

            var store = new MessagingCredentialStore();
            store.Store("Alice", "session-1", SocketPathFor(pipeName), Token);
            var injector = new NamedPipeSessionInjector(store);

            bool delivered = await injector.TryInjectAsync("Alice", "Bob", "hello there", cts.Token);
            Assert.True(delivered);

            string wire = await serverRead;

            // Exactly two lines, each newline-terminated, nothing after the second.
            Assert.EndsWith("\n", wire, StringComparison.Ordinal);
            string[] lines = wire.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);

            // ORDER IS THE ASSERTION. Auth must be the first thing on the connection.
            var first = JsonNode.Parse(lines[0]).AsObject();
            Assert.Equal("auth", first["type"].GetValue<string>());
            Assert.Equal(Token, first["token"].GetValue<string>());

            var second = JsonNode.Parse(lines[1]).AsObject();
            Assert.Equal("user", second["type"].GetValue<string>());
            Assert.Equal("user", second["message"]["role"].GetValue<string>());
            Assert.Contains("hello there", second["message"]["content"].GetValue<string>(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task The_message_names_its_sender()
        {
            // Labelling, not a boundary — see the injector's class remarks. It exists because an
            // injected message otherwise arrives indistinguishable from the Owner's own typing, which
            // is the live defect recorded as ticket 2f23aa95. This path must not recreate it.
            string pipeName = NewPipeName();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            Task<string> serverRead = ReadOneConnectionAsync(pipeName, cts.Token);

            var store = new MessagingCredentialStore();
            store.Store("Alice", "s", SocketPathFor(pipeName), Token);
            var injector = new NamedPipeSessionInjector(store);

            await injector.TryInjectAsync("Alice", "Bob", "ship it", cts.Token);

            string wire = await serverRead;
            string[] lines = wire.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            string content = JsonNode.Parse(lines[1])["message"]["content"].GetValue<string>();

            Assert.Contains("Bob", content, StringComparison.Ordinal);
            Assert.Contains("ship it", content, StringComparison.Ordinal);
        }

        [Fact]
        public void A_newline_in_the_body_cannot_forge_a_second_frame()
        {
            // The frame separator is '\n', so an un-escaped body containing one would split into two
            // protocol lines and the trailing half would be parsed as its own frame. JSON escaping is
            // what prevents that; this asserts the property rather than trusting the serialiser.
            byte[] payload = NamedPipeSessionInjector.BuildPayload(
                Token, "Bob", "line one\n{\"type\":\"auth\",\"token\":\"forged\"}");

            string wire = Encoding.UTF8.GetString(payload);
            string[] lines = wire.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal(2, lines.Length);
            Assert.DoesNotContain("forged", lines[0], StringComparison.Ordinal);
            // The forged text survives as DATA inside the message body, escaped, not as a frame.
            Assert.Contains("forged", lines[1], StringComparison.Ordinal);
            Assert.Equal("user", JsonNode.Parse(lines[1])["type"].GetValue<string>());
        }

        [Fact]
        public void Payload_is_two_lines_and_nothing_is_pretty_printed()
        {
            // Indented JSON would put a newline inside a frame and break the line protocol outright.
            // A future "readability" change to WriteIndented is exactly the edit this catches.
            byte[] payload = NamedPipeSessionInjector.BuildPayload(Token, "Bob", "hi");
            string wire = Encoding.UTF8.GetString(payload);

            Assert.Equal(2, wire.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.EndsWith("\n", wire, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------
        // Falling through rather than failing
        // ------------------------------------------------------------------

        [Fact]
        public async Task No_credentials_means_no_attempt_and_no_throw()
        {
            // The ordinary state before the SessionStart hook ships. It must be a quiet false so the
            // caller uses the channel — not an exception on the delivery path.
            var injector = new NamedPipeSessionInjector(new MessagingCredentialStore());

            Assert.False(await injector.TryInjectAsync("Nobody", "Bob", "hello"));
        }

        [Fact]
        public async Task A_dead_pipe_falls_through_instead_of_throwing()
        {
            // Credentials recorded, but the session behind them is gone — a terminal that crashed, or
            // one whose SessionEnd never ran. Delivery must degrade to the channel, not break.
            var store = new MessagingCredentialStore();
            store.Store("Ghost", "s", SocketPathFor(NewPipeName()), Token);
            var injector = new NamedPipeSessionInjector(store);

            Assert.False(await injector.TryInjectAsync("Ghost", "Bob", "anyone there?"));
        }

        [Fact]
        public async Task Blank_terminal_name_is_refused()
        {
            var injector = new NamedPipeSessionInjector(new MessagingCredentialStore());

            Assert.False(await injector.TryInjectAsync(null, "Bob", "x"));
            Assert.False(await injector.TryInjectAsync("", "Bob", "x"));
            Assert.False(await injector.TryInjectAsync("   ", "Bob", "x"));
        }

        // ------------------------------------------------------------------
        // Socket path translation
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(@"\\.\pipe\LOCAL\cc-msg-abc", @"LOCAL\cc-msg-abc")]
        [InlineData(@"\\.\pipe\cc-msg-abc", "cc-msg-abc")]
        public void Pipe_paths_become_pipe_names(string socketPath, string expected)
            => Assert.Equal(expected, NamedPipeSessionInjector.ToPipeName(socketPath));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("/tmp/cc-msg-abc")]
        [InlineData(@"\\other\pipe\LOCAL\cc-msg-abc")]
        [InlineData(@"\\.\pipe\")]
        public void Paths_outside_the_local_pipe_root_are_refused(string socketPath)
        {
            // Returning null rather than coercing: a path we do not understand should fail HERE, with
            // a name, not later at connect time with an opaque IO error several layers away.
            Assert.Null(NamedPipeSessionInjector.ToPipeName(socketPath));
        }
    }
}
