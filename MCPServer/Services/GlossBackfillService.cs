using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using MultiTerminal.MCPServer.Models;

namespace MultiTerminal.MCPServer.Services
{
    /// <summary>
    /// Spawns a background agent to write the missing plain-language gloss on a task's checklist
    /// (task a455e295). The decisions live in <see cref="GlossBackfillPlanner"/>; this class only
    /// does the IO and the throttling.
    /// </summary>
    /// <remarks>
    /// <para><b>Trigger point.</b> Callers invoke this AFTER a checklist write has committed, never
    /// before and never inside the write's lock. The obvious hook — task activation — is the wrong
    /// one: the standard flow is claim → in_progress → set_active → THEN plan and build the
    /// checklist, so at activation there is usually nothing to explain. An activation-only trigger
    /// would backfill a session late, or never for a task planned and finished in one sitting.
    /// Activation remains useful as a secondary sweep for the pre-feature backlog.</para>
    /// <para><b>This is best-effort and must stay that way.</b> Every entry point swallows its own
    /// failures. A backfill that cannot spawn, or spawns and dies, must never fail, delay, or roll
    /// back the checklist write that triggered it — the user's actual work does not depend on the
    /// documentation being written, and an explanation agent taking a checklist edit down with it
    /// would be an absurd trade.</para>
    /// <para><b>Cost.</b> Each run starts a real agent process that spends tokens. That is why the
    /// throttles below are not optional and why there is an off switch.</para>
    /// </remarks>
    internal sealed class GlossBackfillService
    {
        /// <summary>Master switch. Set to a falsey value to stop all backfill spawning.</summary>
        public const string EnvEnabled = "MULTITERMINAL_GLOSS_BACKFILL";

        /// <summary>Per-task cooldown override, in milliseconds.</summary>
        public const string EnvCooldownMs = "MULTITERMINAL_GLOSS_BACKFILL_COOLDOWN_MS";

        private const int DefaultCooldownMs = 10 * 60 * 1000;   // 10 minutes
        private const int MinCooldownMs = 30 * 1000;            // floor: never hotter than 30s
        private const int MaxCooldownMs = 24 * 60 * 60 * 1000;  // ceiling: 24h

        private readonly IGlossBackfillHost _host;

        /// <summary>Tasks with a run in flight right now. Guards the normal transition cadence.</summary>
        private readonly ConcurrentDictionary<string, byte> _inFlight =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Last completed run per task, for the cooldown.</summary>
        private readonly ConcurrentDictionary<string, DateTime> _lastRunUtc =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private readonly bool _enabled;
        private readonly int _cooldownMs;

        public GlossBackfillService(IGlossBackfillHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _enabled = ReadEnabled(_host);
            _cooldownMs = ReadCooldownMs(_host);

            _host.LogInfo(
                $"GlossBackfillService: enabled={_enabled}, cooldown={_cooldownMs}ms " +
                $"(override with {EnvEnabled} / {EnvCooldownMs}).");
        }

        /// <summary>True when backfill spawning is switched on for this process.</summary>
        public bool Enabled => _enabled;

        /// <summary>
        /// Consider backfilling <paramref name="task"/>'s checklist. Returns immediately; any work
        /// happens on a background task. Never throws.
        /// </summary>
        /// <param name="task">The task as it exists AFTER the committing write.</param>
        /// <param name="reason">Short trigger description, for the log ("checklist write", "activation sweep").</param>
        public void RequestBackfill(KanbanTask task, string reason)
        {
            try
            {
                if (!_enabled || task == null || string.IsNullOrEmpty(task.Id) || task.IsQuickTask)
                {
                    return;
                }

                List<ChecklistItem> checklist;
                try
                {
                    checklist = task.GetChecklist();
                }
                catch (Exception ex)
                {
                    // A checklist that will not deserialize is somebody else's bug; it is certainly
                    // not worth taking down the caller over, and there is nothing to explain anyway.
                    _host.LogWarning($"GlossBackfill: could not read checklist for {task.Id}: {ex.Message}");
                    return;
                }

                var needed = GlossBackfillPlanner.ItemsNeedingGloss(checklist);
                if (needed.Count == 0)
                {
                    return;
                }

                // Cooldown BEFORE in-flight: a task whose checklist is being edited rapidly (the
                // normal transition cadence) must not spawn on every write.
                if (_lastRunUtc.TryGetValue(task.Id, out var last)
                    && (_host.UtcNow - last).TotalMilliseconds < _cooldownMs)
                {
                    return;
                }

                // Claim the slot. TryAdd is the whole mutual exclusion: the loser just returns.
                if (!_inFlight.TryAdd(task.Id, 0))
                {
                    return;
                }

                if (needed.Count > GlossBackfillPlanner.MaxItemsPerRun)
                {
                    // Say what was left out. A silent cap reads as "everything was covered".
                    _host.LogInfo(
                        $"GlossBackfill: {task.Id} has {needed.Count} un-glossed items; writing the first " +
                        $"{GlossBackfillPlanner.MaxItemsPerRun} this run, the remaining " +
                        $"{needed.Count - GlossBackfillPlanner.MaxItemsPerRun} are left for a later pass.");
                    needed = needed.GetRange(0, GlossBackfillPlanner.MaxItemsPerRun);
                }

                var prompt = GlossBackfillPlanner.BuildPrompt(task, checklist, needed);

                // Fire and forget: the caller has already committed its write and is not waiting.
                _ = RunAsync(task.Id, needed.Count, prompt, reason);
            }
            catch (Exception ex)
            {
                // Belt and braces. RequestBackfill sits on a committed write path; it does not get
                // to throw regardless of what went wrong above.
                _host.LogWarning($"GlossBackfill: request failed for {task?.Id}: {ex.Message}");
            }
        }

