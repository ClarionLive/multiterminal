using System;
using System.IO;
using System.Reflection;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Builds the Claude Code CLI launch command for a project.
    /// Constructs the full 'claude' command string with MT config flags (--add-dir, --settings, --mcp-config)
    /// derived dynamically from the assembly location. Returns a LaunchCommand object
    /// consumed by TerminalDocument to start ConPtyTerminal with the correct working directory
    /// and auto-run command.
    /// MCP servers are loaded from %APPDATA%\multiterminal\.mcp.json via --mcp-config flag,
    /// giving MultiTerminal full control over MCP registration (not ~/.claude.json).
    /// </summary>
    public static class LaunchCommandBuilder
    {
        // Cache the MT source path after first discovery so we don't walk the filesystem every time.
        private static string _cachedMtSourcePath = null;

        /// <summary>
        /// Builds the Claude Code launch command for the given project.
        /// </summary>
        /// <param name="project">Project to launch Claude Code for. May be null (launches in user profile dir).</param>
        /// <param name="terminalName">Optional terminal display name (passed as env var by ConPtyTerminal).</param>
        /// <param name="docId">Optional terminal doc ID (passed as env var by ConPtyTerminal).</param>
        /// <returns>LaunchCommand with WorkingDirectory and AutoRunCommand ready for ConPtyTerminal.Start().</returns>
        public static LaunchCommand BuildClaudeCommand(Models.Project project)
        {
            string mtSourcePath = GetMtSourcePath();
            string workingDir = ResolveWorkingDirectory(project);

            // Build the claude CLI flags
            string flags = BuildFlags(mtSourcePath);

            // The autoRunCommand is injected into a PowerShell -Command "..." string by ConPtyTerminal.
            // Single quotes in flag values must be doubled ('') because they appear inside PS single-quoted strings.
            // E.g.: claude --add-dir 'C:\path\to\dir' --settings 'C:\path\.claude\settings.local.json' ...
            string autoRunCommand = $"claude{flags} --dangerously-skip-permissions; exit";

            return new LaunchCommand
            {
                WorkingDirectory = workingDir,
                AutoRunCommand = autoRunCommand,
                ProjectId = project?.Id
            };
        }

        /// <summary>
        /// Builds the optional CLI flags string. Returns empty string if MT source path is unavailable.
        /// Skips --settings flag if the file doesn't exist on disk.
        /// All path values have single quotes doubled for PowerShell single-quoted string safety.
        /// Adds --mcp-config pointing to %APPDATA%\multiterminal\.mcp.json for centralized MCP registration.
        /// </summary>
        private static string BuildFlags(string mtSourcePath)
        {
            if (string.IsNullOrEmpty(mtSourcePath))
                return string.Empty;

            var flags = string.Empty;

            // --add-dir: Adds MT's CLAUDE.md alongside the project's own CLAUDE.md
            string safeMtPath = EscapeSingleQuotes(mtSourcePath);
            flags += $" --add-dir '{safeMtPath}'";

            // --settings: MT's local settings (only if file exists)
            string settingsPath = Path.Combine(mtSourcePath, ".claude", "settings.local.json");
            if (File.Exists(settingsPath))
            {
                string safeSettingsPath = EscapeSingleQuotes(settingsPath);
                flags += $" --settings '{safeSettingsPath}'";
            }

            // --mcp-config: Centralized MCP server config at %APPDATA%\multiterminal\.mcp.json
            string mcpConfigPath = GetMcpConfigPath();
            if (mcpConfigPath != null)
            {
                string safeMcpPath = EscapeSingleQuotes(mcpConfigPath);
                flags += $" --mcp-config '{safeMcpPath}'";
            }

            return flags;
        }

        /// <summary>
        /// Returns the path to the centralized MCP config file if it exists.
        /// Location: %APPDATA%\multiterminal\.mcp.json
        /// </summary>
        public static string GetMcpConfigPath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string mcpConfig = Path.Combine(appData, "multiterminal", ".mcp.json");
            return File.Exists(mcpConfig) ? mcpConfig : null;
        }

        /// <summary>
        /// Resolves the working directory for Claude Code to start in.
        /// Priority: project.SourcePath → project.Path → user profile directory.
        /// </summary>
        private static string ResolveWorkingDirectory(Models.Project project)
        {
            if (project != null)
            {
                if (!string.IsNullOrWhiteSpace(project.SourcePath) && Directory.Exists(project.SourcePath))
                    return project.SourcePath;

                if (!string.IsNullOrWhiteSpace(project.Path) && Directory.Exists(project.Path))
                    return project.Path;
            }

            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        /// <summary>
        /// Finds the MT source directory by walking up from the assembly location
        /// until a directory containing .claude/CLAUDE.md is found.
        /// Caches the result after first successful discovery.
        /// </summary>
        public static string GetMtSourcePath()
        {
            if (_cachedMtSourcePath != null)
                return _cachedMtSourcePath;

            try
            {
                string assemblyPath = Assembly.GetExecutingAssembly().Location;
                string dir = Path.GetDirectoryName(assemblyPath);

                // Walk up the directory tree (max 6 levels to avoid infinite loops)
                for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
                {
                    string marker = Path.Combine(dir, ".claude", "CLAUDE.md");
                    if (File.Exists(marker))
                    {
                        _cachedMtSourcePath = dir;
                        return dir;
                    }

                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LaunchCommandBuilder] Failed to locate MT source path: {ex.Message}");
            }

            // Return null — BuildFlags will skip optional flags gracefully
            return null;
        }

        /// <summary>
        /// Escapes single quotes for use inside a PowerShell single-quoted string.
        /// A literal ' becomes '' in PowerShell.
        /// </summary>
        private static string EscapeSingleQuotes(string value)
        {
            return value?.Replace("'", "''") ?? string.Empty;
        }
    }

    /// <summary>
    /// Result of LaunchCommandBuilder.BuildClaudeCommand — contains the working directory
    /// and the auto-run command string for ConPtyTerminal.Start().
    /// </summary>
    public class LaunchCommand
    {
        /// <summary>
        /// The directory Claude Code should be launched from (project source or user profile).
        /// </summary>
        public string WorkingDirectory { get; set; }

        /// <summary>
        /// The full 'claude ...' command string, ready for use as ConPtyTerminal's autoRunCommand parameter.
        /// </summary>
        public string AutoRunCommand { get; set; }

        /// <summary>
        /// The project ID passed to ConPtyTerminal as MULTITERMINAL_PROJECT_ID env var.
        /// </summary>
        public string ProjectId { get; set; }

        /// <summary>
        /// The MCP Gateway profile name passed to ConPtyTerminal as MCP_GATEWAY_PROFILE env var.
        /// Enables per-project server filtering in the gateway.
        /// </summary>
        public string GatewayProfile { get; set; }
    }
}
