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
        public SpawnedTeammate SpawnTerminal(
            string agentName,
            string agentType,
            string workingDir = null,
            string initialPrompt = null)
        {
            if (string.IsNullOrWhiteSpace(agentName))
                throw new ArgumentException("Agent name cannot be empty", nameof(agentName));

            if (string.IsNullOrWhiteSpace(agentType))
                throw new ArgumentException("Agent type cannot be empty", nameof(agentType));

            // Use current directory if not specified
            if (string.IsNullOrWhiteSpace(workingDir))
                workingDir = Directory.GetCurrentDirectory();

            // Generate unique 8-character doc ID (hex format, like existing system)
            string docId = Guid.NewGuid().ToString("N").Substring(0, 8);

            // Build PowerShell command that sets environment variables and launches Claude Code
            // The startup hook will detect these variables and auto-register
            string command = $@"
$env:MULTITERMINAL_NAME='{agentName}';
$env:MULTITERMINAL_DOC_ID='{docId}';
$env:MULTITERMINAL_ROLE='{agentType}';
cd '{workingDir}';
Write-Host '🤖 Spawning as {agentName} ({agentType})...' -ForegroundColor Cyan;
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
