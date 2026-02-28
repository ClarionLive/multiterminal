namespace MultiTerminal.Models
{
    /// <summary>
    /// Represents a Claude command configuration.
    /// </summary>
    public class ClaudeCommand
    {
        /// <summary>
        /// The command string (e.g., "claude", "claude -c", "claude --dangerously-skip-permissions").
        /// </summary>
        public string Command { get; set; }

        /// <summary>
        /// Whether this is the default command to use when opening projects.
        /// </summary>
        public bool IsDefault { get; set; }

        public ClaudeCommand()
        {
            Command = "";
            IsDefault = false;
        }

        public ClaudeCommand(string command, bool isDefault = false)
        {
            Command = command ?? "";
            IsDefault = isDefault;
        }

        public override string ToString()
        {
            return Command;
        }
    }
}
