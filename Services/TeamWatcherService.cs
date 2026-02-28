using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
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

        public event EventHandler<TeammateDiscoveredEventArgs> TeammateDiscovered;
        public event EventHandler<string> TeamRemoved;
        public event EventHandler<(string TeamName, string MemberName)> MemberRemoved;
        public event EventHandler<TeammateDiscoveredEventArgs> SubagentDiscovered;
        public event EventHandler<(string TeamName, string MemberName, AgentMessage Message)> TeamMessageSent;

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

            // Small delay to let config.json be written
            System.Threading.Thread.Sleep(200);
            ProcessTeamDirectory(teamName);
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

                        System.Threading.Thread.Sleep(100);
                        ReadAndProcessConfig(teamName, configPath);
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
                System.Threading.Thread.Sleep(100);
                ProcessTeamDirectory(teamName);
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

                    if (Directory.Exists(subagentsDir))
                    {
                        // Try to match existing files to new members
                        MatchExistingTranscripts(teamName, subagentsDir, newMembers, state);
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
                    Log($" Session dir not yet created: {parentDir}");
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
                        System.Threading.Thread.Sleep(100);
                        WatchForNewTranscripts(teamName, subagentsDir, newMembers, state);
                    };
                    return;
                }
                return;
            }

            // Don't add duplicate watchers for the same directory
            if (state.HasSubagentsDirWatcher)
            {
                Log($" Subagents dir watcher already running for team '{teamName}'");
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
                // Small delay to let the file be initialized with at least the first line
                System.Threading.Thread.Sleep(500);

                if (state.MatchedTranscripts.Contains(e.FullPath))
                    return;

                // First: try to match to an untracked member from the latest config
                // (use state.KnownMembers which is updated on each config read, not the captured newMembers list)
                // Validate timing to avoid greedily claiming non-team subagent files (Explore, Plan)
                var nextMember = state.KnownMembers.FirstOrDefault(m => !state.TrackedMembers.Contains(m.name));
                if (nextMember.name != null)
                {
                    var memberJoinedUtc = DateTimeOffset.FromUnixTimeMilliseconds(nextMember.joinedAt).UtcDateTime;
                    var fileCreatedUtc = new FileInfo(e.FullPath).CreationTimeUtc;
                    var timingDelta = Math.Abs((fileCreatedUtc - memberJoinedUtc).TotalSeconds);

                    if (timingDelta < 30)
                    {
                        LogInfo($"Watcher matched new file to untracked member '{nextMember.name}': {Path.GetFileName(e.FullPath)} (delta={timingDelta:F1}s)");
                        state.MatchedTranscripts.Add(e.FullPath);
                        CreateTailerForMember(teamName, nextMember.name, e.FullPath, state);
                        return;
                    }
                    else
                    {
                        Log($"Timing mismatch for untracked member '{nextMember.name}': delta={timingDelta:F1}s, skipping greedy match for {Path.GetFileName(e.FullPath)}");
                    }
                }

                // All members already tracked (or timing didn't match) — check if this
                // is actually a team agent transcript before trying heuristic matching.
                // Non-team subagents (Explore, Plan) won't have teammate-message markers.
                if (!LooksLikeTeamTranscript(e.FullPath))
                {
                    Log($"Non-team subagent transcript (no team markers): {Path.GetFileName(e.FullPath)}");
                    return;
                }

                // This is a subsequent wake-up file for a team agent.
                // Identify which member it belongs to and add to their existing tailer.
                string ownerName = IdentifyFileOwner(e.FullPath, state);

                // Fallback: if content matching failed, try inbox-based matching.
                // The agent that just woke up is the one whose inbox was most recently modified.
                if (ownerName == null)
                {
                    ownerName = IdentifyByInboxTiming(e.FullPath, teamName, state);
                }

                // Fallback: if only one member recently sent a message via Agent Panel,
                // the wake-up file likely belongs to them.
                if (ownerName == null)
                {
                    ownerName = IdentifyByRecentSend(state);
                }

                if (ownerName != null && state.MemberTailers.TryGetValue(ownerName, out var existingTailer))
                {
                    state.MatchedTranscripts.Add(e.FullPath);
                    existingTailer.AddTranscriptFile(e.FullPath);
                    LogInfo($"Added subsequent transcript to {ownerName}: {Path.GetFileName(e.FullPath)}");
                }
                else
                {
                    // Has team markers but couldn't identify owner — log for debugging
                    Log($"Unmatched team transcript: {Path.GetFileName(e.FullPath)}");
                }
            };

            Log($" Watching for new transcripts in: {subagentsDir}");
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
                    TeamMessageSent?.Invoke(this, (teamName, memberName, msg));
                };

                // Look up spawner and task metadata from the office hook tracking file
                var tracking = LookupFromTrackingByPath(transcriptPath);

                TeammateDiscovered?.Invoke(this, new TeammateDiscoveredEventArgs
                {
                    TeamName = teamName,
                    MemberName = memberName,
                    Tailer = tailer,
                    SpawnerName = tracking.SpawnerName,
                    TaskDescription = tracking.TaskDescription,
                    SubagentType = tracking.SubagentType
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
                        // Delay to let team watcher claim files first (same as per-project watchers)
                        System.Threading.Thread.Sleep(2000);

                        if (IsFileClaimedByTeam(e.FullPath)) return;

                        lock (_trackedSubagentFiles)
                        {
                            if (_trackedSubagentFiles.Contains(e.FullPath)) return;
                            _trackedSubagentFiles.Add(e.FullPath);
                        }

                        LogInfo($"Global watcher caught orphan subagent: {Path.GetFileName(e.FullPath)}");
                        CreateOrphanSubagentPanel(e.FullPath);
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
                // New session directory created — watch its subagents/ dir
                System.Threading.Thread.Sleep(300);
                WatchSessionSubagentsDir(e.FullPath);
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
                        System.Threading.Thread.Sleep(200);
                        WatchSubagentsForOrphans(e.FullPath);
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
                    // Longer delay than team watcher (500ms) to give teams time to claim files first.
                    // Without this, both watchers race and create duplicate panels.
                    System.Threading.Thread.Sleep(2000);

                    // Skip if already tracked by a team or already tracked as orphan
                    if (IsFileClaimedByTeam(e.FullPath)) return;

                    lock (_trackedSubagentFiles)
                    {
                        if (_trackedSubagentFiles.Contains(e.FullPath)) return;
                        _trackedSubagentFiles.Add(e.FullPath);
                    }

                    CreateOrphanSubagentPanel(e.FullPath);
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
                // Derive a short name from the filename: agent-a1ea2e03... → "Subagent a1ea2e03"
                string fileName = Path.GetFileNameWithoutExtension(transcriptPath); // "agent-a1ea2e03..."
                string shortHash = fileName.Length > 12 ? fileName.Substring(6, 8) : fileName;

                // Look up spawner terminal and task metadata from the office hook tracking file
                var tracking = LookupFromTracking(fileName);

                // Prefer the Task tool's name parameter if recorded; fall back to hex-based name
                string displayName;
                if (!string.IsNullOrEmpty(tracking.AgentName))
                    displayName = tracking.AgentName;
                else
                    displayName = $"Subagent {shortHash}";

                // No inbox path — read-only panel
                var tailer = new TranscriptTailer(transcriptPath, null, displayName);

                lock (_trackedSubagentFiles)
                {
                    _orphanTailers[NormalizeTranscriptPath(transcriptPath)] = tailer;
                }

                tailer.StartAsync();

                LogInfo($"Orphan subagent detected: {displayName} ({Path.GetFileName(transcriptPath)}), spawner: {tracking.SpawnerName ?? "(unknown)"}, desc: \"{tracking.TaskDescription ?? ""}\"");

                SubagentDiscovered?.Invoke(this, new TeammateDiscoveredEventArgs
                {
                    TeamName = null,
                    MemberName = displayName,
                    Tailer = tailer,
                    SpawnerName = tracking.SpawnerName,
                    TaskDescription = tracking.TaskDescription,
                    SubagentType = tracking.SubagentType
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
            foreach (var tailer in state.MemberTailers.Values)
            {
                try { tailer.Dispose(); } catch { }
            }
            state.MemberTailers.Clear();

            state.ConfigWatcher?.Dispose();
            state.ConfigWatcher = null;

            foreach (var watcher in state.Watchers)
            {
                try { watcher.Dispose(); } catch { }
            }
            state.Watchers.Clear();

            TeamRemoved?.Invoke(this, teamName);
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
