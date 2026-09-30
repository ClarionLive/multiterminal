using System;
using System.Collections.Concurrent;

namespace MultiTerminal.MCPServer.Services
{
    /// <summary>
    /// One terminal's Claude Code cross-session messaging ingress: the named pipe its session
    /// listens on, and the token that authorises writing to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The token is a CREDENTIAL, not data. Holding it lets the holder write a user-role message
    /// into a live Claude session, which is indistinguishable at the receiving end from the Owner
    /// typing. Treat it accordingly.
    /// </para>
    /// <para>
    /// <see cref="ToString"/> is overridden to redact, and that is deliberate: a credential that
    /// prints itself will eventually reach a log, an exception message or a debugger dump, because
    /// every one of those calls ToString without anyone deciding to. Making the TYPE unable to
    /// disclose is a structural control; asking callers to remember is not one.
    /// </para>
    /// </remarks>
    public sealed class MessagingCredential
    {
        internal MessagingCredential(string sessionId, string socket, string token, DateTime capturedUtc)
        {
            SessionId = sessionId;
            Socket = socket;
            Token = token;
            CapturedUtc = capturedUtc;
        }

        /// <summary>The Claude Code session id this ingress belongs to, as the hook reported it.</summary>
        public string SessionId { get; }

        /// <summary>The named pipe the session listens on (<c>\\.\pipe\LOCAL\cc-msg-...</c>).</summary>
        public string Socket { get; }

        /// <summary>The auth token. Never log this, never serialise it to a client.</summary>
        public string Token { get; }

        /// <summary>When MT received it.</summary>
        public DateTime CapturedUtc { get; }

        /// <summary>Redacted by construction. See the remarks on this class for why.</summary>
        public override string ToString()
            => $"MessagingCredential(session={SessionId}, socketLen={Socket?.Length ?? 0}, token=<redacted>)";
    }

    /// <summary>
    /// Outcome of <see cref="MessageBroker.TryStoreMessagingCredentials"/> (ticket 9a731cda item 3).
    /// </summary>
    public enum CredentialPostVerdict
    {
        /// <summary>The poster was admitted and the credential stored.</summary>
        Accepted,

        /// <summary>No connected, non-dead terminal holds the name. Nothing stored.</summary>
        NotConnected,

        /// <summary>A pid-held row, and the poster did not present its owner pid. Nothing stored.</summary>
        OwnerPidMismatch,

        /// <summary>A nonce-held row, and the poster presented a different nonce. Nothing stored.</summary>
        NonceMismatch,

        /// <summary>Admitted, but the store refused the values (blank name, socket or token).</summary>
        Invalid,
    }

    /// <summary>
    /// In-memory holder for terminals' native messaging ingress credentials (ticket 0ff1b520, item 3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// IN MEMORY ONLY, NEVER PERSISTED — an Owner decision on 2026-09-21, and the reasoning is worth
    /// keeping because the opposite looks more robust: MT HOSTS the terminals these credentials belong
    /// to, so an MT restart kills every one of those sessions. A persisted token is therefore stale the
    /// moment it would be read back. Writing it to disk would leave a live injection secret at rest in
    /// exchange for a value that can never be used. The absence of a database call here is the feature.
    /// </para>
    /// <para>
    /// Keyed by terminal name with <see cref="StringComparer.OrdinalIgnoreCase"/> and NO trimming,
    /// matching every other name-keyed collection in <c>MessageBroker</c>. That match is deliberate:
    /// a lookup more permissive than the one the broker uses to route would let this store answer for
    /// a terminal the broker resolves differently, and a comparison that disagrees with the one beside
    /// it is how two identities silently merge.
    /// </para>
    /// </remarks>
    public sealed class MessagingCredentialStore
    {
        private readonly ConcurrentDictionary<string, MessagingCredential> _credentials
            = new ConcurrentDictionary<string, MessagingCredential>(StringComparer.OrdinalIgnoreCase);

        /// <summary>How many terminals currently have ingress credentials held.</summary>
        public int Count => _credentials.Count;

        /// <summary>
        /// Records (or replaces) a terminal's ingress. Replacement is the normal case, not an error:
        /// a terminal that is cleared and restarted reports a new pipe and token for the same name.
        /// </summary>
        /// <returns>false when the arguments are unusable, true when stored.</returns>
        public bool Store(string terminalName, string sessionId, string socket, string token)
        {
            if (string.IsNullOrWhiteSpace(terminalName)) return false;
            if (string.IsNullOrWhiteSpace(socket)) return false;
            if (string.IsNullOrWhiteSpace(token)) return false;

            _credentials[terminalName] = new MessagingCredential(
                sessionId ?? string.Empty,
                socket,
                token,
                DateTime.UtcNow);
            return true;
        }

        /// <summary>Looks up a terminal's ingress. Returns false when nothing is held for that name.</summary>
        public bool TryGet(string terminalName, out MessagingCredential credential)
        {
            credential = null;
            if (string.IsNullOrWhiteSpace(terminalName)) return false;
            return _credentials.TryGetValue(terminalName, out credential);
        }

        /// <summary>Whether ingress is held for this terminal, without handing the credential out.</summary>
        public bool Has(string terminalName)
            => !string.IsNullOrWhiteSpace(terminalName) && _credentials.ContainsKey(terminalName);

        /// <summary>
        /// Drops a terminal's ingress. Called when its session ends; idempotent, because the disconnect
        /// path can run more than once for one terminal and a second clear is not an error.
        /// </summary>
        /// <returns>true when something was actually removed.</returns>
        public bool Clear(string terminalName)
        {
            if (string.IsNullOrWhiteSpace(terminalName)) return false;
            return _credentials.TryRemove(terminalName, out _);
        }

        /// <summary>
        /// Drops a terminal's ingress only if it is still exactly <paramref name="expected"/> — the entry
        /// a teardown snapshotted when it decided to act (ticket 0ff1b520, item 14).
        /// </summary>
        /// <remarks>
        /// A broker teardown decides under its lock and clears afterwards, and the SessionStart hook of a
        /// NEW session under the same name can store its credential in between. A plain
        /// <see cref="Clear"/> would then delete the live session's ingress for a dead one. Comparing by
        /// reference is exact: <see cref="Store"/> always creates a new instance, so any store after the
        /// snapshot makes this a no-op.
        /// </remarks>
        /// <returns>true when the snapshotted entry was removed.</returns>
        public bool ClearIfCurrent(string terminalName, MessagingCredential expected)
        {
            if (string.IsNullOrWhiteSpace(terminalName) || expected == null) return false;
            return _credentials.TryRemove(new System.Collections.Generic.KeyValuePair<string, MessagingCredential>(terminalName, expected));
        }

        /// <summary>Drops everything. Used when the messaging subsystem is torn down.</summary>
        public void ClearAll() => _credentials.Clear();
    }
}
