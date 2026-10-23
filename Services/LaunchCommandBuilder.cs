using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
        /// <summary>
        /// Builds the Claude Code launch command for the given project.
        /// </summary>
        /// <param name="project">Project to launch Claude Code for. May be null (launches in user profile dir).</param>
        /// <param name="workingDirectory">
        /// The directory the terminal will actually run in, when the caller knows it and it is
        /// not derivable from <paramref name="project"/> — an API-driven spawn, for instance,
        /// which receives its cwd in the request and may have no project at all. Null means
        /// "resolve from the project as before". This is not a convenience: the flags are
        /// cwd-dependent. <see cref="BuildFlags"/> reads the cwd's
        /// <c>.claude/settings.local.json</c> to merge the project's hooks/deny rules before it
        /// decides whether dropping the LOCAL settings source is safe, so computing them against
        /// the project-resolved default instead of the real cwd would launch under
        /// <c>--dangerously-skip-permissions</c> with that project's local hooks/deny silently
        /// stripped (task 77d1182f).
        /// </param>
        /// <param name="resumeSessionId">
        /// Claude Code session id to resume (<c>--resume &lt;id&gt;</c>), or null/whitespace for a fresh
        /// session. Must be a GUID; anything else throws <see cref="ArgumentException"/> (task ec2ed636).
        /// </param>
        /// <returns>LaunchCommand with WorkingDirectory and AutoRunCommand ready for ConPtyTerminal.Start().</returns>
        public static LaunchCommand BuildClaudeCommand(Models.Project project, string workingDirectory = null, string resumeSessionId = null)
        {
            // Validate first: a bad id must fail before BuildFlags writes the settings file.
            string resumeId = NormalizeResumeSessionId(resumeSessionId);

            string workingDir = workingDirectory ?? ResolveWorkingDirectory(project);

            // Build the claude CLI flags (just --mcp-config now — plugin handles hooks, CLAUDE.md, agents, skills)
            string flags = BuildFlags(workingDir);

            // --resume goes after the flags so the command still starts with the bare `claude` token,
            // which ConPtyTerminal.ApplySessionName needs to splice `-n '<name>'` in after.
            string resumeFlag = resumeId == null ? string.Empty : $" --resume {resumeId}";

            string autoRunCommand = $"claude{flags}{resumeFlag} --dangerously-skip-permissions; exit";

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
        /// <param name="workingDirectory">
        /// Explicit cwd, or null to resolve from <paramref name="project"/>. Same contract as
        /// <see cref="BuildClaudeCommand"/>: the scaffolding side effects below (AGENTS.md,
        /// config.toml) are keyed on the directory the terminal actually runs in, and
        /// <see cref="IsDistinctProjectRoot"/> still guards them when that directory is not a
        /// project root.
        /// </param>
        public static LaunchCommand BuildCodexCommand(Models.Project project, string workingDirectory = null)
        {
            string workingDir = workingDirectory ?? ResolveWorkingDirectory(project);
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
        public static LaunchCommand BuildCommand(Models.TerminalKind kind, Models.Project project, string workingDirectory = null, string resumeSessionId = null)
        {
            // workingDirectory: explicit cwd for callers that know it (API-driven spawn); null
            // resolves from the project. See BuildClaudeCommand for why this is load-bearing.
            switch (kind)
            {
                case Models.TerminalKind.Codex:
                    // Codex resume is out of scope. Refuse rather than drop the id: a silently fresh
                    // session looks like a resumed one until the conversation is missing. Checked
                    // before BuildCodexCommand so its config/broker side effects never run.
                    if (!string.IsNullOrWhiteSpace(resumeSessionId))
                        throw new ArgumentException("Resuming a session is not supported for Codex terminals.", nameof(resumeSessionId));
                    return BuildCodexCommand(project, workingDirectory);
                case Models.TerminalKind.ClaudeCode:
                default:
                    return BuildClaudeCommand(project, workingDirectory, resumeSessionId);
            }
        }

        /// <summary>
        /// Validates a resume session id and returns it in canonical lowercase "D" form
        /// (<c>xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx</c>), or null when none was given (null/whitespace).
        /// Uppercase hex is accepted and lowercased; braced, parenthesised and hyphenless forms are rejected.
        /// <para>WHY A GUID AND NOT JUST QUOTED. The id is spliced unquoted into a PowerShell command line,
        /// and every caller is MT code holding an id it read from Claude Code — so a non-GUID is a bug, and
        /// throwing surfaces it where launching fresh would hide it. The canonical re-format means only
        /// hex and hyphens ever reach the shell.</para>
        /// <para>WHY NOT <c>--continue</c>. It picks the newest session in the cwd, which may be live in
        /// another pane, and cannot reach a session recorded under a worktree's directory.</para>
        /// </summary>
        internal static string NormalizeResumeSessionId(string resumeSessionId)
        {
            if (string.IsNullOrWhiteSpace(resumeSessionId))
                return null;

            if (!Guid.TryParseExact(resumeSessionId, "D", out Guid id))
                throw new ArgumentException($"Resume session id is not a GUID: '{resumeSessionId}'.", nameof(resumeSessionId));

            return id.ToString("D"); // Guid.ToString is always lowercase.
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

            // No channel authorization flag (ticket 0ff1b520, item 16). MT delivers into sessions
            // through Claude Code's native cross-session messaging, so there is no channel server to
            // authorize, and the flag's startup warning dialog no longer needs auto-accepting.

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
        /// Builds the <c>--setting-sources</c> flag from a comma-separated source list.
        /// <para>Emits ONE flag with the list single-quoted (<c>--setting-sources 'user,project'</c>).
        /// The flag is last-wins: repeating it (<c>--setting-sources user --setting-sources project</c>)
        /// keeps only <c>project</c>, so user settings and user plugins are silently ignored (GH #52).
        /// The quotes are needed because the pane runs the command through <c>pwsh -Command</c>, where an
        /// unquoted <c>user,project</c> is an array that reaches claude as <c>user project</c> and fails
        /// with <c>Invalid setting source: user project</c>. Returns a leading-space-prefixed string
        /// ready to append.</para>
        /// </summary>
        public static string BuildSettingSourcesFlags(string commaSeparatedSources)
        {
            if (string.IsNullOrWhiteSpace(commaSeparatedSources)) return string.Empty;
            var sources = commaSeparatedSources
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToArray();
            return sources.Length == 0 ? string.Empty : $" --setting-sources '{string.Join(",", sources)}'";
        }

        /// <summary>
        /// The value MT asks its terminals to use for Claude Code's <c>crossSessionInbound</c>
        /// setting (ticket 0ff1b520, item 2). See <see cref="ApplyCrossSessionInbound"/>.
        /// </summary>
        public const string CrossSessionInboundValue = "accept";

        /// <summary>
        /// Sets <c>crossSessionInbound</c> in MT's emitted launch settings — but ONLY when nothing
        /// already specifies one (ticket 0ff1b520, item 2).
        /// </summary>
        /// <remarks>
        /// <para><b>Why set it at all.</b> Left unset, Claude Code decides per message from the two
        /// sessions' permission modes: a session that bypasses prompts HOLDS an inbound message
        /// unless the sender attests the same class. Every MT terminal launches with bypass today,
        /// so they match and messages auto-deliver — but that is an accident of uniform
        /// configuration, not a guarantee. <c>AgentProcess.SpawnAsync</c> already takes a
        /// <c>permissionMode</c> parameter that no caller overrides, and
        /// <c>SettingsService.GetClaudeCommands</c> offers non-bypass presets behind a
        /// <c>GetDefaultClaudeCommand()</c> with no callers. The moment either is wired up, traffic
        /// between a prompting terminal and a bypass terminal is held in ONE direction, silently,
        /// and the symptom reads as "nobody messaged me" — the worst shape a messaging bug can take.
        /// </para>
        /// <para><b>Why only when absent — and this is the load-bearing half.</b> The caller DROPS
        /// Claude Code's LOCAL source and re-supplies that file's keys here, at <c>--settings</c>
        /// precedence. So if a project's <c>settings.local.json</c> deliberately set
        /// <c>crossSessionInbound</c> to <c>"hold"</c>, overwriting it with <c>"accept"</c> would not
        /// be MT expressing a preference — it would be MT LAUNDERING a repository's tightening
        /// through a higher precedence level than the repository could reach. The CLI is explicit
        /// that a repo may only tighten and that a user's own "accept" cannot override a repo
        /// tightening; re-supplying its keys must not become a way around that. Same fail-closed
        /// instinct as <c>CanDropLocal</c>: never remove a safety choice while carrying it forward.
        /// </para>
        /// <para><b>What this does NOT guarantee.</b> "accept" here is best-effort. Managed settings
        /// outrank it outright, and a repository's own tightening still wins — so a message can
        /// still be held despite this. The setting removes the silent-hold case MT actually causes
        /// (its own uniform-bypass assumption); it does not make delivery certain.</para>
        /// <para><b>Naming debt, recorded rather than fixed here:</b> this key rides in the file
        /// <see cref="BuildForcedStatuslineFlag"/> writes, whose name now understates what it emits.
        /// Renaming is mechanical but touches OracleService and the launch tests, so it is left to
        /// its own change rather than widened into this one.</para>
        /// </remarks>
        public static void ApplyCrossSessionInbound(System.Text.Json.Nodes.JsonObject settings)
        {
            if (settings == null) return;

            // The laundering guard. Demonstrated 2026-09-21: removing this line reddens
            // LaunchSettingsMergeTests.A_projects_own_hold_survives_into_the_emitted_settings along
            // with the never-overwritten facts — i.e. a project's deliberate "hold" would be carried
            // across the precedence boundary and dropped on the way.
            if (settings.ContainsKey("crossSessionInbound")) return;

            settings["crossSessionInbound"] = CrossSessionInboundValue;
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
        /// by <c>BuildFlags</c> (docked and spawned
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
                ApplyCrossSessionInbound(merged);
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
        /// The HKCU key the installer writes the shared plugin location under (ticket c8ee0eec, GitHub #23).
        /// </summary>
        internal const string InstallerRegistryKey = @"Software\MultiTerminal";

        /// <summary>
        /// REG_SZ under <see cref="InstallerRegistryKey"/>: the shared MARKETPLACE ROOT the installer put the
        /// plugin in (the folder equivalent to <c>~\.claude\plugins\marketplaces\multiterminal-marketplace</c>).
        /// Absent or empty means the user chose the default-profile location.
        /// </summary>
        internal const string PluginMarketplacePathValueName = "PluginMarketplacePath";

        /// <summary>
        /// Returns the first MultiTerminal plugin directory that exists, in the order of
        /// <see cref="ExpectedMtPluginPaths"/>, or null when none does.
        /// </summary>
        public static string GetMtPluginPath() =>
            ResolveMtPluginPath(ExpectedMtPluginPaths, Directory.Exists);

        /// <summary>
        /// Every place <see cref="GetMtPluginPath"/> looks, in lookup order, whether or not anything is
        /// there. <see cref="ClaudeIntegrationCheck"/> names all of them in its startup warning.
        /// </summary>
        internal static IReadOnlyList<string> ExpectedMtPluginPaths =>
            MtPluginPathCandidates(
                ReadInstallerPluginMarketplacePath(),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        /// <summary>
        /// Pure: the plugin-directory candidates, in lookup order.
        /// <list type="number">
        /// <item>The installer's shared location, <c>&lt;PluginMarketplacePath&gt;\plugins\multiterminal</c>,
        /// only when the registry value is a usable absolute path. Anything else (null, blank, relative,
        /// malformed) is ignored, so the lookup falls through to the default rather than throwing or
        /// resolving against MT's current directory.</item>
        /// <item>The default profile's marketplace checkout under <paramref name="userProfile"/>.
        /// CLAUDE_CONFIG_DIR is deliberately NOT honoured, and neither is a pane's Claude profile
        /// (ticket c8ee0eec): the plugin is installed once, at the installer's shared location or in
        /// <c>%USERPROFILE%\.claude</c>, and handed to every terminal with <c>--plugin-dir</c>
        /// whichever profile that terminal runs under.</item>
        /// </list>
        /// The default profile's plugin CACHE (<c>~\.claude\plugins\cache\...</c>) is deliberately not a
        /// candidate: Claude Code copies it once at <c>/plugin install</c> time and MultiTerminal's updater
        /// never refreshes it, so it can be months stale, and finding it would also silence the
        /// missing-plugin warning while launching agents with an outdated plugin.
        /// </summary>
        internal static IReadOnlyList<string> MtPluginPathCandidates(string registryMarketplacePath, string userProfile)
        {
            var candidates = new List<string>(2);

            string shared = SharedPluginDirFromRegistryValue(registryMarketplacePath);
            if (shared != null)
                candidates.Add(shared);

            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                candidates.Add(Path.Combine(userProfile, ".claude", "plugins",
                    "marketplaces", "multiterminal-marketplace", "plugins", "multiterminal"));
            }

            return candidates;
        }

        /// <summary>
        /// Pure: the first candidate <paramref name="directoryExists"/> accepts, or null. A probe that
        /// throws counts as "not there" for that candidate only.
        /// </summary>
        internal static string ResolveMtPluginPath(IEnumerable<string> candidates, Func<string, bool> directoryExists)
        {
            foreach (string candidate in candidates)
            {
                try
                {
                    if (directoryExists(candidate))
                        return candidate;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
                {
                    // Unreadable candidate: try the next one.
                }
            }

            return null;
        }

        /// <summary>
        /// <c>&lt;value&gt;\plugins\multiterminal</c> when <paramref name="registryMarketplacePath"/> is a
        /// fully qualified path; otherwise null.
        /// </summary>
        private static string SharedPluginDirFromRegistryValue(string registryMarketplacePath)
        {
            if (string.IsNullOrWhiteSpace(registryMarketplacePath))
                return null;

            string root = registryMarketplacePath.Trim().Trim('"');
            try
            {
                if (!Path.IsPathFullyQualified(root))
                    return null;
                return Path.Combine(root, "plugins", "multiterminal");
            }
            catch (ArgumentException)
            {
                return null; // Illegal characters: treat as absent.
            }
        }

        /// <summary>
        /// HKCU\Software\MultiTerminal\PluginMarketplacePath, or null when absent, not a string, or
        /// unreadable. Never throws: a broken registry must not stop a terminal launching.
        /// </summary>
        private static string ReadInstallerPluginMarketplacePath()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(InstallerRegistryKey);
                return key?.GetValue(PluginMarketplacePathValueName) as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// Returns the path to the centralized MCP config file, healing it first if it has gone
        /// missing or broken since startup (a working file is left alone). Null only when it cannot be
        /// created — and then every launch runs without the core MCP servers, which is the silent
        /// failure GitHub #8 reported.
        /// Location: %APPDATA%\multiterminal\.mcp.json
        /// </summary>
        public static string GetMcpConfigPath()
        {
            Action<string> log = _log == null ? null : msg => _log.Info("LaunchCommandBuilder", msg);
            return CentralMcpConfig.PathForLaunch(
                CentralMcpConfig.DefaultPath,
                () => CentralMcpConfig.EnsureDefault(log),
                log);
        }

        private static DebugLogService _log;

        /// <summary>
        /// Wires the shared <see cref="DebugLogService"/> sink for the launch-time .mcp.json heal. Called
        /// once by MainForm. Unwired (or in unit tests), heal outcomes are simply not logged.
        /// </summary>
        public static void SetLogger(DebugLogService logger) => _log = logger;

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
        /// <remarks>
        /// Internal (not private) since task 77d1182f: <c>MainForm.OnSpawnRequested</c> applies the
        /// same rule to an API-supplied cwd before anything is created, because passing an explicit
        /// <c>workingDirectory</c> to the builders bypasses <c>ResolveWorkingDirectory</c>'s own check.
        /// One rule, one helper — a spawn must not accept a directory a project launch would refuse.
        /// </remarks>
        internal static bool IsUsableProjectPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (!Path.IsPathRooted(path)) return false;
            if (path.StartsWith("\\\\", StringComparison.Ordinal)) return false; // UNC share
            if (!Directory.Exists(path)) return false;
            return true;
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
