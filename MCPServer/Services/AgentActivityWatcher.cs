using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;

namespace MultiTerminal.MCPServer.Services
{
    /// <summary>
    /// Feeds observed tool activity from <c>activity_feed</c> into
    /// <see cref="AgentAttentionService"/>, so the attention rail can clear a block and show what an
    /// agent is actually doing (task edcdcdd5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> Task 2289bb8a shipped a complete state machine whose clear path,
    /// <see cref="AgentAttentionService.NoteObservedActivity"/>, had NO CALLER. So a block was set
    /// and stayed set forever, and <see cref="AttentionState.Working"/> was unreachable in practice —
    /// the owner watched a card read "Finished and idle" for 27 minutes while that agent worked, and
    /// another pulse "needs permission" for 49 minutes after they had dismissed it. This class is
    /// that missing caller.
    /// </para>
    /// <para>
    /// <b>Why polling, and not a REST call per tool.</b> Tool events reach MultiTerminal only as
    /// direct SQLite writes from the Node activity hook, which bypasses the broker entirely, so there
    /// is no event to subscribe to. The alternative — having the hook POST on every tool use — adds
    /// latency to every single tool call in every terminal, permanently, to feed one panel. The rows
    /// are already written and already indexed, so reading them costs nothing an agent can feel.
    /// </para>
    /// <para>
    /// <b>Watermarked by row id, not by timestamp.</b> Rows are written by a separate process, so
    /// their timestamps come from a clock this one does not control. Ids are a monotonic
    /// AUTOINCREMENT. The watermark starts at the table's current maximum so startup does not replay
    /// history — re-applying long-dead tool events would clear blocks raised after them.
    /// </para>
    /// </remarks>
    public sealed class AgentActivityWatcher : IDisposable
    {
        /// <summary>Rows that mean "the agent did something", i.e. the CLEAR edge.</summary>
        /// <remarks>
        /// <c>TOOL_START</c> is deliberately ABSENT. It is written on <c>PreToolUse</c>, and
        /// <c>safety-hook.js</c> is itself a PreToolUse hook returning
        /// <c>permissionDecision: 'ask'</c> — so TOOL_START runs BEFORE the prompt it causes, and
        /// clearing on it would clear a block at the instant of its creation. It feeds the display
        /// line only. This is finding 1 of the 2289bb8a spike and it is the single easiest thing here
        /// to "simplify" into a bug.
        /// </remarks>
        private static readonly HashSet<string> ClearingTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "TOOL_COMPLETE",
            "TOOL_FAILED",      // a DENIED permission is a failure, not an absence — it still clears
            "BUILD_SUCCEEDED",
            "BUILD_FAILED",
        };

