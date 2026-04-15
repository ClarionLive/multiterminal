using System.Collections.Generic;

namespace MultiTerminal.MCPServer.Models
{
    /// <summary>
    /// A generated wiki article describing a subsystem (a coherent module that spans multiple files).
    /// Articles are assembled from the code graph + code digests + route parsing, then written to .claude/wiki/.
    /// </summary>
    public class WikiArticle
    {
        /// <summary>Subsystem identifier (kebab-case, matches manifest).</summary>
        public string Id { get; set; }

        /// <summary>Display name shown in article headers.</summary>
        public string Name { get; set; }

        /// <summary>One-paragraph subsystem description from the manifest.</summary>
        public string Description { get; set; }

        /// <summary>Tags from the manifest, e.g. "core", "ui", "persistence".</summary>
        public List<string> Tags { get; set; } = new List<string>();

        /// <summary>Files covered by this subsystem, with line counts.</summary>
        public List<WikiFileEntry> Files { get; set; } = new List<WikiFileEntry>();

        /// <summary>Key classes found in the root files, with file:line refs.</summary>
        public List<WikiSymbolEntry> KeyClasses { get; set; } = new List<WikiSymbolEntry>();

        /// <summary>Key methods found in the root files, with file:line refs.</summary>
        public List<WikiSymbolEntry> KeyMethods { get; set; } = new List<WikiSymbolEntry>();

        /// <summary>External callers — symbols outside this subsystem that call into it.</summary>
        public List<WikiSymbolEntry> ExternalCallers { get; set; } = new List<WikiSymbolEntry>();

        /// <summary>Subsystems this one depends on (derived from call graph edges).</summary>
        public List<string> DependsOn { get; set; } = new List<string>();

        /// <summary>Subsystems that depend on this one (reverse of DependsOn).</summary>
        public List<string> UsedBy { get; set; } = new List<string>();

        /// <summary>REST routes exposed by this subsystem (parsed from controller attributes).</summary>
        public List<WikiRouteEntry> Routes { get; set; } = new List<WikiRouteEntry>();

        /// <summary>Gotchas aggregated from code_digest entries for the root files.</summary>
        public List<string> Gotchas { get; set; } = new List<string>();

        /// <summary>Generated markdown body for this article.</summary>
        public string Markdown { get; set; }

        /// <summary>ISO timestamp when this article was generated.</summary>
        public string GeneratedAt { get; set; }
    }

    /// <summary>A file listed in a wiki article with its line count.</summary>
    public class WikiFileEntry
    {
        public string FilePath { get; set; }
        public int LineCount { get; set; }
        public string Purpose { get; set; }
    }

    /// <summary>A class or method referenced in a wiki article.</summary>
    public class WikiSymbolEntry
    {
        public string Name { get; set; }
        public string FilePath { get; set; }
        public int LineNumber { get; set; }
        public string SymbolType { get; set; }
    }

    /// <summary>A REST route exposed by a controller in a subsystem.</summary>
    public class WikiRouteEntry
    {
        public string HttpMethod { get; set; }
        public string Path { get; set; }
        public string FilePath { get; set; }
        public int LineNumber { get; set; }
    }
}
