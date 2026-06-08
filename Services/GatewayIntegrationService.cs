using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Lightweight DTO for a gateway server entry.
    /// Returned by GetAllGatewayServers() for UI picker consumption.
    /// Connected and ToolCount are populated from the server_status table (persisted by the gateway process);
    /// defaults to false/0 if the gateway hasn't written status yet.
    /// </summary>
    public class GatewayServerDto
    {
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public bool Enabled { get; set; }
        /// <summary>Whether the gateway reports this server as currently connected. Populated from server_status table.</summary>
        public bool Connected { get; set; }
        /// <summary>Number of tools exposed by this server. Populated from server_status table.</summary>
        public int ToolCount { get; set; }
    }

    /// <summary>
    /// Lightweight DTO for a discovered gateway tool.
    /// Populated from the gateway's server_tools table.
    /// </summary>
    public class GatewayToolDto
    {
        public string ServerName { get; set; }
        public string ToolName { get; set; }
        public string Description { get; set; }
        public string SchemaJson { get; set; }
        public string DiscoveredAt { get; set; }
    }

    /// <summary>
    /// Manages the MCP Gateway from MultiTerminal's side.
    /// Writes directly to the gateway's SQLite database (no IPC needed — both run on the same machine).
    /// All operations are idempotent and degrade gracefully if the gateway is not installed.
    /// </summary>
    public class GatewayIntegrationService : IDisposable
    {
        private SQLiteConnection _connection;
        private readonly string _gatewayDbPath;
        private readonly string _gatewayExePath;
        private readonly Action<string, string> _log;
        private bool _disposed;

        /// <summary>
        /// Path to the McpGateway project directory (used to locate McpGateway.exe).
        /// Configurable via the MT_MCP_GATEWAY_PATH env var; falls back to the historical
        /// dev-box default. An unset OR blank env var is treated as "not configured" so an
        /// empty-string override can't yield a degenerate relative path. When the path
        /// doesn't exist, gateway features degrade gracefully via
        /// <see cref="IsGatewayInstalled"/> (issue #5).
        /// </summary>
        private static readonly string GatewayProjectPath =
            Environment.GetEnvironmentVariable("MT_MCP_GATEWAY_PATH") is string p && !string.IsNullOrWhiteSpace(p)
                ? p
                : @"H:\DevLaptop\ClarionPowerShell\McpGateway";

        public GatewayIntegrationService(Action<string, string> log = null)
        {
            _log = log ?? ((source, msg) => System.Diagnostics.Debug.WriteLine($"[{source}] {msg}"));

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _gatewayDbPath = Path.Combine(appData, "multiterminal", "gateway", "gateway.db");
            _gatewayExePath = Path.Combine(GatewayProjectPath, "bin", "Release", "net8.0", "McpGateway.exe");
        }

        /// <summary>
        /// Returns true if the gateway is available (either the exe or the DB file exists).
        /// </summary>
        public bool IsGatewayInstalled()
        {
            return File.Exists(_gatewayExePath) || File.Exists(_gatewayDbPath);
        }

        /// <summary>
        /// Reads all server entries from the gateway's servers table, joined with server_status
        /// for live connection state and tool counts. Returns an empty list if the gateway DB is unavailable.
        /// </summary>
        public List<GatewayServerDto> GetAllGatewayServers()
        {
            var result = new List<GatewayServerDto>();
            var conn = GetConnection();
            if (conn == null) return result;

            try
            {
                using var cmd = new SQLiteCommand(@"
                    SELECT s.name, s.display_name, s.description, s.enabled,
                           COALESCE(ss.connected, 0) AS connected,
                           COALESCE(ss.tool_count, 0) AS tool_count
                    FROM servers s
                    LEFT JOIN server_status ss ON ss.server_name = s.name
                    ORDER BY s.display_name, s.name",
                    conn);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new GatewayServerDto
                    {
                        Name        = reader.GetString(0),
                        DisplayName = reader.GetString(1),
                        Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                        Enabled     = reader.GetInt32(3) == 1,
                        Connected   = reader.GetInt32(4) == 1,
                        ToolCount   = reader.GetInt32(5)
                    });
                }

                _log("Gateway", $"GetAllGatewayServers: returned {result.Count} server(s)");
            }
            catch (Exception ex)
            {
                _log("Gateway", $"GetAllGatewayServers failed: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Returns all discovered tools for a specific gateway backend server.
        /// Tool metadata is persisted by the gateway process when backends connect.
        /// </summary>
        public List<GatewayToolDto> GetToolsForServer(string serverName)
        {
            var result = new List<GatewayToolDto>();
            var conn = GetConnection();
            if (conn == null) return result;

            try
            {
                using var cmd = new SQLiteCommand(
                    "SELECT server_name, tool_name, description, schema_json, discovered_at FROM server_tools WHERE server_name = @name ORDER BY tool_name",
                    conn);
                cmd.Parameters.AddWithValue("@name", serverName);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    result.Add(ReadToolDto(reader));

                _log("Gateway", $"GetToolsForServer '{serverName}': returned {result.Count} tool(s)");
            }
            catch (Exception ex)
            {
                _log("Gateway", $"GetToolsForServer '{serverName}' failed: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Returns all discovered tools across all connected gateway backend servers.
        /// Optionally filtered to servers in a specific gateway profile.
        /// </summary>
        public List<GatewayToolDto> GetAllDiscoveredTools(string profileName = null)
        {
            var result = new List<GatewayToolDto>();
            var conn = GetConnection();
            if (conn == null) return result;

            try
            {
                if (profileName != null)
                {
                    using var cmd = new SQLiteCommand(@"
                        SELECT st.server_name, st.tool_name, st.description, st.schema_json, st.discovered_at
                        FROM server_tools st
                        INNER JOIN profile_servers ps ON ps.server_name = st.server_name
                        WHERE ps.profile_name = @profile
                        ORDER BY st.server_name, st.tool_name",
                        conn);
                    cmd.Parameters.AddWithValue("@profile", profileName);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                        result.Add(ReadToolDto(reader));
                }
                else
                {
                    using var cmd = new SQLiteCommand(
                        "SELECT server_name, tool_name, description, schema_json, discovered_at FROM server_tools ORDER BY server_name, tool_name",
                        conn);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                        result.Add(ReadToolDto(reader));
                }

                _log("Gateway", $"GetAllDiscoveredTools: returned {result.Count} tool(s)" +
                    (profileName != null ? $" (profile: {profileName})" : ""));
            }
            catch (Exception ex)
            {
                _log("Gateway", $"GetAllDiscoveredTools failed: {ex.Message}");
            }

            return result;
        }

        private static GatewayToolDto ReadToolDto(System.Data.IDataReader reader)
        {
            return new GatewayToolDto
            {
                ServerName  = reader.GetString(0),
                ToolName    = reader.GetString(1),
                Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                SchemaJson  = reader.IsDBNull(3) ? null : reader.GetString(3),
                DiscoveredAt = reader.IsDBNull(4) ? null : reader.GetString(4)
            };
        }

        /// <summary>
        /// Ensures the centralized MCP config file exists at %APPDATA%\multiterminal\.mcp.json
        /// with both mcp-gateway and multiterminal servers configured.
        /// Claude Code loads this file via --mcp-config flag (set by LaunchCommandBuilder).
        /// This replaces the old approach of registering servers at user scope via `claude mcp add`.
        /// </summary>
        public void EnsureGatewayRegistered()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string mcpConfigPath = Path.Combine(appData, "multiterminal", ".mcp.json");
                string mcpIndexJs = Path.Combine(appData, "multiterminal", "mcp", "index.js");

                // Build the mcpServers object
                var servers = new System.Text.StringBuilder();
                servers.AppendLine("{");
                servers.AppendLine("  \"mcpServers\": {");

                bool hasServer = false;

                // 1. mcp-gateway (dotnet run --project)
                if (Directory.Exists(GatewayProjectPath))
                {
                    string escapedPath = GatewayProjectPath.Replace("\\", "\\\\");
                    servers.AppendLine("    \"mcp-gateway\": {");
                    servers.AppendLine("      \"type\": \"stdio\",");
                    servers.AppendLine("      \"command\": \"dotnet\",");
                    servers.AppendLine($"      \"args\": [\"run\", \"--project\", \"{escapedPath}\"]");
                    servers.Append("    }");
                    hasServer = true;
                    _log("Gateway", "Added mcp-gateway to .mcp.json");
                }
                else
                {
                    _log("Gateway", $"Gateway project not found at {GatewayProjectPath}, skipping");
                }

                // 2. multiterminal MCP server (node index.js)
                if (File.Exists(mcpIndexJs))
                {
                    if (hasServer) servers.AppendLine(",");
                    string escapedJs = mcpIndexJs.Replace("\\", "\\\\");
                    servers.AppendLine("    \"multiterminal\": {");
                    servers.AppendLine("      \"type\": \"stdio\",");
                    servers.AppendLine("      \"command\": \"node\",");
                    servers.AppendLine($"      \"args\": [\"{escapedJs}\"]");
                    servers.Append("    }");
                    _log("Gateway", "Added multiterminal to .mcp.json");
                }
                else
                {
                    _log("Gateway", $"MultiTerminal MCP not found at {mcpIndexJs}, skipping");
                }

                servers.AppendLine();
                servers.AppendLine("  }");
                servers.Append("}");

                // Write the file
                File.WriteAllText(mcpConfigPath, servers.ToString(), System.Text.Encoding.UTF8);
                _log("Gateway", $"Wrote MCP config to {mcpConfigPath}");

                // Clean up: remove mcpServers from ~/.claude.json if present
                CleanUserScopeMcpServers();
            }
            catch (Exception ex)
            {
                _log("Gateway", $"EnsureGatewayRegistered failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Removes user-scope MCP server registrations from ~/.claude.json that we now manage
        /// via the centralized .mcp.json file. Uses `claude mcp remove` for clean removal.
        /// </summary>
        private void CleanUserScopeMcpServers()
        {
            try
            {
                foreach (var serverName in new[] { "mcp-gateway", "multiterminal" })
                {
                    var (exitCode, _, _) = RunClaudeCommand($"mcp remove {serverName} --scope user");
                    if (exitCode == 0)
                        _log("Gateway", $"Removed {serverName} from user scope (migrated to .mcp.json)");
                }
            }
            catch (Exception ex)
            {
                _log("Gateway", $"CleanUserScopeMcpServers warning: {ex.Message}");
            }
        }

        /// <summary>
        /// Syncs a project's enabled MCP servers to a gateway profile.
        /// Creates the profile if it doesn't exist, then reconciles the profile_servers join table.
        /// </summary>
        /// <param name="projectId">Project ID (for logging only).</param>
        /// <param name="projectName">Human-readable project name (used to derive profile name).</param>
        /// <param name="enabledServerNames">Set of server names that should be in this profile.</param>
        public void SyncProjectProfile(string projectId, string projectName, IEnumerable<string> enabledServerNames)
        {
            var conn = GetConnection();
            if (conn == null) return;

            string profileName = GetGatewayProfileName(projectName);
            var serverList = enabledServerNames?.ToList() ?? new List<string>();

            try
            {
                // Ensure profile exists
                using (var cmd = new SQLiteCommand(
                    "INSERT OR IGNORE INTO profiles (name, description, is_default, created_at) VALUES (@name, @desc, 0, @now)",
                    conn))
                {
                    cmd.Parameters.AddWithValue("@name", profileName);
                    cmd.Parameters.AddWithValue("@desc", $"Auto-synced from MultiTerminal project: {projectName}");
                    cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
                    cmd.ExecuteNonQuery();
                }

                // Get current profile_servers
                var currentServers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var cmd = new SQLiteCommand(
                    "SELECT server_name FROM profile_servers WHERE profile_name = @profile", conn))
                {
                    cmd.Parameters.AddWithValue("@profile", profileName);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                        currentServers.Add(reader.GetString(0));
                }

                // Add missing servers
                var toAdd = serverList.Where(s => !currentServers.Contains(s)).ToList();
                foreach (var serverName in toAdd)
                {
                    // Only add if the server exists in the gateway's servers table
                    using var cmd = new SQLiteCommand(
                        @"INSERT OR IGNORE INTO profile_servers (profile_name, server_name)
                          SELECT @profile, @server
                          WHERE EXISTS (SELECT 1 FROM servers WHERE name = @server)",
                        conn);
                    cmd.Parameters.AddWithValue("@profile", profileName);
                    cmd.Parameters.AddWithValue("@server", serverName);
                    cmd.ExecuteNonQuery();
                }

                // Remove servers no longer in the enabled set
                var desiredSet = new HashSet<string>(serverList, StringComparer.OrdinalIgnoreCase);
                var toRemove = currentServers.Where(s => !desiredSet.Contains(s)).ToList();
                foreach (var serverName in toRemove)
                {
                    using var cmd = new SQLiteCommand(
                        "DELETE FROM profile_servers WHERE profile_name = @profile AND server_name = @server",
                        conn);
                    cmd.Parameters.AddWithValue("@profile", profileName);
                    cmd.Parameters.AddWithValue("@server", serverName);
                    cmd.ExecuteNonQuery();
                }

                _log("Gateway", $"SyncProjectProfile '{profileName}': added {toAdd.Count}, removed {toRemove.Count}, total {serverList.Count} servers");

                // Signal the gateway to send notifications/tools/list_changed so Claude Code refreshes
                if (toAdd.Count > 0 || toRemove.Count > 0)
                    SignalGatewayRefresh();
            }
            catch (Exception ex)
            {
                _log("Gateway", $"SyncProjectProfile failed for '{profileName}': {ex.Message}");
            }
        }

        /// <summary>
        /// Generates a clean profile name from a project name.
        /// Lowercases, replaces non-alphanumeric chars with hyphens, collapses multiple hyphens.
        /// </summary>
        public static string GetGatewayProfileName(string projectName)
        {
            if (string.IsNullOrWhiteSpace(projectName))
                return "default";

            string clean = projectName.Trim().ToLowerInvariant();
            clean = Regex.Replace(clean, @"[^a-z0-9]+", "-");
            clean = Regex.Replace(clean, @"-+", "-");
            clean = clean.Trim('-');

            return string.IsNullOrEmpty(clean) ? "default" : clean;
        }

        /// <summary>
        /// Writes a .refresh signal file to the gateway data directory.
        /// The gateway's FileSystemWatcher picks this up and sends notifications/tools/list_changed
        /// so Claude Code re-fetches the tool list.
        /// </summary>
        private void SignalGatewayRefresh()
        {
            try
            {
                string gatewayDir = Path.GetDirectoryName(_gatewayDbPath);
                if (gatewayDir == null || !Directory.Exists(gatewayDir)) return;

                string refreshPath = Path.Combine(gatewayDir, ".refresh");
                File.WriteAllText(refreshPath, DateTime.UtcNow.ToString("o"));
                _log("Gateway", "Wrote .refresh signal to gateway directory");
            }
            catch (Exception ex)
            {
                _log("Gateway", $"SignalGatewayRefresh failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Opens a lazy connection to the gateway's SQLite database.
        /// Returns null (with a warning log) if the DB file does not exist.
        /// </summary>
        private SQLiteConnection GetConnection()
        {
            if (_connection != null)
                return _connection;

            if (!File.Exists(_gatewayDbPath))
            {
                _log("Gateway", $"Gateway database not found at {_gatewayDbPath} — gateway operations will be skipped");
                return null;
            }

            try
            {
                _connection = new SQLiteConnection($"Data Source={_gatewayDbPath};Version=3;");
                _connection.Open();

                // Enable WAL mode for cross-process concurrency (gateway writes while we read)
                using var walCmd = new SQLiteCommand("PRAGMA journal_mode=WAL;", _connection);
                walCmd.ExecuteNonQuery();

                // Enable foreign keys
                using var fkCmd = new SQLiteCommand("PRAGMA foreign_keys = ON;", _connection);
                fkCmd.ExecuteNonQuery();

                _log("Gateway", $"Connected to gateway database at {_gatewayDbPath}");
                return _connection;
            }
            catch (Exception ex)
            {
                _log("Gateway", $"Failed to open gateway database: {ex.Message}");
                _connection = null;
                return null;
            }
        }

        /// <summary>
        /// Runs a `claude` CLI command silently (mirrors McpConfigService.RunClaudeCommand).
        /// </summary>
        private (int ExitCode, string Stdout, string Stderr) RunClaudeCommand(string arguments)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c claude {arguments}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _log("Gateway", $"Running: claude {arguments}");

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null)
                return (-1, "", "Failed to start cmd.exe process");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            bool exited = process.WaitForExit(15000);

            if (!exited)
            {
                try { process.Kill(); } catch { }
                return (-2, stdout, "Process timed out after 15 seconds");
            }

            return (process.ExitCode, stdout, stderr);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (disposing)
            {
                try { _connection?.Close(); } catch { }
                try { _connection?.Dispose(); } catch { }
                _connection = null;
            }
        }
    }
}
