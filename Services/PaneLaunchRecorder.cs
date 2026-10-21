using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;

namespace MultiTerminal.Services
{
    /// <summary>
    /// One row of <c>pane_launches</c> (task 7d20e94c): one launch of one pane. A resume keeps the
    /// Claude session id, so the same <see cref="SessionId"/> can appear on several rows.
    /// </summary>
    public sealed class PaneLaunchRecord
    {
        public string Id { get; set; }
        public string MtRunId { get; set; }
        public string DocId { get; set; }
        public string TerminalName { get; set; }
        public string ProjectId { get; set; }
        public string Cwd { get; set; }
        public string Kind { get; set; }
        public string SessionId { get; set; }
        public string ResumedFromSessionId { get; set; }
        public string SpawnerLaunchId { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }

        /// <summary>One of <see cref="PaneLaunchEndReason"/>. NULL with <see cref="EndedAt"/> NULL = still open, or MT crashed.</summary>
        public string EndReason { get; set; }

        /// <summary>
        /// Launch order (pipeline Run 1): <c>MAX(seq) + 1</c> at insert, so unique and increasing. Every
        /// "newest" and "later" decision uses it, never <see cref="StartedAt"/>, which can tie.
        /// </summary>
        public long Seq { get; set; }
    }

    /// <summary>The values of <c>pane_launches.end_reason</c>.</summary>
    public static class PaneLaunchEndReason
    {
        /// <summary>The Owner closed the pane.</summary>
        public const string PaneClosed = "pane_closed";

        /// <summary>The spawner closed the helper through <c>close_helper</c>.</summary>
        public const string CloseHelper = "close_helper";

        /// <summary>The pane's process exited while the pane stayed open. Counts as closed (Owner decision).</summary>
        public const string ProcessExit = "process_exit";

        /// <summary>MT exited with the pane open. The only reason that still counts as open.</summary>
        public const string MtExit = "mt_exit";

        /// <summary>
        /// The pane's process moved to ANOTHER Claude session (<c>/clear</c>, a fork) and a continuation
        /// row now carries the new one (pipeline Run 1). Counts as closed for the OLD session: no process
        /// runs it any more. The row's children stay linked to it, so a later resume of the old session
        /// still finds the helpers that session spawned.
        /// </summary>
        public const string SessionChanged = "session_changed";

        /// <summary>
        /// The Owner closed the helper's PM pane and answered Yes to "also close its helpers" (task
        /// 7d20e94c item 8). MT closed the helper's pane itself, so its process is gone, but the helper
        /// is RESUMABLE with its PM exactly as after <see cref="MtExit"/>: <see cref="PaneLaunchRecorder.GetOpenHelpers"/>
        /// keeps it, and <see cref="PaneLaunchRecorder.ClassifyPriorLaunch"/> answers
        /// <see cref="PriorLaunchState.ClosedWithPm"/>, which waives the recent-write flag.
        /// </summary>
        public const string ClosedWithPm = "closed_with_pm";
    }

    /// <summary>
    /// What the launch record says about the process that last ran a session (task 7d20e94c), for the
    /// "may be open elsewhere" rule. Decided from the session's NEWEST launch row only.
    /// </summary>
    public enum PriorLaunchState
    {
        /// <summary>No launch row (never run by an MT with the record), or the record could not be read.</summary>
        NoRecord = 0,

        /// <summary>
        /// The newest launch belongs to a PREVIOUS MT run, is still open (that MT crashed) or ended
        /// <c>mt_exit</c>, AND that MT process is provably gone (pipeline Run 1): its panes died with it,
        /// so a recent transcript write was that process, not something outside MT.
        /// </summary>
        DiedWithPreviousRun = 1,

        /// <summary>The newest launch belongs to THIS MT run and has not ended: a pane is running it.</summary>
        OpenInCurrentRun = 2,

        /// <summary>Any other newest launch: ended this run, or closed (not by an MT exit) in an earlier one.</summary>
        Ended = 3,