        private async Task RunAsync(string taskId, int itemCount, string prompt, string reason)
        {
            try
            {
                _host.LogInfo($"GlossBackfill: spawning writer for {taskId} ({itemCount} item(s), trigger: {reason}).");

                var (success, error) = await _host.SpawnGlossWriterAsync(taskId, prompt).ConfigureAwait(false);

                if (!success)
                {
                    _host.LogWarning($"GlossBackfill: writer spawn failed for {taskId}: {error}");
                }
            }
            catch (Exception ex)
            {
                _host.LogWarning($"GlossBackfill: writer run failed for {taskId}: {ex.Message}");
            }
            finally
            {
                // Stamp the cooldown even on failure, so a persistently broken spawn path retries
                // on the cooldown rather than on every single checklist edit.
                _lastRunUtc[taskId] = _host.UtcNow;
                _inFlight.TryRemove(taskId, out _);
            }
        }

        // ---- env parsing (same convention + logging as CodeGraphWatcher) ----

        internal static bool ReadEnabled(IGlossBackfillHost host)
        {
            var raw = host.GetEnvironmentVariable(EnvEnabled);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return true;   // on by default; the throttles above are what make that safe
            }

            switch (raw.Trim().ToLowerInvariant())
            {
                case "0":
                case "false":
                case "off":
                case "no":
                case "disabled":
                    return false;
                case "1":
                case "true":
                case "on":
                case "yes":
                case "enabled":
                    return true;
                default:
                    host.LogWarning($"GlossBackfill: unrecognized {EnvEnabled}='{raw}'; treating as enabled.");
                    return true;
            }
        }

        internal static int ReadCooldownMs(IGlossBackfillHost host)
        {
            var raw = host.GetEnvironmentVariable(EnvCooldownMs);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return DefaultCooldownMs;
            }

            if (!int.TryParse(raw.Trim(), out var parsed))
            {
                host.LogWarning($"GlossBackfill: unparseable {EnvCooldownMs}='{raw}'; using {DefaultCooldownMs}ms.");
                return DefaultCooldownMs;
            }

            var clamped = Math.Max(MinCooldownMs, Math.Min(MaxCooldownMs, parsed));
            if (clamped != parsed)
            {
                host.LogWarning($"GlossBackfill: {EnvCooldownMs}={parsed} clamped to {clamped}ms.");
            }

            return clamped;
        }
    }

    /// <summary>
    /// The narrow set of things <see cref="GlossBackfillService"/> needs from the outside world:
    /// spawning, logging, the clock, and environment reads. Narrow on purpose — it is what lets the
    /// service be tested without a broker, a process, or a real clock.
    /// </summary>
    internal interface IGlossBackfillHost
    {
        /// <summary>Current UTC time. Injected so the cooldown is testable without sleeping.</summary>
        DateTime UtcNow { get; }

        /// <summary>Read a process environment variable.</summary>
        string GetEnvironmentVariable(string name);

        /// <summary>Start the writer agent. Returns success plus an error message on failure.</summary>
        Task<(bool success, string error)> SpawnGlossWriterAsync(string taskId, string prompt);

        void LogInfo(string message);

        void LogWarning(string message);
    }
}
