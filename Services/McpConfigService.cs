using System;
using System.IO;
using System.Text;
using System.Text.Json;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Manages MCP configuration lifecycle:
    ///  - Importing servers from an existing .mcp.json or mcp.json file into the registry
    ///  - Generating a combined .mcp.json payload for a project (MT + global + enabled optional)
    ///  - Writing the generated payload to the project's source directory with backup
    /// </summary>
    public class McpConfigService
    {
        private readonly ProjectDatabase _projectDb;

        // Env var key name substrings that indicate the value is a secret.
        // Only applied to values inside "env" objects (not arbitrary string fields).
        // When any of these substrings appear (case-insensitive) in an env var name,
        // its value is replaced with a ${ENV_VAR_NAME} placeholder on import.
        private static readonly string[] SecretKeywords = new[]
        {
            "key", "token", "secret", "password", "pass", "auth", "credential",
            "api_key", "apikey", "access_token", "bearer", "pat", "private",
            "anthropic", "claude", "openai", "gemini", "cohere"
        };

        public McpConfigService(ProjectDatabase projectDb)
        {
            _projectDb = projectDb ?? throw new ArgumentNullException(nameof(projectDb));
        }

        /// <summary>
        /// Reads a Claude MCP config file (either mcp.json or .mcp.json format) and upserts
        /// each server into the mcp_registry table.
        /// Env var values that look like secrets are stored as ${ENV_VAR_NAME} placeholders.
        /// </summary>
        /// <param name="filePath">Absolute path to the MCP config file.</param>
        /// <param name="defaultTier">
        ///   Tier to assign servers that are not auto-detected as "multiterminal".
        ///   Defaults to "optional".
        /// </param>
        /// <returns>Number of servers imported/updated.</returns>
        public int ImportFromMcpJsonFile(string filePath, string defaultTier = "optional")
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"MCP config file not found: {filePath}", filePath);

            string json = File.ReadAllText(filePath, Encoding.UTF8);

            // Both mcp.json ({"mcpServers":{...}}) formats are supported.
            // Claude Code uses "mcpServers" as the top-level key.
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("mcpServers", out var mcpServers))
                return 0;

            int count = 0;

            foreach (var server in mcpServers.EnumerateObject())
            {
                string serverName = server.Name;
                var configObj = server.Value;

                string tier = AutoDetectTier(serverName, configObj, defaultTier);
                string transportType = DetectTransportType(configObj);
                string command = ExtractCommand(configObj, transportType);
                string configJson = SanitizeConfigJson(configObj);
                string displayName = MakeDisplayName(serverName);

                var entry = new McpRegistryEntry
                {
                    ServerName    = serverName,
                    DisplayName   = displayName,
                    Description   = null,   // no description in raw mcp.json — can be set later via UI
                    ConfigJson    = configJson,
                    Tier          = tier,
                    TransportType = transportType,
                    Command       = command,
                    CreatedAt     = DateTime.UtcNow,
                    UpdatedAt     = DateTime.UtcNow
                };

                _projectDb.SaveMcpRegistryEntry(entry);
                count++;
            }

            return count;
        }

        /// <summary>
        /// Seeds the MCP registry from the MultiTerminal .claude/mcp.json file on startup.
        /// Runs once at startup; skips seeding if the "multiterminal" server is already in the registry
        /// (idempotent — safe to call on every startup).
        /// </summary>
        /// <param name="mtSourcePath">Path to the MultiTerminal source directory (contains .claude/mcp.json).</param>
        /// <returns>Number of servers added/updated, or 0 if already seeded or file not found.</returns>
        public int SeedRegistryFromMtMcpJson(string mtSourcePath)
        {
            if (string.IsNullOrWhiteSpace(mtSourcePath))
                return 0;

            // Check if MT server is already in the registry (idempotency check)
            var existing = _projectDb.GetMcpRegistryEntry("multiterminal");
            if (existing != null)
            {
                System.Diagnostics.Debug.WriteLine("[McpConfigService] Registry already seeded (multiterminal entry exists), skipping.");
                return 0;
            }

            // Try mcp.json (Claude Code format used inside .claude/)
            string mcpJsonPath = Path.Combine(mtSourcePath, ".claude", "mcp.json");
            if (!File.Exists(mcpJsonPath))
            {
                System.Diagnostics.Debug.WriteLine($"[McpConfigService] No .claude/mcp.json found at {mtSourcePath}, skipping registry seeding.");
                return 0;
            }

            try
            {
                int count = ImportFromMcpJsonFile(mcpJsonPath, defaultTier: "optional");
                System.Diagnostics.Debug.WriteLine($"[McpConfigService] Seeded {count} MCP server(s) from {mcpJsonPath}");
                return count;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[McpConfigService] Seeding failed: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Generates the combined mcpServers JSON object for a project.
        /// Combines:
        ///   - All "multiterminal" tier entries (always included — the MT MCP server itself)
        ///   - All "global" tier entries (always included for every project)
        ///   - "optional" tier entries that are enabled for the given project
        /// Returns a JSON string: {"mcpServers": { ... }}.
        /// </summary>
        public string GenerateMcpJsonForProject(string projectId)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                throw new ArgumentException("projectId is required", nameof(projectId));

            // Get set of optional server names enabled for this project (reuses existing targeted query)
            var enabledOptional = _projectDb.GetEnabledMcpServerNamesForProject(projectId);

            // Gather all registry entries
            var allEntries = _projectDb.GetAllMcpRegistryEntries();

            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"mcpServers\": {");

            bool first = true;
            foreach (var entry in allEntries)
            {
                bool include = entry.Tier == "multiterminal"
                            || entry.Tier == "global"
                            || (entry.Tier == "optional" && enabledOptional.Contains(entry.ServerName));

                if (!include) continue;

                if (!first) sb.AppendLine(",");
                first = false;

                // Validate ConfigJson is a parseable JSON object before embedding.
                // Malformed or non-object config_json would produce a broken output file.
                string configBody = ValidateConfigJsonBody(entry.ConfigJson, entry.ServerName);
                sb.Append($"    \"{EscapeJsonString(entry.ServerName)}\": {configBody}");
            }

            if (!first) sb.AppendLine();   // newline after last entry

            sb.AppendLine("  }");
            sb.Append("}");

            return sb.ToString();
        }

        /// <summary>
        /// Generates the combined .mcp.json for a project and writes it to the project's source path.
        /// Creates a .mcp.json.bak backup of any existing file before overwriting.
        /// </summary>
        /// <param name="projectId">Project ID.</param>
        /// <returns>Full path of the written .mcp.json file.</returns>
        public string WriteMcpJsonToProject(string projectId)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                throw new ArgumentException("projectId is required", nameof(projectId));

            var project = _projectDb.GetRichProject(projectId);
            if (project == null)
                throw new InvalidOperationException($"Project not found: {projectId}");

            string sourcePath = project.SourcePath ?? project.Path;
            if (string.IsNullOrWhiteSpace(sourcePath))
                throw new InvalidOperationException($"Project {project.Name} has no source path configured.");

            if (!Directory.Exists(sourcePath))
                throw new DirectoryNotFoundException($"Project source directory does not exist: {sourcePath}");

            string outputPath = Path.Combine(sourcePath, ".mcp.json");
            string backupPath = outputPath + ".bak";

            // Backup existing file before overwriting
            if (File.Exists(outputPath))
            {
                File.Copy(outputPath, backupPath, overwrite: true);
                System.Diagnostics.Debug.WriteLine($"[McpConfigService] Backed up existing .mcp.json to {backupPath}");
            }

            string content = GenerateMcpJsonForProject(projectId);
            File.WriteAllText(outputPath, content, Encoding.UTF8);

            System.Diagnostics.Debug.WriteLine($"[McpConfigService] Wrote .mcp.json to {outputPath}");
            return outputPath;
        }

        // ── Private helpers ────────────────────────────────────────────────────

        /// <summary>
        /// Auto-detects the tier for a server based on name/config hints.
        /// Returns "multiterminal" for the MT server, otherwise returns defaultTier.
        /// </summary>
        private static string AutoDetectTier(string serverName, JsonElement configObj, string defaultTier)
        {
            // The MultiTerminal MCP server is always tier "multiterminal"
            if (string.Equals(serverName, "multiterminal", StringComparison.OrdinalIgnoreCase))
                return "multiterminal";

            // If the command/args reference the multiterminal-mcp index.js, treat as multiterminal
            if (configObj.TryGetProperty("args", out var args))
            {
                foreach (var arg in args.EnumerateArray())
                {
                    if (arg.GetString()?.IndexOf("multiterminal-mcp", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "multiterminal";
                }
            }

            return defaultTier;
        }

        /// <summary>
        /// Detects transport type from config: "http"/"sse" if "url" key is present, else "stdio".
        /// </summary>
        private static string DetectTransportType(JsonElement configObj)
        {
            if (configObj.TryGetProperty("url", out _))
            {
                // Check for sse transport hint
                if (configObj.TryGetProperty("transport", out var transport)
                    && string.Equals(transport.GetString(), "sse", StringComparison.OrdinalIgnoreCase))
                    return "sse";

                return "http";
            }

            return "stdio";
        }

        /// <summary>
        /// Extracts the primary command or URL for display purposes (not config generation).
        /// For stdio: returns the "command" field. For http/sse: returns the "url" field.
        /// </summary>
        private static string ExtractCommand(JsonElement configObj, string transportType)
        {
            if (transportType == "stdio")
            {
                return configObj.TryGetProperty("command", out var cmd) ? cmd.GetString() : null;
            }

            return configObj.TryGetProperty("url", out var url) ? url.GetString() : null;
        }

        /// <summary>
        /// Validates that a config_json string is a parseable JSON object.
        /// Returns the original string if valid, or "{}" if null/empty/invalid/non-object.
        /// Logs a warning for invalid entries so they're visible in debug output.
        /// </summary>
        private static string ValidateConfigJsonBody(string configJson, string serverName)
        {
            if (string.IsNullOrWhiteSpace(configJson))
                return "{}";

            try
            {
                using var doc = JsonDocument.Parse(configJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[McpConfigService] Warning: config_json for '{serverName}' is not a JSON object (got {doc.RootElement.ValueKind}), using {{}}");
                    return "{}";
                }

                return configJson;
            }
            catch (JsonException ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[McpConfigService] Warning: config_json for '{serverName}' is malformed JSON, using {{}}: {ex.Message}");
                return "{}";
            }
        }

        /// <summary>
        /// Serializes the server config element back to a JSON string,
        /// replacing secret env var values inside "env" objects with ${ENV_VAR_NAME} placeholders.
        /// Sanitization is scoped to "env" objects only — other string fields are passed through unchanged.
        /// </summary>
        private static string SanitizeConfigJson(JsonElement configObj)
        {
            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false });

            SanitizeJsonElement(writer, configObj, parentKey: null, insideEnvObject: false);

            writer.Flush();
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private static void SanitizeJsonElement(
            Utf8JsonWriter writer,
            JsonElement element,
            string parentKey,
            bool insideEnvObject)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var prop in element.EnumerateObject())
                    {
                        writer.WritePropertyName(prop.Name);
                        // Enter env-object scope when property name is "env" (case-insensitive)
                        bool childIsEnv = string.Equals(prop.Name, "env", StringComparison.OrdinalIgnoreCase);
                        SanitizeJsonElement(writer, prop.Value, prop.Name, insideEnvObject || childIsEnv);
                    }
                    writer.WriteEndObject();
                    break;

                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                    {
                        SanitizeJsonElement(writer, item, parentKey, insideEnvObject);
                    }
                    writer.WriteEndArray();
                    break;

                case JsonValueKind.String:
                    string strVal = element.GetString();
                    // Only redact when we're inside an "env" object AND the key looks like a secret.
                    // This prevents over-sanitizing legitimate fields like "command", "description", etc.
                    if (insideEnvObject && parentKey != null
                        && LooksLikeSecretKey(parentKey) && !string.IsNullOrEmpty(strVal))
                    {
                        writer.WriteStringValue($"${{{parentKey}}}");
                    }
                    else
                    {
                        writer.WriteStringValue(strVal);
                    }
                    break;

                case JsonValueKind.Number:
                    writer.WriteRawValue(element.GetRawText());
                    break;

                case JsonValueKind.True:
                    writer.WriteBooleanValue(true);
                    break;

                case JsonValueKind.False:
                    writer.WriteBooleanValue(false);
                    break;

                case JsonValueKind.Null:
                    writer.WriteNullValue();
                    break;
            }
        }

        /// <summary>
        /// Returns true if the key name suggests it holds a secret value.
        /// </summary>
        private static bool LooksLikeSecretKey(string key)
        {
            string lower = key.ToLowerInvariant();
            foreach (var keyword in SecretKeywords)
            {
                if (lower.Contains(keyword))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Converts a server_name slug to a human-readable display name.
        /// E.g. "everything-search" → "Everything Search", "sqlite" → "Sqlite"
        /// </summary>
        private static string MakeDisplayName(string serverName)
        {
            if (string.IsNullOrWhiteSpace(serverName))
                return serverName;

            // Replace hyphens and underscores with spaces then title-case each word
            var words = serverName.Replace('-', ' ').Replace('_', ' ').Split(' ');
            var sb = new StringBuilder();
            foreach (var word in words)
            {
                if (word.Length == 0) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(char.ToUpperInvariant(word[0]));
                if (word.Length > 1) sb.Append(word.Substring(1));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Escapes a string for use as a JSON string value (handles backslash, quotes, etc.).
        /// </summary>
        private static string EscapeJsonString(string value)
        {
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
        }
    }
}
