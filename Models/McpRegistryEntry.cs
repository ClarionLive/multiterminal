using System;

namespace MultiTerminal.Models
{
    /// <summary>
    /// Represents a globally-known MCP server in the registry.
    /// The registry is a catalog of all MCP servers the system is aware of.
    /// Per-project enablement is stored separately in project_mcp_servers.
    /// </summary>
    public class McpRegistryEntry
    {
        /// <summary>
        /// Auto-incrementing primary key.
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Unique identifier/slug for this MCP server (e.g. "multiterminal", "sqlite", "everything-search").
        /// </summary>
        public string ServerName { get; set; }

        /// <summary>
        /// Human-readable display name (e.g. "MultiTerminal MCP", "SQLite").
        /// </summary>
        public string DisplayName { get; set; }

        /// <summary>
        /// Brief description of what this MCP server provides.
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Full JSON config object for this server (the value of the mcpServers entry).
        /// Stored as a raw JSON blob. Example: {"command":"node","args":["path/to/index.js"]}
        /// Env var values that are secrets should use ${ENV_VAR_NAME} placeholder syntax.
        /// </summary>
        public string ConfigJson { get; set; }

        /// <summary>
        /// Availability tier:
        ///   "multiterminal" — always included, the MT MCP server itself
        ///   "global"        — always included for every project
        ///   "optional"      — included only when enabled for a project
        /// </summary>
        public string Tier { get; set; }

        /// <summary>
        /// Transport type: "stdio", "http", or "sse".
        /// Denormalized from ConfigJson for display/filtering without parsing JSON.
        /// </summary>
        public string TransportType { get; set; }

        /// <summary>
        /// Primary command or URL for this server.
        /// For stdio: the executable command (e.g. "node", "python").
        /// For http/sse: the base URL.
        /// Denormalized from ConfigJson for display/filtering without parsing JSON.
        /// </summary>
        public string Command { get; set; }

        /// <summary>
        /// When this entry was added to the registry.
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// When this entry was last updated.
        /// </summary>
        public DateTime UpdatedAt { get; set; }
    }
}
