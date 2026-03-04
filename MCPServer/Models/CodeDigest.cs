namespace MultiTerminal.MCPServer.Models
{
    /// <summary>
    /// A structured summary of a source file — purpose, key classes/methods, patterns, and gotchas.
    /// Digests go stale when the file's SHA-256 hash changes.
    /// </summary>
    public class CodeDigest
    {
        public int Id { get; set; }

        /// <summary>Project this digest belongs to.</summary>
        public string ProjectId { get; set; }

        /// <summary>Relative or absolute path to the source file.</summary>
        public string FilePath { get; set; }

        /// <summary>SHA-256 hash of the file at digest time. Used for stale detection.</summary>
        public string FileHash { get; set; }

        /// <summary>One-paragraph description of what this file does.</summary>
        public string Purpose { get; set; }

        /// <summary>JSON array of class names defined in the file.</summary>
        public string KeyClasses { get; set; }

        /// <summary>JSON array of {name, purpose, lineRange} objects for notable methods.</summary>
        public string KeyMethods { get; set; }

        /// <summary>Markdown description of architectural patterns used in this file.</summary>
        public string Patterns { get; set; }

        /// <summary>Markdown list of known gotchas, pitfalls, or non-obvious behaviors.</summary>
        public string Gotchas { get; set; }

        /// <summary>JSON array of file/package dependencies.</summary>
        public string Dependencies { get; set; }

        public int LineCount { get; set; }

        /// <summary>Model used to generate this digest (e.g. claude-sonnet-4-6).</summary>
        public string DigestModel { get; set; }

        public string CreatedAt { get; set; }
        public string UpdatedAt { get; set; }
    }
}
