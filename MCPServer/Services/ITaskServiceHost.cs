using System.Collections.Generic;
using System.Threading.Tasks;
using MultiTerminal.MCPServer.Models;

namespace MultiTerminal.MCPServer.Services
{
    /// <summary>
    /// The NARROW collaborator surface <see cref="TaskService"/> needs from <see cref="MessageBroker"/>.
    ///
    /// <para>Ticket e7e89f4b (MessageBroker decomposition, proof-of-pattern). The Kanban-task region was
    /// extracted into <see cref="TaskService"/>, which owns the <c>_tasks</c> cache + all task CRUD and the
    /// single write path (clone→persist→swap, from 1df2a534). Everything a task method touches that lives in
    /// ANOTHER region — event raising, activity/inbox/notifications, project resolution, worktree lifecycle,
    /// task attachments — is reached through this interface, which the broker implements.</para>
    ///
    /// <para>WHY AN INTERFACE, NOT A RAW <see cref="MessageBroker"/> BACK-REFERENCE: a raw broker reference
    /// would re-expose all ~246 broker methods to TaskService and make the decomposition cosmetic. This
    /// interface ENUMERATES exactly the region's outbound coupling — the same "enumerate the bypass, don't
    /// prose it" census discipline the P5 verifiers use — so the coupling is reviewable and bounded. Each
    /// future region extraction gets its OWN host-interface slice; that is the sentence that makes the
    /// pattern generalize to the remaining ~16 regions (see .claude/rules/broker-extraction-pattern.md).</para>
    ///
    /// <para>Members are grouped by the region that OWNS them today. As those regions extract in turn, only
    /// the broker's implementation of these members changes (delegating to the new service) — TaskService is
    /// untouched. That composability is the proof the pattern holds.</para>
    /// </summary>
    internal interface ITaskServiceHost
    {
        // ── Logging (broker DebugLogService wrappers; stamped with source "TaskService") ────────────
        void LogError(string message);
        void LogWarning(string message);
        void LogInfo(string message);
        void LogTrace(string message);

        // ── Event raisers — the Task* events STAY declared on the broker so subscribers (MainForm,
        //    panels, HUD) are untouched; TaskService raises them through these wrappers, which call the
        //    broker's private resilient RaiseSafe dispatch (1df2a534). ──────────────────────────────
        void RaiseTasksUpdated(List<KanbanTask> tasks);
        void RaiseTaskClaimed(TaskClaimedEventArgs args);
        void RaiseTaskActiveChanged(TaskActiveChangedEventArgs args);

        // ── Activity feed / user inbox / two-tier notifications (owning regions stay on broker) ─────
        void RecordActivity(ActivityEvent activity, bool alreadyPersisted = false);
        CreateInboxMessageResult CreateInboxNotification(
            string userId, string taskId, string taskTitle, int? checklistItemIndex,
            string checklistItemName, string type, string summary, string createdBy);
        void NotifyReportSaved(string taskId, string reportId, string agentName, string verdict);
        Task<SendResult> NotifyHelperAdded(string helperName, string taskId, string taskTitle, string assignee);
        Task<SendResult> NotifyHelpRequested(string helperName, string taskId, string taskTitle, string requester, string details = null);

        // ── Project resolution — reads the broker-owned _projects cache. Kept with its owner (single
        //    owner per cache, mirroring bb2b0104); moves to ProjectService when THAT region extracts. ─
        string NormalizeProjectId(string raw);
        string TryNormalizeProjectId(string raw, out bool ambiguous);
        bool TryResolveWorktreeEligibility(
            KanbanTask task, out string projectPath, out string canonicalProjectId, out string skipReason);
        bool TryGetProject(string projectId, out Project project);

        // ── Shared naming utility (also used by profile/registration/worktree regions) ──────────────
        bool IsTemporaryAgent(string name);

        // ── Worktree lifecycle — the NEXT region to extract. TaskService's activation/done flows call
        //    these; when the worktree region extracts, only these implementations change. ────────────
        MultiTerminal.Services.WorktreeManager Worktrees { get; }
        MultiTerminal.Services.WorktreeAutoCommitService AutoCommit { get; }
        MultiTerminal.Services.WorktreeMergeService Merge { get; }
        object TaskWorktreeLock(string taskId);
        WorktreePruningEventArgs FireWorktreePruning(string taskId, string worktreePath, string repoRoot, string agentName);
        void PerformPostPruneMergeAndFireReady(string taskId, KanbanTask task, string projectPath, string worktreePath);
        bool CommitAndIntegrateHelpers(KanbanTask task, string repoRoot, out List<string> integratedBranches);

        // ── DI-set collaborators (assigned on the broker after construction by MainForm wire-up). Task
        //    methods use them for HUD activity, complexity scoring, auto-summaries, changelog entries, and
        //    the default inbox recipient. Nullable in principle (task code already null-guards the services). ─
        ActivityService ActivityService { get; }
        SummaryService SummaryService { get; }
        ComplexityDetector ComplexityDetector { get; }
        MultiTerminal.Services.ChangelogService ChangelogService { get; }
        string DefaultInboxRecipient { get; }

        // ── Task attachments (own region) — a task delete cascades to attachment cleanup. ───────────
        void CleanupTaskAttachments(string taskId);
    }
}
