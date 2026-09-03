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

        /// <summary>Rows that clear a block but must never touch the live display line.</summary>
        /// <remarks>
        /// The exact mirror of <see cref="DisplayOnlyTypes"/>, and it exists because the two
        /// concerns had been fused by accident. <c>activity-hook.js</c>'s <c>SKIP_TOOLS</c>
        /// (Read/Glob/Grep/ToolSearch) dropped those completions ENTIRELY, for a good reason that
        /// is only about one consumer: their lines are too noisy for the human-facing Activity
        /// feed. But a completed Read still PROVES THE AGENT IS RUNNING, and the clear-edge is a
        /// different consumer with a different need.
        /// <para>
        /// Fusing them meant a card stayed blocked through any read-only stretch, and — the case
        /// the Owner actually hit — after every answered question. <c>AskUserQuestion</c> emits no
        /// hook event AT ALL (verified: zero <c>tool=AskUserQuestion</c> entries across 150k+ hook
        /// invocations), and blocks rather than ending a turn, so it produces neither a completion
        /// row nor a <c>TURN_END</c>. The card therefore waited for some later, unrelated
        /// non-skipped tool. Task edcdcdd5, Owner's live pass 2026-09-03.
        /// </para>
        /// <para>
        /// These rows carry no summary into the service, so <c>RecordActivityLine</c>'s blank-guard
        /// leaves the displayed line exactly as it was. They are excluded from the human-facing
        /// readers at source — see <c>ActivityFeedService.QuietToolTypes</c> — so the Activity
        /// panel's readability, which is the whole reason SKIP_TOOLS exists, is preserved.
        /// </para>
        /// </remarks>
        private static readonly HashSet<string> ClearOnlyTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "TOOL_QUIET",
        };

        private const string TurnEndType = "TURN_END";

        private readonly IActivityFeedReader _feed;
        private readonly AgentAttentionService _attention;
        private readonly Action<string> _log;
        private readonly int _pollMs;
        private readonly int _batchLimit;

        /// <summary>
        /// The shared placeholder name a terminal carries until its agent registers a real one.
        /// A row under it is unattributable to one terminal, so it could only ever feed a shared
        /// card that nothing can remove (MainForm skips the same name on registration).
        /// </summary>
        private const string UnassignedSentinel = "Unassigned";

        private readonly DateTime _createdAtUtc = DateTime.UtcNow;

        private Timer _timer;
        private long _watermark;
        private volatile bool _primed;
        private bool _primeEverFailed;
        private DateTime? _replayNotBeforeUtc;
        private int _polling;
        private volatile bool _disposed;

        /// <summary>
        /// Creates the watcher. Nothing is read until <see cref="Start"/> is called.
        /// </summary>
        /// <param name="feed">Source of activity rows.</param>
        /// <param name="attention">The state machine to feed. Sole owner of the attention cache.</param>
        /// <param name="log">Optional logger; failures are reported here rather than thrown.</param>
        public AgentActivityWatcher(IActivityFeedReader feed, AgentAttentionService attention, Action<string> log = null)
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

        /// <summary>True once the watermark has been read; until then no row is applied.</summary>
        public bool IsPrimed => _primed;

        /// <summary>
        /// Sets the watermark to the table's current maximum so nothing already written is ever
        /// applied. Public so a test can drive <see cref="Poll"/> without a timer and still get
        /// the no-replay-at-startup behaviour. Idempotent once it has succeeded.
        /// </summary>
        /// <remarks>
        /// A failure here is NOT fatal. The first cut returned from <see cref="Start"/> without
        /// arming the timer when this threw, and the caller had already stored the instance — so
        /// one transient "database is locked" at startup (this app has a whole WriteContention
        /// diagnostics stream for exactly that window) left the rail silently dead for the life of
        /// the process, which is the owner's original complaint. Now every tick re-tries until it
        /// works, and only then are rows applied.
        /// </remarks>
        /// <returns>False if the watermark could not be read this time.</returns>
        public bool Prime()
        {
            if (_primed) return true;

            try
            {
                long max = _feed.GetMaxActivityId();

                if (_primeEverFailed)
                {
                    // A LATE prime. Rows written while we were unprimed are not history — an agent
                    // was working, and one of those rows may be the TOOL_COMPLETE that clears a
                    // block. Back up one batch and let Apply() keep only rows stamped after this
                    // watcher came to life; everything older really is history. (Pipeline run 2,
                    // adversary finding: the first cut set the watermark to MAX and silently
                    // dropped the whole window.)
                    _watermark = Math.Max(0, max - _batchLimit);
                    _replayNotBeforeUtc = _createdAtUtc;
                    Log($"AgentActivityWatcher primed late at id {max}; replaying up to {_batchLimit} rows stamped after {_createdAtUtc:O}.");
                }
                else
                {
                    _watermark = max;
                }

                _primed = true;
                return true;
            }
            catch (Exception ex)
            {
                // Starting at 0 would replay the entire table on the first tick.
                _primeEverFailed = true;
                Log($"AgentActivityWatcher could not read the watermark, will retry next tick: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Begins polling. Safe to call once; subsequent calls are ignored. The timer is armed
        /// even if the first watermark read fails — see <see cref="Prime"/>.
        /// </summary>
        public void Start()
        {
            if (_disposed || _timer != null) return;

            if (IsDisabled())
            {
                Log("AgentActivityWatcher disabled by MULTITERMINAL_ATTENTION_WATCH.");
                return;
            }

            if (Prime())
            {
                Log($"AgentActivityWatcher started at id {_watermark}, polling every {_pollMs}ms.");
            }
            else
            {
                Log($"AgentActivityWatcher started UNPRIMED, polling every {_pollMs}ms until the watermark can be read.");
            }

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
                // Timer.Dispose does not join a callback already in flight; without this an
                // in-progress tick would keep reading a database that MainForm is tearing down.
                if (_disposed) return;

                if (!Prime()) return;

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
                        Log($"AgentActivityWatcher skipped row {row.Id}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                // The watermark has not moved, so the same batch is re-read next tick. That is
                // only safe because ActivityFeedService.ReadEntry never throws on a row's
                // CONTENT (a bad timestamp or NULL text is tolerated there) — a read that throws
                // here is a connection-level failure, which retrying is the right answer to.
                Log($"AgentActivityWatcher poll failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _polling, 0);
            }
        }

        /// <summary>
        /// Logs without ever throwing. The logger can be a disposed DebugLogService during shutdown,
        /// and this runs on a timer thread where an exception is unhandled.
        /// </summary>
        private void Log(string message)
        {
            try
            {
                _log?.Invoke(message);
            }
            catch
            {
                // Nowhere left to report to.
            }
        }

        private void Apply(ActivityFeedEntry row)
        {
            string agent = row.Actor;
            if (string.IsNullOrWhiteSpace(agent)) return;
            if (string.Equals(agent, UnassignedSentinel, StringComparison.OrdinalIgnoreCase)) return;

            string type = row.ActivityType ?? string.Empty;
            bool clears = ClearingTypes.Contains(type);
            bool displays = DisplayOnlyTypes.Contains(type);
            bool clearsOnly = ClearOnlyTypes.Contains(type);
            bool turnEnded = string.Equals(type, TurnEndType, StringComparison.OrdinalIgnoreCase);

            if (!clears && !displays && !clearsOnly && !turnEnded) return;

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

            // Only reached after a LATE prime backed the watermark up (see Prime). Rows older
            // than this watcher are history and must not be re-applied.
            if (_replayNotBeforeUtc is DateTime notBefore && observedAt < notBefore) return;

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

            // Clear-only: take the clear path with NO summary, so RecordActivityLine's blank-guard
            // leaves the displayed line untouched. Deliberately still subject to
            // NoteObservedActivity's ordering guard (observedAt > EnteredAtUtc), so a queued row
            // that predates the block cannot clear it.
            if (clearsOnly)
            {
                _attention.NoteObservedActivity(
                    sessionKey,
                    observedAt,
                    isSubagent: false,
                    toolUseId: null,
                    agentName: agent,
                    activitySummary: null);
                return;
            }

            // agentName is passed so the supersede path from task cafd47b9 works here too: a
            // terminal that is /cleared and then simply gets back to work never blocks, so
            // ApplyNotification is never reached and its predecessor would otherwise linger as a
            // ghost card. The two tickets compose exactly here.
            _attention.NoteObservedActivity(
                sessionKey,
                observedAt,
                isSubagent: false,
                toolUseId: null,
                agentName: agent,
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
