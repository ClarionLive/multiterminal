using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Ensures MultiTerminal's MCP servers are registered in the user's
    /// ~/.codex/config.toml so Codex CLI terminals launched by MT can reach
    /// them. Source of truth is the Claude Code .mcp.json at
    /// %APPDATA%\multiterminal\.mcp.json — entries are translated to TOML and
    /// written as [mcp_servers.NAME] tables inside a marker-delimited block.
    /// Everything outside the marker block (user settings, other MCP servers,
    /// profiles) is preserved verbatim.
    /// </summary>
    public static class CodexConfigService
    {
        private const string ManagedMarkerBegin = "# >>> MULTITERMINAL MANAGED — do not edit (begin) <<<";
        private const string ManagedMarkerEnd = "# <<< MULTITERMINAL MANAGED — do not edit (end) >>>";

        /// <summary>
        /// Returns the path to ~/.codex/config.toml (user-global Codex config).
        /// </summary>
        public static string GetCodexConfigPath()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(userProfile, ".codex", "config.toml");
        }

        /// <summary>
        /// Returns the path to %APPDATA%\multiterminal\.mcp.json if it exists, else null.
        /// On a fresh install the installer deletes this file (post-install.js treats it
        /// as legacy) — callers that need mcpServers content should prefer
        /// <see cref="GetMcpServersJson"/> which also reads the installed
        /// <c>~/.claude.json</c> as a fallback.
        /// </summary>
        public static string GetSourceMcpJsonPath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string p = Path.Combine(appData, "multiterminal", ".mcp.json");
            return File.Exists(p) ? p : null;
        }

        /// <summary>
        /// Returns a JSON document containing an <c>mcpServers</c> object suitable for
        /// TOML translation, or null if no source of MCP server definitions is
        /// available.
        ///
        /// Lookup order:
        /// 1. <c>%APPDATA%\multiterminal\.mcp.json</c> (legacy path; present on dev
        ///    machines and when something regenerates it).
        /// 2. <c>~/.claude.json</c> — the authoritative Claude Code user config that
        ///    the MultiTerminal installer (post-install.js) writes. We extract just
        ///    the <c>mcpServers</c> subtree so we don't depend on the rest of
        ///    Claude Code's state.
        ///
        /// Returning the JSON text directly (rather than just a path) keeps the
        /// caller free to operate on the content without needing an intermediate
        /// file — important because the legacy path gets deleted on fresh installs
        /// and we don't want to resurrect a file the installer is trying to remove.
        /// </summary>
        public static string GetMcpServersJson()
        {
            string mtLegacy = GetSourceMcpJsonPath();
            if (mtLegacy != null)
            {
                try { return File.ReadAllText(mtLegacy); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CodexConfigService] Legacy .mcp.json read failed, falling back: {ex.Message}");
                }
            }

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string claudeJson = Path.Combine(userProfile, ".claude.json");
            if (!File.Exists(claudeJson))
                return null;

            try
            {
                string full = File.ReadAllText(claudeJson);
                using var doc = JsonDocument.Parse(full);
                if (!doc.RootElement.TryGetProperty("mcpServers", out var servers)
                    || servers.ValueKind != JsonValueKind.Object)
                    return null;

                return "{\"mcpServers\":" + servers.GetRawText() + "}";
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Ensures MultiTerminal's MCP servers are registered in ~/.codex/config.toml.
        /// Idempotent and content-diffing (no write if nothing changed). On first
        /// modification of an existing file, a .multiterminal.bak sibling is created.
        /// Returns the config path written, or null if no MCP servers could be
        /// resolved (neither <c>%APPDATA%\multiterminal\.mcp.json</c> nor
        /// <c>~/.claude.json</c> contained a usable <c>mcpServers</c> object), or
        /// if the resolved <c>mcpServers</c> object is empty (which would otherwise
        /// silently launch a Codex terminal with no MCP wiring — see
        /// <c>NATIVE_TEAMS_CONTINUATION.md</c> for the original failure report).
        ///
        /// Writes are atomic: content is staged in
        /// <c>~/.codex/config.toml.mt-new</c> then replaced via
        /// <see cref="File.Move(string, string, bool)"/> with overwrite=true, which
        /// maps to a single ReplaceFile-style syscall on Windows. This prevents two
        /// concurrent Codex launches from racing read-modify-write cycles and
        /// clobbering each other, and prevents a crash mid-write from leaving a
        /// truncated config file.
        /// </summary>
        public static string EnsureMcpRegistration()
        {
            string jsonText = GetMcpServersJson();
            if (jsonText == null)
                return null;

            // Reject empty mcpServers{}. The JSON is well-formed but has no servers
            // to register. If we continued, BuildManagedTomlBlock would emit a block
            // with just the markers, EnsureMcpRegistration would return a non-null
            // path, and BuildCodexCommand would interpret that as a healthy bootstrap
            // — but Codex would have zero MCP tools and could not join the team.
            // NATIVE_TEAMS_CONTINUATION.md documents the exact empty-state failure.
            if (!HasAnyMcpServer(jsonText))
                return null;

            string managedBlock = BuildManagedTomlBlock(jsonText);

            string configPath = GetCodexConfigPath();
            string configDir = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(configDir))
                Directory.CreateDirectory(configDir);

            string existing = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
            string updated = ReplaceOrAppendManagedBlock(existing, managedBlock);

            if (!string.Equals(existing, updated, StringComparison.Ordinal))
            {
                if (File.Exists(configPath))
                {
                    string bak = configPath + ".multiterminal.bak";
                    if (!File.Exists(bak))
                        File.Copy(configPath, bak, overwrite: false);
                }

                // Atomic write: stage to a sibling file, then replace. On Windows
                // File.Move(source, dest, overwrite:true) invokes MoveFileEx with
                // MOVEFILE_REPLACE_EXISTING which is a single filesystem operation
                // — concurrent readers see either the old file or the new file,
                // never a half-written one.
                string tmpPath = configPath + ".mt-new";
                File.WriteAllText(tmpPath, updated);
                try
                {
                    File.Move(tmpPath, configPath, overwrite: true);
                }
                catch
                {
                    // Best-effort cleanup if the atomic replace itself fails.
                    try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                    throw;
                }
            }

            return configPath;
        }

        /// <summary>
        /// Returns true when the supplied JSON has an <c>mcpServers</c> object with
        /// at least one entry. Used by <see cref="EnsureMcpRegistration"/> to refuse
        /// bootstrapping a Codex terminal against an empty server list.
        /// </summary>
        private static bool HasAnyMcpServer(string jsonText)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonText);
                if (!doc.RootElement.TryGetProperty("mcpServers", out var servers)
                    || servers.ValueKind != JsonValueKind.Object)
                    return false;

                using var enumerator = servers.EnumerateObject();
                return enumerator.MoveNext();
            }
            catch
            {
                return false;
            }
        }

        private static string BuildManagedTomlBlock(string mcpJsonText)
        {
            var sb = new StringBuilder();
            sb.AppendLine(ManagedMarkerBegin);
            sb.AppendLine("# Generated by MultiTerminal — sourced from %APPDATA%\\multiterminal\\.mcp.json");
            sb.AppendLine("# Content between the markers is replaced on every Codex terminal launch.");
            sb.AppendLine();

            using var doc = JsonDocument.Parse(mcpJsonText);
            if (doc.RootElement.TryGetProperty("mcpServers", out var servers)
                && servers.ValueKind == JsonValueKind.Object)
            {
                foreach (var server in servers.EnumerateObject())
                {
                    sb.AppendLine($"[mcp_servers.{TomlKey(server.Name)}]");

                    if (server.Value.TryGetProperty("command", out var cmd)
                        && cmd.ValueKind == JsonValueKind.String)
                    {
                        sb.AppendLine($"command = {TomlQuote(cmd.GetString())}");
                    }

                    if (server.Value.TryGetProperty("args", out var args)
                        && args.ValueKind == JsonValueKind.Array)
                    {
                        sb.Append("args = [");
                        bool first = true;
                        foreach (var a in args.EnumerateArray())
                        {
                            if (!first) sb.Append(", ");
                            sb.Append(TomlQuote(a.GetString() ?? string.Empty));
                            first = false;
                        }
                        sb.AppendLine("]");
                    }

                    if (server.Value.TryGetProperty("env", out var env)
                        && env.ValueKind == JsonValueKind.Object)
                    {
                        sb.AppendLine();
                        sb.AppendLine($"[mcp_servers.{TomlKey(server.Name)}.env]");
                        foreach (var kv in env.EnumerateObject())
                        {
                            string val = kv.Value.ValueKind == JsonValueKind.String
                                ? kv.Value.GetString() ?? string.Empty
                                : kv.Value.GetRawText();
                            sb.AppendLine($"{TomlKey(kv.Name)} = {TomlQuote(val)}");
                        }
                    }

                    sb.AppendLine();
                }
            }

            sb.Append(ManagedMarkerEnd);
            return sb.ToString();
        }

        private static string ReplaceOrAppendManagedBlock(string existing, string managedBlock)
        {
            int beginIdx = existing.IndexOf(ManagedMarkerBegin, StringComparison.Ordinal);
            int endIdx = existing.IndexOf(ManagedMarkerEnd, StringComparison.Ordinal);
            if (beginIdx >= 0 && endIdx > beginIdx)
            {
                int after = endIdx + ManagedMarkerEnd.Length;
                if (after < existing.Length && existing[after] == '\r') after++;
                if (after < existing.Length && existing[after] == '\n') after++;
                return existing.Substring(0, beginIdx) + managedBlock + Environment.NewLine + existing.Substring(after);
            }

            // Append. Separate from any existing content with a blank line.
            var sb = new StringBuilder(existing);
            if (sb.Length > 0 && !existing.EndsWith('\n'))
                sb.Append(Environment.NewLine);
            if (sb.Length > 0)
                sb.Append(Environment.NewLine);
            sb.Append(managedBlock);
            sb.Append(Environment.NewLine);
            return sb.ToString();
        }

        private static string TomlQuote(string value)
        {
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append($"\\u{(int)c:X4}");
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static string TomlKey(string key)
        {
            foreach (char c in key)
            {
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-'))
                    return TomlQuote(key);
            }
            return key;
        }
    }
}
