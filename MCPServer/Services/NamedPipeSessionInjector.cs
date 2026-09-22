using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace MultiTerminal.MCPServer.Services
{
    /// <summary>
    /// Delivers a message straight into a live Claude Code session (ticket 0ff1b520, item 4).
    /// </summary>
    /// <remarks>
    /// This interface exists to be the ONLY door to an unpublished protocol. Everything the rest of
    /// MultiTerminal knows about native delivery is "call this and check the bool".
    /// </remarks>
    public interface ISessionMessageInjector
    {
        /// <summary>
        /// Attempts delivery to <paramref name="terminalName"/>. Returns false — never throws — when
        /// the terminal has no ingress recorded, the pipe is gone, or anything else fails, so the
        /// caller can fall through to another transport.
        /// </summary>
        Task<bool> TryInjectAsync(string terminalName, string fromName, string content, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// The one and only implementation of Claude Code's cross-session message ingress
    /// (ticket 0ff1b520, item 4).
    /// </summary>
    /// <remarks>
    /// <para><b>⚠️ THIS FILE IS A CONTAINMENT BOUNDARY, AND THAT IS ITS ENTIRE PURPOSE.</b> The wire
    /// format below was OBSERVED — by watching a plain node process inject into a live session
    /// (research ticket 447d7cb1) — and is NOT a published API. It can change in any Claude Code
    /// release with no deprecation and no changelog entry. Every fact about it therefore lives in
    /// THIS FILE and nowhere else, so that a CLI upgrade which breaks it is a one-file fix rather
    /// than a hunt through the broker, MainForm and four call sites. If you find yourself writing a
    /// second thing that knows what an "auth" line looks like, stop: it belongs here.</para>
    ///
    /// <para><b>The protocol, exactly as observed.</b> Two newline-terminated JSON lines written to
    /// <c>\\.\pipe\LOCAL\cc-msg-&lt;hex&gt;</c> on ONE connection, auth FIRST:</para>
    /// <code>
    /// {"type":"auth","token":"&lt;CLAUDE_CODE_MESSAGING_TOKEN&gt;"}
    /// {"type":"user","message":{"role":"user","content":"..."}}
    /// </code>
    /// <para>The ordering is load-bearing and is the part no amount of reading the code can verify,
    /// which is why <c>NamedPipeSessionInjectorTests</c> stands up a real pipe server and reads back
    /// what was actually written.</para>
    ///
    /// <para><b>Local only, structurally.</b> <c>\\.\pipe\LOCAL\</c> is the local namespace by
    /// definition, so this cannot reach another machine — not as a limitation to be lifted later, but
    /// as the reason ticket 0ff1b520 scoped itself local-only (item 0). Cross-machine traffic rides
    /// Multi-Connect, never this.</para>
    ///
    /// <para><b>Why the payload is built before connecting.</b> The receiving end closes a connection
    /// that has not sent a complete line within about 30 seconds. Serialising first means the socket
    /// is open only for the write, so a slow JSON encode can never hold a connection open empty.</para>
    ///
    /// <para><b>Attribution is a CONVENTION, not a control — say so plainly.</b> An injected message
    /// arrives in the session as ordinary user input, indistinguishable from the Owner typing. That
    /// is a real defect in MT's existing <c>/api/terminals/inject</c> path (ticket 2f23aa95, where an
    /// agent — me — answered MT's own "initializing..." as though the Owner had said it), and this
    /// path must not recreate it. So the content is wrapped to name its sender. That wrapper is
    /// honest labelling that a cooperating recipient can read; it is NOT a security boundary, because
    /// nothing stops a sender from writing the same characters inside their own message. The real
    /// property remains "who can reach the pipe", which is local processes holding the token. Weaker
    /// and true beats stronger and wrong.</para>
    /// </remarks>
    public sealed class NamedPipeSessionInjector : ISessionMessageInjector
    {
        /// <summary>Prefix of the pipe path the CLI publishes in CLAUDE_CODE_MESSAGING_SOCKET.</summary>
        private const string PipePathPrefix = @"\\.\pipe\";

        /// <summary>How long to wait for the pipe to accept a connection.</summary>
        private const int ConnectTimeoutMs = 3000;

        private readonly MessagingCredentialStore _credentials;
        private readonly Action<string> _log;

        public NamedPipeSessionInjector(MessagingCredentialStore credentials, Action<string> log = null)
        {
            _credentials = credentials;
            _log = log;
        }

        /// <summary>
        /// Whether native injection is enabled at all. Off switch, because this rides an unpublished
        /// protocol on the delivery path: <c>MULTITERMINAL_NATIVE_INJECT</c> set to
        /// 0/false/off/no/disabled turns it off and leaves every message on the channel path.
        /// </summary>
        public static bool IsEnabled()
        {
            string raw = Environment.GetEnvironmentVariable("MULTITERMINAL_NATIVE_INJECT");
            if (string.IsNullOrWhiteSpace(raw)) return true;

            switch (raw.Trim().ToLowerInvariant())
            {
                case "0":
                case "false":
                case "off":
                case "no":
                case "disabled":
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Turns the CLI's socket path into the name <see cref="NamedPipeClientStream"/> wants.
        /// <c>\\.\pipe\LOCAL\cc-msg-abc</c> becomes <c>LOCAL\cc-msg-abc</c>; anything not under the
        /// pipe root returns null rather than being coerced into something that would then fail at
        /// connect time with a far less obvious error.
        /// </summary>
        public static string ToPipeName(string socketPath)
        {
            if (string.IsNullOrWhiteSpace(socketPath)) return null;
            if (!socketPath.StartsWith(PipePathPrefix, StringComparison.OrdinalIgnoreCase)) return null;

            string name = socketPath.Substring(PipePathPrefix.Length);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        /// <summary>
        /// Builds the exact bytes sent on the wire: auth line, then message line, each terminated by
        /// a single '\n'. Public so the protocol can be asserted directly, and static because it must
        /// not depend on anything but its inputs.
        /// </summary>
        /// <remarks>
        /// JSON serialisation does the escaping. A token or body containing a newline could otherwise
        /// split one line into two and turn a message into a second protocol frame — the reason the
        /// hook refuses control characters in the token before it ever reaches here, and the reason
        /// this does not hand-roll string concatenation.
        /// </remarks>
        public static byte[] BuildPayload(string token, string fromName, string content)
        {
            var auth = new JsonObject
            {
                ["type"] = "auth",
                ["token"] = token,
            };

            var message = new JsonObject
            {
                ["type"] = "user",
                ["message"] = new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = WrapForAttribution(fromName, content),
                },
            };

            // AUTH FIRST. Demonstrated 2026-09-21 by swapping these two lines: exactly three facts in
            // NamedPipeSessionInjectorTests go red, led by
            // Writes_auth_line_first_then_the_message_on_one_connection.
            var sb = new StringBuilder();
            sb.Append(auth.ToJsonString(JsonOptions)).Append('\n');
            sb.Append(message.ToJsonString(JsonOptions)).Append('\n');
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            // Compact: each frame must be ONE line, so no indentation, ever.
            WriteIndented = false,
        };

        /// <summary>
        /// Names the sender inside the message body. See the class remarks: labelling, not a boundary.
        /// </summary>
        private static string WrapForAttribution(string fromName, string content)
        {
            if (string.IsNullOrWhiteSpace(fromName)) return content ?? string.Empty;
            return $"[MultiTerminal message from {fromName}]\n\n{content}";
        }

        /// <inheritdoc />
        public async Task<bool> TryInjectAsync(string terminalName, string fromName, string content, CancellationToken cancellationToken = default)
        {
            if (!IsEnabled()) return false;
            if (_credentials == null) return false;
            if (string.IsNullOrWhiteSpace(terminalName)) return false;

            if (!_credentials.TryGet(terminalName, out var credential) || credential == null)
            {
                // The ordinary case before the SessionStart hook has run, or on a build where it has
                // not been deployed. Not a failure — just nothing to inject into, so the caller falls
                // through to the channel. Deliberately not logged at warning level: it would fire for
                // every message on every terminal until the hook ships.
                return false;
            }

            string pipeName = ToPipeName(credential.Socket);
            if (pipeName == null)
            {
                _log?.Invoke($"native inject: unusable socket path for {terminalName} (len={credential.Socket?.Length ?? 0})");
                return false;
            }

            try
            {
                // Built BEFORE connecting — see the class remarks on the 30-second empty-connection rule.
                byte[] payload = BuildPayload(credential.Token, fromName, content);

                using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(ConnectTimeoutMs, cancellationToken).ConfigureAwait(false);

                await pipe.WriteAsync(payload, 0, payload.Length, cancellationToken).ConfigureAwait(false);
                await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);

                _log?.Invoke($"native inject: delivered to {terminalName} ({payload.Length} bytes)");
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (TimeoutException)
            {
                // The session is gone or wedged. Its credentials are stale; the caller falls back.
                _log?.Invoke($"native inject: timed out connecting to {terminalName}'s pipe");
                return false;
            }
            catch (IOException ex)
            {
                _log?.Invoke($"native inject: pipe IO failure for {terminalName}: {ex.Message}");
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                _log?.Invoke($"native inject: access denied on {terminalName}'s pipe: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                // Deliberately broad. This is an unpublished protocol on the delivery path: an
                // unexpected exception type must degrade to "use the channel", never take down
                // message delivery for the whole app.
                _log?.Invoke($"native inject: unexpected failure for {terminalName}: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }
}
