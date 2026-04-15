using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Manages companion processes (external services like MCP Gateway, Caddy, TTS server)
    /// that MultiTerminal can auto-launch on startup and optionally stop on exit.
    /// Config is loaded from %AppData%\MultiTerminal\companion-processes.json.
    /// </summary>
    public class CompanionProcessManager
    {
        private readonly Dictionary<string, Process> _trackedProcesses = new Dictionary<string, Process>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();
        private CompanionConfig _config;

        private static readonly HttpClient _healthClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        /// <summary>
        /// Path to the companion-processes.json config file.
        /// </summary>
        private static string ConfigPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MultiTerminal",
            "companion-processes.json");

        /// <summary>
        /// Creates a new CompanionProcessManager and loads configuration.
        /// </summary>
        public CompanionProcessManager()
        {
            LoadConfig();
        }

        /// <summary>
        /// Loads companion process configuration from disk.
        /// If the file doesn't exist, initializes with an empty config.
        /// </summary>
        public void LoadConfig()
        {
            try
            {
                var path = ConfigPath;
                if (!File.Exists(path))
                {
                    Debug.WriteLine("[CompanionManager] No companion-processes.json found, skipping.");
                    _config = new CompanionConfig();
                    return;
                }

                var json = File.ReadAllText(path);
                _config = JsonSerializer.Deserialize<CompanionConfig>(json, JsonOptions) ?? new CompanionConfig();
                Debug.WriteLine($"[CompanionManager] Loaded {_config.Companions.Count} companion(s) from config.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CompanionManager] Failed to load config: {ex.Message}");
                _config = new CompanionConfig();
            }
        }

        /// <summary>
        /// Starts all companions that have AutoStart=true.
        /// For each companion, checks HealthUrl first (if configured) to avoid duplicate launches.
        /// </summary>
        public void StartAll()
        {
            if (_config?.Companions == null || _config.Companions.Count == 0)
                return;

            foreach (var companion in _config.Companions.Where(c => c.AutoStart))
            {
                try
                {
                    StartCompanion(companion);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CompanionManager] Failed to start '{companion.Name}': {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Starts a single companion process.
        /// </summary>
        private void StartCompanion(CompanionProcess companion)
        {
            if (string.IsNullOrWhiteSpace(companion.Command))
            {
                Debug.WriteLine($"[CompanionManager] Skipping '{companion.Name}' — no command specified.");
                return;
            }

            // Check health URL to see if already running
            if (!string.IsNullOrWhiteSpace(companion.HealthUrl))
            {
                if (IsHealthy(companion.HealthUrl))
                {
                    Debug.WriteLine($"[CompanionManager] '{companion.Name}' already running (health check passed), skipping launch.");
                    return;
                }
            }

            Debug.WriteLine($"[CompanionManager] Starting '{companion.Name}': {companion.Command} {companion.Args}");

            var startInfo = new ProcessStartInfo
            {
                FileName = companion.Command,
                Arguments = companion.Args ?? "",
                UseShellExecute = true,  // Launch in new window
            };

            var process = Process.Start(startInfo);

            if (process != null)
            {
                lock (_lock)
                {
                    _trackedProcesses[companion.Name] = process;
                }
                Debug.WriteLine($"[CompanionManager] '{companion.Name}' started (PID: {process.Id}).");
            }
            else
            {
                Debug.WriteLine($"[CompanionManager] '{companion.Name}' — Process.Start returned null.");
            }
        }

        /// <summary>
        /// Performs a quick HTTP GET health check with a 2-second timeout.
        /// Returns true if the endpoint responds with 200 OK.
        /// </summary>
        private static bool IsHealthy(string healthUrl)
        {
            try
            {
                var response = _healthClient.GetAsync(healthUrl).GetAwaiter().GetResult();
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Returns the status of all configured companions.
        /// </summary>
        public List<CompanionStatus> GetStatus()
        {
            var results = new List<CompanionStatus>();
            if (_config?.Companions == null)
                return results;

            foreach (var companion in _config.Companions)
            {
                var status = new CompanionStatus
                {
                    Name = companion.Name,
                    AutoStart = companion.AutoStart,
                    StopOnExit = companion.StopOnExit,
                };

                // Check health URL first (most reliable)
                if (!string.IsNullOrWhiteSpace(companion.HealthUrl))
                {
                    status.Running = IsHealthy(companion.HealthUrl);
                    status.HealthUrl = companion.HealthUrl;
                }
                else
                {
                    // Fall back to checking tracked process
                    lock (_lock)
                    {
                        if (_trackedProcesses.TryGetValue(companion.Name, out var proc))
                        {
                            try
                            {
                                status.Running = !proc.HasExited;
                                status.Pid = proc.Id;
                            }
                            catch
                            {
                                status.Running = false;
                            }
                        }
                        else
                        {
                            status.Running = false;
                        }
                    }
                }

                results.Add(status);
            }

            return results;
        }

        /// <summary>
        /// Stops all tracked companion processes that have StopOnExit=true.
        /// Called during MultiTerminal shutdown.
        /// </summary>
        public void StopAll()
        {
            if (_config?.Companions == null)
                return;

            var toStop = _config.Companions.Where(c => c.StopOnExit).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Snapshot processes to stop inside the lock, then release before killing
            // so concurrent GetStatus() calls aren't blocked during WaitForExit
            List<KeyValuePair<string, Process>> snapshot;
            lock (_lock)
            {
                snapshot = _trackedProcesses.Where(kvp => toStop.Contains(kvp.Key)).ToList();
                _trackedProcesses.Clear();
            }

            foreach (var kvp in snapshot)
            {
                try
                {
                    if (!kvp.Value.HasExited)
                    {
                        Debug.WriteLine($"[CompanionManager] Killing '{kvp.Key}' (PID: {kvp.Value.Id})...");
                        kvp.Value.Kill();
                        // Don't WaitForExit — Kill() sends the signal, process exit
                        // failsafe handles cleanup. This was blocking 3s per companion.
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CompanionManager] Failed to stop '{kvp.Key}': {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Status information for a companion process, returned by the API.
    /// </summary>
    public class CompanionStatus
    {
        public string Name { get; set; }
        public bool Running { get; set; }
        public bool AutoStart { get; set; }
        public bool StopOnExit { get; set; }
        public string HealthUrl { get; set; }
        public int? Pid { get; set; }
    }
}
