namespace MultiTerminal.MCPServer.Models
{
    /// <summary>
    /// A unit of institutional knowledge captured from sessions, tasks, or manual input.
    /// Categories: decision, pattern, gotcha, anti_pattern, debug_insight, preference.
    /// Confidence levels: observed, confirmed, deprecated.
    /// </summary>
    public class KnowledgeEntry
    {
        public int Id { get; set; }

        /// <summary>Project this knowledge belongs to (null = global).</summary>
        public string ProjectId { get; set; }

        /// <summary>
        /// Semantic category: decision, pattern, gotcha, anti_pattern, debug_insight, preference.
        /// </summary>
        public string Category { get; set; }

        public string Title { get; set; }
        public string Content { get; set; }

        /// <summary>Where this knowledge came from: session, task, manual.</summary>
        public string SourceType { get; set; }

        /// <summary>ID of the source (session ID, task ID, etc.).</summary>
        public string SourceId { get; set; }

        /// <summary>Agent that contributed this knowledge.</summary>
        public string SourceAgent { get; set; }

        /// <summary>Comma-separated tags for filtering.</summary>
        public string Tags { get; set; }

        /// <summary>Confidence level: observed, confirmed, deprecated.</summary>
        public string Confidence { get; set; }

        /// <summary>ID of a newer KnowledgeEntry that supersedes this one.</summary>
        public int? SupersededBy { get; set; }

        public string CreatedAt { get; set; }
        public string UpdatedAt { get; set; }
    }
}
