using System;
using System.Diagnostics;
using System.Reflection;

namespace MultiTerminal.Services.Startup
{
    /// <summary>
    /// Identity payload served at <c>GET /api/health</c> and consumed by the startup
    /// self-probe when :5050 is already bound (task 4fec40e2).
    /// <para>
    /// The <see cref="ServiceMarker"/> constant is the load-bearing field: it lets the
    /// bind-failure classifier positively fingerprint "the port is held by another
    /// MultiTerminal" versus "the port is held by a foreign process". A foreign server
    /// listening on :5050 will not emit this exact marker, so a match is a reliable
    /// MT signal. The constant is referenced by BOTH the producer (HealthController)
    /// and the consumer (StartupPortContentionClassifier) — one source of truth, no
    /// drift between the string we write and the string we look for.
    /// </para>
    /// </summary>
    public sealed class HealthIdentity
    {
        /// <summary>
        /// Stable magic marker identifying the MultiTerminal REST API. Do not change
        /// casually — both the /api/health producer and the startup self-probe compare
        /// against it verbatim.
        /// </summary>
        public const string ServiceMarker = "multiterminal-rest-api";

        /// <summary>The service marker (always <see cref="ServiceMarker"/> for a genuine MT host).</summary>
        public string Service { get; set; } = ServiceMarker;

        /// <summary>Human-facing app name.</summary>
        public string App { get; set; } = "MultiTerminal";

        /// <summary>Assembly version string (informational; not used for classification).</summary>
        public string Version { get; set; } = string.Empty;

        /// <summary>OS process id of the running host — surfaced to the user when a conflict is another MT.</summary>
        public int Pid { get; set; }

        /// <summary>Machine name — distinguishes same-box vs (theoretical) cross-box confusion.</summary>
        public string Machine { get; set; } = string.Empty;

        /// <summary>Interactive user the host runs as — the Local\ mutex is per-session, so a cross-session MT reports a different user here.</summary>
        public string User { get; set; } = string.Empty;

        /// <summary>Windows session id of the host process.</summary>
        public int SessionId { get; set; }

        /// <summary>Port the host is bound to (echoes the probe target).</summary>
        public int Port { get; set; }

        /// <summary>UTC process start time (ISO 8601) — lets the user see whether the holder is a fresh or stale instance.</summary>
        public string StartedUtc { get; set; } = string.Empty;

        /// <summary>
        /// Build the identity for the current process. Reflection/version lookups are
        /// wrapped so a hosting quirk can never make the health endpoint throw.
        /// </summary>
        public static HealthIdentity Current(int port)
        {
            var identity = new HealthIdentity
            {
                Port = port,
                Machine = Safe(() => Environment.MachineName),
                User = Safe(() => Environment.UserName),
            };

            try
            {
                using var proc = Process.GetCurrentProcess();
                identity.Pid = proc.Id;
                identity.SessionId = proc.SessionId;
                identity.StartedUtc = proc.StartTime.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                // Process metadata is best-effort; identity marker + version still stand.
            }

            identity.Version = Safe(() =>
            {
                var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                return !string.IsNullOrWhiteSpace(info) ? info : asm.GetName().Version?.ToString() ?? "unknown";
            });

            return identity;
        }

        private static string Safe(Func<string> get)
        {
            try
            {
                return get() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