        /// <summary>
        /// The newest launch belongs to a previous MT run and is open or <c>mt_exit</c>, but that MT
        /// process cannot be shown to be gone: it is still running, its identity was never recorded, or
        /// it could not be read (pipeline Run 1, fail-closed). The PM's recency flag applies as for
        /// <see cref="NoRecord"/>, and a helper is skipped as "may be open elsewhere".
        /// </summary>
        OpenElsewhere = 4,

        /// <summary>
        /// The newest launch ended <c>closed_with_pm</c> (task 7d20e94c item 8), in this run or an earlier
        /// one: MT closed that helper's pane itself when the Owner closed its PM, and disposing the pane
        /// terminates its process tree (the same Job Object kill a close_helper or a tab close relies on).
        /// So a transcript written seconds before is that process, not something outside MT: the recency
        /// flag is WAIVED, as for <see cref="DiedWithPreviousRun"/>. This run's live set still applies.
        /// </summary>
        ClosedWithPm = 5,

        /// <summary>
        /// The newest launch belongs to THIS MT run and ended <c>pane_closed</c>, <c>close_helper</c> or
        /// <c>process_exit</c>: this MT watched its process end (an exit) or ended it (disposing the pane
        /// terminates its process tree). A transcript written seconds before is that process, so the
        /// recency flag is WAIVED, as for <see cref="ClosedWithPm"/>; without this, a session closed a
        /// minute ago showed "may be open elsewhere". Not <c>session_changed</c>: that process still runs
        /// (under a new session) and stays <see cref="Ended"/>.
        /// </summary>
        ClosedInCurrentRun = 6,
    }

    /// <summary>An MT process as recorded in <c>mt_runs</c>: its id and its start time (UTC).</summary>
    public sealed class MtProcessIdentity
    {
        public MtProcessIdentity(int processId, DateTime startedAtUtc)
        {
            ProcessId = processId;
            StartedAtUtc = startedAtUtc.Kind == DateTimeKind.Utc ? startedAtUtc : startedAtUtc.ToUniversalTime();
        }

        public int ProcessId { get; }

        public DateTime StartedAtUtc { get; }
    }

    /// <summary>The answer of a process-liveness probe about a recorded <see cref="MtProcessIdentity"/>.</summary>
    public enum MtProcessLiveness
    {
        /// <summary>No process has that id, or one has it with a different start time (the id was reused), or it has exited.</summary>
        Gone = 0,

        /// <summary>A running process has that id and that start time.</summary>
        Alive = 1,

        /// <summary>It could not be told (access denied, an unexpected error).</summary>
        Unknown = 2,
    }

    /// <summary>
    /// The real process probe behind <see cref="PaneLaunchRecorder"/> (pipeline Run 1, task 7d20e94c).
    /// Only <see cref="MtProcessLiveness.Gone"/> lets a previous run's open launch count as dead, so
    /// every doubt answers <see cref="MtProcessLiveness.Unknown"/>.
    /// </summary>
    public static class MtProcessProbe
    {
        /// <summary>
        /// Start times are compared with this slack: <c>Process.StartTime</c> is the kernel's creation
        /// time converted to local time and back, which is exact in practice; the slack only absorbs
        /// rounding. A reused id belongs to a process created later, normally far more than this.
        /// </summary>
        internal static readonly TimeSpan StartTimeSlack = TimeSpan.FromSeconds(1);

        /// <summary>This process's id and start time, or null when they cannot be read.</summary>
        public static MtProcessIdentity Current()
        {
            try
            {
                using var self = Process.GetCurrentProcess();
                return new MtProcessIdentity(self.Id, self.StartTime.ToUniversalTime());
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception || ex is NotSupportedException)
            {
                return null;
            }
        }

        /// <summary>Whether the process <paramref name="recorded"/> describes is still running.</summary>
        public static MtProcessLiveness Probe(MtProcessIdentity recorded)
        {
            if (recorded == null) return MtProcessLiveness.Unknown;

            Process process;
            try
            {
                process = Process.GetProcessById(recorded.ProcessId);
            }
            catch (ArgumentException)
            {
                return MtProcessLiveness.Gone; // no process has that id
            }
            catch (Exception)
            {
                return MtProcessLiveness.Unknown;
            }

            using (process)
            {
                try
                {
                    DateTime started = process.StartTime.ToUniversalTime();
                    if ((started - recorded.StartedAtUtc).Duration() > StartTimeSlack)
                        return MtProcessLiveness.Gone; // the id now belongs to another process
                    return process.HasExited ? MtProcessLiveness.Gone : MtProcessLiveness.Alive;
                }
                catch (InvalidOperationException)
                {
                    return MtProcessLiveness.Gone; // exited between the lookup and the read
                }
                catch (Exception)
                {
                    return MtProcessLiveness.Unknown; // access denied (another user's process) and the like
                }
            }
        }
    }

