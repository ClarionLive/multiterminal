using System;
using System.IO;
using System.Reflection;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Builds the Claude Code CLI launch command for a project.
    /// Uses --plugin-dir to load the MultiTerminal plugin (hooks, skills, agents, CLAUDE.md)
    /// and --mcp-config for centralized MCP server registration.
    /// The MT plugin is NOT enabled globally — only MT-spawned terminals get it via --plugin-dir.
    /// </summary>
    public static class LaunchCommandBuilder
    {
        // Cache the MT source path after first discovery so we don't walk the filesystem every time.
        private static string _cachedMtSourcePath = null;

        /// <summary>
        /// Builds the Claude Code launch command for the given project.
        /// </summary>
        /// <param name="project">Project to launch Claude Code for. May be null (launches in user profile dir).</param>
        /// <returns>LaunchCommand with WorkingDirectory and AutoRunCommand ready for ConPtyTerminal.Start().</returns>
        public static LaunchCommand BuildClaudeCommand(Models.Project project)
        {
            string workingDir = ResolveWorkingDirectory(project);

            // Build the claude CLI flags (just --mcp-config now — plugin handles hooks, CLAUDE.md, agents, skills)
            string flags = BuildFlags();

            string autoRunCommand = $"claude{flags} --dangerously-skip-permissions --dangerously-load-development-channels server:multiterminal-channel; exit";

            return new LaunchCommand
            {
                WorkingDirectory = workingDir,
                AutoRunCommand = autoRunCommand,
                ProjectId = project?.Id
            };
        }

        /// <summary>
        /// Builds the CLI flags string.
        /// --plugin-dir loads the MultiTerminal plugin for this session.
        /// --mcp-config loads centralized MCP server registration.
        /// </summary>
        private static string BuildFlags()
        {
            var flags = string.Empty;

            // --plugin-dir: Load the MultiTerminal plugin (hooks, skills, agents, CLAUDE.md)
            // The MT plugin is NOT in global enabledPlugins — only MT terminals get it.
            string pluginDir = GetMtPluginPath();
            if (pluginDir != null)
            {
                string safePluginDir = EscapeSingleQuotes(pluginDir);
                flags += $" --plugin-dir '{safePluginDir}'";
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
        /// Returns the path to the MultiTerminal plugin directory if it exists.
        /// Location: ~/.claude/plugins/marketplaces/multiterminal-marketplace/plugins/multiterminal
        /// </summary>
        private static string GetMtPluginPath()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string pluginDir = Path.Combine(userProfile, ".claude", "plugins", "marketplaces",
                "multiterminal-marketplace", "plugins", "multiterminal");
            return Directory.Exists(pluginDir) ? pluginDir : null;
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
        /// Used by TerminalSpawner to locate multiterminal-rules.md.
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
