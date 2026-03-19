using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Event args for when a native Claude Code teammate is discovered.
    /// </summary>
    public class TeammateDiscoveredEventArgs : EventArgs
    {
        public string TeamName { get; set; }
        public string MemberName { get; set; }
        public IAgentMessageSource Tailer { get; set; }
        public string SpawnerName { get; set; }
        public string TaskDescription { get; set; }
        public string SubagentType { get; set; }
        /// <summary>
        /// The team lead's working directory, used as fallback for spawner
        /// resolution when SpawnerName is null (match against terminal cwds).
        /// </summary>
        public string LeadCwd { get; set; }
        /// <summary>
        /// The parent session ID extracted from the subagent's transcript path.
        /// Used to resolve the spawner terminal when SpawnerName is unknown.
        /// </summary>
        public string ParentSessionId { get; set; }
    }

    /// <summary>
    /// Event args for when a native Claude Code team is removed/deleted.
    /// </summary>
    public class TeamRemovedEventArgs : EventArgs
    {
        public string TeamName { get; set; }
        public List<string> MemberNames { get; set; } = new();
        /// <summary>
        /// All transcript paths that were tracked for this team's agents.
        /// Used by MainForm to find and clean up orphan panels by transcript path match.
        /// </summary>
        public List<string> TranscriptPaths { get; set; } = new();
    }

    /// <summary>
    /// Watches ~/.claude/teams/ for native Claude Code agent teams and creates
    /// TranscriptTailer instances for each discovered teammate. This is the
    /// "tmux replacement" — making invisible in-process teammates visible
    /// in MultiTerminal's Agent Panel.
    ///
    /// File structure watched:
    ///   ~/.claude/teams/{team-name}/config.json     → team config with members
    ///   ~/.claude/teams/{team-name}/inboxes/{name}.json → per-agent inbox
    ///   ~/.claude/projects/{slug}/{session}/subagents/agent-{id}.jsonl → transcripts
    /// </summary>
    public class TeamWatcherService : IDisposable
    {
        private readonly string _teamsDir;
        private readonly string _projectDir;
        private readonly Dictionary<string, TeamState> _activeTeams = new Dictionary<string, TeamState>();
        private FileSystemWatcher _teamsRootWatcher;

        private bool _disposed;

        // Project-level subagent watching (non-team agents)
        private readonly HashSet<string> _trackedSubagentFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TranscriptTailer> _orphanTailers = new Dictionary<string, TranscriptTailer>(StringComparer.OrdinalIgnoreCase);
        private readonly List<FileSystemWatcher> _projectWatchers = new List<FileSystemWatcher>();
        private readonly HashSet<string> _watchedProjectDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Track agent names from recently-deleted teams to prevent orphan watcher from
        // creating stale panels. Entries auto-expire after 60 seconds.
        private readonly ConcurrentDictionary<string, DateTime> _retiredTeamAgentNames = new(StringComparer.OrdinalIgnoreCase);

        public event EventHandler<TeammateDiscoveredEventArgs> TeammateDiscovered;
        public event EventHandler<TeamRemovedEventArgs> TeamRemoved;
        public event EventHandler<(string TeamName, string MemberName)> MemberRemoved;
        public event EventHandler<TeammateDiscoveredEventArgs> SubagentDiscovered;
        public event EventHandler<(string TeamName, string MemberName, AgentMessage Message, string SpawnerName)> TeamMessageSent;

        /// <summary>
        /// Optional debug log service for MCP-visible logging.
        /// </summary>
        public DebugLogService DebugLogService { get; set; }

        private void Log(string msg)
        {
            Debug.WriteLine($"[TeamWatcher] [{DateTime.Now:HH:mm:ss.fff}] {msg}");
            DebugLogService?.Trace("TeamWatcher", msg);
        }

        private void LogInfo(string msg)
        {
            Debug.WriteLine($"[TeamWatcher] [{DateTime.Now:HH:mm:ss.fff}] {msg}");
            DebugLogService?.Info("TeamWatcher", msg);
        }

        private void LogError(string msg)
        {
            Debug.WriteLine($"[TeamWatcher] [{DateTime.Now:HH:mm:ss.fff}] {msg}");
            DebugLogService?.Error("TeamWatcher", msg);
        }

        public TeamWatcherService(string projectSlug)
        {
            string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _teamsDir = Path.Combine(userHome, ".claude", "teams");
            _projectDir = Path.Combine(userHome, ".claude", "projects", projectSlug);

            Log($"Teams dir: {_teamsDir}");
            Log($"Project dir: {_projectDir}");
        }

        /// <summary>
        /// Start watching for native agent teams.
        /// Scans existing teams on startup and watches for new team directories.
        /// </summary>
        public void StartWatching()
        {
            if (_disposed) return;

            // Ensure teams directory exists
            if (!Directory.Exists(_teamsDir))
            {
                Log($" Teams directory does not exist yet: {_teamsDir}");
                // Watch for the directory to be created
                string parentDir = Path.GetDirectoryName(_teamsDir);
                if (parentDir != null && Directory.Exists(parentDir))
                {
                    _teamsRootWatcher = new FileSystemWatcher(parentDir)
                    {
                        Filter = "teams",
                        NotifyFilter = NotifyFilters.DirectoryName,
                        EnableRaisingEvents = true
                    };
                    _teamsRootWatcher.Created += (s, e) =>
                    {
                        _teamsRootWatcher?.Dispose();
                        _teamsRootWatcher = null;
                        StartWatchingTeamsDir();
                    };
                }
                // Still watch for non-team subagents even when no teams dir exists
                StartProjectSubagentWatching();
                return;
            }

            StartWatchingTeamsDir();

            // Also watch for non-team subagents at the project level
            StartProjectSubagentWatching();
        }

        private void StartWatchingTeamsDir()
        {
            if (!Directory.Exists(_teamsDir)) return;

            // Watch for new team subdirectories
            _teamsRootWatcher = new FileSystemWatcher(_teamsDir)
            {
                NotifyFilter = NotifyFilters.DirectoryName,
                EnableRaisingEvents = true
            };
            _teamsRootWatcher.Created += OnTeamDirectoryCreated;
            _teamsRootWatcher.Deleted += OnTeamDirectoryDeleted;

            LogInfo($"Started watching: {_teamsDir}");


            // Never restore old agent panels from previous sessions.
            // Only react to NEW team directories created during this session via the FileSystemWatcher.
        }

        private void OnTeamDirectoryCreated(object sender, FileSystemEventArgs e)
        {
            if (!Directory.Exists(e.FullPath)) return;
            string teamName = Path.GetFileName(e.FullPath);
            LogInfo($"New team directory detected: {teamName}");

            // Async delay to let config.json be written, without blocking FSW thread
            Task.Run(async () =>
            {
                await Task.Delay(200);
                if (_disposed) return;
                ProcessTeamDirectory(teamName);
            });
        }

        private void OnTeamDirectoryDeleted(object sender, FileSystemEventArgs e)
        {
            string teamName = Path.GetFileName(e.FullPath);
            LogInfo($"Team directory deleted: {teamName}");

            if (_activeTeams.TryGetValue(teamName, out var state))
            {
                CleanupTeam(teamName, state);
                _activeTeams.Remove(teamName);
            }
        }


        /// <summary>
        /// Process a team directory: read config.json, discover members, create tailers.
        /// </summary>
        private void ProcessTeamDirectory(string teamName)
        {
            try
            {
                string configPath = Path.Combine(_teamsDir, teamName, "config.json");

                if (!File.Exists(configPath))
                {
                    // Watch for config.json creation
                    WatchForConfigFile(teamName);
                    return;
                }

                ReadAndProcessConfig(teamName, configPath);

                // Watch config.json for changes (new members added)
                if (!_activeTeams.ContainsKey(teamName))
                    _activeTeams[teamName] = new TeamState { TeamName = teamName };

                var state = _activeTeams[teamName];
                if (state.ConfigWatcher == null)
                {
                    string teamDir = Path.Combine(_teamsDir, teamName);
                    state.ConfigWatcher = new FileSystemWatcher(teamDir)
                    {
                        Filter = "config.json",
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                        EnableRaisingEvents = true
                    };
                    state.ConfigWatcher.Changed += (s, e) =>
                    {
                        // Debounce
                        var now = DateTime.Now;
                        if ((now - state.LastConfigChange).TotalMilliseconds < 500) return;
                        state.LastConfigChange = now;

                        Task.Run(async () =>
                        {
                            await Task.Delay(100);
                            if (_disposed) return;
                            ReadAndProcessConfig(teamName, configPath);
                        });
                    };
                }
            }
            catch (Exception ex)
            {
                LogError($"Error processing team '{teamName}': {ex.Message}");
            }
        }

        private void WatchForConfigFile(string teamName)
        {
            string teamDir = Path.Combine(_teamsDir, teamName);
            if (!Directory.Exists(teamDir)) return;

            var watcher = new FileSystemWatcher(teamDir)
            {
                Filter = "config.json",
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };
            watcher.Created += (s, e) =>
            {
                watcher.Dispose();
                Task.Run(async () =>
                {
                    await Task.Delay(100);
                    if (_disposed) return;
                    ProcessTeamDirectory(teamName);
                });
            };
        }

        /// <summary>
        /// Read config.json and discover new team members.
        /// For each new member, watch for their transcript JSONL file and create a TranscriptTailer.
        /// </summary>
        private void ReadAndProcessConfig(string teamName, string configPath)
        {
            try
            {
                string json;
                using (var stream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream))
                {
                    json = reader.ReadToEnd();
                }

                if (string.IsNullOrWhiteSpace(json)) return;

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string leadSessionId = root.TryGetProperty("leadSessionId", out var lsProp) ? lsProp.GetString() : null;
                if (string.IsNullOrEmpty(leadSessionId))
                {
                    LogError($"No leadSessionId in config for team '{teamName}'");
                    return;
                }

                if (!_activeTeams.ContainsKey(teamName))
                    _activeTeams[teamName] = new TeamState { TeamName = teamName };

                var state = _activeTeams[teamName];
                state.LeadSessionId = leadSessionId;

                if (!root.TryGetProperty("members", out var membersArr)) return;

                // Derive the correct project directory from the lead member's cwd
                // (MultiTerminal's own CWD may differ from the Claude Code project path)
                string projectDir = _projectDir;
                foreach (var member in membersArr.EnumerateArray())
                {
                    string agentType = member.TryGetProperty("agentType", out var tp) ? tp.GetString() : null;
                    if (agentType == "team-lead")
                    {
                        string leadCwd = member.TryGetProperty("cwd", out var cwdProp) ? cwdProp.GetString() : null;
                        if (!string.IsNullOrEmpty(leadCwd))
                        {
                            state.LeadCwd = leadCwd;
                            string leadSlug = GetClaudeProjectSlug(leadCwd);
                            string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                            projectDir = Path.Combine(userHome, ".claude", "projects", leadSlug);
                            Log($" Derived project dir from lead cwd: {projectDir}");

                            // Also start orphan subagent watching on this project dir
                            // (the app's CWD may differ from where terminals are running)
                            StartProjectSubagentWatchingForDir(projectDir);
                        }
                        break;
                    }
                }

                // Resolve and cache the spawner terminal name once for the team.
                // Used by CreateTailerForMember and message routing.
                if (string.IsNullOrEmpty(state.SpawnerName))
                {
                    state.SpawnerName = LookupTeamSpawner(teamName);
                    if (!string.IsNullOrEmpty(state.SpawnerName))
                        LogInfo($"Cached spawner for team '{teamName}': {state.SpawnerName}");
                }

                // Collect ALL non-lead members into state.KnownMembers (for watcher closure)
                // and identify which are new (not yet tracked)
                var allMembers = new List<(string name, long joinedAt)>();
                var newMembers = new List<(string name, long joinedAt)>();
                foreach (var member in membersArr.EnumerateArray())
                {
                    string memberName = member.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                    string agentType = member.TryGetProperty("agentType", out var typeProp) ? typeProp.GetString() : null;

                    if (string.IsNullOrEmpty(memberName)) continue;
                    if (agentType == "team-lead") continue;

                    long joinedAt = member.TryGetProperty("joinedAt", out var jProp) ? jProp.GetInt64() : 0;
                    allMembers.Add((memberName, joinedAt));

                    if (!state.TrackedMembers.Contains(memberName))
                        newMembers.Add((memberName, joinedAt));
                }
                state.KnownMembers = allMembers;

                // Detect members that were removed from config (agent shutdown)
                var allMemberNames = new HashSet<string>(allMembers.Select(m => m.name));
                var removedMembers = state.TrackedMembers.Where(m => !allMemberNames.Contains(m)).ToList();
                foreach (var removed in removedMembers)
                {
                    LogInfo($"Member '{removed}' removed from team '{teamName}' config — cleaning up");
                    state.TrackedMembers.Remove(removed);
                    if (state.MemberTailers.TryGetValue(removed, out var tailer))
                    {
                        try { tailer.Dispose(); } catch { }
                        state.MemberTailers.Remove(removed);
                    }
                    MemberRemoved?.Invoke(this, (teamName, removed));
                }

                // Build subagents directory path (needed for watching even if no new members)
                string subagentsDir = Path.Combine(projectDir, leadSessionId, "subagents");

                if (newMembers.Count > 0)
                {
                    LogInfo($"Config has {newMembers.Count} new member(s): {string.Join(", ", newMembers.Select(m => m.name))}. Subagents dir: {subagentsDir}");

                    // Sort new members by joinedAt to match with files in order
                    newMembers.Sort((a, b) => a.joinedAt.CompareTo(b.joinedAt));

                    // PRE-CLAIM: Lock all recent JSONL files in the subagents dir BEFORE
                    // the orphan watcher's sync scan can grab them. The orphan watcher checks
                    // _trackedSubagentFiles and skips claimed files. We'll release unclaimed
                    // files after matching if they don't belong to any team member.
                    var preClaimedFiles = new List<string>();
                    if (Directory.Exists(subagentsDir))
                    {
                        var cutoff = DateTime.UtcNow.AddSeconds(-30);
                        try
                        {
                            foreach (var file in Directory.GetFiles(subagentsDir, "agent-*.jsonl"))
                            {
                                var fi = new FileInfo(file);
                                if (fi.CreationTimeUtc > cutoff)
                                {
                                    lock (_trackedSubagentFiles)
                                    {
                                        if (_trackedSubagentFiles.Add(file))
                                            preClaimedFiles.Add(file);
                                    }
                                }
                            }
                            if (preClaimedFiles.Count > 0)
                                Log($" Pre-claimed {preClaimedFiles.Count} recent JSONL file(s) to block orphan watcher");
                        }
                        catch (Exception ex)
                        {
                            Log($" Pre-claim scan error: {ex.Message}");
                        }

                        // Try to match existing files to new members
                        MatchExistingTranscripts(teamName, subagentsDir, newMembers, state);
                    }

                    // Release pre-claimed files that weren't matched to any team member,
                    // so orphan watcher can adopt them (e.g., Explore/Plan subagents).
                    foreach (var file in preClaimedFiles)
                    {
                        if (!state.MatchedTranscripts.Contains(file))
                        {
                            lock (_trackedSubagentFiles) { _trackedSubagentFiles.Remove(file); }
                            Log($" Released unmatched pre-claim: {Path.GetFileName(file)}");
                        }
                    }
                }

                // Always ensure the subagents directory watcher is running.
                // This picks up new JSONL files created when agents wake up for subsequent turns,
                // even after all members have been initially matched.
                WatchForNewTranscripts(teamName, subagentsDir, newMembers, state);
            }
            catch (Exception ex)
            {
                LogError($"Error reading config for team '{teamName}': {ex.Message}");
            }
        }

        /// <summary>
        /// Match existing transcript files to new members by creation time.
        /// </summary>
        private void MatchExistingTranscripts(string teamName, string subagentsDir,
            List<(string name, long joinedAt)> newMembers, TeamState state)
        {
            var files = Directory.GetFiles(subagentsDir, "agent-*.jsonl")
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.CreationTimeUtc)
                .ToList();

            Log($" MatchExistingTranscripts: {files.Count} files in {subagentsDir}, {newMembers.Count} new members");

            foreach (var member in newMembers)
            {
                if (state.TrackedMembers.Contains(member.name)) continue;

                // Find a transcript file created around the member's joinedAt time
                var memberJoinedUtc = DateTimeOffset.FromUnixTimeMilliseconds(member.joinedAt).UtcDateTime;

                Log($"   Member '{member.name}' joinedAt={memberJoinedUtc:HH:mm:ss.fff}");

                // Look for files created within 30 seconds of the member joining
                var matchingFile = files.FirstOrDefault(f =>
                    Math.Abs((f.CreationTimeUtc - memberJoinedUtc).TotalSeconds) < 30
                    && !state.MatchedTranscripts.Contains(f.FullName));

                if (matchingFile != null)
                {
                    LogInfo($"MATCHED '{member.name}' -> {matchingFile.Name} (created={matchingFile.CreationTimeUtc:HH:mm:ss.fff}, delta={Math.Abs((matchingFile.CreationTimeUtc - memberJoinedUtc).TotalSeconds):F1}s)");
                    state.MatchedTranscripts.Add(matchingFile.FullName);
                    CreateTailerForMember(teamName, member.name, matchingFile.FullName, state);
                }
                else
                {
                    LogError($"NO MATCH for '{member.name}' - closest files:");
                    foreach (var f in files.Where(f => !state.MatchedTranscripts.Contains(f.FullName)).Take(5))
                    {
                        var delta = (f.CreationTimeUtc - memberJoinedUtc).TotalSeconds;
                        Log($"     {f.Name} created={f.CreationTimeUtc:HH:mm:ss.fff} delta={delta:F1}s");
                    }
                }
            }
        }

        /// <summary>
        /// Watch the subagents directory for new transcript files.
        /// Initially matches files to untracked members by timing, then continues watching
        /// permanently to pick up new JSONL files created when agents wake up for subsequent turns.
        /// </summary>
        private void WatchForNewTranscripts(string teamName, string subagentsDir,
            List<(string name, long joinedAt)> newMembers, TeamState state)
        {
            // Ensure directory exists
            if (!Directory.Exists(subagentsDir))
            {
                // Watch for the subagents directory to be created
                string parentDir = Path.GetDirectoryName(subagentsDir);
                if (parentDir != null && !Directory.Exists(parentDir))
                {
                    // Session dir doesn't exist yet — watch the project dir (grandparent)
                    // for it to appear, then chain into the subagents watcher.
                    // Without this, the orphan watcher races us and creates a panel
                    // with a generic "Subagent {hash}" name instead of the team member name.
                    string grandparentDir = Path.GetDirectoryName(parentDir);
                    string sessionDirName = Path.GetFileName(parentDir);
                    if (grandparentDir != null && Directory.Exists(grandparentDir))
                    {
                        Log($" Session dir not yet created: {parentDir} — watching {grandparentDir} for '{sessionDirName}'");
                        var sessionDirWatcher = new FileSystemWatcher(grandparentDir)
                        {
                            Filter = sessionDirName,
                            NotifyFilter = NotifyFilters.DirectoryName,
                            EnableRaisingEvents = true
                        };
                        state.Watchers.Add(sessionDirWatcher);
                        sessionDirWatcher.Created += (s, e) =>
                        {
                            Task.Run(async () =>
                            {
                                await Task.Delay(100);
                                if (_disposed) return;
                                LogInfo($"Session dir appeared: {e.FullPath} — chaining to subagents watcher");
                                WatchForNewTranscripts(teamName, subagentsDir, newMembers, state);
                            });
                        };
                    }
                    else
                    {
                        Log($" Session dir not yet created: {parentDir} (no valid grandparent to watch)");
                    }
                    return;
                }

                if (parentDir != null && Directory.Exists(parentDir))
                {
                    var dirWatcher = new FileSystemWatcher(parentDir)
                    {
                        Filter = "subagents",
                        NotifyFilter = NotifyFilters.DirectoryName,
                        EnableRaisingEvents = true
                    };
                    state.Watchers.Add(dirWatcher);
                    dirWatcher.Created += (s, e) =>
                    {
                        Task.Run(async () =>
                        {
                            await Task.Delay(100);
                            if (_disposed) return;
                            WatchForNewTranscripts(teamName, subagentsDir, newMembers, state);
                        });
                    };
                    return;
                }
                return;
            }

            // Don't add duplicate watchers for the same directory
            if (state.HasSubagentsDirWatcher)
            {
                // Watcher already running, but scan for files that may have been created
                // before the watcher started (e.g., directory and file created simultaneously).
                // This closes the race where WatchForNewTranscripts re-enters after dir creation
                // but the transcript file already exists, so the FSW never fires Created.
                var untrackedMembers = state.KnownMembers
                    .Where(m => !state.TrackedMembers.Contains(m.name))
                    .ToList();
                if (untrackedMembers.Count > 0)
                {
                    LogInfo($"Re-scanning for {untrackedMembers.Count} untracked member(s) in team '{teamName}'");
                    MatchExistingTranscripts(teamName, subagentsDir, untrackedMembers, state);
                }
                return;
            }

            var watcher = new FileSystemWatcher(subagentsDir)
            {
                Filter = "agent-*.jsonl",
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };
            state.Watchers.Add(watcher);
            state.HasSubagentsDirWatcher = true;

            watcher.Created += (s, e) =>
            {
                var fullPath = e.FullPath;

                // Immediately claim the file BEFORE async work, so orphan watcher
                // can't race us and create a duplicate panel.
                lock (_trackedSubagentFiles) { _trackedSubagentFiles.Add(fullPath); }

                // Async delay to let file be initialized, without blocking FSW thread
                Task.Run(async () =>
                {
                    await Task.Delay(500);
                    if (_disposed) return;

                if (state.MatchedTranscripts.Contains(fullPath))
                    return;

                // First: try to match to an untracked member from the latest config
                // (use state.KnownMembers which is updated on each config read, not the captured newMembers list)
                // Validate timing to avoid greedily claiming non-team subagent files (Explore, Plan)
                var nextMember = state.KnownMembers.FirstOrDefault(m => !state.TrackedMembers.Contains(m.name));
                if (nextMember.name != null)
                {
                    var memberJoinedUtc = DateTimeOffset.FromUnixTimeMilliseconds(nextMember.joinedAt).UtcDateTime;
                    var fileCreatedUtc = new FileInfo(fullPath).CreationTimeUtc;
                    var timingDelta = Math.Abs((fileCreatedUtc - memberJoinedUtc).TotalSeconds);

                    if (timingDelta < 30)
                    {
                        LogInfo($"Watcher matched new file to untracked member '{nextMember.name}': {Path.GetFileName(fullPath)} (delta={timingDelta:F1}s)");
                        state.MatchedTranscripts.Add(fullPath);
                        CreateTailerForMember(teamName, nextMember.name, fullPath, state);
                        return;
                    }
                    else
                    {
                        Log($"Timing mismatch for untracked member '{nextMember.name}': delta={timingDelta:F1}s, skipping greedy match for {Path.GetFileName(fullPath)}");
                    }
                }

                // All members already tracked (or timing didn't match) — check if this
                // is actually a team agent transcript before trying heuristic matching.
                // Non-team subagents (Explore, Plan) won't have teammate-message markers.
                if (!LooksLikeTeamTranscript(fullPath))
                {
                    // Not a team transcript — release our early claim so orphan watcher can adopt it
                    lock (_trackedSubagentFiles) { _trackedSubagentFiles.Remove(fullPath); }
                    Log($"Non-team subagent transcript (no team markers), released claim: {Path.GetFileName(fullPath)}");
                    return;
                }

                // This is a subsequent wake-up file for a team agent.
                // Identify which member it belongs to and add to their existing tailer.
                string ownerName = IdentifyFileOwner(fullPath, state);

                // Fallback: if content matching failed, try inbox-based matching.
                // The agent that just woke up is the one whose inbox was most recently modified.
                if (ownerName == null)
                {
                    ownerName = IdentifyByInboxTiming(fullPath, teamName, state);
                }

                // Fallback: if only one member recently sent a message via Agent Panel,
                // the wake-up file likely belongs to them.
                if (ownerName == null)
                {
                    ownerName = IdentifyByRecentSend(state);
                }

                if (ownerName != null && state.MemberTailers.TryGetValue(ownerName, out var existingTailer))
                {
                    state.MatchedTranscripts.Add(fullPath);
                    // _trackedSubagentFiles already added synchronously before Task.Run
                    existingTailer.AddTranscriptFile(fullPath);
                    LogInfo($"Added subsequent transcript to {ownerName}: {Path.GetFileName(fullPath)}");
                }
                else
                {
                    // Has team markers but couldn't identify owner — log for debugging.
                    // File already claimed in _trackedSubagentFiles (pre-Task.Run), so orphan watcher is blocked.
                    state.MatchedTranscripts.Add(fullPath);
                    Log($"Unmatched team transcript (blocked orphan adoption): {Path.GetFileName(fullPath)}");
                }
                }); // end Task.Run
            };

            Log($" Watching for new transcripts in: {subagentsDir}");

            // Scan existing files that may have been created before the watcher started.
            // This closes the race where the subagents/ dir and agent-*.jsonl file are created
            // nearly simultaneously, so the FSW never fires Created for the already-existing file.
            var untrackedAfterSetup = state.KnownMembers
                .Where(m => !state.TrackedMembers.Contains(m.name))
                .ToList();
            if (untrackedAfterSetup.Count > 0)
            {
                MatchExistingTranscripts(teamName, subagentsDir, untrackedAfterSetup, state);
            }
        }

        /// <summary>
        /// Identify which team member a new transcript JSONL file belongs to.
        /// Uses a multi-step heuristic:
        ///   1. Check if the first user message content explicitly names the target ("You are Agent X")
        ///   2. Check if the content mentions a team member name (excluding the sender)
        ///   3. Fallback: check the first assistant response for member names
        /// </summary>
        private string IdentifyFileOwner(string filePath, TeamState state)
        {
            try
            {
                string firstLine = null;
                string secondLine = null;
                string thirdLine = null;

                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream))
                {
                    firstLine = reader.ReadLine();
                    secondLine = reader.ReadLine();
                    thirdLine = reader.ReadLine();
                }

                if (string.IsNullOrEmpty(firstLine)) return null;

                // Extract the sender and content from the first user message
                string sender = null;
                string content = null;

                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(firstLine);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("message", out var msgObj)
                        && msgObj.ValueKind == System.Text.Json.JsonValueKind.Object
                        && msgObj.TryGetProperty("content", out var contentProp))
                    {
                        if (contentProp.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            content = contentProp.GetString();
                        }
                        else if (contentProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            // Extract text from content blocks
                            foreach (var block in contentProp.EnumerateArray())
                            {
                                if (block.TryGetProperty("type", out var bt) && bt.GetString() == "text"
                                    && block.TryGetProperty("text", out var txt))
                                {
                                    content = txt.GetString();
                                    break;
                                }
                            }
                        }
                    }
                }
                catch { }

                if (string.IsNullOrEmpty(content)) return null;

                // Extract sender from <teammate-message teammate_id="SENDER">
                var senderMatch = System.Text.RegularExpressions.Regex.Match(
                    content, @"<teammate-message\s+teammate_id=""([^""]+)""");
                if (senderMatch.Success)
                {
                    sender = senderMatch.Groups[1].Value;
                }

                // Strategy 1: Check for explicit naming "You are Agent X"
                var youAreMatch = System.Text.RegularExpressions.Regex.Match(
                    content, @"You are (\w[\w\s]*?)(?:,|\.|!|\n)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (youAreMatch.Success)
                {
                    string candidateName = youAreMatch.Groups[1].Value.Trim();
                    if (state.TrackedMembers.Contains(candidateName))
                    {
                        Log($" IdentifyFileOwner: matched by 'You are {candidateName}'");
                        return candidateName;
                    }
                }

                // Strategy 2: Check content for any member name (excluding sender)
                string bestMatch = FindMemberNameInText(content, sender, state);
                if (bestMatch != null)
                {
                    Log($" IdentifyFileOwner: matched by content mention → {bestMatch}");
                    return bestMatch;
                }

                // Strategy 3: Check second/third lines (first assistant response)
                // Exclude the sender — the assistant text is from the RECIPIENT's process,
                // and it often mentions the sender by name (e.g. "I received the haiku from Agent Beta")
                string assistantText = ExtractAssistantText(secondLine) ?? ExtractAssistantText(thirdLine);
                if (assistantText != null)
                {
                    bestMatch = FindMemberNameInText(assistantText, sender, state);
                    if (bestMatch != null)
                    {
                        Log($" IdentifyFileOwner: matched by assistant response → {bestMatch}");
                        return bestMatch;
                    }
                }

                // Strategy 4: If only one non-sender member exists, it must be them
                var candidates = new List<string>();
                foreach (var member in state.TrackedMembers)
                {
                    if (member != sender && member != "team-lead")
                        candidates.Add(member);
                }
                if (candidates.Count == 1)
                {
                    Log($" IdentifyFileOwner: matched by elimination → {candidates[0]}");
                    return candidates[0];
                }

                Log($" IdentifyFileOwner: no match found (sender={sender}, candidates={string.Join(",", candidates)})");
            }
            catch (Exception ex)
            {
                Log($" IdentifyFileOwner error: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Find a team member name mentioned in text, excluding the sender.
        /// First tries exact name match, then falls back to partial name matching
        /// (individual significant words from multi-word names like "Agent Alpha" → "Alpha").
        /// Returns the first match found, or null.
        /// </summary>
        private static string FindMemberNameInText(string text, string excludeSender, TeamState state)
        {
            if (string.IsNullOrEmpty(text)) return null;

            // Pass 1: Exact full-name match
            foreach (var member in state.TrackedMembers)
            {
                if (member == excludeSender || member == "team-lead") continue;

                if (text.IndexOf(member, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return member;
                }
            }

            // Pass 2: Partial name match — check individual significant words
            // e.g., "Agent Alpha" → check for "Alpha" (skip generic words like "Agent")
            var genericWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "agent", "the", "a", "an", "my", "team" };

            foreach (var member in state.TrackedMembers)
            {
                if (member == excludeSender || member == "team-lead") continue;

                var words = member.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length <= 1) continue; // Single-word names already checked in pass 1

                foreach (var word in words)
                {
                    if (word.Length < 3 || genericWords.Contains(word)) continue;

                    // Use word boundary check to avoid false matches (e.g., "Alpha" in "alphabet")
                    int idx = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        // Verify it's a standalone word (not part of a larger word)
                        bool startOk = idx == 0 || !char.IsLetterOrDigit(text[idx - 1]);
                        bool endOk = idx + word.Length >= text.Length || !char.IsLetterOrDigit(text[idx + word.Length]);
                        if (startOk && endOk)
                        {
                            return member;
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Extract assistant text content from a JSONL line.
        /// Returns the text content if the line is an assistant message, null otherwise.
        /// </summary>
        private static string ExtractAssistantText(string jsonLine)
        {
            if (string.IsNullOrEmpty(jsonLine)) return null;

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(jsonLine);
                var root = doc.RootElement;

                string type = root.TryGetProperty("type", out var tp) ? tp.GetString() : null;
                if (type != "assistant") return null;

                if (root.TryGetProperty("message", out var msgObj)
                    && msgObj.ValueKind == System.Text.Json.JsonValueKind.Object
                    && msgObj.TryGetProperty("content", out var contentProp))
                {
                    if (contentProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var block in contentProp.EnumerateArray())
                        {
                            if (block.TryGetProperty("type", out var bt) && bt.GetString() == "text"
                                && block.TryGetProperty("text", out var txt))
                            {
                                return txt.GetString();
                            }
                            // Also check SendMessage tool_use for identity clues
                            if (block.TryGetProperty("type", out var bt2) && bt2.GetString() == "tool_use"
                                && block.TryGetProperty("name", out var tn) && tn.GetString() == "SendMessage"
                                && block.TryGetProperty("input", out var inp)
                                && inp.ValueKind == System.Text.Json.JsonValueKind.Object)
                            {
                                // The summary field often contains the agent's name
                                string summary = inp.TryGetProperty("summary", out var sp) ? sp.GetString() : null;
                                string msgContent = inp.TryGetProperty("content", out var cp) ? cp.GetString() : null;
                                return summary ?? msgContent;
                            }
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Identify the owner of a new transcript file by checking which agent's inbox
        /// was most recently modified. When an agent wakes up to process a message,
        /// its inbox was just read/written shortly before the transcript file was created.
        /// </summary>
        private string IdentifyByInboxTiming(string transcriptPath, string teamName, TeamState state)
        {
            try
            {
                var fileCreationUtc = new FileInfo(transcriptPath).CreationTimeUtc;
                string bestMatch = null;
                double bestDelta = double.MaxValue;

                foreach (var memberName in state.TrackedMembers)
                {
                    if (memberName == "team-lead") continue;

                    string inboxFileName = memberName.Replace(" ", "-") + ".json";
                    string inboxPath = Path.Combine(_teamsDir, teamName, "inboxes", inboxFileName);

                    if (!File.Exists(inboxPath)) continue;

                    var inboxModUtc = new FileInfo(inboxPath).LastWriteTimeUtc;
                    // Inbox should be modified BEFORE the transcript file is created
                    var delta = (fileCreationUtc - inboxModUtc).TotalSeconds;

                    if (delta >= -2 && delta < 30 && delta < bestDelta)
                    {
                        bestDelta = delta;
                        bestMatch = memberName;
                    }
                }

                if (bestMatch != null)
                {
                    Log($" IdentifyByInboxTiming: matched '{bestMatch}' (inbox modified {bestDelta:F1}s before transcript created)");
                }

                return bestMatch;
            }
            catch (Exception ex)
            {
                Log($" IdentifyByInboxTiming error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Identify the owner by checking which TranscriptTailer recently sent a message
        /// (via Agent Panel UI). The agent that was sent a message is the one that woke up.
        /// </summary>
        private static string IdentifyByRecentSend(TeamState state)
        {
            string bestMatch = null;
            DateTime bestTime = DateTime.MinValue;
            var cutoff = DateTime.UtcNow.AddSeconds(-30);

            foreach (var kvp in state.MemberTailers)
            {
                if (kvp.Value.LastMessageSentAt.HasValue
                    && kvp.Value.LastMessageSentAt.Value > cutoff
                    && kvp.Value.LastMessageSentAt.Value > bestTime)
                {
                    bestTime = kvp.Value.LastMessageSentAt.Value;
                    bestMatch = kvp.Key;
                }
            }

            return bestMatch;
        }

        /// <summary>
        /// Create a TranscriptTailer for a team member and fire the TeammateDiscovered event.
        /// </summary>
        private void CreateTailerForMember(string teamName, string memberName, string transcriptPath, TeamState state)
        {
            try
            {
                // Reclaim from orphan watcher: if an orphan tailer already exists for this
                // transcript file, dispose it so the orphan panel is auto-removed from MainForm.
                // This happens when the orphan watcher's sync scan beats the team watcher.
                ReclaimFromOrphan(transcriptPath);

                // Claude Code uses dashes in inbox filenames (e.g. "Agent-Alpha.json" not "Agent Alpha.json")
                string inboxFileName = memberName.Replace(" ", "-") + ".json";
                string inboxPath = Path.Combine(_teamsDir, teamName, "inboxes", inboxFileName);

                var tailer = new TranscriptTailer(transcriptPath, inboxPath, memberName);
                tailer.IsTeamAgent = true; // Don't auto-stop on Result — team agents stay alive between turns
                state.TrackedMembers.Add(memberName);
                state.MemberTailers[memberName] = tailer;

                // Mark as claimed so project-level watcher skips it
                lock (_trackedSubagentFiles) { _trackedSubagentFiles.Add(transcriptPath); }

                // Start tailing (catches up on existing content)
                tailer.StartAsync();

                LogInfo($"Created tailer for '{memberName}' in team '{teamName}': {Path.GetFileName(transcriptPath)}");

                // Bridge team agent SendMessage calls to ChatPanel
                tailer.TeamMessageSent += (s, msg) =>
                {
                    TeamMessageSent?.Invoke(this, (teamName, memberName, msg, state.SpawnerName));
                };

                // Look up spawner and task metadata from the office hook tracking file
                var tracking = LookupFromTrackingByPath(transcriptPath);

                // Resolve spawner with cascading fallbacks:
                // 1. Cached spawner from TeamState (resolved once in ReadAndProcessConfig)
                // 2. Tracking file by path (mt-office-agents.json)
                // 3. Team spawner mapping (mt-team-spawners.json)
                string resolvedSpawner = state.SpawnerName;
                if (string.IsNullOrEmpty(resolvedSpawner))
                    resolvedSpawner = tracking.SpawnerName;
                if (string.IsNullOrEmpty(resolvedSpawner))
                {
                    resolvedSpawner = LookupTeamSpawner(teamName);
                    if (!string.IsNullOrEmpty(resolvedSpawner))
                    {
                        state.SpawnerName = resolvedSpawner;
                        LogInfo($"Resolved spawner for team '{teamName}' via team-spawner mapping: {resolvedSpawner}");
                    }
                }

                TeammateDiscovered?.Invoke(this, new TeammateDiscoveredEventArgs
                {
                    TeamName = teamName,
                    MemberName = memberName,
                    Tailer = tailer,
                    SpawnerName = resolvedSpawner,
                    TaskDescription = tracking.TaskDescription,
                    SubagentType = tracking.SubagentType,
                    LeadCwd = state.LeadCwd
                });
            }
            catch (Exception ex)
            {
                LogError($"Error creating tailer for '{memberName}': {ex.Message}");
            }
        }

        /// <summary>
        /// Watch the project's session directories for non-team subagent JSONL files.
        /// This catches agents spawned via Task tool that don't belong to any team.
        /// Scans all project dirs with recent activity, not just the app's CWD project.
        /// </summary>
        private void StartProjectSubagentWatching()
        {
            // Always watch the app's own project dir
            StartProjectSubagentWatchingForDir(_projectDir);

            string projectsRoot = Path.GetDirectoryName(_projectDir);

            // Scan all project dirs for recent session-level activity.
            // We check session directories INSIDE each project (not just the project dir's
            // own LastWriteTimeUtc, which only updates when new session dirs are added —
            // not when transcript files are written inside existing sessions).
            try
            {
                if (projectsRoot != null && Directory.Exists(projectsRoot))
                {
                    var cutoff = DateTime.UtcNow.AddHours(-2);
                    foreach (var projDir in Directory.GetDirectories(projectsRoot))
                    {
                        if (string.Equals(projDir, _projectDir, StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (HasRecentSessionActivity(projDir, cutoff))
                        {
                            StartProjectSubagentWatchingForDir(projDir);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($" Error scanning project dirs for subagent watching: {ex.Message}");
            }

            // Global catch-all: watch ALL of ~/.claude/projects/ recursively for agent-*.jsonl
            // files. This handles projects that become active after the app starts, or projects
            // the scan above missed. The _trackedSubagentFiles HashSet prevents duplicates with
            // per-project watchers.
            try
            {
                if (projectsRoot != null && Directory.Exists(projectsRoot))
                {
                    var globalWatcher = new FileSystemWatcher(projectsRoot)
                    {
                        Filter = "agent-*.jsonl",
                        NotifyFilter = NotifyFilters.FileName,
                        IncludeSubdirectories = true,
                        EnableRaisingEvents = true
                    };
                    _projectWatchers.Add(globalWatcher);

                    globalWatcher.Created += (s, e) =>
                    {
                        // Async delay to let team watcher claim files first, without blocking
                        // the FileSystemWatcher thread pool thread.
                        var fullPath = e.FullPath;
                        Task.Run(async () =>
                        {
                            await Task.Delay(2000);
                            if (_disposed) return;

                            if (IsFileClaimedByTeam(fullPath)) return;

                            lock (_trackedSubagentFiles)
                            {
                                if (_trackedSubagentFiles.Contains(fullPath)) return;
                                _trackedSubagentFiles.Add(fullPath);
                            }

                            LogInfo($"Global watcher caught orphan subagent: {Path.GetFileName(fullPath)}");
                            CreateOrphanSubagentPanel(fullPath);
                        });
                    };

                    Log($" Global subagent watcher started on: {projectsRoot}");
                }
            }
            catch (Exception ex)
            {
                Log($" Error setting up global subagent watcher: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if a project directory has any session directories with recent activity.
        /// Looks at session dir timestamps AND subagents/ dir timestamps, which are more
        /// reliable than the project dir's own LastWriteTimeUtc.
        /// </summary>
        private static bool HasRecentSessionActivity(string projectDir, DateTime cutoff)
        {
            try
            {
                foreach (var sessionDir in Directory.GetDirectories(projectDir))
                {
                    var di = new DirectoryInfo(sessionDir);
                    if (!Guid.TryParse(di.Name, out _)) continue;
                    if (di.LastWriteTimeUtc > cutoff) return true;

                    // Also check if subagents/ dir has recent activity
                    string subagentsDir = Path.Combine(sessionDir, "subagents");
                    if (Directory.Exists(subagentsDir))
                    {
                        var subDi = new DirectoryInfo(subagentsDir);
                        if (subDi.LastWriteTimeUtc > cutoff) return true;
                    }
                }
            }
            catch
            {
                // If we can't read the directory, skip it
            }
            return false;
        }

        /// <summary>
        /// Watch a specific project directory's session directories for non-team subagent JSONL files.
        /// Can be called multiple times for different projects (e.g., when team config reveals
        /// terminals running in a different project than the app's CWD).
        /// </summary>
        private void StartProjectSubagentWatchingForDir(string projectDir)
        {
            if (!Directory.Exists(projectDir))
            {
                Log($" Project dir not found for subagent watching: {projectDir}");
                return;
            }

            // Guard against duplicate watching of the same project dir
            lock (_watchedProjectDirs)
            {
                if (!_watchedProjectDirs.Add(projectDir))
                {
                    Log($" Already watching project dir for orphan subagents: {projectDir}");
                    return;
                }
            }

            // Watch for new session directories (UUID-named subdirectories)
            var sessionDirWatcher = new FileSystemWatcher(projectDir)
            {
                NotifyFilter = NotifyFilters.DirectoryName,
                EnableRaisingEvents = true
            };
            _projectWatchers.Add(sessionDirWatcher);
            sessionDirWatcher.Created += (s, e) =>
            {
                var fullPath = e.FullPath;
                Task.Run(async () =>
                {
                    await Task.Delay(300);
                    if (_disposed) return;
                    WatchSessionSubagentsDir(fullPath);
                });
            };

            // Scan recent session directories on startup (most recently modified first, limit to 3)
            var recentSessions = Directory.GetDirectories(projectDir)
                .Select(d => new DirectoryInfo(d))
                .Where(d => Guid.TryParse(d.Name, out _)) // only UUID-named dirs
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .Take(3);

            foreach (var session in recentSessions)
            {
                WatchSessionSubagentsDir(session.FullName);
            }

            LogInfo($"Project-level subagent watching started: {projectDir}");
        }

        /// <summary>
        /// Watch a specific session's subagents/ directory for orphan (non-team) agent JSONL files.
        /// </summary>
        private void WatchSessionSubagentsDir(string sessionDir)
        {
            if (!Directory.Exists(sessionDir)) return;

            string subagentsDir = Path.Combine(sessionDir, "subagents");

            if (!Directory.Exists(subagentsDir))
            {
                // Watch for subagents/ directory creation
                try
                {
                    var dirWatcher = new FileSystemWatcher(sessionDir)
                    {
                        Filter = "subagents",
                        NotifyFilter = NotifyFilters.DirectoryName,
                        EnableRaisingEvents = true
                    };
                    _projectWatchers.Add(dirWatcher);
                    dirWatcher.Created += (s, e) =>
                    {
                        var fullPath = e.FullPath;
                        Task.Run(async () =>
                        {
                            await Task.Delay(200);
                            if (_disposed) return;
                            WatchSubagentsForOrphans(fullPath);
                        });
                    };
                }
                catch (Exception ex)
                {
                    Log($" Error watching session dir for subagents: {ex.Message}");
                }
                return;
            }

            WatchSubagentsForOrphans(subagentsDir);
        }

        /// <summary>
        /// Watch a subagents directory and create panels for any agent-*.jsonl files
        /// not already claimed by a team.
        /// </summary>
        private void WatchSubagentsForOrphans(string subagentsDir)
        {
            if (!Directory.Exists(subagentsDir)) return;

            // Check if we already have a project-level watcher on this directory
            lock (_trackedSubagentFiles)
            {
                if (_trackedSubagentFiles.Contains(subagentsDir + ":watching"))
                    return;
                _trackedSubagentFiles.Add(subagentsDir + ":watching");
            }

            try
            {
                var watcher = new FileSystemWatcher(subagentsDir)
                {
                    Filter = "agent-*.jsonl",
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true
                };
                _projectWatchers.Add(watcher);

                watcher.Created += (s, e) =>
                {
                    // Async delay to let team watcher claim files first, without blocking
                    // the FileSystemWatcher thread pool thread.
                    var fullPath = e.FullPath;
                    Task.Run(async () =>
                    {
                        await Task.Delay(2000);
                        if (_disposed) return;

                        // Skip if already tracked by a team or already tracked as orphan
                        if (IsFileClaimedByTeam(fullPath)) return;

                        lock (_trackedSubagentFiles)
                        {
                            if (_trackedSubagentFiles.Contains(fullPath)) return;
                            _trackedSubagentFiles.Add(fullPath);
                        }

                        CreateOrphanSubagentPanel(fullPath);
                    });
                };

                Log($" Watching for orphan subagents in: {subagentsDir}");

                // Scan for recently-created files that may have been written before the watcher started.
                // This closes the race where subagents/ dir and agent-*.jsonl are created nearly simultaneously:
                // the dir-creation callback sleeps 200ms, during which the JSONL file is already written,
                // so the FileSystemWatcher never fires Created. We only scan files < 30s old to avoid
                // picking up dead agents from previous sessions.
                var cutoff = DateTime.UtcNow.AddSeconds(-30);
                foreach (var existingFile in Directory.GetFiles(subagentsDir, "agent-*.jsonl"))
                {
                    try
                    {
                        var fi = new FileInfo(existingFile);
                        if (fi.CreationTimeUtc < cutoff) continue;
                        if (IsFileClaimedByTeam(existingFile)) continue;

                        lock (_trackedSubagentFiles)
                        {
                            if (_trackedSubagentFiles.Contains(existingFile)) continue;
                            _trackedSubagentFiles.Add(existingFile);
                        }

                        Log($" Found recent orphan subagent file (created {(DateTime.UtcNow - fi.CreationTimeUtc).TotalSeconds:F1}s ago): {Path.GetFileName(existingFile)}");
                        CreateOrphanSubagentPanel(existingFile);
                    }
                    catch (Exception scanEx)
                    {
                        Log($" Error scanning existing subagent file: {scanEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($" Error watching subagents dir: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if a transcript file is already claimed by any active team.
        /// </summary>
        private bool IsFileClaimedByTeam(string filePath)
        {
            foreach (var state in _activeTeams.Values)
            {
                if (state.MatchedTranscripts.Contains(filePath))
                    return true;

                // Also check if any active team has unmatched members whose joinedAt
                // timing matches this file. This prevents the orphan watcher from racing
                // the team watcher when the session dir appears after the team config.
                var untrackedMembers = state.KnownMembers
                    .Where(m => !state.TrackedMembers.Contains(m.name))
                    .ToList();
                if (untrackedMembers.Count > 0)
                {
                    try
                    {
                        var fileCreatedUtc = new FileInfo(filePath).CreationTimeUtc;
                        foreach (var member in untrackedMembers)
                        {
                            var memberJoinedUtc = DateTimeOffset.FromUnixTimeMilliseconds(member.joinedAt).UtcDateTime;
                            if (Math.Abs((fileCreatedUtc - memberJoinedUtc).TotalSeconds) < 30)
                            {
                                Log($" IsFileClaimedByTeam: deferring '{Path.GetFileName(filePath)}' — likely belongs to unmatched team member '{member.name}'");
                                return true;
                            }
                        }
                    }
                    catch { /* file may not exist yet */ }
                }
            }
            return false;
        }

        /// <summary>
        /// Check if a transcript JSONL file looks like it belongs to a Claude Code internal
        /// system agent (e.g., the compact/summary agent). These are spawned internally by
        /// Claude Code for housekeeping, not by user code via the Task tool, and shouldn't
        /// appear in the Agent Panel.
        /// </summary>
        private static bool LooksLikeSystemAgent(string filePath)
        {
            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);

                // Check first 5 lines for known system agent signatures
                for (int i = 0; i < 5; i++)
                {
                    string line = reader.ReadLine();
                    if (string.IsNullOrEmpty(line)) break;

                    // Compact/summary agent: has a distinctive summarization prompt
                    if (line.Contains("create a detailed summary of the conversation so far"))
                        return true;

                    // Compact agent also instructs: no tools, only summary output
                    if (line.Contains("IMPORTANT: Do NOT use any tools") && line.Contains("summary"))
                        return true;
                }
            }
            catch
            {
                // If we can't read, don't filter — err on the side of showing
            }

            return false;
        }

        /// <summary>
        /// Check if a transcript JSONL file looks like it belongs to a team agent.
        /// Team agent wakeup transcripts contain teammate-message markers in their
        /// first few lines. Non-team subagents (Explore, Plan) have plain task prompts
        /// without these markers, and should not be claimed by the team watcher.
        /// </summary>
        private static bool LooksLikeTeamTranscript(string filePath)
        {
            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);

                // Check first 3 lines for team-specific markers
                for (int i = 0; i < 3; i++)
                {
                    string line = reader.ReadLine();
                    if (string.IsNullOrEmpty(line)) break;

                    // Team agents receive messages wrapped in <teammate-message> tags
                    if (line.Contains("<teammate-message") || line.Contains("teammate_id"))
                        return true;

                    // Team agents may reference the teams inbox system
                    if (line.Contains("/teams/") && line.Contains("/inboxes/"))
                        return true;
                }
            }
            catch
            {
                // If we can't read the file, err on the side of not claiming it
            }

            return false;
        }

        /// <summary>
        /// Create a read-only TranscriptTailer for a non-team subagent and fire SubagentDiscovered.
        /// </summary>
        private void CreateOrphanSubagentPanel(string transcriptPath)
        {
            try
            {
                // Filter out Claude Code system agents (compact/summary, etc.)
                if (LooksLikeSystemAgent(transcriptPath))
                {
                    LogInfo($"Skipping system agent (compact/summary): {Path.GetFileName(transcriptPath)}");
                    return;
                }

                // Derive a short name from the filename: agent-a1ea2e03... → "Subagent a1ea2e03"
                string fileName = Path.GetFileNameWithoutExtension(transcriptPath); // "agent-a1ea2e03..."
                string shortHash = fileName.Length > 12 ? fileName.Substring(6, 8) : fileName;

                // Look up spawner terminal and task metadata from the office hook tracking file.
                // The subagent-office-hook.js writes an entry for every agent spawned from a
                // MultiTerminal-tracked terminal. If there's NO entry, this subagent was spawned
                // by an external Claude instance (e.g., Clarion IDE addin) and should not appear
                // in MultiTerminal's Agent Panel.
                var tracking = LookupFromTracking(fileName);
                if (string.IsNullOrEmpty(tracking.SpawnerName) && string.IsNullOrEmpty(tracking.AgentName)
                    && string.IsNullOrEmpty(tracking.TaskDescription))
                {
                    Log($"Skipping external subagent (no tracking entry): {Path.GetFileName(transcriptPath)}");
                    return;
                }

                // Prefer the Task tool's name parameter if recorded; fall back to hex-based name
                string displayName;
                if (!string.IsNullOrEmpty(tracking.AgentName))
                    displayName = tracking.AgentName;
                else
                    displayName = $"Subagent {shortHash}";

                // Filter out agents from recently-deleted teams (prevents orphan panel
                // from being created after TeamDelete when agent wakes for shutdown)
                if (_retiredTeamAgentNames.ContainsKey(displayName))
                {
                    LogInfo($"Skipping retired team agent: {displayName}");
                    return;
                }

                // Filter out background agents (name starts with '_')
                if (displayName.StartsWith("_"))
                {
                    LogInfo($"Skipping background agent (underscore prefix): {displayName}");
                    return;
                }

                // No inbox path — read-only panel
                var tailer = new TranscriptTailer(transcriptPath, null, displayName);

                lock (_trackedSubagentFiles)
                {
                    _orphanTailers[NormalizeTranscriptPath(transcriptPath)] = tailer;
                }

                tailer.StartAsync();

                LogInfo($"Orphan subagent detected: {displayName} ({Path.GetFileName(transcriptPath)}), spawner: {tracking.SpawnerName ?? "(unknown)"}, desc: \"{tracking.TaskDescription ?? ""}\"");

                // Extract parent session ID from transcript path:
                // .../projects/<project-hash>/<session-id>/subagents/agent-xxx.jsonl
                string parentSessionId = null;
                try
                {
                    var subagentsDir = Path.GetDirectoryName(transcriptPath); // .../subagents
                    if (subagentsDir != null)
                    {
                        var sessionDir = Path.GetDirectoryName(subagentsDir); // .../<session-id>
                        if (sessionDir != null)
                            parentSessionId = Path.GetFileName(sessionDir);
                    }
                }
                catch { /* best effort */ }

                SubagentDiscovered?.Invoke(this, new TeammateDiscoveredEventArgs
                {
                    TeamName = null,
                    MemberName = displayName,
                    Tailer = tailer,
                    SpawnerName = tracking.SpawnerName,
                    TaskDescription = tracking.TaskDescription,
                    SubagentType = tracking.SubagentType,
                    ParentSessionId = parentSessionId
                });
            }
            catch (Exception ex)
            {
                LogError($"Error creating orphan subagent tailer: {ex.Message}");
            }
        }

        /// <summary>
        /// Look up agent metadata from the office hook tracking file.
        /// The SubagentStart hook writes { agentId: { spawnerName, taskDescription, subagentType, ... } }
        /// to mt-office-agents.json.
        /// </summary>
        private (string SpawnerName, string TaskDescription, string SubagentType, string AgentName) LookupFromTracking(string agentFileName)
        {
            try
            {
                string trackingPath = Path.Combine(Path.GetTempPath(), "mt-office-agents.json");
                if (!File.Exists(trackingPath)) return (null, null, null, null);

                // Extract agentId from filename: "agent-a7f57ee" → "a7f57ee"
                string agentId = agentFileName.StartsWith("agent-", StringComparison.OrdinalIgnoreCase)
                    ? agentFileName.Substring(6)
                    : agentFileName;

                string json = File.ReadAllText(trackingPath);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty(agentId, out var entry) &&
                    entry.ValueKind == JsonValueKind.Object)
                {
                    string spawnerName = null;
                    string taskDescription = null;
                    string subagentType = null;
                    string agentName = null;

                    if (entry.TryGetProperty("spawnerName", out var spawner))
                    {
                        string name = spawner.GetString();
                        if (!string.IsNullOrEmpty(name) && name != "Claude")
                            spawnerName = name;
                    }
                    if (entry.TryGetProperty("taskDescription", out var desc))
                        taskDescription = desc.GetString();
                    if (entry.TryGetProperty("subagentType", out var stype))
                        subagentType = stype.GetString();
                    if (entry.TryGetProperty("agentName", out var aname))
                        agentName = aname.GetString();

                    return (spawnerName, taskDescription, subagentType, agentName);
                }
            }
            catch (Exception ex)
            {
                LogError($"Error reading spawner tracking: {ex.Message}");
            }

            return (null, null, null, null);
        }

        /// <summary>
        /// Reverse-lookup agent metadata from the tracking file by matching transcriptPath.
        /// Used for team agents where we have the transcript path but not the agentId.
        /// </summary>
        private (string SpawnerName, string TaskDescription, string SubagentType, string AgentName) LookupFromTrackingByPath(string transcriptPath)
        {
            try
            {
                string trackingPath = Path.Combine(Path.GetTempPath(), "mt-office-agents.json");
                if (!File.Exists(trackingPath)) return (null, null, null, null);

                string json = File.ReadAllText(trackingPath);
                using var doc = JsonDocument.Parse(json);

                // Normalize path for comparison
                string normalizedTarget = transcriptPath.Replace('/', '\\');

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                    if (!prop.Value.TryGetProperty("transcriptPath", out var pathEl)) continue;

                    string entryPath = pathEl.GetString();
                    if (string.IsNullOrEmpty(entryPath)) continue;

                    // Match by normalized path or by substring (transcript paths may vary in prefix)
                    string normalizedEntry = entryPath.Replace('/', '\\');
                    if (!normalizedEntry.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase)
                        && !normalizedTarget.EndsWith(Path.GetFileName(normalizedEntry), StringComparison.OrdinalIgnoreCase))
                        continue;

                    string spawnerName = null;
                    string taskDescription = null;
                    string subagentType = null;
                    string agentName = null;

                    if (prop.Value.TryGetProperty("spawnerName", out var spawner))
                    {
                        string name = spawner.GetString();
                        if (!string.IsNullOrEmpty(name) && name != "Claude")
                            spawnerName = name;
                    }
                    if (prop.Value.TryGetProperty("taskDescription", out var desc))
                        taskDescription = desc.GetString();
                    if (prop.Value.TryGetProperty("subagentType", out var stype))
                        subagentType = stype.GetString();
                    if (prop.Value.TryGetProperty("agentName", out var aname))
                        agentName = aname.GetString();

                    return (spawnerName, taskDescription, subagentType, agentName);
                }
            }
            catch (Exception ex)
            {
                LogError($"Error reading tracking by path: {ex.Message}");
            }

            return (null, null, null, null);
        }

        /// <summary>
        /// Look up which terminal (spawner name) created a given team.
        /// Reads the mt-team-spawners.json mapping file written by task-to-agent-hook.js.
        /// </summary>
        private string LookupTeamSpawner(string teamName)
        {
            try
            {
                string mappingPath = Path.Combine(Path.GetTempPath(), "mt-team-spawners.json");
                if (!File.Exists(mappingPath)) return null;

                string json = File.ReadAllText(mappingPath);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty(teamName, out var entry) &&
                    entry.ValueKind == JsonValueKind.Object)
                {
                    if (entry.TryGetProperty("spawnerName", out var spawner))
                    {
                        string name = spawner.GetString();
                        if (!string.IsNullOrEmpty(name) && name != "Claude")
                            return name;
                    }
                }
            }
            catch (Exception ex)
            {
                LogError($"Error reading team-spawner mapping: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Reclaim a transcript file from the orphan watcher. If an orphan tailer already owns
        /// this file, dispose it so the orphan panel is auto-removed from MainForm (via the
        /// Stopped event → HandleEmbeddedStopped closure). The team watcher then creates its
        /// own tailer and panel with the correct team-keyed name.
        /// This fixes the race where the orphan watcher's sync scan claims a team agent's
        /// transcript before the team watcher can match it to a member.
        /// </summary>
        private void ReclaimFromOrphan(string transcriptPath)
        {
            string normalizedPath = NormalizeTranscriptPath(transcriptPath);
            TranscriptTailer orphanTailer = null;

            lock (_trackedSubagentFiles)
            {
                if (_orphanTailers.TryGetValue(normalizedPath, out orphanTailer))
                {
                    _orphanTailers.Remove(normalizedPath);
                }
            }

            if (orphanTailer != null)
            {
                LogInfo($"Reclaiming orphan tailer for team: {orphanTailer.AgentName} ({Path.GetFileName(transcriptPath)})");
                try { orphanTailer.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// Stop a specific orphan subagent tailer by transcript path, triggering its Stopped event
        /// which causes the auto-close logic in MainForm to close the agent panel.
        /// Called from the REST API when the SubagentStop hook fires.
        /// </summary>
        public bool StopOrphanTailer(string transcriptPath)
        {
            if (string.IsNullOrEmpty(transcriptPath))
                return false;

            // Both store and lookup use NormalizeTranscriptPath so keys always match
            string normalizedPath = NormalizeTranscriptPath(transcriptPath);
            TranscriptTailer tailer = null;
            lock (_trackedSubagentFiles)
            {
                if (_orphanTailers.TryGetValue(normalizedPath, out tailer))
                {
                    _orphanTailers.Remove(normalizedPath);
                }
            }

            if (tailer != null)
            {
                LogInfo($"Stopping orphan tailer via hook: {Path.GetFileName(transcriptPath)}");
                tailer.StopAsync(); // Fires Stopped event → MainForm auto-close
                return true;
            }

            LogInfo($"No orphan tailer found for: {Path.GetFileName(transcriptPath)}");
            return false;
        }

        /// <summary>
        /// Normalize a transcript path so that keys stored and looked up in _orphanTailers
        /// always use the same canonical form, regardless of separator style or relative vs absolute.
        /// </summary>
        private static string NormalizeTranscriptPath(string path)
        {
            try { return Path.GetFullPath(path); }
            catch { return path; }
        }

        /// <summary>
        /// Stop watching and clean up all resources.
        /// </summary>
        public void StopWatching()
        {
            foreach (var kvp in _activeTeams.ToList())
            {
                CleanupTeam(kvp.Key, kvp.Value);
            }
            _activeTeams.Clear();

            _teamsRootWatcher?.Dispose();
            _teamsRootWatcher = null;


            // Clean up project-level watchers and orphan tailers
            foreach (var watcher in _projectWatchers)
            {
                try { watcher.Dispose(); } catch { }
            }
            _projectWatchers.Clear();

            foreach (var tailer in _orphanTailers.Values)
            {
                try { tailer.Dispose(); } catch { }
            }
            _orphanTailers.Clear();
            _trackedSubagentFiles.Clear();
        }

        private void CleanupTeam(string teamName, TeamState state)
        {
            // Collect member names before disposal for panel cleanup and orphan prevention
            var memberNames = state.KnownMembers.Select(m => m.name).ToList();

            // Add transcript paths to tracked set so orphan watchers skip them
            lock (_trackedSubagentFiles)
            {
                foreach (var path in state.MatchedTranscripts)
                    _trackedSubagentFiles.Add(path);
            }

            // Add agent names to retired set so CreateOrphanSubagentPanel skips them
            var now = DateTime.UtcNow;
            foreach (var name in memberNames)
                _retiredTeamAgentNames[name] = now;

            foreach (var tailer in state.MemberTailers.Values)
            {
                try { tailer.Dispose(); } catch { }
            }
            state.MemberTailers.Clear();

            // Also stop any orphan tailers that share this team's transcript paths
            // (catches orphan panels created before the team watcher claimed the file)
            lock (_trackedSubagentFiles)
            {
                var orphansToRemove = new List<string>();
                foreach (var kvp in _orphanTailers)
                {
                    if (state.MatchedTranscripts.Any(p =>
                        string.Equals(NormalizeTranscriptPath(p), kvp.Key, StringComparison.OrdinalIgnoreCase)))
                    {
                        orphansToRemove.Add(kvp.Key);
                    }
                }
                // Also match orphans by agent name (catches name-based duplicates)
                foreach (var kvp in _orphanTailers)
                {
                    if (!orphansToRemove.Contains(kvp.Key) &&
                        memberNames.Any(name => string.Equals(kvp.Value.AgentName, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        orphansToRemove.Add(kvp.Key);
                    }
                }
                foreach (var key in orphansToRemove)
                {
                    if (_orphanTailers.Remove(key, out var orphanTailer))
                    {
                        LogInfo($"Stopping orphan tailer for team agent: {orphanTailer.AgentName}");
                        try { orphanTailer.Dispose(); } catch { }
                    }
                }
            }

            state.ConfigWatcher?.Dispose();
            state.ConfigWatcher = null;

            foreach (var watcher in state.Watchers)
            {
                try { watcher.Dispose(); } catch { }
            }
            state.Watchers.Clear();

            TeamRemoved?.Invoke(this, new TeamRemovedEventArgs
            {
                TeamName = teamName,
                MemberNames = memberNames,
                TranscriptPaths = state.MatchedTranscripts.ToList()
            });

            // Prune expired retired names (older than 60 seconds)
            var cutoff = now.AddSeconds(-60);
            foreach (var kvp in _retiredTeamAgentNames)
            {
                if (kvp.Value < cutoff)
                    _retiredTeamAgentNames.TryRemove(kvp.Key, out _);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopWatching();
        }

        /// <summary>
        /// Convert a project path to Claude's slug format.
        /// E.g., H:\DevLaptop\Foo\Bar → H--DevLaptop-Foo-Bar
        /// </summary>
        private static string GetClaudeProjectSlug(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath)) return "unknown";

            string normalized = projectPath.Replace('/', '\\').TrimEnd('\\');
            var sb = new System.Text.StringBuilder();

            for (int i = 0; i < normalized.Length; i++)
            {
                char c = normalized[i];
                if (c == ':')
                {
                    sb.Append("--");
                    if (i + 1 < normalized.Length && normalized[i + 1] == '\\')
                        i++; // Skip the backslash after colon
                }
                else if (c == '\\')
                {
                    sb.Append('-');
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Internal state for tracking a single active team.
        /// </summary>
        private class TeamState
        {
            public string TeamName;
            public string LeadSessionId;
            public string SpawnerName;
            public string LeadCwd;
            public HashSet<string> TrackedMembers = new HashSet<string>();
            public HashSet<string> MatchedTranscripts = new HashSet<string>();
            public List<(string name, long joinedAt)> KnownMembers = new List<(string name, long joinedAt)>();
            public Dictionary<string, TranscriptTailer> MemberTailers = new Dictionary<string, TranscriptTailer>();
            public FileSystemWatcher ConfigWatcher;
            public List<FileSystemWatcher> Watchers = new List<FileSystemWatcher>();
            public DateTime LastConfigChange;
            public bool HasSubagentsDirWatcher;
        }
    }
}
