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
    /// <b>Armed</b> when MT submits an agent-requested <c>/compact</c>, and <b>disarmed</b> again if that
    /// submission fails, so a request that never reached the pane cannot be claimed by a later one.
    /// </para>
    /// <para>
    /// <b>Consumed only by a MANUAL compaction.</b> SessionStart(compact) fires for manual and automatic
    /// compactions alike and does not say which, so the plugin's PreCompact hook reports the trigger
    /// first (<see cref="NoteCompactionStarting"/>). This matters exactly when the feature is used: an
    /// agent asks to compact because its context is nearly full, its <c>/compact</c> waits for the turn
    /// to end, and an automatic compaction can run first, mid-turn. That one must not take the request,
    /// or the continue prompt is typed while the requested <c>/compact</c> is running and nothing follows
    /// it. An automatic compaction leaves the request armed for the manual one still to come.
    /// </para>
    /// <para>
    /// Not proof of WHICH manual compaction: one the Owner types within <see cref="Expiry"/> of a
    /// request whose <c>/compact</c> was submitted but never ran would claim it. The REST routes are
    /// local and unauthenticated like every MT route (Owner decision), so this is a convenience
    /// convention, not a security boundary.
    /// </para>
    /// <para>One gate per pane, held by the pane's <c>TerminalControl</c>. Locked: nothing guarantees
    /// that every caller stays on the UI thread.</para>
    /// </remarks>
    internal sealed class CompactContinueGate
    {
        /// <summary>How long an armed request stays valid. A compaction that has not finished by then is not this one.</summary>
        internal static readonly TimeSpan Expiry = TimeSpan.FromMinutes(10);

        private readonly object _lock = new object();
        private DateTime? _armedAtUtc;
        private bool _manualCompactionStarted;

        /// <summary>Records that the pane's next manual compaction should be followed by a continue prompt.</summary>
        public void Arm(DateTime nowUtc)
        {
            lock (_lock)
            {
                _armedAtUtc = nowUtc;
                _manualCompactionStarted = false;
            }
        }

        /// <summary>Withdraws the request: its <c>/compact</c> was never submitted.</summary>
        public void Disarm()
        {
            lock (_lock)
            {
                _armedAtUtc = null;
                _manualCompactionStarted = false;
            }
        }

        /// <summary>The plugin's PreCompact hook: a compaction is starting, manual or automatic.</summary>
        public void NoteCompactionStarting(bool manual)
        {
            lock (_lock)
            {
                if (_armedAtUtc != null)
                {
                    _manualCompactionStarted = manual;
                }
            }
        }

        /// <summary>
        /// True at most once per <see cref="Arm"/>: only within <see cref="Expiry"/>, and only when the
        /// compaction that just finished was reported as manual. An expired request is cleared; a request
        /// met by an automatic compaction stays armed for the manual one.
        /// </summary>
        public bool TryConsume(DateTime nowUtc)
        {
            lock (_lock)
            {
                if (_armedAtUtc is not DateTime armedAt)
                {
                    return false;
                }

                TimeSpan age = nowUtc - armedAt;
                if (age < TimeSpan.Zero || age > Expiry)
                {
                    _armedAtUtc = null;
                    _manualCompactionStarted = false;
                    return false;
                }

                if (!_manualCompactionStarted)
                {
                    return false;
                }

                _armedAtUtc = null;
                _manualCompactionStarted = false;
                return true;
            }
        }
    }

    /// <summary>What a main-thread activity row says about the agent.</summary>
    internal enum CompactContinueSignal
    {
        /// <summary>A <c>TURN_END</c> row: the turn is over and the prompt is free.</summary>
        TurnEnded,

        /// <summary>Any other main-thread row (a tool or a build): the agent is working.</summary>
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

        /// <summary>Typed, but no turn was seen (or it could not be seen safely). Finished without another key; logged.</summary>
        NotVerified,

        /// <summary>Never typed: idle could not be established. Finished; logged with <see cref="CompactContinueSequencer.GiveUpReason"/>.</summary>
        GaveUp,
    }

    /// <summary>Why the prompt was typed when it was. Logged, so a live test can tell which edge fired.</summary>
    internal enum CompactContinueTypeReason
    {
        /// <summary>Not typed.</summary>
        None,

        /// <summary>A <c>TURN_END</c> arrived after the compaction finished, with no work after it.</summary>
        TurnEnded,

        /// <summary>No main-thread row for <see cref="CompactContinueTiming.Quiet"/> of uninterrupted, successful feed reads.</summary>
        Quiet,
    }

    /// <summary>Why nothing was typed.</summary>
    internal enum CompactContinueGiveUpReason
    {
        /// <summary>Not given up.</summary>
        None,

        /// <summary>The agent was still working at <see cref="CompactContinueTiming.MaxIdleWait"/>, e.g. blocked on a permission prompt.</summary>
        StillWorking,

        /// <summary>The activity feed could not be read long enough to prove the pane idle.</summary>
        FeedUnavailable,

        /// <summary>Someone typed in the pane after the <c>/compact</c> was submitted; their input is not ours to submit.</summary>
        OwnerTyping,
    }

    /// <summary>Time limits for <see cref="CompactContinueSequencer"/>.</summary>
    internal sealed class CompactContinueTiming
    {
        /// <summary>Minimum wait after the compaction finished, as the post-/clear inject waits for the session to settle.</summary>
        public TimeSpan Settle { get; init; } = TimeSpan.FromMilliseconds(1500);

        /// <summary>
        /// No main-thread row for this long, over uninterrupted successful reads, counts as idle. The
        /// common case: a manual <c>/compact</c> runs only between turns, and the turn's TURN_END came
        /// BEFORE the compaction, so none follows it.
        /// </summary>
        public TimeSpan Quiet { get; init; } = TimeSpan.FromSeconds(4);

        /// <summary>Longest wait for idle. Reached without it, nothing is typed.</summary>
        public TimeSpan MaxIdleWait { get; init; } = TimeSpan.FromMinutes(5);

        /// <summary>How long after typing (or after the second Enter) a turn must show activity.</summary>
        public TimeSpan Verify { get; init; } = TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// The idle and verify decisions for the post-compaction continue prompt (ticket c0ebcfd4). Pure:
    /// it is told what was observed and when, and answers what to do next. It FAILS CLOSED: whenever
    /// idle cannot be shown, nothing is typed, and an extra Enter is sent only when the evidence that
    /// nothing started is complete.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why wait for idle.</b> A prompt typed while Claude Code is busy was seen to sit in the input
    /// box unsubmitted (Charlie's and Frank's panes, 2026-10-19/20). Worse, a turn blocked on a
    /// permission prompt shows a <c>TOOL_START</c> and no <c>TURN_END</c>, and an Enter typed there picks
    /// the prompt's default. So a working agent is never typed into, however long it works.
    /// </para>
    /// <para>
    /// <b>Idle evidence</b>, from the activity feed the plugin's activity hook writes: a
    /// <c>TURN_END</c> with no later work, or no main-thread row for <see cref="CompactContinueTiming.Quiet"/>
    /// of uninterrupted successful reads (a failed read restarts that window; a feed that cannot be
    /// read never counts as quiet). Either way the latest read must have succeeded and drained the
    /// feed to its end, so no later row can be hiding behind a failure or a full batch. Without either by <see cref="CompactContinueTiming.MaxIdleWait"/>,
    /// it gives up.
    /// </para>
    /// <para>
    /// <b>Verify.</b> After typing, any main-thread row proves a turn ran. If none arrives within
    /// <see cref="CompactContinueTiming.Verify"/>, Enter is sent ONCE more, but only if the feed was read
    /// successfully throughout and nobody typed in the pane since: a spare Enter on an empty prompt
    /// does nothing, on someone's half-typed text it submits it. Never a third Enter.
    /// </para>
    /// <para>
    /// Times are when MT OBSERVED a row, not the row's own timestamp: rows are written by another
    /// process, whose clock this one does not control (the same reason
    /// <see cref="AgentActivityWatcher"/> watermarks by id).
    /// </para>
    /// <para>
    /// Not covered: a turn that is running but has written no row for <see cref="CompactContinueTiming.Quiet"/>
    /// (the model thinking before its first tool) looks quiet. After a manual compaction no turn is
    /// running unless something submitted one in those seconds.
    /// </para>
    /// </remarks>
    internal sealed class CompactContinueSequencer
    {
        private readonly DateTime _finishedAtUtc;
        private readonly DateTime _ownerInputSinceUtc;
        private readonly CompactContinueTiming _timing;

        // The LAST main-thread signal in feed (row id) order. Not times: rows read in one poll share an
        // observation time, so "work after the turn end" must come from order, not from a comparison
        // of equal timestamps (found by Runner_drains_a_backlog_past_one_batch_before_deciding).
        private CompactContinueSignal? _lastSignal;
        private DateTime? _healthySinceUtc;
        private DateTime? _lastFeedFailureUtc;
        private DateTime? _lastUserInputUtc;
        private DateTime? _typedAtUtc;
        private DateTime? _enterResentAtUtc;
        private bool _activitySinceTyped;
        private CompactContinueStep? _final;

        /// <summary>Starts the sequence at the moment MT learned the compaction finished.</summary>
        /// <param name="finishedAtUtc">When MT learned the compaction finished.</param>
        /// <param name="timing">Time limits; defaults when null.</param>
        /// <param name="ownerInputSinceUtc">
        /// Keystrokes after this mean someone is using the input line. Normally shortly after the
        /// <c>/compact</c> was submitted, because the input line accepts typing for the whole compaction;
        /// <paramref name="finishedAtUtc"/> when that time is unknown.
        /// </param>
        public CompactContinueSequencer(DateTime finishedAtUtc, CompactContinueTiming timing = null, DateTime? ownerInputSinceUtc = null)
        {
            _finishedAtUtc = finishedAtUtc;
            _ownerInputSinceUtc = ownerInputSinceUtc ?? finishedAtUtc;
            _timing = timing ?? new CompactContinueTiming();
        }

        /// <summary>Why the prompt was typed, once it has been.</summary>
        public CompactContinueTypeReason TypeReason { get; private set; }

        /// <summary>Why nothing was typed, once the sequence has given up.</summary>
        public CompactContinueGiveUpReason GiveUpReason { get; private set; }

        /// <summary>Records a main-thread row, in feed order, seen at <paramref name="observedAtUtc"/>.</summary>
        public void Observe(CompactContinueSignal signal, DateTime observedAtUtc)
        {
            if (_typedAtUtc != null)
            {
                _activitySinceTyped = true;
                return;
            }

            _lastSignal = signal;
        }

        /// <summary>Records whether a read of the activity feed succeeded.</summary>
        public void NoteFeedRead(bool ok, DateTime atUtc)
        {
            if (ok)
            {
                _healthySinceUtc ??= atUtc;
            }
            else
            {
                _healthySinceUtc = null;
                _lastFeedFailureUtc = atUtc;
            }
        }

        /// <summary>Records the latest keystroke typed into the pane by a person, if any.</summary>
        public void NoteUserInput(DateTime? atUtc)
        {
            if (atUtc is DateTime at && (_lastUserInputUtc == null || at > _lastUserInputUtc))
            {
                _lastUserInputUtc = at;
            }
        }

        /// <summary>Records that the prompt has been typed (and its Enter sent) at <paramref name="nowUtc"/>.</summary>
        public void NoteTyped(DateTime nowUtc) => _typedAtUtc = nowUtc;

        /// <summary>Records that the one extra Enter was sent at <paramref name="nowUtc"/>.</summary>
        public void NoteEnterResent(DateTime nowUtc) => _enterResentAtUtc = nowUtc;

        /// <summary>What to do at <paramref name="nowUtc"/>.</summary>
        public CompactContinueStep Next(DateTime nowUtc)
        {
            if (_final is CompactContinueStep final)
            {
                return final;
            }

            return _typedAtUtc is DateTime typedAt ? NextAfterTyping(nowUtc, typedAt) : NextBeforeTyping(nowUtc);
        }

        private CompactContinueStep NextBeforeTyping(DateTime nowUtc)
        {
            if (_lastUserInputUtc is DateTime input && input > _ownerInputSinceUtc)
            {
                return GiveUp(CompactContinueGiveUpReason.OwnerTyping);
            }

            TimeSpan sinceFinished = nowUtc - _finishedAtUtc;
            if (sinceFinished < _timing.Settle)
            {
                return CompactContinueStep.Wait;
            }

            bool busy = _lastSignal == CompactContinueSignal.Working;

            // Every way to type needs the LATEST read to have succeeded: a TURN_END seen before a failed
            // read says nothing about work written during the outage (pipeline run 2).
            if (_healthySinceUtc == null)
            {
                return sinceFinished >= _timing.MaxIdleWait
                    ? GiveUp(busy ? CompactContinueGiveUpReason.StillWorking : CompactContinueGiveUpReason.FeedUnavailable)
                    : CompactContinueStep.Wait;
            }

            if (_lastSignal == CompactContinueSignal.TurnEnded)
            {
                TypeReason = CompactContinueTypeReason.TurnEnded;
                return CompactContinueStep.Type;
            }

            if (_lastSignal == null && _healthySinceUtc is DateTime healthySince)
            {
                DateTime quietFrom = healthySince > _finishedAtUtc ? healthySince : _finishedAtUtc;
                if (nowUtc - quietFrom >= _timing.Quiet)
                {
                    TypeReason = CompactContinueTypeReason.Quiet;
                    return CompactContinueStep.Type;
                }
            }

            if (sinceFinished >= _timing.MaxIdleWait)
            {
                return GiveUp(busy ? CompactContinueGiveUpReason.StillWorking : CompactContinueGiveUpReason.FeedUnavailable);
            }

            return CompactContinueStep.Wait;
        }

        private CompactContinueStep NextAfterTyping(DateTime nowUtc, DateTime typedAt)
        {
            if (_activitySinceTyped)
            {
                _final = CompactContinueStep.Verified;
                return CompactContinueStep.Verified;
            }

            DateTime windowStart = _enterResentAtUtc ?? typedAt;
            if (nowUtc - windowStart < _timing.Verify)
            {
                return CompactContinueStep.Wait;
            }

            // Nothing seen for a whole window. A second Enter only when that silence is trustworthy
            // and the input line is still ours.
            bool feedFailedSinceTyping = _lastFeedFailureUtc is DateTime failed && failed >= typedAt;
            bool someoneTypedSince = _lastUserInputUtc is DateTime input && input >= typedAt;
            if (_enterResentAtUtc != null || feedFailedSinceTyping || someoneTypedSince)
            {
                _final = CompactContinueStep.NotVerified;
                return CompactContinueStep.NotVerified;
            }

            return CompactContinueStep.SendEnterAgain;
        }

        private CompactContinueStep GiveUp(CompactContinueGiveUpReason reason)
        {
            GiveUpReason = reason;
            _final = CompactContinueStep.GaveUp;
            return CompactContinueStep.GaveUp;
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

        /// <summary>Poll interval for the activity feed.</summary>
        internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

        /// <summary>Rows per feed read.</summary>
        internal const int BatchSize = 200;

        /// <summary>Reads per poll before an undrained feed counts as not readable this poll.</summary>
        internal const int MaxBatchesPerPoll = 20;

        private readonly IActivityFeedReader _feed;
        private readonly string _agentName;
        private readonly Func<bool> _paneAlive;
        private readonly Func<DateTime?> _lastUserInput;
        private readonly Func<string, Task> _type;
        private readonly Func<Task<bool>> _sendEnter;
        private readonly Action<string> _log;
        private readonly Func<DateTime> _utcNow;
        private readonly Func<TimeSpan, Task> _delay;
        private readonly CompactContinueTiming _timing;
        private readonly DateTime? _ownerInputSinceUtc;

        public CompactContinueRunner(
            IActivityFeedReader feed,
            string agentName,
            Func<bool> paneAlive,
            Func<DateTime?> lastUserInput,
            Func<string, Task> type,
            Func<Task<bool>> sendEnter,
            Action<string> log,
            Func<DateTime> utcNow = null,
            Func<TimeSpan, Task> delay = null,
            CompactContinueTiming timing = null,
            DateTime? ownerInputSinceUtc = null)
        {
            _feed = feed;
            _ownerInputSinceUtc = ownerInputSinceUtc;
            _agentName = agentName ?? throw new ArgumentNullException(nameof(agentName));
            _paneAlive = paneAlive ?? throw new ArgumentNullException(nameof(paneAlive));
            _lastUserInput = lastUserInput ?? (() => null);
            _type = type ?? throw new ArgumentNullException(nameof(type));
            _sendEnter = sendEnter ?? throw new ArgumentNullException(nameof(sendEnter));
            _log = log ?? (_ => { });
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _delay = delay ?? (t => Task.Delay(t));
            _timing = timing ?? new CompactContinueTiming();
        }

        /// <summary>
        /// Runs the sequence to its end. Returns the final step (Verified, NotVerified or GaveUp), or
        /// Wait if the pane went away.
        /// </summary>
        public async Task<CompactContinueStep> RunAsync()
        {
            if (_feed == null)
            {
                // Without the feed idle can never be shown, and typing blind is what this class exists to avoid.
                _log($"compact-continue for '{_agentName}': no activity feed; not typing");
                return CompactContinueStep.GaveUp;
            }

            var sequencer = new CompactContinueSequencer(_utcNow(), _timing, _ownerInputSinceUtc);
            long? watermark = null;

            while (true)
            {
                if (!_paneAlive())
                {
                    _log($"compact-continue for '{_agentName}': pane gone, stopped");
                    return CompactContinueStep.Wait;
                }

                watermark = Read(watermark, sequencer);
                sequencer.NoteUserInput(_lastUserInput());
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
                        _log($"compact-continue for '{_agentName}': typed, but no turn was seen; no further keys sent");
                        return step;

                    case CompactContinueStep.GaveUp:
                        _log($"compact-continue for '{_agentName}': not typing ({sequencer.GiveUpReason})");
                        return step;
                }

                await _delay(PollInterval);
            }
        }

        /// <summary>
        /// One poll. The first successful read only sets the watermark, so nothing written before the
        /// compaction finished is counted. A failure keeps the watermark, so no row is lost, and tells
        /// the sequencer, which restarts its quiet window.
        /// </summary>
        private long? Read(long? watermark, CompactContinueSequencer sequencer)
        {
            try
            {
                if (watermark is not long after)
                {
                    long max = _feed.GetMaxActivityId();
                    sequencer.NoteFeedRead(true, _utcNow());
                    return max;
                }

                DateTime now = _utcNow();
                for (int batch = 0; batch < MaxBatchesPerPoll; batch++)
                {
                    List<ActivityFeedEntry> rows = _feed.GetActivitiesAfterId(after, BatchSize) ?? new List<ActivityFeedEntry>();

                    foreach (ActivityFeedEntry row in rows)
                    {
                        if (row == null) continue;
                        if (row.Id > after) after = row.Id;

                        if (Classify(row, _agentName) is CompactContinueSignal signal)
                        {
                            sequencer.Observe(signal, now);
                        }
                    }

                    if (rows.Count < BatchSize)
                    {
                        // Drained to the end: this read can vouch for "nothing later".
                        sequencer.NoteFeedRead(true, now);
                        return after;
                    }
                }

                // Still full after the last batch: rows remain unread, so this read cannot vouch for idle.
                sequencer.NoteFeedRead(false, now);
                return after;
            }
            catch (Exception ex)
            {
                sequencer.NoteFeedRead(false, _utcNow());
                _log($"compact-continue for '{_agentName}': activity read failed ({ex.Message}); will retry");
                return watermark;
            }
        }

        /// <summary>
        /// What a row says about <paramref name="agentName"/>'s main thread, or null when it says nothing:
        /// another agent's row, a subagent's row (the same rule <see cref="AgentActivityWatcher"/> uses),
        /// or a row type that is not tool or build activity.
        /// </summary>
        /// <remarks>
        /// "Working" is every tool and build type <see cref="ActivityFeedService"/> defines, read from its
        /// own sets so the two cannot drift. Unlike the attention rail, TOOL_START counts here: for this
        /// question (is a turn running?) a tool that has started is the strongest evidence there is.
        /// </remarks>
        internal static CompactContinueSignal? Classify(ActivityFeedEntry row, string agentName)
        {
            // Names compared as the broker compares terminal names: ordinal-ignore-case, untrimmed.
            if (!string.Equals(row.Actor, agentName, StringComparison.OrdinalIgnoreCase)) return null;
            if (AgentActivityWatcher.LooksLikeSubagent(row.DetailsJson)) return null;

            string type = row.ActivityType ?? string.Empty;
            if (string.Equals(type, "TURN_END", StringComparison.OrdinalIgnoreCase)) return CompactContinueSignal.TurnEnded;

            if (ActivityFeedService.ValidToolTypes.Contains(type)
                || ActivityFeedService.QuietToolTypes.Contains(type)
                || ActivityFeedService.ValidBuildTypes.Contains(type))
            {
                return CompactContinueSignal.Working;
            }

            return null;
        }
    }
}