    /// <summary>A helper session a PM session left open, as returned by <see cref="PaneLaunchRecorder.GetOpenHelpers"/>.</summary>
    public sealed class OpenHelper
    {
        public string SessionId { get; set; }
        public string TerminalName { get; set; }
        public string Cwd { get; set; }
        public string ProjectId { get; set; }
        public string Kind { get; set; }
        public DateTime LastStartedAt { get; set; }
    }

    /// <summary>
    /// Records pane launches in <c>pane_launches</c> and answers "which helpers did this PM session
    /// leave open" (task 7d20e94c). Never throws: every failure is logged and turned into a null,
    /// false or empty result, because every caller is on a UI or launch path.
    /// </summary>
    public sealed class PaneLaunchRecorder : IPaneLaunchSink
    {
        private const string LogSource = "PaneLaunchRecorder";

        private readonly TaskDatabase _db;
        private readonly Action<string> _logError;
        private readonly Func<DateTime> _utcNow;
        private readonly Func<MtProcessIdentity, MtProcessLiveness> _probe;

        /// <summary>
        /// Creates the recorder for this MT process and records the process's id and start time under
        /// its run id (pipeline Run 1). <paramref name="log"/> may be null.
        /// </summary>
        public PaneLaunchRecorder(TaskDatabase db, DebugLogService log)
            : this(db, log == null ? null : (Action<string>)(m => log.Error(LogSource, m)), null, null, MtProcessProbe.Current(), MtProcessProbe.Probe)
        {
        }

        /// <summary>Test seam: injectable error sink, run id and clock. Records no process, so its run is never provably gone.</summary>
        internal PaneLaunchRecorder(TaskDatabase db, Action<string> logError, string mtRunId, Func<DateTime> utcNow)
            : this(db, logError, mtRunId, utcNow, null, null)
        {
        }

        /// <summary>
        /// Test seam: also the process this run records (<paramref name="thisProcess"/>; null records
        /// none) and the liveness probe (null = <see cref="MtProcessProbe.Probe"/>).
        /// </summary>
        internal PaneLaunchRecorder(TaskDatabase db, Action<string> logError, string mtRunId, Func<DateTime> utcNow,
            MtProcessIdentity thisProcess, Func<MtProcessIdentity, MtProcessLiveness> probe)
        {
            _db = db;
            _logError = logError;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _probe = probe ?? MtProcessProbe.Probe;
            MtRunId = string.IsNullOrEmpty(mtRunId) ? Guid.NewGuid().ToString() : mtRunId;

            // No process (a test, or Process.StartTime unreadable): nothing is recorded, and this run's open
            // launches read as OpenElsewhere to the next run, the fail-closed direction.
            if (thisProcess == null)
                return;

            try
            {
                _db.InsertMtRun(MtRunId, thisProcess.ProcessId, thisProcess.StartedAtUtc, _utcNow());
            }
            catch (Exception ex)
            {
                Log($"Recording this MT run's process failed: {ex.Message}");
            }
        }

        /// <summary>A new GUID per MT process; every launch row this recorder writes carries it.</summary>
        public string MtRunId { get; }

