namespace MultiTerminal.Models
{
    /// <summary>
    /// Represents the saved state of a terminal for session persistence.
    /// </summary>
    public class TerminalSessionInfo
    {
        /// <summary>
        /// The working directory the terminal was in when saved.
        /// </summary>
        public string WorkingDirectory { get; set; }

        /// <summary>
        /// The font size of the terminal.
        /// </summary>
        public float FontSize { get; set; }

        /// <summary>
        /// Custom user-defined tab title (optional).
        /// </summary>
        public string CustomTitle { get; set; }

        /// <summary>
        /// The pane's stable key (task d71bb221). A pane's DocId is minted afresh every run, so it
        /// cannot carry a link from one run to the next; this key is minted once per pane and
        /// restored with it. Null for an entry saved before the key existed.
        /// </summary>
        public string PaneKey { get; set; }

        /// <summary>
        /// The agent name this pane was a spawned helper under, or null when it is not a helper.
        /// The link holds only while the pane runs under this same name.
        /// </summary>
        public string HelperName { get; set; }

        /// <summary>
        /// The <see cref="PaneKey"/> of the pane that spawned this helper, proven at spawn time.
        /// Null with a <see cref="HelperName"/> means "a helper whose PM is unknown".
        /// </summary>
        public string SpawnerPaneKey { get; set; }
    }
}
