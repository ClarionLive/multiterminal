using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Service for programmatically spawning PowerShell terminals with Claude Code.
    /// Enables agent teams by launching separate windows with pre-configured environment.
    /// </summary>
    public class TerminalSpawner
    {
        private readonly Dictionary<string, SpawnedTeammate> _spawnedTeammates;
        private readonly object _lock = new object();
        private static string _detectedPowerShell = null;
        private const int FirstChannelPort = 8801;
        private const int MaxChannelPort = 8899;
        private int _nextChannelPort = FirstChannelPort;

        /// <summary>
        /// Creates a new TerminalSpawner instance.
        /// </summary>
        public TerminalSpawner()
        {
            _spawnedTeammates = new Dictionary<string, SpawnedTeammate>();
        }

        /// <summary>
        /// Detects which PowerShell executable is available on the system.
        /// Tries PowerShell Core (pwsh.exe) first, falls back to Windows PowerShell (powershell.exe).
        /// </summary>
        private static string GetPowerShellExecutable()
        {
            // Cache the result so we only detect once
            if (_detectedPowerShell != null)
                return _detectedPowerShell;

            // Try PowerShell Core first (pwsh.exe)
            if (IsPowerShellAvailable("pwsh.exe"))
            {
                _detectedPowerShell = "pwsh.exe";
                return _detectedPowerShell;
            }

            // Fall back to Windows PowerShell (powershell.exe)
            if (IsPowerShellAvailable("powershell.exe"))
            {
                _detectedPowerShell = "powershell.exe";
                return _detectedPowerShell;
            }

            // Neither found - throw exception
            throw new InvalidOperationException(
                "No PowerShell installation found. Please install Windows PowerShell or PowerShell Core.");
        }

        /// <summary>
        /// Checks if a PowerShell executable is available in PATH.
        /// </summary>
        private static bool IsPowerShellAvailable(string executable)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "-Command \"exit 0\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var process = Process.Start(psi))
                {
                    process.WaitForExit(2000); // 2 second timeout
                    return process.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Spawns a new PowerShell terminal running Claude Code with pre-configured environment.
        /// </summary>
        /// <param name="agentName">Display name for the teammate (e.g., "Alice", "Bob")</param>
        /// <param name="agentType">Agent role/type (e.g., "researcher", "implementer")</param>
        /// <param name="workingDir">Working directory for the terminal (defaults to current)</param>
        /// <param name="initialPrompt">Optional prompt to send after registration</param>
        /// <returns>SpawnedTeammate with DocId for tracking</returns>
        /// <summary>
        /// Sanitizes a string for safe interpolation into a PowerShell single-quoted string.
        /// Escapes single quotes by doubling them (' → '').
        /// </summary>
        private static string SanitizeForPowerShell(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Replace("'", "''");
        }

        /// <summary>
        /// Validates that an agent name contains only safe characters (alphanumeric, spaces, hyphens, underscores).
        /// </summary>
        private static void ValidateAgentName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Agent name cannot be empty", nameof(name));

            foreach (char c in name)
            {
                if (!char.IsLetterOrDigit(c) && c != ' ' && c != '-' && c != '_')
                    throw new ArgumentException(
                        $"Agent name contains invalid character '{c}'. Only letters, digits, spaces, hyphens, and underscores are allowed.",
                        nameof(name));
            }
        }

        /// <summary>
        /// Validates that a working directory path is safe (rooted, no UNC, exists).
        /// </summary>
        private static void ValidateWorkingDir(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            if (!Path.IsPathRooted(path))
                throw new ArgumentException("Working directory must be an absolute path", nameof(path));

            if (path.StartsWith(@"\\"))
                throw new ArgumentException("UNC paths are not allowed for working directory", nameof(path));

            if (!Directory.Exists(path))
                throw new ArgumentException($"Working directory does not exist: {path}", nameof(path));
        }

        /// <summary>
        /// Case-insensitive normalized path equality for the workingDir vs
        /// taskWorktreePath comparison in the stale-worktree guard.
        /// </summary>
        private static bool PathsEqual(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                string na = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string nb = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Three-state classification of a worktree path, replacing the prior
        /// binary <c>IsValidWorktree</c> (cycle-2 adversary finding: don't
        /// collapse a slow git probe into "invalid" — that silently re-scopes
        /// the agent away from the task worktree on cold-cache / AV-heavy
        /// Windows).
        ///
        /// <para>Fast-path: cheap filesystem check for <c>.git</c> presence
        /// (file or dir; secondary worktrees use a <c>.git</c> file pointing
        /// at the admin dir). If present, return <c>Valid</c> without forking
        /// git. Only when the cheap check fails do we fork <c>git rev-parse
        /// --git-dir</c> with a generous 10s budget — and on timeout we return
        /// <c>Inconclusive</c> rather than <c>Invalid</c> so the caller can
        /// pass the path through unchanged.</para>
        /// </summary>
        private enum WorktreeValidity { Valid, Invalid, Inconclusive }

        private static WorktreeValidity ClassifyWorktree(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return WorktreeValidity.Invalid;
            try
            {
                if (!Directory.Exists(path)) return WorktreeValidity.Invalid;
            }
            catch
            {
                return WorktreeValidity.Invalid;
            }

            if (WorktreeLayout.IsLikelyGitRepoRoot(path))
            {
                return WorktreeValidity.Valid;
            }

            // Cheap check failed — fall back to git. 10s budget covers cold
            // caches; on timeout, return Inconclusive (don't drop the env var).
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = path,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("rev-parse");
                psi.ArgumentList.Add("--git-dir");

                using var proc = Process.Start(psi);
                if (proc == null) return WorktreeValidity.Inconclusive;
                if (!proc.WaitForExit(10000))
                {
                    try { proc.Kill(); } catch { }
                    Debug.WriteLine($"[TerminalSpawner] git rev-parse --git-dir timed out at '{path}' (>10s) — classifying inconclusive.");
                    return WorktreeValidity.Inconclusive;
                }
                return proc.ExitCode == 0 ? WorktreeValidity.Valid : WorktreeValidity.Invalid;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TerminalSpawner] git probe threw for '{path}': {ex.Message} — classifying inconclusive.");
                return WorktreeValidity.Inconclusive;
            }
        }

        /// <summary>
        /// Best-effort derivation of a repo root from a worktree path that may
        /// no longer exist on disk. Delegates to
        /// <see cref="WorktreeLayout.DeriveRepoRootFromParent"/> so the layout
        /// heuristic stays in one place (cycle-2 code-reviewer dedup finding).
        /// Returns <c>null</c> when the parent doesn't match a known layout
        /// or the derived candidate isn't a git repo.
        /// </summary>
        private static string TryDeriveRepoRootFromWorktree(string worktreePath)
        {
            if (string.IsNullOrWhiteSpace(worktreePath)) return null;
            try
            {
                string parentDir = Path.GetDirectoryName(worktreePath);
                return WorktreeLayout.DeriveRepoRootFromParent(parentDir);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Generates a system prompt file for a spawned agent at %APPDATA%\multiterminal\agent-{name}-prompt.md.
        /// Contains a subset of multiterminal-rules.md plus agent-specific identity.
        /// Returns the absolute path to the generated file.
        /// </summary>
        private static string GenerateAgentSystemPrompt(string agentName, string agentType)
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string mtDir = Path.Combine(appData, "multiterminal");
            Directory.CreateDirectory(mtDir);

            string fileName = $"agent-{agentName.ToLowerInvariant().Replace(" ", "-")}-prompt.md";
            string filePath = Path.Combine(mtDir, fileName);

            // Try to read rules from the project's multiterminal-rules.md
            string rules = string.Empty;
            string mtSourcePath = LaunchCommandBuilder.GetMtSourcePath();
            if (mtSourcePath != null)
            {
                string rulesPath = Path.Combine(mtSourcePath, "multiterminal-rules.md");
                if (File.Exists(rulesPath))
                    rules = File.ReadAllText(rulesPath);
            }

            string prompt = $@"# Agent Identity

You are **{agentName}**, a spawned team agent (role: {agentType}).
You were launched by MultiTerminal to work on a specific task.
Always use ""{agentName}"" as your name when registering, claiming tasks, or sending messages.

# Agent Rules

{rules}

# Agent Constraints

- You are a spawned agent — focus on your assigned task only.
- Do not create new tasks unless explicitly asked.
- Do not modify global settings or hooks.
- Report back to your spawner when your work is complete.
";

            File.WriteAllText(filePath, prompt);
            return filePath;
        }

        public SpawnedTeammate SpawnTerminal(
            string agentName,
            string agentType,
            string workingDir = null,
            string initialPrompt = null,
            string taskWorktreePath = null)
        {
            ValidateAgentName(agentName);
            ValidateAgentName(agentType); // Same whitelist — alphanumeric, spaces, hyphens, underscores

            // Use current directory if not specified
            if (string.IsNullOrWhiteSpace(workingDir))
                workingDir = Directory.GetCurrentDirectory();

            // Stale worktree guard (task db4b18c6): three failure modes
            // converge here, all leading to the same broken state — the agent
            // shell lands in an empty-dir shell that isn't a git worktree.
            //
            //   (a) The path was pruned but Windows held the cwd handle so the
            //       empty shell remains on disk (the original bug).
            //   (b) The path is currently being pruned by another thread
            //       (TOCTOU window between PruneForTaskAsync deciding and git
            //       actually unregistering — cycle-2 adversary finding).
            //   (c) workingDir is a stale worktree even though taskWorktreePath
            //       isn't (or vice-versa) — cycle-2 debugger finding.
            //
            // Guard rules:
            //   1. If taskWorktreePath is being pruned right now → drop it.
            //   2. If taskWorktreePath isn't a valid worktree → drop it; if
            //      workingDir equals it, rewrite workingDir to repo root.
            //   3. INDEPENDENTLY, if workingDir looks like a worktree path but
            //      isn't a valid one → rewrite it to repo root. Catches the
            //      divergent-stale case.
            if (!string.IsNullOrEmpty(taskWorktreePath))
            {
                bool drop = false;
                string dropReason = null;

                if (WorktreePruneCoordinator.IsPruning(taskWorktreePath))
                {
                    drop = true;
                    dropReason = "prune in progress for this path";
                }
                else
                {
                    var validity = ClassifyWorktree(taskWorktreePath);
                    if (validity == WorktreeValidity.Invalid)
                    {
                        drop = true;
                        dropReason = "not a valid git worktree";
                    }
                    else if (validity == WorktreeValidity.Inconclusive)
                    {
                        // Cycle-2 adversary finding: don't collapse timeout
                        // into invalidity. Keep the env var; let the agent
                        // discover the truth via its own startup check.
                        Debug.WriteLine(
                            $"[TerminalSpawner] taskWorktreePath '{taskWorktreePath}' validation inconclusive (slow git probe) — passing through unchanged.");
                    }
                }

                if (drop)
                {
                    Debug.WriteLine(
                        $"[TerminalSpawner] taskWorktreePath '{taskWorktreePath}' dropped ({dropReason}).");
                    if (PathsEqual(workingDir, taskWorktreePath))
                    {
                        string repoRoot = TryDeriveRepoRootFromWorktree(taskWorktreePath);
                        if (!string.IsNullOrEmpty(repoRoot))
                        {
                            Debug.WriteLine(
                                $"[TerminalSpawner] Rewriting workingDir from stale worktree to repo root '{repoRoot}'.");
                            workingDir = repoRoot;
                        }
                    }
                    taskWorktreePath = null;
                }
            }

            // Independent workingDir check — fires regardless of taskWorktreePath
            // state. Catches the case where workingDir is stale but
            // taskWorktreePath is empty / different.
            if (!string.IsNullOrEmpty(workingDir))
            {
                string derived = TryDeriveRepoRootFromWorktree(workingDir);
                if (!string.IsNullOrEmpty(derived)
                    && ClassifyWorktree(workingDir) == WorktreeValidity.Invalid)
                {
                    Debug.WriteLine(
                        $"[TerminalSpawner] workingDir '{workingDir}' is a stale worktree path; rewriting to repo root '{derived}'.");
                    workingDir = derived;
                }
            }

            // Launch-at-root (task 0134ec2f): when a VALID task worktree is in
            // play, launch the agent at the repo ROOT rather than inside the
            // worktree, and let the session-start skill EnterWorktree(path) narrow
            // into it (CLI >= 2.1.157). The old in-shell `cd '<worktree>'` pinned
            // the harness cwd to the worktree — when the task completed and the
            // worktree was pruned, the shell was stranded inside a deleted dir and
            // `git worktree remove` could not remove its own cwd. Only rewrite when
            // workingDir actually IS the worktree, so callers that already pass the
            // repo root (the ConPtyTerminal launch path) are left untouched.
            // MULTITERMINAL_TASK_WORKTREE (set below) still carries the path so the
            // skill + MCP tools resolve it.
            if (!string.IsNullOrEmpty(taskWorktreePath) && PathsEqual(workingDir, taskWorktreePath))
            {
                string launchRoot = TryDeriveRepoRootFromWorktree(taskWorktreePath);
                if (!string.IsNullOrEmpty(launchRoot) && Directory.Exists(launchRoot))
                {
                    Debug.WriteLine(
                        $"[TerminalSpawner] Launch-at-root: workingDir '{workingDir}' -> repo root '{launchRoot}'; EnterWorktree will narrow into the worktree.");
                    workingDir = launchRoot;
                }
            }

            ValidateWorkingDir(workingDir);

            // Generate unique 8-character doc ID (hex format, like existing system)
            string docId = Guid.NewGuid().ToString("N").Substring(0, 8);

            // Sanitize all values for safe PowerShell single-quote interpolation
            string safeName = SanitizeForPowerShell(agentName);
            string safeDocId = SanitizeForPowerShell(docId);
            string safeType = SanitizeForPowerShell(agentType);
            string safeDir = SanitizeForPowerShell(workingDir);

            // Assign a unique channel port for this terminal (wraps within allowed range)
            int channelPort;
            lock (_lock)
            {
                channelPort = _nextChannelPort;
                _nextChannelPort = _nextChannelPort >= MaxChannelPort ? FirstChannelPort : _nextChannelPort + 1;
            }

            // Generate agent system prompt file for subprocess isolation
            string systemPromptFile = GenerateAgentSystemPrompt(agentName, agentType);
            string safePromptFile = SanitizeForPowerShell(systemPromptFile);

            // Build Claude CLI flags for spawned agents:
            //   --system-prompt-file: controlled rules (no global CLAUDE.md leakage)
            //   --setting-sources project,local: skip user-level settings/hooks
            //   --plugin-dir: loads the MT plugin (hooks, skills, agents, channels)
            string pluginDir = LaunchCommandBuilder.GetMtPluginPath();
            string pluginFlag = pluginDir != null ? $" --plugin-dir '{SanitizeForPowerShell(pluginDir)}'" : "";
            string channelFlag = "";
            if (pluginDir != null && Directory.Exists(Path.Combine(pluginDir, "server")))
            {
                string pName = Path.GetFileName(pluginDir);
                channelFlag = $" --dangerously-load-development-channels plugin:{pName}@inline";
            }
            string claudeFlags = $"--system-prompt-file '{safePromptFile}' --setting-sources project,local{pluginFlag}{channelFlag}";

            // Add MCP config if available
            string mcpConfig = LaunchCommandBuilder.GetMcpConfigPath();
            if (mcpConfig != null)
            {
                string safeMcpConfig = SanitizeForPowerShell(mcpConfig);
                claudeFlags += $" --mcp-config '{safeMcpConfig}'";
            }

            // Phase 1 worktree isolation: pass the worktree path through to the
            // spawned agent for introspection. Empty when no task worktree is in
            // play (i.e. WORKTREE_MODE=off or this terminal isn't task-scoped).
            // Stale-path validation already ran above (task db4b18c6).
            string safeWorktreePath = string.IsNullOrEmpty(taskWorktreePath)
                ? string.Empty
                : SanitizeForPowerShell(taskWorktreePath);

            // Build PowerShell command that sets environment variables and launches Claude Code
            // The startup hook will detect these variables and auto-register
            string command = $@"
$env:MULTITERMINAL_NAME='{safeName}';
$env:MULTITERMINAL_DOC_ID='{safeDocId}';
$env:MULTITERMINAL_ROLE='{safeType}';
$env:MULTITERMINAL_SPAWNER='{SanitizeForPowerShell(Environment.GetEnvironmentVariable("MULTITERMINAL_NAME") ?? "host")}';
$env:MULTITERMINAL_TASK_WORKTREE='{safeWorktreePath}';
$env:CHANNEL_PORT='{channelPort}';
$env:CLAUDE_CODE_NO_FLICKER='1';
cd '{safeDir}';
Write-Host '🤖 Spawning as {safeName} ({safeType}) [channel port {channelPort}]...' -ForegroundColor Cyan;
claude {claudeFlags}
".Trim();

            // Spawn PowerShell process (auto-detect pwsh.exe or powershell.exe)
            var psi = new ProcessStartInfo
            {
                FileName = GetPowerShellExecutable(), // Auto-detects available PowerShell
                Arguments = $"-NoExit -Command \"{command}\"",
                UseShellExecute = true, // Opens in new window
                CreateNoWindow = false,
                WorkingDirectory = workingDir
            };

            // Cycle-3 adversary HIGH fix: last-moment re-check before
            // launch. The entry-point IsPruning check (above) can race with a
            // broker MarkPruning that fires during the rest of SpawnTerminal's
            // setup work. Re-checking here shrinks the window to just the
            // Process.Start syscall itself. If the path is now pruning, drop
            // the env var and (when workingDir would match) rewrite workingDir
            // to repo root — same handling as the entry-point guard.
            if (!string.IsNullOrEmpty(taskWorktreePath) && WorktreePruneCoordinator.IsPruning(taskWorktreePath))
            {
                Debug.WriteLine(
                    $"[TerminalSpawner] taskWorktreePath '{taskWorktreePath}' began pruning during spawn setup; dropping env var at launch.");
                if (PathsEqual(workingDir, taskWorktreePath))
                {
                    string repoRoot = TryDeriveRepoRootFromWorktree(taskWorktreePath);
                    if (!string.IsNullOrEmpty(repoRoot))
                    {
                        workingDir = repoRoot;
                        psi.WorkingDirectory = workingDir;
                    }
                }
                // Rebuild the command without the env var. Cheapest path: a
                // second pass through the template with safeWorktreePath empty.
                safeWorktreePath = string.Empty;
                command = $@"
$env:MULTITERMINAL_NAME='{safeName}';
$env:MULTITERMINAL_DOC_ID='{safeDocId}';
$env:MULTITERMINAL_ROLE='{safeType}';
$env:MULTITERMINAL_SPAWNER='{SanitizeForPowerShell(Environment.GetEnvironmentVariable("MULTITERMINAL_NAME") ?? "host")}';
$env:MULTITERMINAL_TASK_WORKTREE='{safeWorktreePath}';
$env:CHANNEL_PORT='{channelPort}';
$env:CLAUDE_CODE_NO_FLICKER='1';
cd '{SanitizeForPowerShell(workingDir)}';
Write-Host '🤖 Spawning as {safeName} ({safeType}) [channel port {channelPort}]...' -ForegroundColor Cyan;
claude {claudeFlags}
".Trim();
                psi.Arguments = $"-NoExit -Command \"{command}\"";
            }

            Process process;
            try
            {
                process = Process.Start(psi);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to spawn terminal for {agentName}: {ex.Message}", ex);
            }

            // Track the spawned teammate
            var teammate = new SpawnedTeammate
            {
                DocId = docId,
                Name = agentName,
                AgentType = agentType,
                WorkingDirectory = workingDir,
                Process = process,
                SpawnedAt = DateTime.Now,
                IsRegistered = false,
                InitialPrompt = initialPrompt,
                ChannelPort = channelPort
            };

            lock (_lock)
            {
                _spawnedTeammates[docId] = teammate;
            }

            return teammate;
        }

        /// <summary>
        /// Marks a teammate as registered after MessageBroker confirms registration.
        /// </summary>
        /// <param name="docId">Document ID of the teammate</param>
        /// <param name="terminalId">Terminal ID assigned by MessageBroker</param>
        public void MarkAsRegistered(string docId, string terminalId)
        {
            lock (_lock)
            {
                if (_spawnedTeammates.TryGetValue(docId, out var teammate))
                {
                    teammate.IsRegistered = true;
                    teammate.TerminalId = terminalId;
                }
            }
        }

        /// <summary>
        /// Gets a spawned teammate by document ID.
        /// </summary>
        public SpawnedTeammate GetTeammate(string docId)
        {
            lock (_lock)
            {
                return _spawnedTeammates.TryGetValue(docId, out var teammate) ? teammate : null;
            }
        }

        /// <summary>
        /// Gets a spawned teammate by name.
        /// </summary>
        public SpawnedTeammate GetTeammateByName(string name)
        {
            lock (_lock)
            {
                return _spawnedTeammates.Values.FirstOrDefault(t =>
                    t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>
        /// Gets all spawned teammates.
        /// </summary>
        public List<SpawnedTeammate> GetAllTeammates()
        {
            lock (_lock)
            {
                return _spawnedTeammates.Values.ToList();
            }
        }

        /// <summary>
        /// Waits for a teammate to complete registration.
        /// </summary>
        /// <param name="docId">Document ID of the teammate</param>
        /// <param name="timeoutMs">Timeout in milliseconds (default: 30000 = 30 seconds)</param>
        /// <returns>True if registration completed, false if timeout</returns>
        public async Task<bool> WaitForRegistration(string docId, int timeoutMs = 30000)
        {
            var startTime = DateTime.Now;
            var timeout = TimeSpan.FromMilliseconds(timeoutMs);

            while (DateTime.Now - startTime < timeout)
            {
                var teammate = GetTeammate(docId);
                if (teammate?.IsRegistered == true)
                    return true;

                // Poll every 500ms
                await Task.Delay(500);
            }

            return false;
        }

        /// <summary>
        /// Removes a teammate from tracking (cleanup after exit).
        /// </summary>
        public void RemoveTeammate(string docId)
        {
            lock (_lock)
            {
                _spawnedTeammates.Remove(docId);
            }
        }

        /// <summary>
        /// Checks if a process is still running.
        /// </summary>
        public bool IsProcessRunning(string docId)
        {
            var teammate = GetTeammate(docId);
            if (teammate?.Process == null)
                return false;

            try
            {
                return !teammate.Process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Gets count of active (running) teammates.
        /// </summary>
        public int GetActiveCount()
        {
            lock (_lock)
            {
                return _spawnedTeammates.Values.Count(t =>
                {
                    try { return t.Process != null && !t.Process.HasExited; }
                    catch { return false; }
                });
            }
        }
    }
}