        /// <summary>
        /// Writes a new open launch row. A resume's row carries the resumed session id from the start
        /// (pipeline Run 1): a resumed pane closed before the statusline reported its session would
        /// otherwise end with no session, and the older open row of that session would bring it back.
        /// The first adoption overwrites it if the session turns out to differ.
        /// </summary>
        /// <returns>The launch id, or null when the row could not be written.</returns>
        public string BeginLaunch(string docId, string name, string projectId, string cwd, string kind, string resumedFromSessionId)
        {
            try
            {
                var id = Guid.NewGuid().ToString();
                string resumed = string.IsNullOrEmpty(resumedFromSessionId) ? null : resumedFromSessionId;
                _db.InsertPaneLaunch(new PaneLaunchRecord
                {
                    Id = id,
                    MtRunId = MtRunId,
                    DocId = docId,
                    TerminalName = name,
                    ProjectId = projectId,
                    Cwd = cwd,
                    Kind = kind,
                    SessionId = resumed,
                    ResumedFromSessionId = resumed,
                    StartedAt = _utcNow(),
                });
                return id;
            }
            catch (Exception ex)
            {
                Log($"BeginLaunch failed for '{name}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Records the Claude session id a launch is running. When <paramref name="sessionChangedInLaunch"/>
        /// (the pane moved from one session to another within this launch) and the row already carries
        /// another session, the row is ended <see cref="PaneLaunchEndReason.SessionChanged"/> and a
        /// continuation row carries the new one (see <see cref="TaskDatabase.AdoptPaneLaunchSession"/>).
        /// </summary>
        /// <returns>The launch id now carrying the session (the continuation's when one was made), or null.</returns>
        public string AdoptSession(string launchId, string sessionId, bool sessionChangedInLaunch = false)
        {
            if (string.IsNullOrEmpty(launchId) || string.IsNullOrEmpty(sessionId)) return null;
            try
            {
                return _db.AdoptPaneLaunchSession(
                    launchId,
                    sessionId,
                    sessionChangedInLaunch,
                    sessionChangedInLaunch ? Guid.NewGuid().ToString() : null,
                    _utcNow(),
                    PaneLaunchEndReason.SessionChanged);
            }
            catch (Exception ex)
            {
                Log($"AdoptSession failed for launch {launchId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Links a helper's launch to the launch of the pane that spawned it.</summary>
        public void LinkSpawner(string childLaunchId, string spawnerLaunchId)
        {
            if (string.IsNullOrEmpty(childLaunchId) || string.IsNullOrEmpty(spawnerLaunchId)) return;
            try
            {
                _db.SetPaneLaunchSpawner(childLaunchId, spawnerLaunchId);
            }
            catch (Exception ex)
            {
                Log($"LinkSpawner failed for launch {childLaunchId}: {ex.Message}");
            }
        }

        /// <summary>Ends a launch with one of <see cref="PaneLaunchEndReason"/>. An already-ended launch keeps its first reason.</summary>
        public void End(string launchId, string reason)
        {
            if (string.IsNullOrEmpty(launchId) || string.IsNullOrEmpty(reason)) return;
            try
            {
                _db.EndPaneLaunch(launchId, reason);
            }
            catch (Exception ex)
            {
                Log($"End failed for launch {launchId}: {ex.Message}");
            }
        }

        /// <summary>Marks every still-open launch of this MT run <see cref="PaneLaunchEndReason.MtExit"/>.</summary>
        public void EndAllForShutdown()
        {
            try
            {
                _db.EndOpenPaneLaunchesForRun(MtRunId, PaneLaunchEndReason.MtExit);
            }
            catch (Exception ex)
            {
                Log($"EndAllForShutdown failed: {ex.Message}");
            }
        }

        /// <summary>
        /// The helper sessions <paramref name="pmSessionId"/> left open, newest first, at most
        /// <paramref name="max"/> (Owner-approved rule, task 7d20e94c). "Newest" and "later" are by
        /// <see cref="PaneLaunchRecord.Seq"/> (pipeline Run 1), never by start time, which can tie.
        /// <list type="number">
        /// <item>Every launch of the PM session, including earlier resumes (a resume keeps the session id)
        /// and launch rows ended <see cref="PaneLaunchEndReason.SessionChanged"/> (the pane later moved
        /// to another session; the helpers it spawned while on this one are still this session's).</item>
        /// <item>Their direct child launches only (no helpers of helpers) that carry a session id other than the PM's.</item>
        /// <item>Per helper session, the newest child launch.</item>
        /// <item>Kept only while resumable: not ended (a crash), or ended by <see cref="PaneLaunchEndReason.MtExit"/>
        /// or <see cref="PaneLaunchEndReason.ClosedWithPm"/> (item 8: closed together with its PM).
        /// <c>pane_closed</c>, <c>close_helper</c>, <c>process_exit</c> and <c>session_changed</c> are closed.</item>
        /// <item>Dropped when any LATER launch of the same helper session, spawned by anyone, ended closed
        /// (a later <c>closed_with_pm</c> or <c>mt_exit</c> is not closed).</item>
        /// </list>
        /// </summary>
        public IReadOnlyList<OpenHelper> GetOpenHelpers(string pmSessionId, int max = 6)
        {
            if (string.IsNullOrEmpty(pmSessionId) || max <= 0) return Array.Empty<OpenHelper>();
            try
            {
                var pmLaunches = _db.GetPaneLaunchesBySession(pmSessionId);
                if (pmLaunches.Count == 0) return Array.Empty<OpenHelper>();

                var children = _db.GetChildLaunches(pmLaunches.Select(l => l.Id));
                var newestPerSession = children
                    .Where(c => !string.IsNullOrEmpty(c.SessionId)
                                && !string.Equals(c.SessionId, pmSessionId, StringComparison.Ordinal))
                    .GroupBy(c => c.SessionId, StringComparer.Ordinal)
                    .Select(g => g.OrderByDescending(c => c.Seq).First())
                    .Where(IsResumable)
                    .OrderByDescending(c => c.Seq)
                    .ToList();

                var result = new List<OpenHelper>();
                foreach (var child in newestPerSession)
                {
                    if (result.Count >= max) break;
                    bool closedLater = _db.GetPaneLaunchesBySession(child.SessionId)
                        .Any(l => l.Seq > child.Seq && !IsResumable(l));
                    if (closedLater) continue;

                    result.Add(new OpenHelper
                    {
                        SessionId = child.SessionId,
                        TerminalName = child.TerminalName,
                        Cwd = child.Cwd,
                        ProjectId = child.ProjectId,
                        Kind = child.Kind,
                        LastStartedAt = child.StartedAt,
                    });
                }
                return result;
            }
            catch (Exception ex)
            {
                Log($"GetOpenHelpers failed for session {pmSessionId}: {ex.Message}");
                return Array.Empty<OpenHelper>();
            }
        }

        /// <summary>
        /// <see cref="PriorLaunchState"/> of <paramref name="sessionId"/>, from its newest launch row,
        /// this recorder's <see cref="MtRunId"/>, and whether the MT process of a previous run is
        /// provably gone. A failed read is <see cref="PriorLaunchState.NoRecord"/>, which keeps the
        /// recency rule in force (the safe direction).
        /// </summary>
        public PriorLaunchState GetPriorLaunchState(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return PriorLaunchState.NoRecord;
            try
            {
                return ClassifyPriorLaunch(_db.GetPaneLaunchesBySession(sessionId), MtRunId, IsRunProvablyGone);
            }
            catch (Exception ex)
            {
                Log($"GetPriorLaunchState failed for session {sessionId}: {ex.Message}");
                return PriorLaunchState.NoRecord;
            }
        }

        /// <summary>
        /// The pure rule behind <see cref="GetPriorLaunchState"/>: only the newest row (by
        /// <see cref="PaneLaunchRecord.Seq"/>) counts.
        ///
        /// <para><b>Fail-closed (pipeline Run 1).</b> A previous run's launch that is still open or ended
        /// <c>mt_exit</c> is <see cref="PriorLaunchState.DiedWithPreviousRun"/> ONLY when
        /// <paramref name="previousRunProvablyGone"/> says that run's MT process is gone; anything else
        /// (alive, never recorded, unreadable, a throwing predicate, a null predicate) is
        /// <see cref="PriorLaunchState.OpenElsewhere"/>.</para>
        ///
        /// <para>Before this check the rule rested on two facts, both true in the code today and both
        /// worth stating because the check exists for the day either stops holding:
        /// (1) <c>Services/Startup/SingleInstanceGuard</c> holds the <c>Local\MultiTerminal.SingleInstance</c>
        /// mutex for the whole life of the process (<c>Program.Main</c> acquires it before any UI and a
        /// second copy in the session exits), so a new MT run cannot start while the old one still runs;
        /// (2) every pane's process tree is in a Job Object created with
        /// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> (<c>ConPtyTerminal.CreateJobAndAssignProcess</c>), so
        /// when the MT process dies, even by a crash, the kernel closes the job handle and kills every
        /// pane's tree. Neither is airtight: the guard FAILS OPEN when the mutex cannot be created, the
        /// <c>Local\</c> mutex does not stop a copy in another Windows session, and the job assignment
        /// is best-effort (a failed <c>AssignProcessToJobObject</c> is only logged). Hence the probe.</para>
        /// </summary>
        public static PriorLaunchState ClassifyPriorLaunch(IEnumerable<PaneLaunchRecord> launches, string currentRunId, Func<string, bool> previousRunProvablyGone)
        {
            var newest = (launches ?? Enumerable.Empty<PaneLaunchRecord>())
                .Where(l => l != null)
                .OrderByDescending(l => l.Seq)
                .ThenByDescending(l => l.StartedAt)
                .FirstOrDefault();
            if (newest == null) return PriorLaunchState.NoRecord;

            // Item 8: MT itself closed this helper with its PM, so no process runs it, in this run or any.
            if (newest.EndedAt != null && string.Equals(newest.EndReason, PaneLaunchEndReason.ClosedWithPm, StringComparison.Ordinal))
                return PriorLaunchState.ClosedWithPm;

            bool currentRun = !string.IsNullOrEmpty(currentRunId)
                && string.Equals(newest.MtRunId, currentRunId, StringComparison.Ordinal);
            if (currentRun)
            {
                if (newest.EndedAt == null) return PriorLaunchState.OpenInCurrentRun;
                return EndedByThisRunsClose(newest.EndReason) ? PriorLaunchState.ClosedInCurrentRun : PriorLaunchState.Ended;
            }
            if (!IsOpen(newest))
                return PriorLaunchState.Ended;

            bool gone;
            try
            {
                gone = previousRunProvablyGone != null && previousRunProvablyGone(newest.MtRunId);
            }
            catch (Exception)
            {
                gone = false;
            }

            return gone ? PriorLaunchState.DiedWithPreviousRun : PriorLaunchState.OpenElsewhere;
        }

        /// <summary>The end reasons that mean this run saw the launch's process end or ended it itself.</summary>
        private static bool EndedByThisRunsClose(string endReason) =>
            string.Equals(endReason, PaneLaunchEndReason.PaneClosed, StringComparison.Ordinal)
            || string.Equals(endReason, PaneLaunchEndReason.CloseHelper, StringComparison.Ordinal)
            || string.Equals(endReason, PaneLaunchEndReason.ProcessExit, StringComparison.Ordinal);

        /// <summary>True only when the run recorded its process and the probe says that process is gone.</summary>
        private bool IsRunProvablyGone(string runId)
        {
            if (string.IsNullOrEmpty(runId)) return false;
            try
            {
                var process = _db.GetMtRun(runId);
                return process != null && _probe(process) == MtProcessLiveness.Gone;
            }
            catch (Exception ex)
            {
                Log($"Checking whether MT run {runId} is gone failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Still open: never ended (MT crashed or it is running) or ended only because MT exited.</summary>
        private static bool IsOpen(PaneLaunchRecord launch)
            => launch.EndedAt == null
               || string.Equals(launch.EndReason, PaneLaunchEndReason.MtExit, StringComparison.Ordinal);

        /// <summary>
        /// Resumable with its PM (item 8): open as <see cref="IsOpen"/> says, or closed together with its PM
        /// (<see cref="PaneLaunchEndReason.ClosedWithPm"/>), which the Owner was told brings it back.
        /// </summary>
        private static bool IsResumable(PaneLaunchRecord launch)
            => IsOpen(launch)
               || string.Equals(launch.EndReason, PaneLaunchEndReason.ClosedWithPm, StringComparison.Ordinal);

        private void Log(string message)
        {
            try
            {
                _logError?.Invoke(message);
            }
            catch
            {
                // A failing log sink must not turn a swallowed failure into a thrown one.
            }
        }
    }
}
