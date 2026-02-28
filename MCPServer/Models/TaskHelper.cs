using System;

namespace MultiTerminal.MCPServer.Models
{
    /// <summary>
    /// Represents a helper assigned to a task for team collaboration.
    /// Helpers can assist the primary assignee but don't own the task.
    /// </summary>
    public class TaskHelper
    {
        /// <summary>
        /// Unique helper assignment identifier.
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);

        /// <summary>
        /// The task ID this helper is assigned to.
        /// </summary>
        public string TaskId { get; set; }

        /// <summary>
        /// Terminal name of the helper.
        /// </summary>
        public string HelperName { get; set; }

        /// <summary>
        /// Terminal name that added this helper.
        /// </summary>
        public string AddedBy { get; set; }

        /// <summary>
        /// When the helper was added.
        /// </summary>
        public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Result of adding a helper to a task.
    /// </summary>
    public class AddTaskHelperResult
    {
        public bool Success { get; set; }
        public string HelperId { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Result of removing a helper from a task.
    /// </summary>
    public class RemoveTaskHelperResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
    }
}
