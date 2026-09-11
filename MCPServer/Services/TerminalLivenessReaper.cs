using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace MultiTerminal.MCPServer.Services
{
    /// <summary>
    /// Periodically turns terminals whose owner process is provably dead into real disconnects
    /// (task d1151661 item 3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> <see cref="MessageBroker.GetTerminals"/> hides a dead-owner row when it
    /// is read, but the Attention rail, Office, Tasks panel, Dashboard header and Chat do not read it.
    /// They react to <see cref="MessageBroker.TerminalDisconnected"/>, which only an explicit teardown
    /// raises. A session that crashes or is killed never raises it, so its Attention card stayed for
    /// MT's whole uptime. This timer is the caller that turns "provably dead" into that event.
    /// </para>
    /// <para>
    /// All the judgement lives in <see cref="MessageBroker.ReapDeadOwnerTerminals"/>. This class only
    /// owns the timer, which is why it takes a delegate rather than the broker: it has no business
    /// reaching any other broker member.
    /// </para>
    /// <para>
    /// Tuning (read once at construction, <c>MULTITERMINAL_*</c> convention):
    /// <c>MULTITERMINAL_TERMINAL_REAPER</c> = <c>0</c>/<c>false</c>/<c>off</c>/<c>no</c>/<c>disabled</c>
    /// turns it off; <c>MULTITERMINAL_TERMINAL_REAP_MS</c> sets the sweep interval (default 30000,
    /// clamped to 5000-600000).
    /// </para>
    /// </remarks>
    public sealed class TerminalLivenessReaper : IDisposable
    {
        private const int DefaultIntervalMs = 30000;
        private const int MinIntervalMs = 5000;
        private const int MaxIntervalMs = 600000;

        private readonly Func<IReadOnlyList<string>> _reap;
        private readonly Action<string> _log;
        private readonly int _intervalMs;

        private Timer _timer;
        private int _sweeping;
        private volatile bool _disposed;

        /// <summary>
        /// Creates the reaper. Nothing runs until <see cref="Start"/> is called.
        /// </summary>
        /// <param name="reap">One sweep; returns the names it released. Normally
        /// <see cref="MessageBroker.ReapDeadOwnerTerminals"/>.</param>
        /// <param name="log">Optional logger; failures are reported here rather than thrown.</param>
        public TerminalLivenessReaper(Func<IReadOnlyList<string>> reap, Action<string> log = null)
        {
            _reap = reap ?? throw new ArgumentNullException(nameof(reap));
            _log = log;
            _intervalMs = ReadIntervalMs();
        }

        /// <summary>The sweep interval in effect after env parsing and clamping.</summary>
        public int IntervalMs => _intervalMs;

        /// <summary>True when the reaper is disabled by <c>MULTITERMINAL_TERMINAL_REAPER</c>.</summary>
        public static bool IsDisabled()
        {
            string raw = Environment.GetEnvironmentVariable("MULTITERMINAL_TERMINAL_REAPER");
            if (string.IsNullOrWhiteSpace(raw)) return false;

            switch (raw.Trim().ToLowerInvariant())
            {
                case "0":
                case "false":
                case "off":
                case "no":
                case "disabled":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Arms the timer. Safe to call once; later calls are ignored.</summary>
        public void Start()
        {
            if (_disposed || _timer != null) return;

            if (IsDisabled())
            {
                Log("TerminalLivenessReaper disabled by MULTITERMINAL_TERMINAL_REAPER.");
                return;
            }

            Log($"TerminalLivenessReaper started, sweeping every {_intervalMs}ms.");
            _timer = new Timer(_ => Sweep(), null, _intervalMs, _intervalMs);
        }

        /// <summary>
        /// Runs one sweep now. Public so tests can drive it without a timer. Never throws: the reaper
        /// running on a timer thread must not take the process down, and one failed sweep is retried
        /// by the next tick.
        /// </summary>
        public void Sweep()
        {
            // A slow sweep (many rows, slow process probes) must not stack ticks on top of each other.
            if (Interlocked.Exchange(ref _sweeping, 1) == 1) return;

            try
            {
                // Timer.Dispose does not join a callback already in flight.
                if (_disposed) return;

                IReadOnlyList<string> released = _reap();
                if (released != null && released.Count > 0)
                {
                    Log($"TerminalLivenessReaper released {released.Count} dead terminal(s): {string.Join(", ", released)}.");
                }
            }
            catch (Exception ex)
            {
                Log($"TerminalLivenessReaper sweep failed, will retry next tick: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _sweeping, 0);
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _timer?.Dispose();
            _timer = null;
        }

        private int ReadIntervalMs()
        {
            const string name = "MULTITERMINAL_TERMINAL_REAP_MS";
            string raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw)) return DefaultIntervalMs;

            if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                Log($"{name}='{raw}' is not a number; using {DefaultIntervalMs}.");
                return DefaultIntervalMs;
            }

            int clamped = Math.Clamp(parsed, MinIntervalMs, MaxIntervalMs);
            if (clamped != parsed)
            {
                Log($"{name}={parsed} clamped to {clamped} (range {MinIntervalMs}-{MaxIntervalMs}).");
            }

            return clamped;
        }

        private void Log(string message) => _log?.Invoke(message);
    }
}
