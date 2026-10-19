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

        /// <summary>
        /// Bumped by every agent write to the tab (task c133b27d); 0 for a tab no agent has written.
        /// A counter rather than <see cref="UpdatedAt"/>, which has one-second resolution.
        /// </summary>
        public long AgentRev { get; set; }

        /// <summary>The highest <see cref="AgentRev"/> the Owner has seen.</summary>
        public long OwnerReadRev { get; set; }

        /// <summary>
        /// The Owner pinned the note to the top of the Notes rail (task 9a4a9d5e). Pinned notes keep the
        /// Owner's drag order in <see cref="Order"/>; every other note is sorted, never ordered by hand.
        /// </summary>
        public bool IsPinned { get; set; }

        /// <summary>The note's category id, or null for General (task 9a4a9d5e).</summary>
        public long? CategoryId { get; set; }

        /// <summary>The note's category name; <see cref="NoteCategory.General"/> when it has none.</summary>
        public string Category { get; set; } = NoteCategory.General;

        /// <summary>
        /// An agent wrote this tab after the Owner last saw it. Same rule as
        /// <c>TaskDatabase.NoteTabUnreadSql</c>: an Owner save clears <see cref="UpdatedBy"/>, which also
        /// marks the tab read.
        /// </summary>
        public bool IsUnread => !string.IsNullOrEmpty(UpdatedBy) && AgentRev > OwnerReadRev;

        public bool IsAgentWritten => !string.IsNullOrEmpty(CreatedBy);
    }

    /// <summary>
    /// A note category (task 9a4a9d5e). One list shared by every project, so the same names mean the same
    /// thing everywhere. <see cref="General"/> is built in: never stored, never renamed or deleted, and
    /// what a note with no category shows as.
    /// </summary>
    public sealed class NoteCategory
    {
        public const string General = "General";

        /// <summary>The stored id; null for <see cref="General"/>.</summary>
        public long? Id { get; init; }

        public string Name { get; init; }
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

        /// <summary>The category the note is filed under after the write (task 9a4a9d5e).</summary>
        public string Category { get; init; }

        /// <summary>
        /// Set when the requested category was not used: unknown (the note went to General), or the note
        /// already existed and keeps the category it has. Null when the request was honoured or omitted.
        /// </summary>
        public string CategoryNote { get; init; }

        public bool Succeeded => Outcome != AgentNoteWriteOutcome.Refused;

        public static AgentNoteWriteResult Refused(string tabName, string reason)
            => new AgentNoteWriteResult { Outcome = AgentNoteWriteOutcome.Refused, TabName = tabName, Reason = reason };
    }

    /// <summary>
    /// Args of <c>MessageBroker.ProjectNoteReadStateChanged</c> (task c133b27d): which project's unread
    /// count may have changed. Every listener recounts. When the Owner renamed or deleted a tab,
    /// <see cref="TabsChanged"/> is set so open panes also take the new tab list, not only the flags.
    /// </summary>
    public sealed class ProjectNoteReadStateChangedEventArgs : EventArgs
    {
        public ProjectNoteReadStateChangedEventArgs(string projectKey, bool tabsChanged = false, string renamedFrom = null, string renamedTo = null)
        {
            ProjectKey = projectKey;
            TabsChanged = tabsChanged;
            RenamedFrom = renamedFrom;
            RenamedTo = renamedTo;
        }

        /// <summary>The canonical note-tab key (<c>TaskDatabase.CanonicalNoteTabKey</c>).</summary>
        public string ProjectKey { get; }

        /// <summary>A tab was renamed or deleted, so other panes' tab lists are stale.</summary>
        public bool TabsChanged { get; }

        /// <summary>For a rename: the old name, so a pane can carry the Owner's unsaved typing across.</summary>
        public string RenamedFrom { get; }

        /// <summary>For a rename: the new name.</summary>
        public string RenamedTo { get; }
    }
}
