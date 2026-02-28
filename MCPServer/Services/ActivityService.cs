using System;
using System.Collections.Generic;
using System.Linq;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.Services;

namespace MultiTerminal.MCPServer.Services
{
    /// <summary>
    /// Service for tracking terminal activity status in real-time.
    /// Provides foundation for live activity tracking feature.
    /// Uses TerminalActivity class defined in TaskDatabase.
    /// </summary>
    public class ActivityService
    {
        private readonly TaskDatabase _db;
        private readonly TimeSpan _stalenessThreshold = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Raised when activity is updated for UI refresh.
        /// </summary>
        public event EventHandler<TerminalActivity> ActivityUpdated;

        public ActivityService(TaskDatabase db)
        {
            _db = db;
        }

        /// <summary>
        /// Update activity status for a terminal.
        /// Called automatically by tool hooks or manually via MCP tool.
        /// </summary>
        public void UpdateActivity(
            string terminalName,
            string status,
            string activity,
            string taskId = null,
            string planId = null,
            string blockedBy = null)
        {
            if (string.IsNullOrWhiteSpace(terminalName))
                return;

            var record = new TerminalActivity
            {
                Terminal = terminalName,
                Status = status ?? "idle",
                Activity = activity ?? "",
                TaskId = taskId,
                PlanId = planId,
                BlockedBy = blockedBy,
                UpdatedAt = DateTime.UtcNow
            };

            _db.SaveTerminalActivity(record);
            ActivityUpdated?.Invoke(this, record);

            System.Diagnostics.Debug.WriteLine(
                $"[Activity] {terminalName}: {status} - {activity}");
        }

        /// <summary>
        /// Get activity for all online terminals, marking stale entries.
        /// Only returns activities for terminals with online profiles.
        /// </summary>
        public List<TerminalActivity> GetTeamActivity()
        {
            var activities = _db.GetAllTerminalActivities();
            var staleThreshold = DateTime.UtcNow - _stalenessThreshold;

            // Get all profiles to check online status
            var profiles = _db.LoadAllProfiles();
            var onlineProfiles = profiles.Where(p => p.IsOnline && p.Id != "Unassigned").Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Filter activities to only online terminals and mark stale entries
            var filteredActivities = new List<TerminalActivity>();
            foreach (var activity in activities)
            {
                // Only include terminals with online profiles
                if (!onlineProfiles.Contains(activity.Terminal))
                    continue;

                if (activity.UpdatedAt < staleThreshold)
                {
                    activity.Status = "unknown";
                }

                filteredActivities.Add(activity);
            }

            return filteredActivities.OrderBy(a => a.Terminal).ToList();
        }

        /// <summary>
        /// Get activity for a specific terminal.
        /// Returns null for stale activities (older than 5 minutes) so UI shows "Ready to work".
        /// </summary>
        public TerminalActivity GetActivity(string terminalName)
        {
            var activity = _db.GetTerminalActivity(terminalName);
            if (activity == null)
                return null;

            var staleThreshold = DateTime.UtcNow - _stalenessThreshold;
            if (activity.UpdatedAt < staleThreshold)
            {
                // Return null for stale activities - UI will show "Ready to work"
                return null;
            }

            return activity;
        }
    }
}
