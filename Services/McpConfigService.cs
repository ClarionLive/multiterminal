using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        private readonly Action<string, string> _log;

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

        /// <param name="projectDb">Project database for registry access.</param>
        /// <param name="log">
        ///   Optional logging callback (source, message). When null, falls back to Debug.WriteLine.
        ///   Wire this to DebugLogService.Info for visibility in the MultiTerminal debug panel.
        /// </param>
        public McpConfigService(ProjectDatabase projectDb, Action<string, string> log = null)
        {
            _projectDb = projectDb ?? throw new ArgumentNullException(nameof(projectDb));
            _log = log ?? ((source, msg) => System.Diagnostics.Debug.WriteLine($"[{source}] {msg}"));
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
                _log("McpConfig", "Registry already seeded (multiterminal entry exists), skipping.");
                return 0;
            }

            // Try mcp.json (Claude Code format used inside .claude/)
            string mcpJsonPath = Path.Combine(mtSourcePath, ".claude", "mcp.json");
            if (!File.Exists(mcpJsonPath))
            {
                _log("McpConfig", $"No .claude/mcp.json found at {mtSourcePath}, skipping registry seeding.");
                return 0;
            }

            try
            {
                int count = ImportFromMcpJsonFile(mcpJsonPath, defaultTier: "optional");
                _log("McpConfig", $"Seeded {count} MCP server(s) from {mcpJsonPath}");
                return count;
            }
            catch (Exception ex)
            {
                _log("McpConfig", $"Seeding failed: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Generates the project-level mcpServers JSON.
        /// Includes only:
        ///   - "multiterminal" tier entries (the MT MCP server itself)
        ///   - "optional" tier entries that are enabled for the given project
        /// Global-tier servers are NOT included here — they're synced to Claude Code's
        /// user scope via CLI (see SyncGlobalMcpServers).
        /// Returns a JSON string: {"mcpServers": { ... }}.
        /// </summary>
        public string GenerateMcpJsonForProject(string projectId)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                throw new ArgumentException("projectId is required", nameof(projectId));

            // Get set of optional server names enabled for this project
            var enabledOptional = _projectDb.GetEnabledMcpServerNamesForProject(projectId);

            // Gather all registry entries
            var allEntries = _projectDb.GetAllMcpRegistryEntries();

            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"mcpServers\": {");

            bool first = true;
            foreach (var entry in allEntries)
            {
                // Project-level: multiterminal + enabled optional only.
                // Global-tier servers are synced via CLI (SyncGlobalMcpServers).
                bool include = entry.Tier == "multiterminal"
                            || (entry.Tier == "optional" && enabledOptional.Contains(entry.ServerName));

                if (!include) continue;

                if (!first) sb.AppendLine(",");
                first = false;

                string configBody = ValidateConfigJsonBody(entry.ConfigJson, entry.ServerName);
                sb.Append($"    \"{EscapeJsonString(entry.ServerName)}\": {configBody}");
            }

            if (!first) sb.AppendLine();   // newline after last entry

            sb.AppendLine("  }");
            sb.Append("}");

            return sb.ToString();
        }

        /// <summary>
        /// Syncs global-tier MCP servers to Claude Code's user scope via the CLI.
        /// Runs `claude mcp add --scope user` for each global-tier registry entry,
        /// and removes user-scope servers that were demoted from global in the registry.
        /// Returns the number of servers synced (added/updated).
        /// </summary>
        public int SyncGlobalMcpServers()
        {
            var allEntries = _projectDb.GetAllMcpRegistryEntries();
            var globalEntries = allEntries.Where(e => e.Tier == "global").ToList();
            int synced = 0;

            foreach (var entry in globalEntries)
            {
                try
                {
                    // Remove first to ensure clean state (ignore errors — may not exist yet)
                    RunClaudeCommand($"mcp remove \"{entry.ServerName}\" --scope user");

                    if (AddClaudeMcpServer(entry))
                        synced++;
                }
                catch (Exception ex)
                {
                    _log("McpConfig", $"Failed to sync global server '{entry.ServerName}': {ex.Message}");
                }
            }

            // Remove user-scope servers that are in our registry but no longer global tier.
            // (They were demoted from global to optional/multiterminal.)
            // Never touch servers we don't know about — user may have added them via CLI.
            var globalNames = new HashSet<string>(
                globalEntries.Select(e => e.ServerName), StringComparer.OrdinalIgnoreCase);
            var registryNames = new HashSet<string>(
                allEntries.Select(e => e.ServerName), StringComparer.OrdinalIgnoreCase);

            foreach (var entry in allEntries)
            {
                if (entry.Tier == "global") continue;  // already handled above
                if (!registryNames.Contains(entry.ServerName)) continue;

                // This server is in our registry but NOT global — remove from user scope
                try
                {
                    RunClaudeCommand($"mcp remove \"{entry.ServerName}\" --scope user");
                }
                catch (Exception ex)
                {
                    _log("McpConfig", $"Failed to remove demoted server '{entry.ServerName}': {ex.Message}");
                }
            }

            _log("McpConfig", $"SyncGlobalMcpServers: synced {synced} of {globalEntries.Count} global server(s)");
            return synced;
        }

        /// <summary>
        /// Adds an MCP server to Claude Code's user scope via CLI.
        /// Stdio:    claude mcp add {name} --scope user -- {command} {args...}
        /// HTTP/SSE: claude mcp add {name} --transport {type} --scope user {url}
        /// Runs silently (no visible window). Returns true on success.
        /// </summary>
        private bool AddClaudeMcpServer(McpRegistryEntry entry)
        {
            string arguments = BuildMcpAddArguments(entry);
            if (arguments == null)
            {
                _log("McpConfig", $"Could not build CLI args for '{entry.ServerName}' — skipping");
                return false;
            }

            var (exitCode, stdout, stderr) = RunClaudeCommand(arguments);

            if (exitCode != 0)
            {
                _log("McpConfig", $"Add '{entry.ServerName}' failed: exit={exitCode}, stderr={stderr.Trim()}");
                return false;
            }

            _log("McpConfig", $"Added '{entry.ServerName}' to user scope via CLI: {stdout.Trim()}");
            return true;
        }

        /// <summary>
        /// Builds the CLI arguments string for `claude mcp add`.
        /// Parses the stored config_json to extract command/args (stdio) or url (http/sse).
        /// Includes -e KEY=VALUE for any env vars in the config.
        /// Returns null if the config can't be parsed.
        /// </summary>
        private string BuildMcpAddArguments(McpRegistryEntry entry)
        {
            try
            {
                using var doc = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(entry.ConfigJson) ? "{}" : entry.ConfigJson);
                var root = doc.RootElement;

                if (entry.TransportType == "http" || entry.TransportType == "sse")
                {
                    if (!root.TryGetProperty("url", out var urlProp)) return null;
                    string url = urlProp.GetString();
                    string envFlags = BuildEnvFlags(root);
                    return $"mcp add \"{entry.ServerName}\" --transport {entry.TransportType} --scope user {envFlags}\"{url}\"";
                }

                // stdio transport
                if (!root.TryGetProperty("command", out var cmdProp)) return null;
                string command = cmdProp.GetString();

                var argParts = new List<string>();
                if (root.TryGetProperty("args", out var argsProp))
                {
                    foreach (var arg in argsProp.EnumerateArray())
                    {
                        string val = arg.GetString();
                        if (val != null)
                            argParts.Add($"\"{val}\"");
                    }
                }

                string envFlags2 = BuildEnvFlags(root);
                string argsStr = argParts.Count > 0 ? " " + string.Join(" ", argParts) : "";
                return $"mcp add \"{entry.ServerName}\" --scope user {envFlags2}-- {command}{argsStr}";
            }
            catch (Exception ex)
            {
                _log("McpConfig", $"Failed to parse config for '{entry.ServerName}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Extracts env vars from config JSON and returns -e KEY=VALUE flags.
        /// Returns empty string if no env vars present.
        /// </summary>
        private static string BuildEnvFlags(JsonElement root)
        {
            if (!root.TryGetProperty("env", out var envObj)) return "";
            if (envObj.ValueKind != JsonValueKind.Object) return "";

            var sb = new StringBuilder();
            foreach (var prop in envObj.EnumerateObject())
            {
                string val = prop.Value.GetString() ?? "";
                sb.Append($"-e {prop.Name}=\"{val}\" ");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Runs a `claude` CLI command silently (no visible window, captures output).
        /// Uses cmd.exe /c to resolve claude.cmd (npm installs the CLI as a .cmd shim;
        /// Process.Start with UseShellExecute=false calls CreateProcess which only
        /// resolves .exe files, not .cmd/.bat).
        /// Returns (exitCode, stdout, stderr). Times out after 15 seconds.
        /// </summary>
        private (int ExitCode, string Stdout, string Stderr) RunClaudeCommand(string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c claude {arguments}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _log("McpConfig", $"Running: claude {arguments}");

            using var process = Process.Start(psi);
            if (process == null)
            {
                _log("McpConfig", "ERROR: Failed to start cmd.exe process");
                return (-1, "", "Failed to start cmd.exe process");
            }

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            bool exited = process.WaitForExit(15000);

            if (!exited)
            {
                _log("McpConfig", $"TIMEOUT: claude {arguments} did not exit within 15s");
                try { process.Kill(); } catch { }
                return (-2, stdout, "Process timed out after 15 seconds");
            }

            if (process.ExitCode != 0)
                _log("McpConfig", $"FAILED (exit={process.ExitCode}): claude {arguments} — stderr: {stderr.Trim()}");

            return (process.ExitCode, stdout, stderr);
        }

        /// <summary>
        /// Ensures MCP configs are up-to-date for a project launch:
        ///   1. Global-tier servers synced to Claude Code user scope via CLI
        ///   2. Project {sourcePath}/.mcp.json (multiterminal + optional servers)
        /// Called before terminal launch to guarantee Claude Code picks up current settings.
        /// </summary>
        public void EnsureMcpConfigsForProject(string projectId, string sourcePath = null)
        {
            // Sync global-tier servers to Claude Code user scope
            try
            {
                SyncGlobalMcpServers();
            }
            catch (Exception ex)
            {
                _log("McpConfig", $"Failed to sync global MCP servers: {ex.Message}");
            }

            // Write project-level config
            try
            {
                WriteMcpJsonToProject(projectId, sourcePath);
            }
            catch (Exception ex)
            {
                _log("McpConfig", $"Failed to write project .mcp.json: {ex.Message}");
            }
        }

        /// <summary>
        /// Regenerates all MCP configs: global-tier via CLI + .mcp.json for every project
        /// that has a source path. Returns (globalCount, projectCount) on success.
        /// </summary>
        public (int GlobalCount, int ProjectCount) RegenerateAllMcpConfigs()
        {
            _log("McpConfig", "RegenerateAllMcpConfigs: START");
            int globalCount = SyncGlobalMcpServers();
            _log("McpConfig", $"RegenerateAllMcpConfigs: synced {globalCount} global server(s) via CLI");

            int projectCount = 0;
            var projects = _projectDb.GetAllRichProjects();
            _log("McpConfig", $"RegenerateAllMcpConfigs: found {projects.Count} projects");
            foreach (var project in projects)
            {
                string sourcePath = project.SourcePath ?? project.Path;
                if (string.IsNullOrWhiteSpace(sourcePath) || !Directory.Exists(sourcePath))
                {
                    _log("McpConfig", $"Skipping project '{project.Name}': no valid source path ({sourcePath})");
                    continue;
                }

                try
                {
                    WriteMcpJsonToProject(project.Id, sourcePath);
                    projectCount++;
                    _log("McpConfig", $"Wrote .mcp.json for project '{project.Name}' at {sourcePath}");
                }
                catch (Exception ex)
                {
                    _log("McpConfig", $"Skipped project '{project.Name}': {ex.Message}");
                }
            }

            _log("McpConfig", $"RegenerateAllMcpConfigs: DONE global={globalCount}, projects={projectCount}");
            return (globalCount, projectCount);
        }

        /// <summary>
        /// Generates the combined .mcp.json for a project and writes it to the project's source path.
        /// Creates a .mcp.json.bak backup of any existing file before overwriting.
        /// </summary>
        /// <param name="projectId">Project ID.</param>
        /// <param name="sourcePath">
        ///   Optional: caller-provided source path for the project directory.
        ///   When supplied, the SQLite project lookup is skipped — useful when the project
        ///   originates from the JSON registry and may not yet be in the SQLite projects table.
        ///   When null or empty, falls back to a SQLite lookup via GetRichProject().
        /// </param>
        /// <returns>Full path of the written .mcp.json file.</returns>
        public string WriteMcpJsonToProject(string projectId, string sourcePath = null)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                throw new ArgumentException("projectId is required", nameof(projectId));

            // Use the caller-provided path when available — avoids needing the project in SQLite
            // (projects from the JSON registry may not be migrated to SQLite yet).
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                var project = _projectDb.GetRichProject(projectId);
                if (project == null)
                    throw new InvalidOperationException($"Project not found: {projectId}");

                sourcePath = project.SourcePath ?? project.Path;
            }

            if (string.IsNullOrWhiteSpace(sourcePath))
                throw new InvalidOperationException($"Project '{projectId}' has no source path configured.");

            if (!Directory.Exists(sourcePath))
                throw new DirectoryNotFoundException($"Project source directory does not exist: {sourcePath}");

            string outputPath = Path.Combine(sourcePath, ".mcp.json");
            string backupPath = outputPath + ".bak";

            // Backup existing file before overwriting
            if (File.Exists(outputPath))
            {
                File.Copy(outputPath, backupPath, overwrite: true);
                _log("McpConfig", $"Backed up existing .mcp.json to {backupPath}");
            }

            string content = GenerateMcpJsonForProject(projectId);
            File.WriteAllText(outputPath, content, Encoding.UTF8);

            _log("McpConfig", $"Wrote .mcp.json to {outputPath}");
            return outputPath;
        }

        /// <summary>
        /// Seeds the MCP registry from the user's Claude Code config (~/.claude.json).
        /// This file contains MCP servers configured at the user scope via `claude mcp add --scope user`.
        /// Imported servers default to "global" tier (available to all projects).
        /// Idempotent — skips servers that already exist in the registry (upsert).
        /// </summary>
        /// <returns>Number of servers imported/updated, or 0 if file not found or no mcpServers key.</returns>
        public int SeedRegistryFromUserConfig()
        {
            // %USERPROFILE%\.claude.json — Claude Code's user-level config
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string claudeJsonPath = Path.Combine(userProfile, ".claude.json");

            if (!File.Exists(claudeJsonPath))
            {
                _log("McpConfig", $"No user config found at {claudeJsonPath}, skipping user MCP import.");
                return 0;
            }

            try
            {
                int count = ImportFromMcpJsonFile(claudeJsonPath, defaultTier: "global");
                _log("McpConfig", $"Imported {count} MCP server(s) from user config {claudeJsonPath}");
                return count;
            }
            catch (Exception ex)
            {
                _log("McpConfig", $"Failed to import from user config: {ex.Message}");
                return 0;
            }
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
                    // Static method — can't use _log. This is a rare edge case during JSON generation.
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
