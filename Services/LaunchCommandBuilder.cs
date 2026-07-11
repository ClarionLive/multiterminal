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
            string flags = BuildFlags(workingDir);

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

        /// <summary>
        /// Returns true when <paramref name="targetDir"/> is <paramref name="repoRoot"/>
        /// itself or a descendant of it (case-insensitive, separator-normalized).
        /// Used by MainForm's AC7 spawn-dir guard: a launch with an EXPLICIT target
        /// folder (New Project dialog, project card, Just Claude folder picker) may
        /// only be redirected to the agent's active-task worktree when that target
        /// lives inside the task's repo — otherwise the user's explicit choice wins
        /// (task f1f74a8f: Diana's MT task hijacked a POSitiveMobile launch).
        /// Pure and non-throwing: malformed paths return false.
        /// </summary>
        public static bool IsWithinRepo(string targetDir, string repoRoot)
        {
            if (string.IsNullOrWhiteSpace(targetDir) || string.IsNullOrWhiteSpace(repoRoot))
                return false;

            try
            {
                string target = Path.GetFullPath(targetDir).TrimEnd('\\', '/');
                string root = Path.GetFullPath(repoRoot).TrimEnd('\\', '/');

                if (string.Equals(target, root, StringComparison.OrdinalIgnoreCase))
                    return true;

                // Descendant check with a separator appended so "C:\repo2" is not
                // treated as inside "C:\repo".
                return target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                // GetFullPath throws on invalid characters/segments — treat as "not inside".
                return false;
            }
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
        private static string BuildFlags(string workingDirectory)
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
            var (forcedStatusline, canDropLocal) = BuildForcedStatuslineFlag(workingDirectory);
            if (!string.IsNullOrEmpty(forcedStatusline))
            {
                // Only exclude the LOCAL source when the merge actually re-supplied it (fail closed,
                // Run-2 security HIGH) — otherwise a project's local hooks/deny would be silently
                // stripped under --dangerously-skip-permissions. When we can't drop LOCAL, the
                // project's own statusLine wins and MT's header may stay "--%" for that project — a
                // benign display loss, not a safety loss.
                if (canDropLocal) flags += BuildSettingSourcesFlags("user,project");
                flags += forcedStatusline;
            }

            return flags;
        }

        /// <summary>
        /// Builds the <c>--setting-sources</c> flags from a comma-separated source list.
        /// <para><b>Why not just pass the comma string?</b> Claude Code 2.1.168 has a regression:
        /// although <c>--help</c> documents <c>--setting-sources</c> as a "comma-separated list",
        /// the multi-value comma form is mis-parsed — the comma is collapsed to a space and the
        /// whole token is validated as one source, so <c>--setting-sources user,project</c> fails
        /// with <c>Invalid setting source: user project</c>. Single values still parse, and
        /// REPEATING the flag once per source works. So we emit one <c>--setting-sources</c> per
        /// source (e.g. <c>--setting-sources user --setting-sources project</c>) regardless of how
        /// many sources are requested. Returns a leading-space-prefixed string ready to append.</para>
        /// </summary>
        public static string BuildSettingSourcesFlags(string commaSeparatedSources) =>
            string.IsNullOrWhiteSpace(commaSeparatedSources)
                ? string.Empty
                : string.Concat(commaSeparatedSources
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => $" --setting-sources {s.Trim()}"));

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
        /// <para><b>Returns <c>(Flag, CanDropLocal)</c>.</b> <c>Flag</c> is the <c>--settings</c>
        /// string, or "" if the bundled script can't be located or the settings file can't be
        /// written. <c>CanDropLocal</c> tells the caller whether it is SAFE to exclude the LOCAL
        /// source: true when there is no local file (nothing to lose) or the merge re-supplied it;
        /// FALSE when a local file exists but couldn't be read/parsed/merged. Callers MUST NOT
        /// drop LOCAL when <c>CanDropLocal</c> is false — otherwise a transient read/parse failure
        /// would silently strip the project's hooks/deny under --dangerously-skip-permissions
        /// (Run-2 security HIGH, fail-closed). Both fallbacks are logged so a silent "--%"
        /// regression stays traceable.</para>
        /// <para>When <paramref name="workingDirectory"/> is supplied, the project's
        /// <c>.claude/settings.local.json</c> is read and MERGED into the emitted settings
        /// (its keys preserved, only <c>statusLine</c> overridden) so that excluding the LOCAL
        /// source at the call site does not strip the project's local hooks/deny rules. The
        /// merged content is therefore per-project; the file is keyed by a hash of the working
        /// dir. Lives in a per-user-private dir (LocalApplicationData), not the world-shared
        /// temp root, and is swapped in atomically (write temp + rename) so a concurrently
        /// spawning terminal's <c>claude --settings</c> read never sees a truncated file. Shared
        /// by <c>TerminalSpawner.SpawnTerminal</c> (spawned teammates), <c>BuildFlags</c> (docked
        /// terminals), and <c>OracleService</c>.</para>
        /// </summary>
        public static (string Flag, bool CanDropLocal) BuildForcedStatuslineFlag(string workingDirectory = null)
        {
            try
            {
                string scriptPath = Path.Combine(AppContext.BaseDirectory, "scripts", "statusline.js");
                if (!File.Exists(scriptPath))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[LaunchCommandBuilder] Forced statusLine skipped: bundled script not found at '{scriptPath}'. " +
                        "Context may stay '--%' for project terminals that override statusLine.");
                    return (string.Empty, false);
                }

                // Forward slashes are valid in JSON without escaping and are accepted by
                // node on Windows; the path is quoted so spaces (e.g. "Program Files") work.
                string scriptForJson = scriptPath.Replace("\\", "/");
                var statusLineNode = new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "command",
                    ["command"] = $"node \"{scriptForJson}\""
                };

                // Merge, don't clobber (task 1ba59334, security gate). The caller pairs this
                // --settings with --setting-sources that EXCLUDES the LOCAL source so a project's
                // settings.local.json statusLine can't outrank us. But dropping LOCAL wholesale
                // would also drop a project's local HOOKS / deny rules — a real safety layer under
                // --dangerously-skip-permissions. So we RE-SUPPLY the project's LOCAL settings here
                // (at --settings precedence) and override ONLY statusLine, preserving everything else.
                //
                // FAIL CLOSED (Run-2 security HIGH): if a LOCAL file EXISTS but we cannot re-supply
                // its keys (unreadable, unparseable, or not a JSON object), we must NOT tell the
                // caller it's safe to drop LOCAL — doing so would silently strip the project's
                // hooks/deny while --dangerously-skip-permissions is active. canDropLocal=false in
                // that case keeps LOCAL active (the project's own statusLine then wins and MT's
                // header may stay "--%" for that one project — a benign display loss, not a safety
                // loss). canDropLocal stays true when there is NO local file (nothing to lose) or
                // the merge succeeded (keys re-supplied).
                System.Text.Json.Nodes.JsonObject merged = null;
                bool canDropLocal = true;
                if (!string.IsNullOrEmpty(workingDirectory))
                {
                    string localPath = Path.Combine(workingDirectory, ".claude", "settings.local.json");
                    if (File.Exists(localPath))
                    {
                        // Read with a short retry on transient IO (Run-3 adversary MEDIUM): a lock /
                        // sharing violation / mid-write at launch shouldn't concede CanDropLocal=false
                        // (which keeps the project's statusLine override winning and the header stale)
                        // when a retry would have read the file. Retry only TRANSIENT IOException;
                        // durable failures (ACL) and parse/schema errors do not spin.
                        string localText = null;
                        for (int attempt = 0; attempt < 4 && localText == null; attempt++)
                        {
                            try { localText = File.ReadAllText(localPath); }
                            catch (IOException) { if (attempt < 3) System.Threading.Thread.Sleep(15); }
                            catch (Exception) { break; } // durable (e.g. UnauthorizedAccessException) — don't spin
                        }

                        if (localText == null)
                        {
                            // Couldn't read after retries — fail closed: keep LOCAL active so a
                            // lock/ACL error can't silently strip the project's hooks/deny under
                            // --dangerously-skip-permissions.
                            canDropLocal = false;
                            System.Diagnostics.Debug.WriteLine(
                                $"[LaunchCommandBuilder] Could not read '{localPath}' (after retries) to preserve local settings. Keeping LOCAL active (MT statusLine not forced for this project).");
                        }
                        else
                        {
                            try
                            {
                                merged = System.Text.Json.Nodes.JsonNode.Parse(localText) as System.Text.Json.Nodes.JsonObject;
                                if (merged == null)
                                {
                                    // Valid JSON but not an object (array/scalar) — can't preserve keys.
                                    canDropLocal = false;
                                    System.Diagnostics.Debug.WriteLine(
                                        $"[LaunchCommandBuilder] '{localPath}' is not a JSON object; cannot preserve its local settings. Keeping LOCAL active (MT statusLine not forced for this project).");
                                }
                            }
                            catch (Exception ex)
                            {
                                // Durable schema/parse error — fail closed.
                                canDropLocal = false;
                                System.Diagnostics.Debug.WriteLine(
                                    $"[LaunchCommandBuilder] Could not parse '{localPath}' to preserve local settings: {ex.Message}. Keeping LOCAL active (MT statusLine not forced for this project).");
                            }
                        }
                    }
                }
                merged ??= new System.Text.Json.Nodes.JsonObject();
                merged["statusLine"] = statusLineNode;
                string json = merged.ToJsonString();

                // Per-user-private dir, not the shared temp root.
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MultiTerminal");
                Directory.CreateDirectory(dir);
                // The merged content is now PER-PROJECT (it carries that project's local settings),
                // so key the file by a stable hash of the working dir to avoid cross-project
                // clobbering when several projects launch concurrently.
                string settingsPath = Path.Combine(dir, $"forced-statusline-{HashForFileName(workingDirectory)}.settings.json");

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

                return ($" --settings '{EscapeSingleQuotes(settingsPath)}'", canDropLocal);
            }
            catch (Exception ex)
            {
                // Best-effort: if the settings file can't be written, fall back to whatever
                // statusLine the resolved sources provide (Context may stay "--%" there). Fail
                // closed on CanDropLocal — no merged file means we can't have re-supplied LOCAL.
                System.Diagnostics.Debug.WriteLine(
                    $"[LaunchCommandBuilder] Forced statusLine skipped (settings write failed): {ex.Message}. " +
                    "Context may stay '--%' for project terminals that override statusLine.");
                return (string.Empty, false);
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

        /// <summary>
        /// Stable collision-resistant tag derived from a working directory, used to name the
        /// per-project forced-statusline settings file so concurrent launches of different
        /// projects don't clobber each other's merged settings. Returns "default" when no
        /// working directory is supplied (statusLine-only content).
        /// <para>Uses the FULL SHA-256 (64 hex chars), not a truncation: this file is passed to
        /// <c>claude --settings</c> at CLI precedence and carries the project's merged
        /// hooks/deny/env, so a colliding filename would let one project's settings overwrite
        /// another's. A short (e.g. 32-bit) tag left a feasible cross-project collision surface
        /// (Run-2 security MEDIUM); the full digest removes it.</para>
        /// </summary>
        private static string HashForFileName(string value)
        {
            if (string.IsNullOrEmpty(value)) return "default";
            using var sha = System.Security.Cryptography.SHA256.Create();
            byte[] h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
            return Convert.ToHexString(h).ToLowerInvariant(); // full 64 hex chars (collision-resistant)
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
