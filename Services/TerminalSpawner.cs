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

        public SpawnedTeammate SpawnTerminal(
            string agentName,
            string agentType,
            string workingDir = null,
            string initialPrompt = null)
        {
            ValidateAgentName(agentName);

            if (string.IsNullOrWhiteSpace(agentType))
                throw new ArgumentException("Agent type cannot be empty", nameof(agentType));

            // Use current directory if not specified
            if (string.IsNullOrWhiteSpace(workingDir))
                workingDir = Directory.GetCurrentDirectory();

            ValidateWorkingDir(workingDir);

            // Generate unique 8-character doc ID (hex format, like existing system)
            string docId = Guid.NewGuid().ToString("N").Substring(0, 8);

            // Sanitize all values for safe PowerShell single-quote interpolation
            string safeName = SanitizeForPowerShell(agentName);
            string safeDocId = SanitizeForPowerShell(docId);
            string safeType = SanitizeForPowerShell(agentType);
            string safeDir = SanitizeForPowerShell(workingDir);

            // Build PowerShell command that sets environment variables and launches Claude Code
            // The startup hook will detect these variables and auto-register
            string command = $@"
$env:MULTITERMINAL_NAME='{safeName}';
$env:MULTITERMINAL_DOC_ID='{safeDocId}';
$env:MULTITERMINAL_ROLE='{safeType}';
cd '{safeDir}';
Write-Host '🤖 Spawning as {safeName} ({safeType})...' -ForegroundColor Cyan;
claude
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
                InitialPrompt = initialPrompt
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
