using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MultiTerminal.MCPServer.Services
{
    /// <summary>
    /// Represents a discovered Claude Code session with identity information.
    /// </summary>
    public class DiscoveredSession
    {
        public string SessionId { get; set; }
        public string IdentityName { get; set; }
        public string ProjectPath { get; set; }
        public string FirstPrompt { get; set; }
        public string Summary { get; set; }
        public DateTime Created { get; set; }
        public DateTime Modified { get; set; }
        public int MessageCount { get; set; }
    }

    /// <summary>
    /// Discovers Claude Code sessions and matches them to MultiTerminal identities.
    /// Reads from ~/.claude/projects/*/sessions-index.json
    /// </summary>
    public class SessionDiscovery
    {
        private readonly string _claudeProjectsPath;

        // Pattern 1: Session received message FROM identity (e.g., "[Alice]: Hello")
        // This means the session IS that identity
        private static readonly Regex ReceivedMessagePattern = new Regex(
            @"^\[(\w+)\]:",
            RegexOptions.Compiled);

        // Pattern 2: Explicit registration instruction
        private static readonly Regex RegisterPattern = new Regex(
            @"register\s+(?:as|with\s+name)\s+[""']?(\w+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Pattern 3: System hook injection (if it appears in firstPrompt)
        private static readonly Regex SystemHookPattern = new Regex(
            @"MULTITERMINAL:\s*You\s+are\s+registered\s+as\s+(\w+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public SessionDiscovery()
        {
            // Default Claude projects path
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _claudeProjectsPath = Path.Combine(userProfile, ".claude", "projects");
        }

        public SessionDiscovery(string claudeProjectsPath)
        {
            _claudeProjectsPath = claudeProjectsPath;
        }

        /// <summary>
        /// Convert a project path to Claude's folder name format.
        /// Example: "H:\DevLaptop\Project" -> "H--DevLaptop-Project"
        /// </summary>
        public static string GetProjectFolderName(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath))
                return null;

            return projectPath
                .Replace(":\\", "--")
                .Replace("\\", "-")
                .Replace("/", "-");
        }

        /// <summary>
        /// Extract identity name from a firstPrompt using multiple pattern matching.
        /// </summary>
        public static string ExtractIdentity(string firstPrompt)
        {
            if (string.IsNullOrEmpty(firstPrompt))
                return null;

            // Priority 1: System hook pattern (most explicit)
            var hookMatch = SystemHookPattern.Match(firstPrompt);
            if (hookMatch.Success)
                return hookMatch.Groups[1].Value;

            // Priority 2: Received message pattern (very reliable for MT sessions)
            var messageMatch = ReceivedMessagePattern.Match(firstPrompt);
            if (messageMatch.Success)
                return messageMatch.Groups[1].Value;

            // Priority 3: Explicit registration instruction
            var registerMatch = RegisterPattern.Match(firstPrompt);
            if (registerMatch.Success)
                return registerMatch.Groups[1].Value;

            return null;
        }

        /// <summary>
        /// Discover sessions for a specific identity in a specific project.
        /// Returns sessions sorted by modified date (most recent first).
        /// </summary>
        public List<DiscoveredSession> DiscoverSessionsForIdentity(string identityName, string projectPath)
        {
            var results = new List<DiscoveredSession>();

            var folderName = GetProjectFolderName(projectPath);
            if (folderName == null)
                return results;

            var projectFolder = Path.Combine(_claudeProjectsPath, folderName);
            var indexPath = Path.Combine(projectFolder, "sessions-index.json");

            if (!File.Exists(indexPath))
                return results;

            try
            {
                var json = File.ReadAllText(indexPath);
                var index = JsonSerializer.Deserialize<SessionsIndex>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (index?.Entries == null)
                    return results;

                foreach (var entry in index.Entries)
                {
                    var extractedIdentity = ExtractIdentity(entry.FirstPrompt);
                    if (extractedIdentity != null &&
                        extractedIdentity.Equals(identityName, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(new DiscoveredSession
                        {
                            SessionId = entry.SessionId,
                            IdentityName = extractedIdentity,
                            ProjectPath = entry.ProjectPath ?? index.OriginalPath,
                            FirstPrompt = entry.FirstPrompt,
                            Summary = entry.Summary,
                            Created = ParseDateTime(entry.Created),
                            Modified = ParseDateTime(entry.Modified),
                            MessageCount = entry.MessageCount
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SessionDiscovery] Error reading {indexPath}: {ex.Message}");
            }

            return results.OrderByDescending(s => s.Modified).ToList();
        }

        /// <summary>
        /// Discover the most recent session for an identity in a project.
        /// </summary>
        public DiscoveredSession DiscoverLatestSession(string identityName, string projectPath)
        {
            var sessions = DiscoverSessionsForIdentity(identityName, projectPath);
            return sessions.FirstOrDefault();
        }

        /// <summary>
        /// Discover all sessions for an identity across all projects.
        /// </summary>
        public List<DiscoveredSession> DiscoverAllSessionsForIdentity(string identityName)
        {
            var results = new List<DiscoveredSession>();

            if (!Directory.Exists(_claudeProjectsPath))
                return results;

            foreach (var projectFolder in Directory.GetDirectories(_claudeProjectsPath))
            {
                var indexPath = Path.Combine(projectFolder, "sessions-index.json");
                if (!File.Exists(indexPath))
                    continue;

                try
                {
                    var json = File.ReadAllText(indexPath);
                    var index = JsonSerializer.Deserialize<SessionsIndex>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (index?.Entries == null)
                        continue;

                    foreach (var entry in index.Entries)
                    {
                        var extractedIdentity = ExtractIdentity(entry.FirstPrompt);
                        if (extractedIdentity != null &&
                            extractedIdentity.Equals(identityName, StringComparison.OrdinalIgnoreCase))
                        {
                            results.Add(new DiscoveredSession
                            {
                                SessionId = entry.SessionId,
                                IdentityName = extractedIdentity,
                                ProjectPath = entry.ProjectPath ?? index.OriginalPath,
                                FirstPrompt = entry.FirstPrompt,
                                Summary = entry.Summary,
                                Created = ParseDateTime(entry.Created),
                                Modified = ParseDateTime(entry.Modified),
                                MessageCount = entry.MessageCount
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SessionDiscovery] Error reading {indexPath}: {ex.Message}");
                }
            }

            return results.OrderByDescending(s => s.Modified).ToList();
        }

        /// <summary>
        /// Scan a project's sessions and return all identities found.
        /// Useful for discovering which identities have been used in a project.
        /// </summary>
        public Dictionary<string, DiscoveredSession> DiscoverIdentitiesInProject(string projectPath)
        {
            var identities = new Dictionary<string, DiscoveredSession>(StringComparer.OrdinalIgnoreCase);

            var folderName = GetProjectFolderName(projectPath);
            if (folderName == null)
                return identities;

            var projectFolder = Path.Combine(_claudeProjectsPath, folderName);
            var indexPath = Path.Combine(projectFolder, "sessions-index.json");

            if (!File.Exists(indexPath))
                return identities;

            try
            {
                var json = File.ReadAllText(indexPath);
                var index = JsonSerializer.Deserialize<SessionsIndex>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (index?.Entries == null)
                    return identities;

                foreach (var entry in index.Entries.OrderByDescending(e => ParseDateTime(e.Modified)))
                {
                    var extractedIdentity = ExtractIdentity(entry.FirstPrompt);
                    if (extractedIdentity != null && !identities.ContainsKey(extractedIdentity))
                    {
                        identities[extractedIdentity] = new DiscoveredSession
                        {
                            SessionId = entry.SessionId,
                            IdentityName = extractedIdentity,
                            ProjectPath = entry.ProjectPath ?? index.OriginalPath,
                            FirstPrompt = entry.FirstPrompt,
                            Summary = entry.Summary,
                            Created = ParseDateTime(entry.Created),
                            Modified = ParseDateTime(entry.Modified),
                            MessageCount = entry.MessageCount
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SessionDiscovery] Error reading {indexPath}: {ex.Message}");
            }

            return identities;
        }

        private static DateTime ParseDateTime(string dateStr)
        {
            if (DateTime.TryParse(dateStr, out var result))
                return result;
            return DateTime.MinValue;
        }

        #region JSON Models

        private class SessionsIndex
        {
            public int Version { get; set; }
            public List<SessionEntry> Entries { get; set; }
            public string OriginalPath { get; set; }
        }

        private class SessionEntry
        {
            public string SessionId { get; set; }
            public string FullPath { get; set; }
            public long FileMtime { get; set; }
            public string FirstPrompt { get; set; }
            public string Summary { get; set; }
            public int MessageCount { get; set; }
            public string Created { get; set; }
            public string Modified { get; set; }
            public string GitBranch { get; set; }
            public string ProjectPath { get; set; }
            public bool IsSidechain { get; set; }
        }

        #endregion
    }
}
