using System;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Represents a saved prompt that can be reused in the terminal.
    /// </summary>
    public class Prompt
    {
        public string Id { get; set; }           // GUID
        public string Category { get; set; }     // e.g., "Claude", "Git", "Build"
        public string Description { get; set; }  // Short label for display
        public string Text { get; set; }         // Full prompt content
        public bool IsGlobal { get; set; }       // Global vs local storage
        public DateTime CreatedAt { get; set; }  // Timestamp
    }
}
