using System.Collections.Generic;

namespace MultiTerminal.MCPServer.Models
{
    /// <summary>
    /// A manifest entry defining a subsystem — a coherent module that spans multiple files.
    /// Loaded from .claude/wiki/wiki-manifest.json by the wiki generator.
    /// </summary>
    public class SubsystemDefinition
    {
        /// <summary>Kebab-case identifier used as the article filename (e.g. "message-broker").</summary>
        public string Id { get; set; }

        /// <summary>Display name shown in article headers (e.g. "MessageBroker").</summary>
        public string Name { get; set; }

        /// <summary>One-paragraph description of what the subsystem does.</summary>
        public string Description { get; set; }

        /// <summary>Root file paths relative to the project root. Defines the subsystem boundary.</summary>
        public List<string> RootFiles { get; set; } = new List<string>();

        /// <summary>
        /// Optional glob pattern for aggregating additional files (e.g. "API/Controllers/*.cs"
        /// for the REST API subsystem). Files matching the glob are included as root files.
        /// </summary>
        public string ControllerGlob { get; set; }

        /// <summary>Tags for categorization (e.g. "core", "ui", "persistence").</summary>
        public List<string> Tags { get; set; } = new List<string>();
    }

    /// <summary>Top-level manifest loaded from wiki-manifest.json.</summary>
    public class WikiManifest
    {
        public int Version { get; set; }
        public string Description { get; set; }
        public List<SubsystemDefinition> Subsystems { get; set; } = new List<SubsystemDefinition>();
    }
}