        /// <summary>Rows that feed the live display line but must never clear a block.</summary>
        private static readonly HashSet<string> DisplayOnlyTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "TOOL_START",
        };

        private const string TurnEndType = "TURN_END";

        private readonly ActivityFeedService _feed;
        private readonly AgentAttentionService _attention;
        private readonly Action<string> _log;
        private readonly int _pollMs;
        private readonly int _batchLimit;

        private Timer _timer;
        private long _watermark;
        private int _polling;
        private bool _disposed;

        /// <summary>
        /// Creates the watcher. Nothing is read until <see cref="Start"/> is called.
        /// </summary>
        /// <param name="feed">Source of activity rows.</param>
        /// <param name="attention">The state machine to feed. Sole owner of the attention cache.</param>
        /// <param name="log">Optional logger; failures are reported here rather than thrown.</param>
        public AgentActivityWatcher(ActivityFeedService feed, AgentAttentionService attention, Action<string> log = null)
        {
            _feed = feed ?? throw new ArgumentNullException(nameof(feed));
            _attention = attention ?? throw new ArgumentNullException(nameof(attention));
            _log = log;

            _pollMs = ReadEnvInt("MULTITERMINAL_ATTENTION_POLL_MS", 2000, 250, 60000);
            _batchLimit = ReadEnvInt("MULTITERMINAL_ATTENTION_BATCH", 200, 10, 2000);
        }

        /// <summary>True when the watcher is disabled by environment variable.</summary>
        public static bool IsDisabled()
        {
            string raw = Environment.GetEnvironmentVariable("MULTITERMINAL_ATTENTION_WATCH");
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

        /// <summary>
        /// Begins polling. Safe to call once; subsequent calls are ignored.
        /// </summary>
        public void Start()
        {
            if (_disposed || _timer != null) return;

            if (IsDisabled())
            {
                _log?.Invoke("AgentActivityWatcher disabled by MULTITERMINAL_ATTENTION_WATCH.");
                return;
            }

            try
            {
                _watermark = _feed.GetMaxActivityId();
            }
            catch (Exception ex)
            {
                // Starting at 0 would replay the entire table on the first tick.
                _log?.Invoke($"AgentActivityWatcher could not read the watermark, staying idle: {ex.Message}");
                return;
            }

            _log?.Invoke($"AgentActivityWatcher started at id {_watermark}, polling every {_pollMs}ms.");
            _timer = new Timer(_ => Poll(), null, _pollMs, _pollMs);
        }

        /// <summary>
        /// Reads one batch and applies it. Public so a test can drive it without a timer.
        /// </summary>
        public void Poll()
        {
            // A slow read must not stack ticks on top of each other.
            if (Interlocked.Exchange(ref _polling, 1) == 1) return;

            try
            {
                List<ActivityFeedEntry> rows = _feed.GetActivitiesAfterId(_watermark, _batchLimit);
                if (rows == null || rows.Count == 0) return;

                foreach (var row in rows)
                {
                    if (row == null) continue;
                    if (row.Id > _watermark) _watermark = row.Id;

                    try
                    {
                        Apply(row);
                    }
                    catch (Exception ex)
                    {
                        // One malformed row must not stop the pump, and must not stop the watermark
                        // advancing past it — otherwise it is retried forever.
                        _log?.Invoke($"AgentActivityWatcher skipped row {row.Id}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"AgentActivityWatcher poll failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _polling, 0);
            }
        }

        private void Apply(ActivityFeedEntry row)
        {
            string agent = row.Actor;
            if (string.IsNullOrWhiteSpace(agent)) return;

            string type = row.ActivityType ?? string.Empty;
            bool clears = ClearingTypes.Contains(type);
            bool displays = DisplayOnlyTypes.Contains(type);
            bool turnEnded = string.Equals(type, TurnEndType, StringComparison.OrdinalIgnoreCase);

            if (!clears && !displays && !turnEnded) return;

            bool isSubagent = LooksLikeSubagent(row.DetailsJson);

            // Subagent rows say nothing about the parent, in either direction. Dropping them here
            // rather than inside the service keeps the "never clear" rule and the "never mislabel the
            // display line" rule in one place.
            if (isSubagent) return;

            string sessionKey = ResolveSessionKey(agent);
            if (string.IsNullOrWhiteSpace(sessionKey)) return;

            DateTime observedAt = row.Timestamp.Kind == DateTimeKind.Utc
                ? row.Timestamp
                : row.Timestamp.ToUniversalTime();

            if (turnEnded)
            {
                _attention.NoteTurnEnded(sessionKey, observedAt, isSubagent: false);
                return;
            }

            if (displays)
            {
                // Display-only: record the line WITHOUT taking the clear path. Passing isSubagent
                // true would also suppress the line, so the summary is written directly.
                _attention.NoteActivityLineOnly(sessionKey, row.Summary, observedAt);
                return;
            }

            _attention.NoteObservedActivity(
                sessionKey,
                observedAt,
                isSubagent: false,
                toolUseId: null,
                activitySummary: row.Summary);
        }

        /// <summary>
        /// Maps an agent name to the attention cache key.
        /// </summary>
        /// <remarks>
        /// <c>activity_feed.actor</c> is the agent NAME; the cache is keyed by session id. When no
        /// entry exists yet the name is used directly, which matches
        /// <see cref="AgentAttentionService.ApplyNotification"/>'s own fallback for a payload with no
        /// session id — so the two agree on the key instead of creating a duplicate entry.
        /// </remarks>
        private string ResolveSessionKey(string agent)
        {
            var entry = _attention.GetByAgent(agent);
            return entry != null && !string.IsNullOrWhiteSpace(entry.SessionId) ? entry.SessionId : agent;
        }

        /// <summary>
        /// Whether a row came from a SUBAGENT rather than the main thread.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Defaults to the SAFE value — <c>true</c> — whenever the row cannot say. That covers every
        /// row written before the hook began stamping provenance, and any row whose
        /// <c>details_json</c> is absent or unparseable.
        /// </para>
        /// <para>
        /// The polarity is not arbitrary. Refusing to clear leaves a stale pulse, which the owner
        /// can see and dismiss. Clearing wrongly renders a CALM CARD, which is indistinguishable
        /// from nobody needing you — a silent failure that hides a waiting agent. Subagent tool
        /// calls are logged under the PARENT's name and were measured at 19.5% of PreToolUse events,
        /// so this is not a rare edge.
        /// </para>
        /// <para>
        /// An explicit <c>"agent_id": null</c> means "the hook looked and it was the main thread".
        /// A MISSING key means "this row predates provenance and cannot say". Those are different,
        /// and conflating them inverts the safe default on exactly the historical data.
        /// </para>
        /// </remarks>
        internal static bool LooksLikeSubagent(string detailsJson)
        {
            if (string.IsNullOrWhiteSpace(detailsJson)) return true;

            try
            {
                using var doc = JsonDocument.Parse(detailsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return true;

                if (!doc.RootElement.TryGetProperty("agent_id", out var agentId)) return true;

                // Present and null => the hook looked, and it was the main thread.
                if (agentId.ValueKind == JsonValueKind.Null) return false;

                if (agentId.ValueKind == JsonValueKind.String)
                {
                    return !string.IsNullOrWhiteSpace(agentId.GetString());
                }

                return true;
            }
            catch (JsonException)
            {
                return true;
            }
        }

        private int ReadEnvInt(string name, int fallback, int min, int max)
        {
            string raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw)) return fallback;

            if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                _log?.Invoke($"{name}='{raw}' is not a number; using {fallback}.");
                return fallback;
            }

            int clamped = Math.Clamp(parsed, min, max);
            if (clamped != parsed)
            {
                _log?.Invoke($"{name}={parsed} clamped to {clamped} (range {min}-{max}).");
            }

            return clamped;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _timer?.Dispose();
            _timer = null;
        }
    }
}
