using System;

namespace MultiTerminal.MCPServer.Models
{
    /// <summary>
    /// One project note tab with its authorship (task 5d94ac91).
    /// A null <see cref="CreatedBy"/> means the Owner wrote the tab: every row that predates
    /// authorship, and every tab the Notes pane creates. A null <see cref="UpdatedBy"/> means the
    /// Owner was the last to save it.
    /// </summary>
    public sealed class ProjectNoteTab
    {
        public string Name { get; set; }

        public string Content { get; set; }

        public bool IsDefault { get; set; }

        public int Order { get; set; }

        /// <summary>The agent that created the tab, or null when the Owner did.</summary>
        public string CreatedBy { get; set; }

        /// <summary>The agent that last wrote the tab, or null when the Owner did.</summary>
        public string UpdatedBy { get; set; }

        public DateTime? UpdatedAt { get; set; }

        public bool IsAgentWritten => !string.IsNullOrEmpty(CreatedBy);
    }

    /// <summary>How an agent's note write treats an existing tab of the same name.</summary>
    public enum AgentNoteWriteMode
    {
        /// <summary>Add a new tab. Collides with an own tab: refused. With anyone else's: a suffixed name.</summary>
        New,

        /// <summary>Append to the agent's own tab (created when missing). Never touches anyone else's.</summary>
        Append,

        /// <summary>Replace the agent's own tab, only while nobody else has written it since.</summary>
        Replace,
    }

    public enum AgentNoteWriteOutcome
    {
        Created,
        Appended,
        Replaced,
        Refused,
    }

    /// <summary>
    /// Args of <c>MessageBroker.ProjectNotesChanged</c>. Immutable, like the other broker event args,
    /// so an earlier subscriber cannot redirect later ones at a different project.
    /// </summary>
    public sealed class ProjectNotesChangedEventArgs : EventArgs
    {
        public ProjectNotesChangedEventArgs(string projectKey, string tabName, string agentName, AgentNoteWriteOutcome outcome)
        {
            ProjectKey = projectKey;
            TabName = tabName;
            AgentName = agentName;
            Outcome = outcome;
        }

        /// <summary>The canonical note-tab key (<c>TaskDatabase.CanonicalNoteTabKey</c>).</summary>
        public string ProjectKey { get; }

        public string TabName { get; }

        public string AgentName { get; }

        public AgentNoteWriteOutcome Outcome { get; }
    }

    /// <summary>Result of <c>TaskDatabase.WriteAgentNoteTab</c>.</summary>
    public sealed class AgentNoteWriteResult
    {
        public AgentNoteWriteOutcome Outcome { get; init; }

        /// <summary>The tab actually written: differs from the requested name when a collision was suffixed.</summary>
        public string TabName { get; init; }

        /// <summary>Why a write was refused (null otherwise).</summary>
        public string Reason { get; init; }

        public bool Succeeded => Outcome != AgentNoteWriteOutcome.Refused;

        public static AgentNoteWriteResult Refused(string tabName, string reason)
            => new AgentNoteWriteResult { Outcome = AgentNoteWriteOutcome.Refused, TabName = tabName, Reason = reason };
    }
}
