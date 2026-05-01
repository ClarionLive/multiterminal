using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Builds a <see cref="ProjectAnalytics"/> snapshot for a single project from
    /// kanban tasks (and task_reports). Pure aggregation — no UI dependencies.
    /// Designed to be invoked from HudDashboardRenderer in response to
    /// <c>TasksUpdated</c> / <c>ProjectsUpdated</c> events, behind the existing
    /// 250 ms debounce.
    /// </summary>
    /// <remarks>
    /// Threading: <see cref="ComputeAsync"/> wraps the aggregation in
    /// <see cref="Task.Run(Func{ProjectAnalytics})"/> so the caller can safely
    /// <c>await</c> it from a broker event handler without blocking the UI thread.
    /// </remarks>
    public sealed class ProjectAnalyticsService
    {
        // 24h: an in_progress task with no checklist note in the last 24h is Blocked.
        // Confirmed by Owner 2026-04-25; matches our sub-daily cadence.
        private static readonly TimeSpan BlockedThreshold = TimeSpan.FromHours(24);
        private const int TimelineDays = 30;

        // Health Timeline thresholds. Red is reserved for genuinely bad days
        // (pipeline failure or a Blocked queue forming); Yellow is the "drifting"
        // state. Tweak only with Owner sign-off — these drive the band color.
        private const int RedBlockedFloor = 4; // > 3 in_progress blocked → red

        // Per-task report fetch cap — generous for re-runs (~6/day for 30 days).
        // Code-reviewer note: explicit ceiling beats a magic literal at the call site.
        private const int MaxReportsPerTask = 200;

        private readonly TaskDatabase _taskDb;

        public ProjectAnalyticsService(TaskDatabase taskDb)
        {
            _taskDb = taskDb ?? throw new ArgumentNullException(nameof(taskDb));
        }

        /// <summary>
        /// Builds analytics for the given task list. Caller is expected to have
        /// already filtered tasks by project (matches the existing renderer flow
        /// of <c>broker.GetTasks(projectId)</c>).
        /// </summary>
        public Task<ProjectAnalytics> ComputeAsync(
            string projectId,
            IReadOnlyList<KanbanTask> tasks,
            CancellationToken cancellationToken = default)
        {
            var snapshot = tasks ?? Array.Empty<KanbanTask>();
            return Task.Run(() => Compute(projectId, snapshot, cancellationToken), cancellationToken);
        }

        private ProjectAnalytics Compute(
            string projectId,
            IReadOnlyList<KanbanTask> tasks,
            CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;

            // Tracks whether ANY per-task data was dropped during aggregation
            // (corrupted checklist_json, SQLiteException on GetTaskReports, etc).
            // Set true in catch blocks; bubbles into the final DTO so the UI can
            // surface an "incomplete data" indicator instead of silently
            // rendering plausible-looking-but-partial Health colors.
            bool degraded = false;

            // Single pass: classify status, gather inProgress, parse all checklist notes.
            int openCount = 0;
            int doneCount = 0;
            var inProgress = new List<KanbanTask>();
            var notesByTask = new Dictionary<string, List<DateTime>>(tasks.Count);
            var doneTransitionsByTask = new Dictionary<string, List<DateTime>>(tasks.Count);
            DateTime? globalLastActivity = null;
            string globalLastAgent = null;

            foreach (var t in tasks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (t == null) continue;

                switch (t.Status)
                {
                    case "todo": openCount++; break;
                    case "done": doneCount++; break;
                    case "in_progress": inProgress.Add(t); break;
                }

                var noteTimes = new List<DateTime>();
                var doneTimes = new List<DateTime>();
                try
                {
                    CollectNoteTimestamps(t, noteTimes, doneTimes);
                }
                catch (System.Text.Json.JsonException ex)
                {
                    // A single corrupted checklist_json must not blank the whole
                    // dashboard. Mirror the per-task isolation already used around
                    // GetTaskReports below.
                    degraded = true;
                    System.Diagnostics.Debug.WriteLine(
                        $"[ProjectAnalyticsService] Skipping task {t.Id} note timestamps (JSON error): {ex.Message}");
                }

                if (noteTimes.Count > 0)
                {
                    notesByTask[t.Id] = noteTimes;
                    var maxAt = noteTimes.Max();
                    if (globalLastActivity == null || maxAt > globalLastActivity)
                    {
                        globalLastActivity = maxAt;
                        globalLastAgent = t.Assignee;
                    }
                }

                if (doneTimes.Count > 0)
                {
                    doneTransitionsByTask[t.Id] = doneTimes;
                }
            }

            int inProgressCount = inProgress.Count;

            // Live Blocked count (used for KPI strip — reflects "right now").
            int blockedNow = CountBlockedAt(inProgress, notesByTask, now);

            // Paused tasks have an assignee but the agent is not actively working
            // them — counting paused as "working now" inflates the live-capacity
            // signal (Codex adversary Run 1 finding).
            int agentsWorkingNow = inProgress
                .Where(t => !string.IsNullOrEmpty(t.Assignee)
                            && !string.Equals(t.SubStatus, "paused", StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Assignee)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            int totalForCompletion = openCount + inProgressCount + doneCount;
            int completionPct = totalForCompletion == 0
                ? 0
                : (int)Math.Round(100.0 * doneCount / totalForCompletion);

            int addedThisWeek = tasks.Count(t => t != null && (now - t.CreatedAt).TotalDays <= 7);

            // Health Timeline — must run before the KPI Health card so the card's
            // sublabel can read off the timeline ("Stable since 16 Apr"). The
            // tuple's second item folds in any per-task GetTaskReports failures
            // into the run-wide `degraded` flag.
            var (healthDays, healthDegraded) = ComputeHealthTimeline(
                tasks, inProgress, notesByTask, doneTransitionsByTask, now, cancellationToken);
            degraded = degraded || healthDegraded;
            string healthSublabel = DescribeHealthStreak(healthDays);
            string todayStatus = healthDays.Count > 0
                ? healthDays[healthDays.Count - 1].Status
                : HealthDay.StatusUnknown;

            string blockedSublabel = blockedNow > 0 ? "no activity > 24h" : "all clear";
            string blockedTone = blockedNow > 0 ? KpiCard.ToneBad : KpiCard.ToneGood;

            // Blocked depends on notesByTask via CountBlockedAt; a JsonException
            // on a checklist falls back to CreatedAt and can miscount tasks as
            // blocked or understate their idle time. Same fail-closed rule as
            // Health: nudge Good up to Warn but never downgrade Bad. Codex
            // adversary Run 5 finding — disclosure scope had missed this widget.
            if (degraded)
            {
                if (!string.Equals(blockedTone, KpiCard.ToneBad, StringComparison.Ordinal))
                {
                    blockedTone = KpiCard.ToneWarn;
                }
                blockedSublabel = $"{blockedSublabel} · data incomplete";
            }

            string inProgressSublabel = agentsWorkingNow == 1
                ? "1 agent working now"
                : $"{agentsWorkingNow} agents working now";

            string healthLabel;
            string healthTone;
            switch (todayStatus)
            {
                case HealthDay.StatusGreen:
                    healthLabel = "Stable";
                    healthTone = KpiCard.ToneGood;
                    break;
                case HealthDay.StatusYellow:
                    healthLabel = "Drifting";
                    healthTone = KpiCard.ToneWarn;
                    break;
                case HealthDay.StatusRed:
                    healthLabel = "At Risk";
                    healthTone = KpiCard.ToneBad;
                    break;
                default:
                    healthLabel = "—";
                    healthTone = KpiCard.ToneNeutral;
                    break;
            }

            // When data was dropped, fail-closed by adding caution — but never
            // DOWNgrade an already-bad status. If real evidence says At Risk
            // (red), the dropped read can't make it less severe; we only nudge
            // up from neutral/good to warn. Codex adversary Run 4 finding:
            // overwriting red → yellow weakens urgency in the wrong direction.
            if (degraded)
            {
                if (!string.Equals(healthTone, KpiCard.ToneBad, StringComparison.Ordinal))
                {
                    healthTone = KpiCard.ToneWarn;
                }
                healthSublabel = string.IsNullOrEmpty(healthSublabel)
                    ? "data incomplete"
                    : $"{healthSublabel} · data incomplete";
            }

            string lastActivityValue = globalLastActivity.HasValue
                ? FormatRelative(now - globalLastActivity.Value)
                : "—";
            string lastActivitySublabel = !string.IsNullOrEmpty(globalLastAgent)
                ? $"by {globalLastAgent}"
                : "no recent activity";

            // Last Activity reads from the same checklist-note timestamps that
            // CollectNoteTimestamps dropped on JsonException, so degraded runs
            // could be missing the actual most-recent activity. Surface that.
            if (degraded)
            {
                lastActivitySublabel = $"{lastActivitySublabel} · data incomplete";
            }

            var kpis = new KpiStripData(
                Open: new KpiCard(openCount.ToString(CultureInfo.InvariantCulture),
                                  $"{addedThisWeek} added this week",
                                  KpiCard.ToneNeutral),
                InProgress: new KpiCard(inProgressCount.ToString(CultureInfo.InvariantCulture),
                                        inProgressSublabel,
                                        KpiCard.ToneNeutral),
                Blocked: new KpiCard(blockedNow.ToString(CultureInfo.InvariantCulture),
                                     blockedSublabel,
                                     blockedTone),
                Completion: new KpiCard($"{completionPct}%",
                                        $"{doneCount} of {totalForCompletion} done",
                                        KpiCard.ToneNeutral),
                Health: new KpiCard(healthLabel, healthSublabel, healthTone),
                LastActivity: new KpiCard(lastActivityValue, lastActivitySublabel, KpiCard.ToneNeutral));

            var progressPoints = ComputeProgressPoints(tasks, doneTransitionsByTask, now, cancellationToken);

            return new ProjectAnalytics(kpis, healthDays, progressPoints, degraded);
        }

        // ---------------------------------------------------------------------
        // Progress Over Time
        // ---------------------------------------------------------------------

        /// <summary>
        /// Builds the 30-day Progress Over Time series. Per-day end-of-day
        /// snapshot:
        /// <list type="bullet">
        /// <item><description><see cref="ProgressPoint.Total"/> — tasks where CreatedAt ≤ dayEnd.</description></item>
        /// <item><description><see cref="ProgressPoint.Done"/> — currently-done tasks whose latest checklist "→ done" transition ≤ dayEnd.</description></item>
        /// <item><description><see cref="ProgressPoint.Open"/> — Total − Done (outstanding work at EoD).</description></item>
        /// </list>
        /// </summary>
        private static List<ProgressPoint> ComputeProgressPoints(
            IReadOnlyList<KanbanTask> tasks,
            Dictionary<string, List<DateTime>> doneTransitionsByTask,
            DateTime now,
            CancellationToken cancellationToken)
        {
            var today = now.Date;

            // For currently-done tasks: latest "→ done" transition is the
            // best-available task-completion timestamp. Falls back to UpdatedAt
            // (we don't carry that yet) → CreatedAt as a last resort.
            var taskCompletedAt = new Dictionary<string, DateTime>(tasks.Count);
            foreach (var t in tasks)
            {
                if (t == null || t.Status != "done") continue;
                if (doneTransitionsByTask.TryGetValue(t.Id, out var times) && times.Count > 0)
                {
                    taskCompletedAt[t.Id] = times.Max();
                }
                else
                {
                    taskCompletedAt[t.Id] = t.CreatedAt;
                }
            }

            var result = new List<ProgressPoint>(TimelineDays);
            for (int i = TimelineDays - 1; i >= 0; i--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var day = today.AddDays(-i);
                var dayEnd = day.AddDays(1);
                var dayKey = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

                int total = 0;
                int done = 0;
                foreach (var t in tasks)
                {
                    if (t == null) continue;
                    if (t.CreatedAt > dayEnd) continue;
                    total++;
                    if (taskCompletedAt.TryGetValue(t.Id, out var doneAt) && doneAt <= dayEnd)
                    {
                        done++;
                    }
                }
                int open = total - done;
                result.Add(new ProgressPoint(dayKey, done, open, total));
            }

            return result;
        }

        // ---------------------------------------------------------------------
        // Health Timeline
        // ---------------------------------------------------------------------

        /// <summary>
        /// Builds the 30-day Health Timeline. Rules per day (UTC bucket):
        /// <list type="bullet">
        /// <item><description><b>Red</b> — at least one pipeline-fail event that day, OR Blocked &gt; 3 at end-of-day.</description></item>
        /// <item><description><b>Yellow</b> — any Blocked tasks at end-of-day, OR no checklist "→ done" transitions that day.</description></item>
        /// <item><description><b>Green</b> — no Blocked at end-of-day AND ≥1 done transition that day.</description></item>
        /// </list>
        /// Limitation: "Blocked at past EoD" uses the task's <i>current</i> status —
        /// we don't have a status-history table. A task that's now <c>done</c> won't
        /// register as historically blocked. Accuracy is best near the present.
        /// </summary>
        /// <returns>
        /// Tuple of (Days, Degraded). Degraded is true if any per-task
        /// <c>GetTaskReports</c> read failed — caller should bubble this into
        /// the run-wide degraded flag so the UI can fail-closed instead of
        /// rendering authoritative colors over partial evidence.
        /// </returns>
        private (List<HealthDay> Days, bool Degraded) ComputeHealthTimeline(
            IReadOnlyList<KanbanTask> tasks,
            List<KanbanTask> inProgress,
            Dictionary<string, List<DateTime>> notesByTask,
            Dictionary<string, List<DateTime>> doneTransitionsByTask,
            DateTime now,
            CancellationToken cancellationToken)
        {
            bool degraded = false;
            var today = now.Date;
            var earliest = today.AddDays(-(TimelineDays - 1));

            // Done events bucketed by day.
            var doneEventsByDay = new Dictionary<DateTime, int>(TimelineDays);
            foreach (var times in doneTransitionsByTask.Values)
            {
                foreach (var ts in times)
                {
                    var day = ts.Date;
                    if (day < earliest || day > today) continue;
                    doneEventsByDay[day] = (doneEventsByDay.TryGetValue(day, out var c) ? c : 0) + 1;
                }
            }

            // Pipeline-fail events bucketed by day. One SQL call per task; tasks per
            // project are typically <100 and this is debounced + off the UI thread.
            // Cancellation checked before each per-task DB read so a project switch
            // doesn't keep the prior compute hammering SQLite.
            var failsByDay = new Dictionary<DateTime, int>(TimelineDays);
            foreach (var t in tasks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (t == null || string.IsNullOrEmpty(t.Id)) continue;
                List<Dictionary<string, object>> reports;
                try
                {
                    reports = _taskDb.GetTaskReports(t.Id, agentName: null, limit: MaxReportsPerTask);
                }
                catch (System.Data.SQLite.SQLiteException ex)
                {
                    // A flaky DB read on one task shouldn't blank the whole timeline,
                    // but the run IS degraded — pipeline-fail evidence may be missing,
                    // and the UI must surface that rather than imply clean health.
                    degraded = true;
                    System.Diagnostics.Debug.WriteLine(
                        $"[ProjectAnalyticsService] Skipping task {t.Id} report read (SQLite error): {ex.Message}");
                    continue;
                }
                catch (InvalidOperationException ex)
                {
                    degraded = true;
                    System.Diagnostics.Debug.WriteLine(
                        $"[ProjectAnalyticsService] Skipping task {t.Id} report read (invalid op): {ex.Message}");
                    continue;
                }
                if (reports == null) continue;
                foreach (var r in reports)
                {
                    if (!TryGetString(r, "verdict", out var verdict)) continue;
                    if (!string.Equals(verdict, "FAIL", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!TryGetString(r, "created_at", out var atStr)) continue;
                    if (!TryParseTimestamp(atStr, out var at)) continue;
                    var day = at.Date;
                    if (day < earliest || day > today) continue;
                    failsByDay[day] = (failsByDay.TryGetValue(day, out var c) ? c : 0) + 1;
                }
            }

            var result = new List<HealthDay>(TimelineDays);
            for (int i = TimelineDays - 1; i >= 0; i--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var day = today.AddDays(-i);
                var dayEnd = day.AddDays(1);
                var dayKey = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

                int doneEvents = doneEventsByDay.TryGetValue(day, out var d) ? d : 0;
                int fails = failsByDay.TryGetValue(day, out var f) ? f : 0;
                int blockedAtEod = CountBlockedAtPastEod(inProgress, notesByTask, dayEnd);

                string status;
                string reason;
                if (fails > 0 && blockedAtEod >= RedBlockedFloor)
                {
                    status = HealthDay.StatusRed;
                    reason = $"{fails} pipeline failure(s); {blockedAtEod} blocked";
                }
                else if (fails > 0)
                {
                    status = HealthDay.StatusRed;
                    reason = fails == 1 ? "1 pipeline failure" : $"{fails} pipeline failures";
                }
                else if (blockedAtEod >= RedBlockedFloor)
                {
                    status = HealthDay.StatusRed;
                    reason = $"{blockedAtEod} tasks blocked";
                }
                else if (blockedAtEod > 0 && doneEvents == 0)
                {
                    status = HealthDay.StatusYellow;
                    reason = $"{blockedAtEod} blocked, no work completed";
                }
                else if (blockedAtEod > 0)
                {
                    status = HealthDay.StatusYellow;
                    reason = blockedAtEod == 1 ? "1 task blocked" : $"{blockedAtEod} tasks blocked";
                }
                else if (doneEvents == 0)
                {
                    status = HealthDay.StatusYellow;
                    reason = "no work completed";
                }
                else
                {
                    status = HealthDay.StatusGreen;
                    reason = doneEvents == 1 ? "1 item completed" : $"{doneEvents} items completed";
                }

                result.Add(new HealthDay(dayKey, status, reason));
            }

            return (result, degraded);
        }

        /// <summary>
        /// Builds the "Stable since {date}" / "{N} days at risk" sublabel from the
        /// already-computed health array. Reads the trailing run starting at today
        /// and walks backward to find the streak boundary.
        /// </summary>
        private static string DescribeHealthStreak(List<HealthDay> healthDays)
        {
            if (healthDays.Count == 0) return string.Empty;
            var todayBucket = healthDays[healthDays.Count - 1];
            string streakStatus = todayBucket.Status;

            int runLength = 1;
            for (int i = healthDays.Count - 2; i >= 0; i--)
            {
                if (healthDays[i].Status != streakStatus) break;
                runLength++;
            }

            switch (streakStatus)
            {
                case HealthDay.StatusGreen:
                    if (runLength == healthDays.Count) return "Stable for 30+ days";
                    var streakStart = healthDays[healthDays.Count - runLength].Date;
                    if (DateTime.TryParse(streakStart, CultureInfo.InvariantCulture,
                                           DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                                           out var dt))
                    {
                        return $"Stable since {dt:dd MMM}";
                    }
                    return $"Stable for {runLength} day(s)";
                case HealthDay.StatusYellow:
                    return runLength == 1 ? "Drifting today" : $"{runLength} days drifting";
                case HealthDay.StatusRed:
                    return runLength == 1 ? "At risk today" : $"{runLength} days at risk";
                default:
                    return string.Empty;
            }
        }

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        private static int CountBlockedAt(
            List<KanbanTask> inProgress,
            Dictionary<string, List<DateTime>> notesByTask,
            DateTime asOf)
        {
            int count = 0;
            foreach (var t in inProgress)
            {
                var last = LastNoteAtOrBefore(t, notesByTask, asOf) ?? t.CreatedAt;
                if (asOf - last > BlockedThreshold) count++;
            }
            return count;
        }

        /// <summary>
        /// Counts in_progress tasks blocked at end-of-day-D. A task is excluded if
        /// it didn't exist yet by then (CreatedAt &gt; dayEnd).
        /// </summary>
        private static int CountBlockedAtPastEod(
            List<KanbanTask> inProgress,
            Dictionary<string, List<DateTime>> notesByTask,
            DateTime dayEnd)
        {
            int count = 0;
            foreach (var t in inProgress)
            {
                if (t.CreatedAt > dayEnd) continue;
                var last = LastNoteAtOrBefore(t, notesByTask, dayEnd) ?? t.CreatedAt;
                if (dayEnd - last > BlockedThreshold) count++;
            }
            return count;
        }

        private static DateTime? LastNoteAtOrBefore(
            KanbanTask task,
            Dictionary<string, List<DateTime>> notesByTask,
            DateTime cutoff)
        {
            if (!notesByTask.TryGetValue(task.Id, out var times) || times.Count == 0)
                return null;
            DateTime? best = null;
            foreach (var ts in times)
            {
                if (ts > cutoff) continue;
                if (best == null || ts > best) best = ts;
            }
            return best;
        }

        /// <summary>
        /// Walks the task's checklist once, populating <paramref name="allNoteTimes"/>
        /// with every parsed timestamp and <paramref name="doneTransitionTimes"/>
        /// with timestamps of notes whose Transition string includes "→ done".
        /// Done events feed the Health Timeline (Alice point 4 — checklist-item
        /// "→ done" transitions, not task-level status changes).
        /// </summary>
        private static void CollectNoteTimestamps(
            KanbanTask task,
            List<DateTime> allNoteTimes,
            List<DateTime> doneTransitionTimes)
        {
            foreach (var item in task.GetChecklist())
            {
                if (item.Notes == null) continue;
                foreach (var note in item.Notes)
                {
                    if (note?.At == null) continue;
                    if (!TryParseTimestamp(note.At, out var dt)) continue;
                    allNoteTimes.Add(dt);
                    var transition = note.Transition ?? string.Empty;
                    // Match both Unicode arrow and ASCII fallback.
                    if (transition.IndexOf("→ done", StringComparison.OrdinalIgnoreCase) >= 0
                        || transition.IndexOf("-> done", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        doneTransitionTimes.Add(dt);
                    }
                }
            }
        }

        private static bool TryParseTimestamp(string raw, out DateTime utc)
        {
            utc = default;
            if (string.IsNullOrEmpty(raw)) return false;
            if (!DateTime.TryParse(
                    raw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var dt))
            {
                return false;
            }
            utc = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
            return true;
        }

        private static bool TryGetString(IReadOnlyDictionary<string, object> row, string key, out string value)
        {
            value = null;
            if (row == null || !row.TryGetValue(key, out var raw) || raw == null) return false;
            value = raw.ToString();
            return !string.IsNullOrEmpty(value);
        }

        private static string FormatRelative(TimeSpan ago)
        {
            if (ago.TotalSeconds < 0) return "now";
            if (ago.TotalMinutes < 1) return "now";
            if (ago.TotalMinutes < 60) return $"{(int)ago.TotalMinutes}m ago";
            if (ago.TotalHours < 24) return $"{(int)ago.TotalHours}h ago";
            return $"{(int)ago.TotalDays}d ago";
        }
    }
}
