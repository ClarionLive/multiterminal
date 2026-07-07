using System;
using System.Globalization;
using System.Net.Sockets;

namespace MultiTerminal.Services.Startup
{
    /// <summary>How the :5050 bind failure was classified.</summary>
    public enum PortContentionVerdict
    {
        /// <summary>The port is held by another MultiTerminal host (the self-probe reached an MT /api/health).</summary>
        MultiTerminalAlreadyRunning,

        /// <summary>The port is held by a foreign process, or the holder could not be probed (treated as unknown → foreign).</summary>
        ForeignHolder,
    }

    /// <summary>Result of self-probing <c>http://127.0.0.1:port/api/health</c> after a bind failure.</summary>
    public sealed class HealthProbeResult
    {
        /// <summary>True if the probe got any HTTP response at all (even a non-MT one).</summary>
        public bool Reached { get; init; }

        /// <summary>True if the response carried <see cref="HealthIdentity.ServiceMarker"/> — a positive MT signal.</summary>
        public bool IsMultiTerminal { get; init; }

        /// <summary>The parsed identity when <see cref="IsMultiTerminal"/> is true; otherwise null.</summary>
        public HealthIdentity Identity { get; init; }

        /// <summary>A probe that reached nothing (timeout / connection reset) — classified as unknown holder.</summary>
        public static HealthProbeResult NotReached() => new() { Reached = false, IsMultiTerminal = false };
    }

    /// <summary>Owning process of the port, as resolved from the OS TCP table.</summary>
    public sealed class PortHolderInfo
    {
        /// <summary>Owning process id, or 0 if it could not be resolved.</summary>
        public int Pid { get; init; }

        /// <summary>Owning process image name, or null if it could not be resolved.</summary>
        public string ProcessName { get; init; }

        /// <summary>True when a concrete PID was resolved.</summary>
        public bool HasPid => Pid > 0;

        /// <summary>Sentinel for "the TCP table lookup could not identify the holder".</summary>
        public static PortHolderInfo Unknown => new() { Pid = 0, ProcessName = null };
    }

    /// <summary>
    /// Pure decision logic for the startup port-contention path (task 4fec40e2).
    /// <para>
    /// Deliberately free of I/O: the HTTP probe, the TCP-table PID lookup, and the
    /// Retry/Exit dialog live in thin adapters (StartupHealthProbe, TcpPortOwnerLookup,
    /// MainForm). Everything here is a pure function of its inputs so it can be unit
    /// tested without launching a second process or opening a socket — which matters
    /// because the end-to-end flow can only be exercised by the Owner (an agent inside
    /// MultiTerminal cannot launch a second instance to trigger it).
    /// </para>
    /// </summary>
    public static class StartupPortContentionClassifier
    {
        /// <summary>
        /// True if <paramref name="ex"/> (or anything in its inner-exception chain) is a
        /// "port already in use" failure. Kestrel surfaces this as an <see cref="System.IO.IOException"/>
        /// wrapping a <see cref="SocketException"/> with <see cref="SocketError.AddressAlreadyInUse"/>;
        /// we also match on the well-known Win32 code (10048) and a message fallback so a
        /// future host/runtime that reshapes the exception still routes to the friendly path.
        /// </summary>
        public static bool IsAddressInUse(Exception ex)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (current is SocketException se &&
                    (se.SocketErrorCode == SocketError.AddressAlreadyInUse || se.ErrorCode == 10048))
                {
                    return true;
                }

                // Message fallback for a future runtime that reshapes the exception. Match only
                // ADDRESS-IN-USE-specific phrasings — NOT the generic "failed to bind", which
                // Kestrel also emits for permission-denied binds and would misroute those to the
                // port-contention path (task 4fec40e2 code-review finding). The structured
                // SocketException/errno-10048 check above already covers the real-world case.
                var msg = current.Message;
                if (!string.IsNullOrEmpty(msg) &&
                    (msg.IndexOf("address already in use", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     msg.IndexOf("only one usage of each socket address", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Classify the contention from the self-probe result. A positive MT marker means
        /// another MultiTerminal owns the port; anything else — a foreign 200, a reset, or
        /// a timeout — is treated as a foreign/unknown holder, per the plan's "treat probe
        /// timeout as unknown-holder and fall through to PID identification" rule.
        /// </summary>
        public static PortContentionVerdict Classify(HealthProbeResult probe)
        {
            return probe is { IsMultiTerminal: true }
                ? PortContentionVerdict.MultiTerminalAlreadyRunning
                : PortContentionVerdict.ForeignHolder;
        }

        /// <summary>
        /// Classify, then cross-check a marker-positive probe against the real OS TCP owner.
        /// The <see cref="HealthIdentity.ServiceMarker"/> is a public constant, so a hostile
        /// process squatting on the port can echo it and return a fake PID/user in the body —
        /// but it cannot fake which PID actually owns the socket. If the health-claimed PID
        /// disagrees with the TCP-table owner PID, downgrade to <see cref="PortContentionVerdict.ForeignHolder"/>
        /// so the user sees the OS-resolved holder instead of a spoofed "another MultiTerminal"
        /// message (task 4fec40e2 security finding). When the owner PID can't be resolved
        /// (<paramref name="holder"/> has no PID), the marker is trusted — a genuine MT is
        /// normally resolvable, and failing closed on a transient lookup miss would misreport a
        /// real second instance as foreign.
        /// </summary>
        public static PortContentionVerdict ClassifyWithOwner(HealthProbeResult probe, PortHolderInfo holder)
        {
            var verdict = Classify(probe);
            if (verdict == PortContentionVerdict.MultiTerminalAlreadyRunning &&
                holder is { HasPid: true } &&
                probe?.Identity != null &&
                probe.Identity.Pid != holder.Pid)
            {
                return PortContentionVerdict.ForeignHolder;
            }

            return verdict;
        }

        /// <summary>
        /// Build the user-facing dialog body for a classified contention. Kept here (not in
        /// the WinForms layer) so the exact wording is covered by unit tests.
        /// </summary>
        public static string BuildMessage(
            PortContentionVerdict verdict,
            int port,
            HealthProbeResult probe,
            PortHolderInfo holder)
        {
            switch (verdict)
            {
                case PortContentionVerdict.MultiTerminalAlreadyRunning:
                {
                    var id = probe?.Identity;
                    string who = id != null
                        ? $" (PID {id.Pid}, user \"{id.User}\", session {id.SessionId})"
                        : string.Empty;
                    return
                        $"MultiTerminal is already running{who} and is holding port {port.ToString(CultureInfo.InvariantCulture)}.\n\n" +
                        "This copy cannot start a second REST API on the same port. Switch to the running " +
                        "instance, or close it first.\n\n" +
                        "Click Retry after closing the other instance, or Cancel to exit.";
                }

                case PortContentionVerdict.ForeignHolder:
                default:
                {
                    string who;
                    if (holder is { HasPid: true })
                    {
                        string name = string.IsNullOrWhiteSpace(holder.ProcessName) ? "unknown process" : holder.ProcessName;
                        who = $"process \"{name}\" (PID {holder.Pid.ToString(CultureInfo.InvariantCulture)})";
                    }
                    else
                    {
                        who = "another process (could not be identified)";
                    }

                    return
                        $"Port {port.ToString(CultureInfo.InvariantCulture)} is already in use by {who}.\n\n" +
                        "MultiTerminal requires this port for its REST API and MCP integration and does not " +
                        "fall back to another port (the port is hard-coded across the MCP server and hooks).\n\n" +
                        "Free the port (close the process above), then click Retry — or Cancel to exit.";
                }
            }
        }
    }
}
