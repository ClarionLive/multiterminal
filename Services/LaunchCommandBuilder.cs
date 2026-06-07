using System;
using System.IO;
using System.Linq;
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

            string autoRunCommand = $"claude{flags} --dangerously-skip-permissions; exit";

            return new LaunchCommand
            {
                WorkingDirectory = workingDir,
                AutoRunCommand = autoRunCommand,
                ProjectId = project?.Id
            };
        }

        /// <summary>
        /// Builds the Codex CLI launch command for the given project.
        /// Refreshes ~/.codex/config.toml so Codex picks up MultiTerminal's MCP servers
        /// (asymmetric with BuildClaudeCommand because Codex has no --mcp-config flag —
        /// MCP registration lives in the config file). Project context comes from
        /// AGENTS.md at the project root (checklist item 4). MultiTerminal env vars
        /// (MULTITERMINAL_NAME, MULTITERMINAL_DOC_ID, etc.) are injected by
        /// ConPtyTerminal.StartProcess into the parent PowerShell and inherited by
        /// the codex child — no extra plumbing required here.
        ///
        /// Fail-closed bootstrap: when MCP registration or startup-file scaffolding
        /// fails, the returned <see cref="LaunchCommand"/> has <c>AutoRunCommand=null</c>
        /// and <see cref="LaunchCommand.BootstrapError"/> populated with a human-readable
        /// reason. Callers must check <see cref="LaunchCommand.BootstrapError"/> before
        /// starting a terminal — launching with a null autorun would drop Codex into an
        /// unwired shell with no way to reach the team's MCP server, which is worse than
        /// surfacing an error to the user.
        /// </summary>
        /// <param name="project">Project to launch Codex for. May be null (launches in user profile dir).</param>
        public static LaunchCommand BuildCodexCommand(Models.Project project)
        {
            string workingDir = ResolveWorkingDirectory(project);
            bool workingDirIsProject = IsDistinctProjectRoot(project, workingDir);

            // --- Preflight: clean stale Codex broker state ------------------------
            // The Codex companion's getCodexAuthStatus uses reuseExistingBroker:true
            // (lib/app-server.mjs:336-338) which reads the cached broker.json
            // endpoint without verifying the named pipe is alive. After a machine
            // reboot or MT host restart the cached endpoint is stale, and every
            // subsequent codex invocation reports loggedIn:false even though
            // auth.json is fine. We can't patch the third-party plugin, so probe +
            // clean the state ourselves before the launch. Synchronous and bounded
            // (~200ms per stale candidate) — acceptable on the launch path which
            // already takes seconds for cold start. Wrapped in try/catch: a
            // preflight error must never block a launch.
            try
            {
                CodexBrokerHealthService.EnsureFreshBrokerState(workingDir);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LaunchCommandBuilder] Codex broker preflight failed: {ex.Message}");
            }

            // --- Critical step 1: MCP registration --------------------------------
            // Without this, Codex launches but cannot call register_terminal,
            // send_message, or any team tool. That's a non-functional terminal —
            // refuse to launch rather than leave the user confused.
            string mcpError;
            try
            {
                string written = CodexConfigService.EnsureMcpRegistration();
                mcpError = written == null
                    ? "MCP server list not found (expected in ~/.claude.json or %APPDATA%\\multiterminal\\.mcp.json)"
                    : null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LaunchCommandBuilder] Codex MCP config refresh failed: {ex.Message}");
                mcpError = "Writing ~/.codex/config.toml failed: " + ex.Message;
            }

            // --- Best-effort step: AGENTS.md --------------------------------------
            // Non-critical: Codex works without it (just loses project context).
            // Skip entirely when workingDir is the home-dir fallback — we don't
            // want to plant an AGENTS.md in %USERPROFILE% that subsequent launches
            // of unrelated projects would inherit.
            if (workingDirIsProject)
            {
                try
                {
                    CodexAgentsService.EnsureAgentsMd(workingDir, project?.Name);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[LaunchCommandBuilder] AGENTS.md ensure failed: {ex.Message}");
                }
            }

            // --- Critical step 2: startup prompt + launcher script ---------------
            // Without the launcher script we fall back to running bare `codex`
            // with no briefing. That's the exact silent-failure mode the adversary
            // flagged — Codex has a prompt telling it to register as a team
            // member, without that prompt it never calls register_terminal.
            //
            // Validate BOTH files: codex-launch.ps1 AND startup-prompt.md. The
            // launcher script's own internal fallback runs bare `codex` when the
            // prompt is missing/unreadable, so checking only the script would
            // still admit the "plausible-looking terminal with no briefing" case.
            string launchScriptPath = null;
            string scriptError = null;
            try
            {
                CodexPromptService.EnsureStartupFiles();
                launchScriptPath = CodexPromptService.GetLaunchScriptPath();
                string promptPath = CodexPromptService.GetStartupPromptPath();
                if (string.IsNullOrEmpty(launchScriptPath) || !File.Exists(launchScriptPath))
                    scriptError = "Codex launcher script not available at " + (launchScriptPath ?? "<null>");
                else if (string.IsNullOrEmpty(promptPath) || !File.Exists(promptPath))
                    scriptError = "Codex startup prompt not available at " + (promptPath ?? "<null>");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LaunchCommandBuilder] Codex startup-files write failed: {ex.Message}");
                scriptError = "Writing Codex startup files failed: " + ex.Message;
            }

            // Aggregate bootstrap errors. If either critical step failed, refuse
            // to launch and let the caller surface the reason.
            string combinedError = CombineErrors(mcpError, scriptError);
            if (combinedError != null)
            {
                return new LaunchCommand
                {
                    WorkingDirectory = workingDir,
                    AutoRunCommand = null,
                    ProjectId = project?.Id,
                    BootstrapError = combinedError
                };
            }

            // Codex binary: user-configured path if set, otherwise PATH lookup.
            string codexBinary = SettingsService.Default?.GetCodexBinaryPath();
            if (string.IsNullOrWhiteSpace(codexBinary))
                codexBinary = "codex";

            string safeScript = EscapeSingleQuotes(launchScriptPath);
            string safeBinary = EscapeSingleQuotes(codexBinary);
            // Pass the codex binary path into the launcher via env var so the
            // script can honor the user's custom path without duplicating logic.
            string autoRunCommand = $"$env:MULTITERMINAL_CODEX_BIN = '{safeBinary}'; & '{safeScript}'; exit";

            return new LaunchCommand
            {
                WorkingDirectory = workingDir,
                AutoRunCommand = autoRunCommand,
                ProjectId = project?.Id
            };
        }

        /// <summary>
        /// Returns true when <paramref name="workingDir"/> represents an actual
        /// project directory (not the user-profile fallback). We only want to
        /// write AGENTS.md (or run project-scoped MCP cleanup) when the working
        /// directory is a real project root — otherwise a broken/moved project
        /// would plant a file in %USERPROFILE% that later non-project launches
        /// would pick up, or mutate <c>~/.mcp.json</c> via
        /// <c>McpConfigService.WriteMcpJsonToProject</c>.
        ///
        /// Public so MainForm launch paths can reuse the same guard when deciding
        /// whether to call <c>EnsureMcpConfigsForProjectWithGateway</c>.
        /// </summary>
        public static bool IsDistinctProjectRoot(Models.Project project, string workingDir)
        {
            if (project == null) return false;
            if (string.IsNullOrWhiteSpace(workingDir)) return false;

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.Equals(Path.GetFullPath(workingDir).TrimEnd('\\', '/'),
                              Path.GetFullPath(home).TrimEnd('\\', '/'),
                              StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        private static string CombineErrors(params string[] errors)
        {
            string joined = string.Join("; ", errors.Where(e => !string.IsNullOrEmpty(e)));
            return joined.Length == 0 ? null : joined;
        }

        /// <summary>
        /// Dispatches to the appropriate builder based on terminal kind. Callers
        /// that honor a project's default-terminal choice (start screen, project
        /// card split button) should use this instead of calling the per-kind
        /// builder directly.
        /// </summary>
        public static LaunchCommand BuildCommand(Models.TerminalKind kind, Models.Project project)
        {
            switch (kind)
            {
                case Models.TerminalKind.Codex:
                    return BuildCodexCommand(project);
                case Models.TerminalKind.ClaudeCode:
                default:
                    return BuildClaudeCommand(project);
            }
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

            // --dangerously-load-development-channels: Authorize the plugin's channel server.
            // Plugins loaded via --plugin-dir get the sentinel marketplace "inline" (not the
            // actual marketplace directory name). Claude Code sets source to "name@inline".
            if (pluginDir != null && Directory.Exists(Path.Combine(pluginDir, "server")))
            {
                string pluginName = Path.GetFileName(pluginDir);
                flags += $" --dangerously-load-development-channels plugin:{pluginName}@inline";
            }

            // Force MT's own statusline.js as the statusLine command. A project that
            // overrides statusLine in its .claude/settings.local.json (e.g. ClarionAssistant's
            // ca-statusline.js) would otherwise run instead of MT's, so MT's per-terminal
            // context stats file is never written (stale/"--%" header — tasks 72444250,
            // 1ba59334). Claude Code's LOCAL settings outrank the --settings flag, so we ALSO
            // pass --setting-sources user,project to DROP the LOCAL source; MT's --settings
            // statusLine then wins for every docked terminal regardless of project overrides.
            // Both-or-neither: only drop LOCAL when MT's statusLine is actually being forced
            // (never strip a project's local settings without supplying a replacement).
            string forcedStatusline = BuildForcedStatuslineFlag();
            if (!string.IsNullOrEmpty(forcedStatusline))
            {
                flags += " --setting-sources user,project";
                flags += forcedStatusline;
            }

            return flags;
        }

        /// <summary>
        /// Builds the <c>--settings &lt;file&gt;</c> flag that points Claude Code at
        /// MultiTerminal's bundled <c>scripts/statusline.js</c> as the <c>statusLine</c>
        /// command, so MT's script writes <c>mt-statusline-{name}-{docId}.json</c> (task 72444250).
        /// <para><b>Caller responsibility (task 1ba59334).</b> This flag ALONE does not beat a
        /// project that overrides <c>statusLine</c> in its <c>.claude/settings.local.json</c>:
        /// Claude Code's LOCAL source outranks the <c>--settings</c> flag (confirmed against
        /// the CLI docs — the earlier "--settings outranks local" assumption was wrong, which
        /// is why ClarionAssistant's ca-statusline.js kept winning and the header stayed
        /// stale). To make MT's statusLine actually win, the CALLER must also EXCLUDE the
        /// LOCAL source via <c>--setting-sources</c> (e.g. docked terminals pass
        /// <c>user,project</c>; spawned teammates pass <c>project</c>). The exact source set
        /// differs per launch path, so it lives at the call site, not here.</para>
        /// Returns "" (no flag) if the bundled script can't be located or the settings file
        /// can't be written; both fallbacks are logged so a silent "--%" regression stays
        /// traceable.
        /// <para>The settings file lives in a per-user-private dir (LocalApplicationData)
        /// rather than the world-shared temp root, and is swapped in atomically (write
        /// temp + rename) so a concurrently-spawning terminal's <c>claude --settings</c>
        /// read never sees a truncated file. Content is per-install-constant. This is the
        /// canonical implementation shared by <c>TerminalSpawner.SpawnTerminal</c> (spawned
        /// teammates) and <c>BuildFlags</c> (docked terminals).</para>
        /// </summary>
        public static string BuildForcedStatuslineFlag()
        {
            try
            {
                string scriptPath = Path.Combine(AppContext.BaseDirectory, "scripts", "statusline.js");
                if (!File.Exists(scriptPath))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[LaunchCommandBuilder] Forced statusLine skipped: bundled script not found at '{scriptPath}'. " +
                        "Context may stay '--%' for project terminals that override statusLine.");
                    return string.Empty;
                }

                // Forward slashes are valid in JSON without escaping and are accepted by
                // node on Windows; the path is quoted so spaces (e.g. "Program Files") work.
                string scriptForJson = scriptPath.Replace("\\", "/");
                string json = "{\"statusLine\":{\"type\":\"command\",\"command\":\"node \\\"" + scriptForJson + "\\\"\"}}";

                // Per-user-private dir, not the shared temp root.
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MultiTerminal");
                Directory.CreateDirectory(dir);
                string settingsPath = Path.Combine(dir, "forced-statusline.settings.json");

                // Atomic swap: write a unique temp then rename over the target. A rename is a
                // whole-file replace, so a concurrent reader never observes a torn file.
                string tmpPath = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(tmpPath, json);
                    for (int attempt = 0; ; attempt++)
                    {
                        try { File.Move(tmpPath, settingsPath, overwrite: true); break; }
                        catch (IOException) when (attempt < 3) { System.Threading.Thread.Sleep(15); }
                        catch (UnauthorizedAccessException) when (attempt < 3) { System.Threading.Thread.Sleep(15); }
                    }
                }
                catch
                {
                    try { File.Delete(tmpPath); } catch { /* best-effort cleanup */ }
                    throw;
                }

                return $" --settings '{EscapeSingleQuotes(settingsPath)}'";
            }
            catch (Exception ex)
            {
                // Best-effort: if the settings file can't be written, fall back to whatever
                // statusLine the resolved sources provide (Context may stay "--%" there).
                System.Diagnostics.Debug.WriteLine(
                    $"[LaunchCommandBuilder] Forced statusLine skipped (settings write failed): {ex.Message}. " +
                    "Context may stay '--%' for project terminals that override statusLine.");
                return string.Empty;
            }
        }

        /// <summary>
        /// Returns the path to the MultiTerminal plugin directory if it exists.
        /// Location: ~/.claude/plugins/marketplaces/multiterminal-marketplace/plugins/multiterminal
        /// </summary>
        public static string GetMtPluginPath()
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
        ///
        /// Applies the same validation that <c>MainForm.OnProjectLaunchRequested</c>
        /// uses before starting a terminal: the path must be rooted, must not be a
        /// UNC share, and must exist. Without this parity, Codex bootstrap
        /// side-effects (AGENTS.md generation in particular) would land on paths
        /// the UI actively rejects — e.g. an AGENTS.md written to a UNC share while
        /// the terminal itself starts from the home-directory fallback.
        /// </summary>
        private static string ResolveWorkingDirectory(Models.Project project)
        {
            if (project != null)
            {
                if (IsUsableProjectPath(project.SourcePath))
                    return project.SourcePath;

                if (IsUsableProjectPath(project.Path))
                    return project.Path;
            }

            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        /// <summary>
        /// Validation mirror of the path check used in
        /// <c>MainForm.OnProjectLaunchRequested</c> (~line 5135). Keeps the builder
        /// and the UI in sync about what counts as a launchable project root.
        /// </summary>
        private static bool IsUsableProjectPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (!Path.IsPathRooted(path)) return false;
            if (path.StartsWith("\\\\", StringComparison.Ordinal)) return false; // UNC share
            if (!Directory.Exists(path)) return false;
            return true;
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

        /// <summary>
        /// Populated by <see cref="LaunchCommandBuilder.BuildCodexCommand"/> when a
        /// critical bootstrap step fails (MCP registration or startup scaffolding).
        /// When non-null, <see cref="AutoRunCommand"/> is null and callers must
        /// surface the error to the user instead of starting the terminal —
        /// launching a Codex session without MCP/prompt wiring would produce a
        /// plausible-looking but non-functional terminal.
        /// </summary>
        public string BootstrapError { get; set; }
    }
}
