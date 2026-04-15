namespace MultiTerminal.MCPServer.Models
{
    /// <summary>
    /// A unit of institutional knowledge captured from sessions, tasks, or manual input.
    /// Categories: decision, pattern, gotcha, anti_pattern, debug_insight, preference, web_research.
    /// Confidence levels: observed, confirmed, likely, deprecated.
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

        /// <summary>Confidence level: observed, confirmed, likely, deprecated.</summary>
        public string Confidence { get; set; }

        /// <summary>ID of a newer KnowledgeEntry that supersedes this one.</summary>
        public int? SupersededBy { get; set; }

        /// <summary>SHA256 hash of normalized query for research cache deduplication.</summary>
        public string QueryHash { get; set; }

        /// <summary>Last time this entry was referenced (queried/injected). Used for attention decay.</summary>
        public string LastReferenced { get; set; }

        /// <summary>Number of times this entry has been referenced. Used for attention decay ranking.</summary>
        public int ReferenceCount { get; set; }

        public string CreatedAt { get; set; }
        public string UpdatedAt { get; set; }
    }
}
