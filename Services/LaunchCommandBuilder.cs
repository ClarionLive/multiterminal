using System;
using System.IO;
using System.Reflection;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Builds the Claude Code CLI launch command for a project.
    /// Constructs the full 'claude' command string with MT config flags (--add-dir, --mcp-config,
    /// --settings) derived dynamically from the assembly location. Returns a LaunchCommand object
    /// consumed by TerminalDocument to start ConPtyTerminal with the correct working directory
    /// and auto-run command.
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
        public static LaunchCommand BuildClaudeCommand(
            Models.Project project,
            string terminalName = null,
            string docId = null)
        {
            string mtSourcePath = GetMtSourcePath();
            string workingDir = ResolveWorkingDirectory(project);

            // Build the claude CLI flags
            string flags = BuildFlags(mtSourcePath);

            // The autoRunCommand is injected into a PowerShell -Command "..." string by ConPtyTerminal.
            // Single quotes in flag values must be doubled ('') because they appear inside PS single-quoted strings.
            // E.g.: claude --add-dir 'C:\path\to\dir' --mcp-config 'C:\path\.claude\mcp.json' ...
            string autoRunCommand = $"claude{flags} --dangerously-skip-permissions";

            return new LaunchCommand
            {
                WorkingDirectory = workingDir,
                AutoRunCommand = autoRunCommand,
                ProjectId = project?.Id
            };
        }

        /// <summary>
        /// Builds the optional CLI flags string. Returns empty string if MT source path is unavailable.
        /// Skips --mcp-config / --settings flags if the files don't exist on disk.
        /// All path values have single quotes doubled for PowerShell single-quoted string safety.
        /// </summary>
        private static string BuildFlags(string mtSourcePath)
        {
            if (string.IsNullOrEmpty(mtSourcePath))
                return string.Empty;

            var flags = string.Empty;

            // --add-dir: Adds MT's CLAUDE.md alongside the project's own CLAUDE.md
            string safeMtPath = EscapeSingleQuotes(mtSourcePath);
            flags += $" --add-dir '{safeMtPath}'";

            // --mcp-config: MT's MCP server configuration (only if file exists)
            string mcpConfigPath = Path.Combine(mtSourcePath, ".claude", "mcp.json");
            if (File.Exists(mcpConfigPath))
            {
                string safeMcpPath = EscapeSingleQuotes(mcpConfigPath);
                flags += $" --mcp-config '{safeMcpPath}'";
            }

            // --settings: MT's local settings (only if file exists)
            string settingsPath = Path.Combine(mtSourcePath, ".claude", "settings.local.json");
            if (File.Exists(settingsPath))
            {
                string safeSettingsPath = EscapeSingleQuotes(settingsPath);
                flags += $" --settings '{safeSettingsPath}'";
            }

            return flags;
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
    }
}
