using System;

namespace MultiTerminal.MCPServer.Models
{
    /// <summary>
    /// Represents a terminal's current activity status for live tracking.
    /// </summary>
    public class TerminalActivity
    {
        /// <summary>
        /// Terminal name (primary key).
        /// </summary>
        public string Terminal { get; set; }

        /// <summary>
        /// Current status: working, idle, blocked, unknown
        /// </summary>
        public string Status { get; set; } = "idle";

        /// <summary>
        /// Description of current work.
        /// </summary>
        public string Activity { get; set; }

        /// <summary>
        /// What the terminal is blocked by (if status is 'blocked').
        /// </summary>
        public string BlockedBy { get; set; }

        /// <summary>
        /// Related Kanban task ID.
        /// </summary>
        public string TaskId { get; set; }

        /// <summary>
        /// Related Plan ID.
        /// </summary>
        public string PlanId { get; set; }

        /// <summary>
        /// When the activity was last updated.
        /// </summary>
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Whether the terminal is in a critical section (e.g., mid-commit, mid-build).
        /// When true, urgent tasks will be queued rather than interrupting immediately.
        /// </summary>
        public bool InCriticalSection { get; set; }

        /// <summary>
        /// When the critical section times out (max 30 seconds from when set).
        /// After this time, the critical section is no longer honored.
        /// </summary>
        public DateTime? CriticalSectionTimeout { get; set; }

        /// <summary>
        /// Valid activity statuses.
        /// </summary>
        public static readonly string[] ValidStatuses = { "working", "idle", "blocked", "unknown" };

        /// <summary>
        /// Maximum duration for critical section (30 seconds).
        /// </summary>
        public const int MaxCriticalSectionSeconds = 30;

        /// <summary>
        /// Staleness threshold in minutes.
        /// </summary>
        public const int StalenessThresholdMinutes = 5;
    }

    /// <summary>
    /// Extended activity info with staleness flag for get_team_activity response.
    /// </summary>
    public class TerminalActivityInfo
    {
        public string Terminal { get; set; }
        public string Status { get; set; }
        public string Activity { get; set; }
        public string BlockedBy { get; set; }
        public string TaskId { get; set; }
        public string PlanId { get; set; }
        public DateTime UpdatedAt { get; set; }
        public bool IsStale { get; set; }
    }
}
