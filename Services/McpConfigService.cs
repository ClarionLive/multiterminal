using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Manages per-project MCP configuration.
    /// Core MCP servers (multiterminal + mcp-gateway) are centralized in %APPDATA%\multiterminal\.mcp.json
    /// and loaded via --mcp-config flag (see LaunchCommandBuilder). Per-project .mcp.json files are
    /// empty — project-specific server selection is handled by the gateway profile system at runtime.
    /// </summary>
    public class McpConfigService
    {
        private readonly ProjectDatabase _projectDb;
        private readonly Action<string, string> _log;

        // Hardcoded multiterminal MCP server config.
        // The MCP server index.js lives at %APPDATA%\multiterminal\mcp\index.js.
        private static readonly string MultiterminalMcpArgsJson;

        static McpConfigService()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string indexJs = Path.Combine(appData, "multiterminal", "mcp", "index.js")
                             .Replace("\\", "\\\\");
            MultiterminalMcpArgsJson = $"[\"{indexJs}\"]";
        }

        // Hardcoded MCP Gateway server config.
        private const string GatewayProjectPath = @"H:\DevLaptop\ClarionPowerShell\McpGateway";

        /// <summary>
        /// Optional gateway integration service. When set, gateway-aware methods use it
        /// to sync project profiles before writing .mcp.json.
        /// </summary>
        public GatewayIntegrationService GatewayService { get; set; }

        public McpConfigService(ProjectDatabase projectDb, Action<string, string> log = null)
        {
            _projectDb = projectDb ?? throw new ArgumentNullException(nameof(projectDb));
            _log = log ?? ((source, msg) => System.Diagnostics.Debug.WriteLine($"[{source}] {msg}"));
        }

        /// <summary>
        /// Generates a .mcp.json string. Since multiterminal and mcp-gateway are registered
        /// at user scope, the project .mcp.json is empty. Per-project server filtering is
        /// handled by calling gateway__set_profile at runtime.
        /// </summary>
        public string GenerateSimpleMcpJson(string gatewayProfile = null)
        {
            // Both core MCPs (multiterminal + mcp-gateway) are registered at user scope.
            // The gateway profile parameter is kept for backward compatibility but is no longer
            // written into .mcp.json — profile switching happens at runtime via gateway__set_profile.
            return "{\n  \"mcpServers\": {}\n}";
        }

        /// <summary>
        /// Writes .mcp.json (multiterminal + mcp-gateway) to the project's source path.
        /// Creates a .mcp.json.bak backup of any existing file.
        /// When GatewayService is available, syncs the project's gateway profile first.
        /// </summary>
        public string WriteMcpJsonToProject(string projectId, string sourcePath = null, string projectName = null)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                throw new ArgumentException("projectId is required", nameof(projectId));

            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(projectName))
            {
                var project = _projectDb.GetRichProject(projectId);
                if (project == null)
                    throw new InvalidOperationException($"Project not found: {projectId}");
                if (string.IsNullOrWhiteSpace(sourcePath))
                    sourcePath = project.SourcePath ?? project.Path;
                if (string.IsNullOrWhiteSpace(projectName))
                    projectName = project.Name;
            }

            if (string.IsNullOrWhiteSpace(sourcePath) || !Directory.Exists(sourcePath))
                throw new DirectoryNotFoundException($"Project source directory does not exist: {sourcePath}");

            // Sync gateway profile if available
            string gatewayProfile = null;
            if (GatewayService != null && GatewayService.IsGatewayInstalled() && !string.IsNullOrWhiteSpace(projectName))
            {
                try
                {
                    var enabledNames = _projectDb.GetEnabledMcpServerNamesForProject(projectId);
                    GatewayService.SyncProjectProfile(projectId, projectName, enabledNames);
                    gatewayProfile = GatewayIntegrationService.GetGatewayProfileName(projectName);
                }
                catch (Exception ex)
                {
                    _log("McpConfig", $"Gateway profile sync failed, continuing without profile: {ex.Message}");
                }
            }

            string content = GenerateSimpleMcpJson(gatewayProfile);
            string outputPath = Path.Combine(sourcePath, ".mcp.json");
            string backupPath = outputPath + ".bak";

            if (File.Exists(outputPath))
            {
                File.Copy(outputPath, backupPath, overwrite: true);
                _log("McpConfig", $"Backed up existing .mcp.json to {backupPath}");
            }

            File.WriteAllText(outputPath, content, Encoding.UTF8);
            _log("McpConfig", $"Wrote simplified .mcp.json (multiterminal + mcp-gateway) to {outputPath}");
            return outputPath;
        }

        /// <summary>
        /// Ensures MCP config is up-to-date for a project launch.
        /// Writes .mcp.json (multiterminal + mcp-gateway) to the project source path.
        /// </summary>
        public void EnsureMcpConfigsForProject(string projectId, string sourcePath = null, string projectName = null)
        {
            try
            {
                WriteMcpJsonToProject(projectId, sourcePath, projectName);
            }
            catch (Exception ex)
            {
                _log("McpConfig", $"Failed to write project .mcp.json: {ex.Message}");
            }
        }

        /// <summary>
        /// Gateway-aware variant — equivalent to EnsureMcpConfigsForProject since the simplified
        /// version always uses the gateway. Kept for call-site compatibility.
        /// </summary>
        public void EnsureMcpConfigsForProjectWithGateway(string projectId, string sourcePath, string projectName)
        {
            EnsureMcpConfigsForProject(projectId, sourcePath, projectName);
        }

        /// <summary>
        /// Regenerates .mcp.json for every project that has a valid source path.
        /// Returns (0, projectCount) — global count is always 0 (CLI sync removed).
        /// </summary>
        public (int GlobalCount, int ProjectCount) RegenerateAllMcpConfigs()
        {
            _log("McpConfig", "RegenerateAllMcpConfigs: START");
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
                    WriteMcpJsonToProject(project.Id, sourcePath, project.Name);
                    projectCount++;
                    _log("McpConfig", $"Wrote .mcp.json for project '{project.Name}' at {sourcePath}");
                }
                catch (Exception ex)
                {
                    _log("McpConfig", $"Skipped project '{project.Name}': {ex.Message}");
                }
            }

            _log("McpConfig", $"RegenerateAllMcpConfigs: DONE projects={projectCount}");
            return (0, projectCount);
        }

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
