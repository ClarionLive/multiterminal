using System;
using System.Collections.Generic;
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
        /// The newest launch belongs to a PREVIOUS MT run and is still open (that MT crashed) or ended
        /// <c>mt_exit</c>: its process died with that MT, so a recent transcript write was that process,
        /// not something outside MT.
        /// </summary>
        DiedWithPreviousRun = 1,

        /// <summary>The newest launch belongs to THIS MT run and has not ended: a pane is running it.</summary>
        OpenInCurrentRun = 2,

        /// <summary>Any other newest launch: ended this run, or closed (not by an MT exit) in an earlier one.</summary>
        Ended = 3,
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

        /// <summary>Creates the recorder for this MT process. <paramref name="log"/> may be null.</summary>
        public PaneLaunchRecorder(TaskDatabase db, DebugLogService log)
            : this(db, log == null ? null : (Action<string>)(m => log.Error(LogSource, m)), null, null)
        {
        }

        /// <summary>Test seam: injectable error sink, run id and clock.</summary>
        internal PaneLaunchRecorder(TaskDatabase db, Action<string> logError, string mtRunId, Func<DateTime> utcNow)
        {
            _db = db;
            _logError = logError;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            MtRunId = string.IsNullOrEmpty(mtRunId) ? Guid.NewGuid().ToString() : mtRunId;
        }

        /// <summary>A new GUID per MT process; every launch row this recorder writes carries it.</summary>
        public string MtRunId { get; }

        /// <summary>Writes a new open launch row.</summary>
        /// <returns>The launch id, or null when the row could not be written.</returns>
        public string BeginLaunch(string docId, string name, string projectId, string cwd, string kind, string resumedFromSessionId)
        {
            try
            {
                var id = Guid.NewGuid().ToString();
                _db.InsertPaneLaunch(new PaneLaunchRecord
                {
                    Id = id,
                    MtRunId = MtRunId,
                    DocId = docId,
                    TerminalName = name,
                    ProjectId = projectId,
                    Cwd = cwd,
                    Kind = kind,
                    ResumedFromSessionId = string.IsNullOrEmpty(resumedFromSessionId) ? null : resumedFromSessionId,
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

        /// <summary>Records the Claude session id a launch is running.</summary>
        public void AdoptSession(string launchId, string sessionId)
        {
            if (string.IsNullOrEmpty(launchId) || string.IsNullOrEmpty(sessionId)) return;
            try
            {
                _db.SetPaneLaunchSession(launchId, sessionId);
            }
            catch (Exception ex)
            {
                Log($"AdoptSession failed for launch {launchId}: {ex.Message}");
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
        /// <paramref name="max"/> (Owner-approved rule, task 7d20e94c):
        /// <list type="number">
        /// <item>Every launch of the PM session, including earlier resumes (a resume keeps the session id).</item>
        /// <item>Their direct child launches only (no helpers of helpers) that carry a session id other than the PM's.</item>
        /// <item>Per helper session, the newest child launch.</item>
        /// <item>Kept only while open: not ended (a crash) or ended by <see cref="PaneLaunchEndReason.MtExit"/>.
        /// <c>pane_closed</c>, <c>close_helper</c> and <c>process_exit</c> are closed.</item>
        /// <item>Dropped when any LATER launch of the same helper session, spawned by anyone, ended closed.</item>
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
                    .Select(g => g.OrderByDescending(c => c.StartedAt).First())
                    .Where(IsOpen)
                    .OrderByDescending(c => c.StartedAt)
                    .ToList();

                var result = new List<OpenHelper>();
                foreach (var child in newestPerSession)
                {
                    if (result.Count >= max) break;
                    bool closedLater = _db.GetPaneLaunchesBySession(child.SessionId)
                        .Any(l => l.StartedAt > child.StartedAt && !IsOpen(l));
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
        /// <see cref="PriorLaunchState"/> of <paramref name="sessionId"/>, from its newest launch row and
        /// this recorder's <see cref="MtRunId"/>. A failed read is <see cref="PriorLaunchState.NoRecord"/>,
        /// which keeps the recency rule in force (the safe direction).
        /// </summary>
        public PriorLaunchState GetPriorLaunchState(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return PriorLaunchState.NoRecord;
            try
            {
                return ClassifyPriorLaunch(_db.GetPaneLaunchesBySession(sessionId), MtRunId);
            }
            catch (Exception ex)
            {
                Log($"GetPriorLaunchState failed for session {sessionId}: {ex.Message}");
                return PriorLaunchState.NoRecord;
            }
        }

        /// <summary>The pure rule behind <see cref="GetPriorLaunchState"/>: only the newest row (by start time) counts.</summary>
        public static PriorLaunchState ClassifyPriorLaunch(IEnumerable<PaneLaunchRecord> launches, string currentRunId)
        {
            var newest = (launches ?? Enumerable.Empty<PaneLaunchRecord>())
                .Where(l => l != null)
                .OrderByDescending(l => l.StartedAt)
                .FirstOrDefault();
            if (newest == null) return PriorLaunchState.NoRecord;

            bool currentRun = !string.IsNullOrEmpty(currentRunId)
                && string.Equals(newest.MtRunId, currentRunId, StringComparison.Ordinal);
            if (currentRun)
                return newest.EndedAt == null ? PriorLaunchState.OpenInCurrentRun : PriorLaunchState.Ended;
            return IsOpen(newest) ? PriorLaunchState.DiedWithPreviousRun : PriorLaunchState.Ended;
        }

        /// <summary>Still open: never ended (MT crashed or it is running) or ended only because MT exited.</summary>
        private static bool IsOpen(PaneLaunchRecord launch)
            => launch.EndedAt == null
               || string.Equals(launch.EndReason, PaneLaunchEndReason.MtExit, StringComparison.Ordinal);

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
