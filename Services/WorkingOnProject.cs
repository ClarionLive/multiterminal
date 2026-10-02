using System;
using System.Collections.Generic;
using System.Linq;
using MultiTerminal.MCPServer.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// "Working on: X" — the project of an agent's ACTIVE task, when it is not the project the
    /// agent's session runs in (task 19a26090, Owner decision 2026-10-01: show both).
    ///
    /// <para>The terminal header and the Attention card each used to answer "which project?" from
    /// one source — the header from the folder, the card from the task — so Grace read MultiTerminal
    /// in one place and Clarion Addin Registry in the other with nothing saying why. Both now show
    /// where the session runs, and both add this marker from this one function, so the two cannot
    /// describe the same agent differently.</para>
    ///
    /// <para>Pure and in-memory on purpose: the header refreshes on the UI thread on every activity
    /// update, and the broker's own active-task lookup can fall through to a database query.</para>
    /// </summary>
    internal static class WorkingOnProject
    {
        /// <summary>
        /// Returns the active task's project name, or null when there is nothing to add: no active
        /// task, a task with no nameable project, or a task in the session's own project.
        /// </summary>
        /// <param name="tasks">The broker's in-memory task list.</param>
        /// <param name="agentName">The agent whose active task to look at.</param>
        /// <param name="sessionProjectId">Id of the project the session runs in, when known.</param>
        /// <param name="sessionProjectName">Its display name; compared only when no id is known.</param>
        /// <param name="projectNames">Project id → display name.</param>
        internal static string Resolve(
            IEnumerable<KanbanTask> tasks,
            string agentName,
            string sessionProjectId,
            string sessionProjectName,
            IReadOnlyDictionary<string, string> projectNames)
        {
            string taskProjectName = ActiveTaskProjectName(tasks, agentName, projectNames, out string taskProjectId);
            if (taskProjectName == null) return null;

            bool sameProject = !string.IsNullOrEmpty(sessionProjectId)
                ? string.Equals(sessionProjectId, taskProjectId, StringComparison.OrdinalIgnoreCase)
                : string.Equals(sessionProjectName, taskProjectName, StringComparison.OrdinalIgnoreCase);

            return sameProject ? null : taskProjectName;
        }

        /// <summary>
        /// Project id → display name, skipping entries with no id or no name. The one builder for
        /// the lookup both the header and the Attention refresh pass in.
        /// </summary>
        internal static Dictionary<string, string> NameIndex(IEnumerable<(string Id, string Name)> projects)
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (projects == null) return names;
            foreach (var (id, name) in projects)
            {
                if (id != null && !string.IsNullOrWhiteSpace(name)) names[id] = name;
            }
            return names;
        }

        /// <summary>
        /// The display name of the project the agent's ACTIVE task belongs to, or null when the
        /// agent has no active task or its project cannot be named.
        /// </summary>
        internal static string ActiveTaskProjectName(
            IEnumerable<KanbanTask> tasks,
            string agentName,
            IReadOnlyDictionary<string, string> projectNames,
            out string projectId)
        {
            projectId = null;
            if (tasks == null || projectNames == null || string.IsNullOrWhiteSpace(agentName)) return null;

            // Same predicate as TaskService.GetMyActiveTask, including its deterministic winner when
            // a failed sibling-pause leaves two actives (newest CreatedAt, then id).
            var active = tasks
                .Where(t => t != null
                    && t.Assignee != null
                    && t.Assignee.Equals(agentName, StringComparison.OrdinalIgnoreCase)
                    && t.Status == "in_progress"
                    && t.SubStatus == "active")
                .OrderByDescending(t => t.CreatedAt)
                .ThenBy(t => t.Id, StringComparer.Ordinal)
                .FirstOrDefault();

            if (active?.ProjectId == null) return null;
            if (!projectNames.TryGetValue(active.ProjectId, out string name) || string.IsNullOrWhiteSpace(name))
                return null;

            projectId = active.ProjectId;
            return name;
        }
    }
}
