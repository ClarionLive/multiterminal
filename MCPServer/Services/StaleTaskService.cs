using System;
using System.Collections.Generic;
using System.Linq;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.Services;

namespace MultiTerminal.MCPServer.Services
{
    /// <summary>
    /// Service for detecting and managing stale paused tasks.
    /// Flags tasks paused for 7+ days as warning, 14+ days as urgent.
    /// </summary>
    public class StaleTaskService
    {
        private readonly TaskDatabase _taskDb;

        public const int Day7WarningDays = 7;
        public const int Day14UrgentDays = 14;

        public StaleTaskService(TaskDatabase taskDb)
        {
            _taskDb = taskDb;
        }

        /// <summary>
        /// Check all paused tasks and update stale flags.
        /// Call this periodically (e.g., on startup, hourly).
        /// </summary>
        /// <returns>List of newly flagged tasks</returns>
        public List<StaleFlagResult> CheckAndFlagStaleTasks()
        {
            var results = new List<StaleFlagResult>();
            var now = DateTime.UtcNow;

            // Get tasks paused for 7+ days that haven't been flagged yet
            var staleTasks = _taskDb.GetStaleTasks();

            foreach (var task in staleTasks)
            {
                if (task.PausedAt == null) continue;

                var daysPaused = (now - task.PausedAt.Value).TotalDays;
                // Flag as level 1 (day 7 warning)
                _taskDb.UpdateStaleLevel(task.Id, 1);
                results.Add(new StaleFlagResult
                {
                    TaskId = task.Id,
                    TaskTitle = task.Title,
                    Assignee = task.Assignee,
                    DaysPaused = (int)daysPaused,
                    NewLevel = 1,
                    PreviousLevel = task.StaleLevel
                });
            }

            // Get tasks paused for 14+ days that are already at level 1
            var criticalTasks = _taskDb.GetCriticalStaleTasks();

            foreach (var task in criticalTasks)
            {
                if (task.PausedAt == null) continue;

                var daysPaused = (now - task.PausedAt.Value).TotalDays;
                // Flag as level 2 (day 14 urgent)
                _taskDb.UpdateStaleLevel(task.Id, 2);
                results.Add(new StaleFlagResult
                {
                    TaskId = task.Id,
                    TaskTitle = task.Title,
                    Assignee = task.Assignee,
                    DaysPaused = (int)daysPaused,
                    NewLevel = 2,
                    PreviousLevel = task.StaleLevel
                });
            }

            return results;
        }

        /// <summary>
        /// Get stale tasks for a specific terminal (assigned to or created by).
        /// </summary>
        public List<KanbanTask> GetStaleTasksForTerminal(string terminalName)
        {
            // Load all tasks and filter by terminal and stale status
            var allTasks = _taskDb.LoadAllTasks();
            return allTasks
                .Where(t => t.StaleLevel > 0 &&
                           (t.Assignee == terminalName || t.CreatedBy == terminalName))
                .ToList();
        }

        /// <summary>
        /// Acknowledge a stale task (user says "still relevant").
        /// Clears the stale flag and records the response.
        /// </summary>
        public void AcknowledgeStaleTask(string taskId)
        {
            _taskDb.RecordStaleResponse(taskId, "still_relevant");
            _taskDb.ClearStaleTracking(taskId);
        }

        /// <summary>
        /// Record that the user will close the task.
        /// </summary>
        public void MarkTaskForClosure(string taskId)
        {
            _taskDb.RecordStaleResponse(taskId, "will_close");
        }

        /// <summary>
        /// Record that the task was reprioritized.
        /// </summary>
        public void MarkTaskReprioritized(string taskId)
        {
            _taskDb.RecordStaleResponse(taskId, "reprioritized");
            _taskDb.ClearStaleTracking(taskId);
        }

        /// <summary>
        /// Flag a task as stale with a specific level.
        /// Called by the flag_stale_task MCP tool.
        /// </summary>
        /// <param name="taskId">The task ID to flag.</param>
        /// <param name="staleLevel">The stale level (1=day7 warning, 2=day14 urgent).</param>
        public void FlagTaskAsStale(string taskId, int staleLevel)
        {
            _taskDb.FlagTaskAsStale(taskId, staleLevel);
        }

        /// <summary>
        /// Record a response to a stale task notification.
        /// Called by the respond_to_stale MCP tool.
        /// </summary>
        /// <param name="taskId">The task ID.</param>
        /// <param name="response">The response: 'still_relevant', 'will_close', or 'reprioritize'.</param>
        public void RecordStaleResponse(string taskId, string response)
        {
            _taskDb.RecordStaleResponse(taskId, response);
        }

        /// <summary>
        /// Get message for stale level.
        /// </summary>
        public static string GetStaleMessage(int staleLevel, int daysPaused)
        {
            if (staleLevel == 2)
                return $"⚠️ URGENT: Paused {daysPaused} days - close or re-prioritize?";
            else if (staleLevel == 1)
                return $"📋 Paused {daysPaused} days - still relevant?";
            return null;
        }
    }

    /// <summary>
    /// Result of flagging a stale task.
    /// </summary>
    public class StaleFlagResult
    {
        public string TaskId { get; set; }
        public string TaskTitle { get; set; }
        public string Assignee { get; set; }
        public int DaysPaused { get; set; }
        public int NewLevel { get; set; }
        public int PreviousLevel { get; set; }
    }
}
