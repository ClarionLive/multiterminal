using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MultiTerminal.MCPServer.Services;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Whether a pane asked to be told to continue after its next compaction (ticket c0ebcfd4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A manual <c>/compact</c> ends without starting a turn, so an agent that compacted itself with
    /// <c>compact_my_context</c> sat idle until the Owner typed something. Claude Code's automatic
    /// compaction continues by itself and needs nothing.
    /// </para>
    /// <para>
    /// Armed when MT submits an agent-requested <c>/compact</c>, consumed by the plugin's
    /// SessionStart(compact) report. A <c>/compact</c> the Owner typed arms nothing, so it is followed
    /// by nothing. One gate per pane, held by the pane's <c>TerminalControl</c>, so the flag belongs
    /// to the pane that compacted and not to whatever later answers to its name.
    /// </para>
    /// <para>Thread-safe: armed on the UI thread, consumed from the REST event on the UI thread too,
    /// but nothing guarantees that for a future caller.</para>
    /// </remarks>
    internal sealed class CompactContinueGate
    {
        /// <summary>How long an armed request stays valid. A compaction that has not finished by then is not this one.</summary>
        internal static readonly TimeSpan Expiry = TimeSpan.FromMinutes(10);

        private readonly object _lock = new object();
        private DateTime? _armedAtUtc;

        /// <summary>Records that the pane's next compaction should be followed by a continue prompt.</summary>
        public void Arm(DateTime nowUtc)
        {
            lock (_lock)
            {
                _armedAtUtc = nowUtc;
            }
        }

        /// <summary>
        /// True at most once per <see cref="Arm"/>, and only within <see cref="Expiry"/>. An expired
        /// request is cleared, so it cannot fire later.
        /// </summary>
        public bool TryConsume(DateTime nowUtc)
        {
            lock (_lock)
            {
                if (_armedAtUtc is not DateTime armedAt)
                {
                    return false;
                }

                _armedAtUtc = null;
                TimeSpan age = nowUtc - armedAt;
                return age >= TimeSpan.Zero && age <= Expiry;
            }
        }
    }

    /// <summary>What a main-thread activity row says about the agent.</summary>
    internal enum CompactContinueSignal
    {
        /// <summary>A <c>TURN_END</c> row: the turn is over and the prompt is free.</summary>
        TurnEnded,

        /// <summary>Any other main-thread row (a tool started, finished or failed): the agent is working.</summary>
        Working,
    }

    /// <summary>The next thing <see cref="CompactContinueRunner"/> should do.</summary>
    internal enum CompactContinueStep
    {
        /// <summary>Poll again.</summary>
        Wait,

        /// <summary>Type the continue prompt now.</summary>
        Type,

        /// <summary>Nothing started after the prompt was typed: press Enter once more.</summary>
        SendEnterAgain,

        /// <summary>A turn started. Finished.</summary>
        Verified,

        /// <summary>No turn started even after the second Enter. Finished; log it.</summary>
        NotVerified,
    }

    /// <summary>Why the prompt was typed when it was. Logged, so a live test can tell which edge fired.</summary>
    internal enum CompactContinueTypeReason
    {
        /// <summary>Not typed yet.</summary>
        None,

        /// <summary>A <c>TURN_END</c> arrived after the compaction finished, with no work after it.</summary>
        TurnEnded,

        /// <summary>No main-thread activity at all for <see cref="CompactContinueTiming.Quiet"/> after the compaction finished.</summary>
        Quiet,

        /// <summary>The agent was still working at <see cref="CompactContinueTiming.MaxIdleWait"/>; typed anyway, and the verify step decides.</summary>
        GaveUpWaiting,
    }

    /// <summary>Time limits for <see cref="CompactContinueSequencer"/>.</summary>
    internal sealed class CompactContinueTiming
    {
        /// <summary>Minimum wait after the compaction finished, as the post-/clear inject waits for the session to settle.</summary>
        public TimeSpan Settle { get; init; } = TimeSpan.FromMilliseconds(1500);

        /// <summary>
        /// No main-thread row at all for this long after the compaction finished counts as idle. The
        /// common case: <c>/compact</c> runs after the requesting turn ended, so the TURN_END came
        /// BEFORE the compaction and none follows it.
        /// </summary>
        public TimeSpan Quiet { get; init; } = TimeSpan.FromSeconds(4);

        /// <summary>Longest wait for a working agent to end its turn before typing anyway.</summary>
        public TimeSpan MaxIdleWait { get; init; } = TimeSpan.FromMinutes(2);

        /// <summary>How long after typing (or after the second Enter) a turn must show activity.</summary>
        public TimeSpan Verify { get; init; } = TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// The idle and verify decisions for the post-compaction continue prompt (ticket c0ebcfd4). Pure:
    /// it is told what was observed and when, and answers what to do next.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why wait for idle.</b> A prompt typed while Claude Code is busy was seen to sit in the input
    /// box unsubmitted (Charlie's and Frank's panes, 2026-10-19/20). So nothing is typed while the
    /// agent's main thread is working.
    /// </para>
    /// <para>
    /// <b>Idle evidence</b>, from the activity feed the plugin's activity hook writes: a
    /// <c>TURN_END</c> with no later work, or no main-thread row at all for
    /// <see cref="CompactContinueTiming.Quiet"/>. Work seen without a later <c>TURN_END</c> means
    /// busy, which waits up to <see cref="CompactContinueTiming.MaxIdleWait"/>.
    /// </para>
    /// <para>
    /// <b>Verify.</b> After typing, any main-thread row (a tool or a <c>TURN_END</c>) proves a turn ran.
    /// If none arrives within <see cref="CompactContinueTiming.Verify"/>, Enter is sent ONCE more, and
    /// after a second silent window the result is reported as not verified. Never a third Enter.
    /// A spare Enter on an empty prompt does nothing in Claude Code; a model that thinks longer than
    /// the window before its first tool gets that harmless extra Enter.
    /// </para>
    /// <para>
    /// Times are when MT OBSERVED a row, not the row's own timestamp: rows are written by another
    /// process, whose clock this one does not control (the same reason
    /// <see cref="AgentActivityWatcher"/> watermarks by id).
    /// </para>
    /// </remarks>
    internal sealed class CompactContinueSequencer
    {
        private readonly DateTime _finishedAtUtc;
        private readonly CompactContinueTiming _timing;

        private DateTime? _lastTurnEndUtc;
        private DateTime? _lastWorkUtc;
        private DateTime? _typedAtUtc;
        private DateTime? _enterResentAtUtc;
        private bool _activitySinceTyped;
        private bool _done;

        /// <summary>Starts the sequence at the moment MT learned the compaction finished.</summary>
        public CompactContinueSequencer(DateTime finishedAtUtc, CompactContinueTiming timing = null)
        {
            _finishedAtUtc = finishedAtUtc;
            _timing = timing ?? new CompactContinueTiming();
        }

        /// <summary>Why the prompt was typed, once it has been.</summary>
        public CompactContinueTypeReason TypeReason { get; private set; }

        /// <summary>Records a main-thread row seen at <paramref name="observedAtUtc"/>.</summary>
        public void Observe(CompactContinueSignal signal, DateTime observedAtUtc)
        {
            if (_typedAtUtc != null)
            {
                _activitySinceTyped = true;
                return;
            }

            if (signal == CompactContinueSignal.TurnEnded)
            {
                _lastTurnEndUtc = observedAtUtc;
            }
            else
            {
                _lastWorkUtc = observedAtUtc;
            }
        }

        /// <summary>Records that the prompt has been typed (and its Enter sent) at <paramref name="nowUtc"/>.</summary>
        public void NoteTyped(DateTime nowUtc) => _typedAtUtc = nowUtc;

        /// <summary>Records that the one extra Enter was sent at <paramref name="nowUtc"/>.</summary>
        public void NoteEnterResent(DateTime nowUtc) => _enterResentAtUtc = nowUtc;

        /// <summary>What to do at <paramref name="nowUtc"/>.</summary>
        public CompactContinueStep Next(DateTime nowUtc)
        {
            if (_done)
            {
                return _activitySinceTyped ? CompactContinueStep.Verified : CompactContinueStep.NotVerified;
            }

            if (_typedAtUtc is DateTime typedAt)
            {
                if (_activitySinceTyped)
                {
                    _done = true;
                    return CompactContinueStep.Verified;
                }

                if (_enterResentAtUtc is DateTime resentAt)
                {
                    if (nowUtc - resentAt >= _timing.Verify)
                    {
                        _done = true;
                        return CompactContinueStep.NotVerified;
                    }

                    return CompactContinueStep.Wait;
                }

                return nowUtc - typedAt >= _timing.Verify ? CompactContinueStep.SendEnterAgain : CompactContinueStep.Wait;
            }

            TimeSpan sinceFinished = nowUtc - _finishedAtUtc;
            if (sinceFinished < _timing.Settle)
            {
                return CompactContinueStep.Wait;
            }

            bool busy = _lastWorkUtc is DateTime work && (_lastTurnEndUtc is not DateTime end || work > end);

            if (!busy && _lastTurnEndUtc != null)
            {
                TypeReason = CompactContinueTypeReason.TurnEnded;
                return CompactContinueStep.Type;
            }

            if (_lastWorkUtc == null && _lastTurnEndUtc == null && sinceFinished >= _timing.Quiet)
            {
                TypeReason = CompactContinueTypeReason.Quiet;
                return CompactContinueStep.Type;
            }

            if (sinceFinished >= _timing.MaxIdleWait)
            {
                TypeReason = CompactContinueTypeReason.GaveUpWaiting;
                return CompactContinueStep.Type;
            }

            return CompactContinueStep.Wait;
        }
    }

    /// <summary>
    /// Drives <see cref="CompactContinueSequencer"/> from the activity feed for one pane (ticket c0ebcfd4).
    /// Everything it touches is passed in, so it holds no reference to a form or a terminal.
    /// </summary>
    internal sealed class CompactContinueRunner
    {
        /// <summary>The prompt typed into the pane once it is idle.</summary>
        internal const string ContinuePrompt =
            "Compaction finished. Continue where you left off; your continuation notes on the active task are the authoritative record.";

        private readonly IActivityFeedReader _feed;
        private readonly string _agentName;
        private readonly Func<bool> _paneAlive;
        private readonly Func<string, Task> _type;
        private readonly Func<Task<bool>> _sendEnter;
        private readonly Action<string> _log;
        private readonly Func<DateTime> _utcNow;
        private readonly Func<TimeSpan, Task> _delay;
        private readonly CompactContinueTiming _timing;

        /// <summary>Poll interval for the activity feed.</summary>
        internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

        public CompactContinueRunner(
            IActivityFeedReader feed,
            string agentName,
            Func<bool> paneAlive,
            Func<string, Task> type,
            Func<Task<bool>> sendEnter,
            Action<string> log,
            Func<DateTime> utcNow = null,
            Func<TimeSpan, Task> delay = null,
            CompactContinueTiming timing = null)
        {
            _feed = feed;
            _agentName = agentName ?? throw new ArgumentNullException(nameof(agentName));
            _paneAlive = paneAlive ?? throw new ArgumentNullException(nameof(paneAlive));
            _type = type ?? throw new ArgumentNullException(nameof(type));
            _sendEnter = sendEnter ?? throw new ArgumentNullException(nameof(sendEnter));
            _log = log ?? (_ => { });
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _delay = delay ?? (t => Task.Delay(t));
            _timing = timing ?? new CompactContinueTiming();
        }

        /// <summary>Runs the sequence to its end. Returns the final step (Verified or NotVerified), or Wait if the pane went away.</summary>
        public async Task<CompactContinueStep> RunAsync()
        {
            var sequencer = new CompactContinueSequencer(_utcNow(), _timing);
            long watermark = ReadWatermark();

            while (true)
            {
                if (!_paneAlive())
                {
                    _log($"compact-continue for '{_agentName}': pane gone, stopped");
                    return CompactContinueStep.Wait;
                }

                watermark = Drain(watermark, sequencer);
                CompactContinueStep step = sequencer.Next(_utcNow());

                switch (step)
                {
                    case CompactContinueStep.Type:
                        _log($"compact-continue for '{_agentName}': idle ({sequencer.TypeReason}), typing the continue prompt");
                        await _type(ContinuePrompt);
                        sequencer.NoteTyped(_utcNow());
                        break;

                    case CompactContinueStep.SendEnterAgain:
                        bool sent = await _sendEnter();
                        _log($"compact-continue for '{_agentName}': no turn started after typing; sent Enter once more (sent={sent})");
                        sequencer.NoteEnterResent(_utcNow());
                        break;

                    case CompactContinueStep.Verified:
                        _log($"compact-continue for '{_agentName}': a turn started");
                        return step;

                    case CompactContinueStep.NotVerified:
                        _log($"compact-continue for '{_agentName}': no turn started after the second Enter; giving up");
                        return step;
                }

                await _delay(PollInterval);
            }
        }

        private long ReadWatermark()
        {
            if (_feed == null) return 0;

            try
            {
                return _feed.GetMaxActivityId();
            }
            catch (Exception ex)
            {
                // Unreadable feed: the sequencer sees no rows, so it types on the quiet edge and the
                // verify step then sends one extra Enter at most.
                _log($"compact-continue for '{_agentName}': activity feed unreadable ({ex.Message}); relying on the quiet window");
                return long.MaxValue;
            }
        }

        private long Drain(long watermark, CompactContinueSequencer sequencer)
        {
            if (_feed == null || watermark == long.MaxValue) return watermark;

            try
            {
                List<ActivityFeedEntry> rows = _feed.GetActivitiesAfterId(watermark, 200);
                if (rows == null) return watermark;

                DateTime now = _utcNow();
                foreach (ActivityFeedEntry row in rows)
                {
                    if (row == null) continue;
                    if (row.Id > watermark) watermark = row.Id;

                    if (Classify(row, _agentName) is CompactContinueSignal signal)
                    {
                        sequencer.Observe(signal, now);
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"compact-continue for '{_agentName}': activity read failed ({ex.Message}); will retry");
            }

            return watermark;
        }

        /// <summary>
        /// What a row says about <paramref name="agentName"/>'s main thread, or null when it says nothing:
        /// another agent's row, a subagent's row (the same rule <see cref="AgentActivityWatcher"/> uses),
        /// or a row type that is not activity.
        /// </summary>
        internal static CompactContinueSignal? Classify(ActivityFeedEntry row, string agentName)
        {
            // Names compared as the broker compares terminal names: ordinal-ignore-case, untrimmed.
            if (!string.Equals(row.Actor, agentName, StringComparison.OrdinalIgnoreCase)) return null;
            if (AgentActivityWatcher.LooksLikeSubagent(row.DetailsJson)) return null;

            switch ((row.ActivityType ?? string.Empty).ToUpperInvariant())
            {
                case "TURN_END":
                    return CompactContinueSignal.TurnEnded;
                case "TOOL_START":
                case "TOOL_COMPLETE":
                case "TOOL_FAILED":
                case "TOOL_QUIET":
                case "BUILD_SUCCEEDED":
                case "BUILD_FAILED":
                    return CompactContinueSignal.Working;
                default:
                    return null;
            }
        }
    }
}
