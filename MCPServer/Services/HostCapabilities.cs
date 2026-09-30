using System;
using System.Collections.Generic;
using System.Linq;

namespace MultiTerminal.MCPServer.Services
{
    /// <summary>
    /// Contributes capability tokens to <c>GET /api/health</c> (ticket 9a731cda item 2). A provider
    /// answers from runtime state at the moment it is asked, so a token appears only while the
    /// behaviour it names is actually in force.
    /// </summary>
    public interface ICapabilityProvider
    {
        /// <summary>The tokens this provider vouches for right now. May be empty; must not be null.</summary>
        IEnumerable<string> GetCapabilities();
    }

    /// <summary>
    /// Vouches for <see cref="Token"/>: MT can host ClarionAssistant-embedded sessions, which register
    /// by name and owner pid, post their credentials, and are released by being killed.
    /// </summary>
    /// <remarks>
    /// Three conditions, each read from the object that implements it, never from a list here:
    /// <list type="bullet">
    /// <item>The liveness reaper is running (<see cref="TerminalLivenessReaper.IsRunning"/>). It is
    /// such a session's only release path, since nothing disconnects a killed process. Off when
    /// <c>MULTITERMINAL_TERMINAL_REAPER</c> disables it, when MainForm never started it, and after
    /// it is disposed.</item>
    /// <item>Reaping clears credentials (<see cref="MessageBroker.ReapClearsMessagingCredentials"/>).</item>
    /// <item>The credentials endpoint demands proof of origin
    /// (<see cref="MessageBroker.CredentialPostProofEnforced"/>).</item>
    /// </list>
    /// </remarks>
    public sealed class CaEmbeddedCapabilityProvider : ICapabilityProvider
    {
        /// <summary>The capability token. Clients compare it verbatim.</summary>
        public const string Token = "ca-embedded-v1";

        private readonly MessageBroker _broker;

        /// <summary>Creates the provider over the broker whose runtime state it reports.</summary>
        public CaEmbeddedCapabilityProvider(MessageBroker broker)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        }

        /// <inheritdoc />
        public IEnumerable<string> GetCapabilities()
        {
            bool reaperRunning = _broker.LivenessReaper?.IsRunning == true;
            if (reaperRunning
                && _broker.ReapClearsMessagingCredentials
                && _broker.CredentialPostProofEnforced)
            {
                return new[] { Token };
            }

            return Array.Empty<string>();
        }
    }

    /// <summary>Folds providers into the <c>capabilities</c> list served by <c>GET /api/health</c>.</summary>
    public static class HostCapabilities
    {
        /// <summary>
        /// Distinct, ordinally sorted tokens from every provider. A provider that throws contributes
        /// nothing rather than failing the health endpoint, which the startup self-probe depends on.
        /// </summary>
        public static IReadOnlyList<string> Collect(IEnumerable<ICapabilityProvider> providers)
        {
            var tokens = new SortedSet<string>(StringComparer.Ordinal);
            if (providers == null) return tokens.ToList();

            foreach (ICapabilityProvider provider in providers)
            {
                try
                {
                    foreach (string token in provider?.GetCapabilities() ?? Enumerable.Empty<string>())
                    {
                        if (!string.IsNullOrWhiteSpace(token)) tokens.Add(token);
                    }
                }
#pragma warning disable CA1031 // A capability probe must never take /api/health down; the startup self-probe reads it.
                catch (Exception)
#pragma warning restore CA1031
                {
                    // Omitted: an unanswerable capability is not advertised.
                }
            }

            return tokens.ToList();
        }
    }
}
