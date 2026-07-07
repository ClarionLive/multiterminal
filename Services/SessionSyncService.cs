using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Represents a Claude Code session with its metadata.
    /// </summary>
    public class ClaudeSession
    {
        public string SessionId { get; set; }
        public string FullPath { get; set; }
        public long FileMtime { get; set; }
        public string FirstPrompt { get; set; }
        public string Summary { get; set; }
        public int MessageCount { get; set; }
        public DateTime Created { get; set; }
        public DateTime Modified { get; set; }
        public string GitBranch { get; set; }
        public string ProjectPath { get; set; }
        public bool IsSidechain { get; set; }
    }

    /// <summary>
    /// Represents a single message/event from a Claude Code session JSONL file.
    /// </summary>
    public class ClaudeSessionMessage
    {
        public string Type { get; set; }  // user, assistant, progress, file-history-snapshot
        public string Uuid { get; set; }
        public string ParentUuid { get; set; }
        public string SessionId { get; set; }
        public DateTime Timestamp { get; set; }
        public string Cwd { get; set; }
        public string Role { get; set; }  // user, assistant
        public string Content { get; set; }
        public string Model { get; set; }
        public bool IsSidechain { get; set; }

        // Tool use information
        public List<ToolUseInfo> ToolUses { get; set; } = new List<ToolUseInfo>();

        // File references found in the message
        public List<string> FileReferences { get; set; } = new List<string>();

        // Error information if present
        public string ErrorMessage { get; set; }
        public bool IsError { get; set; }

        // Git branch if available
        public string GitBranch { get; set; }
    }

    /// <summary>
    /// Represents a tool use within a Claude message.
    /// </summary>
    public class ToolUseInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Input { get; set; }  // JSON string of input parameters
    }

    /// <summary>
    /// Service for syncing Claude Code sessions from the local filesystem.
    /// Reads sessions from %USERPROFILE%\.claude\projects\ folder structure.
    /// </summary>
    public class SessionSyncService
    {
        private readonly string _claudeProjectsRoot;

        /// <summary>
        /// Creates a new SessionSyncService with the default Claude projects folder.
        /// </summary>
        public SessionSyncService()
        {
            _claudeProjectsRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude",
                "projects");
        }

        /// <summary>
        /// Creates a new SessionSyncService with a custom Claude projects folder.
        /// </summary>
        /// <param name="claudeProjectsRoot">Path to the Claude projects folder.</param>
        public SessionSyncService(string claudeProjectsRoot)
        {
            _claudeProjectsRoot = claudeProjectsRoot;
        }

        #region Path Conversion

        /// <summary>
        /// Converts a project path like "H:\DevLaptop\MultiTerminal" to Claude's folder name format "H--DevLaptop-MultiTerminal".
        /// </summary>
        /// <param name="projectPath">The absolute path to the project.</param>
        /// <returns>The folder name Claude uses for this project.</returns>
        public string GetClaudeProjectFolderName(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath))
                return null;

            // Normalize path separators
            string normalized = projectPath.Replace('/', '\\').TrimEnd('\\');

            // Replace drive colon with double dash (e.g., "H:" -> "H--")
            // Replace backslash with single dash
            // Note: ":\\" is treated as a unit and becomes "--" (not "---")
            var sb = new StringBuilder();
            for (int i = 0; i < normalized.Length; i++)
            {
                char c = normalized[i];
                if (c == ':')
                {
                    sb.Append("--");
                    // Skip the following backslash if present (:\\ → --)
                    if (i + 1 < normalized.Length && normalized[i + 1] == '\\')
                    {
                        i++; // Skip the backslash
                    }
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
        /// Gets the full path to Claude's project folder for a given project path.
        /// </summary>
        /// <param name="projectPath">The absolute path to the project.</param>
        /// <returns>The full path to Claude's project folder, or null if not found.</returns>
        public string GetClaudeProjectPath(string projectPath)
        {
            string folderName = GetClaudeProjectFolderName(projectPath);
            if (string.IsNullOrEmpty(folderName))
                return null;

            // Try exact match first
            string claudePath = Path.Combine(_claudeProjectsRoot, folderName);
            // CA3003: claudePath is Path.Combine(_claudeProjectsRoot, folderName) where _claudeProjectsRoot
            // is Environment.GetFolderPath(UserProfile)+".claude/projects" and folderName is produced by
            // GetClaudeProjectFolderName, which replaces every ':' with "--" and every '\' with '-' —
            // making path-traversal segments impossible in the appended component. Existence check only.
#pragma warning disable CA3003
            if (Directory.Exists(claudePath))
                return claudePath;

            // Case-insensitive search (Windows paths are case-insensitive)
            if (Directory.Exists(_claudeProjectsRoot))
#pragma warning restore CA3003
            {
                try
                {
                    foreach (var dir in Directory.GetDirectories(_claudeProjectsRoot))
                    {
                        if (Path.GetFileName(dir).Equals(folderName, StringComparison.OrdinalIgnoreCase))
                            return dir;
                    }
                }
                catch
                {
                    // Ignore directory enumeration errors
                }
            }

            return null;
        }

        #endregion

        #region JSONL Parsing

        /// <summary>
        /// Parses a session JSONL file and extracts all messages.
        /// </summary>
        /// <param name="jsonlPath">Path to the JSONL file.</param>
        /// <returns>List of parsed messages.</returns>
        public List<ClaudeSessionMessage> ParseSessionFile(string jsonlPath)
        {
            var messages = new List<ClaudeSessionMessage>();

            if (!File.Exists(jsonlPath))
                return messages;

            try
            {
                // Use FileShare.ReadWrite to allow reading files being written by Claude
                using (var stream = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        try
                        {
                            using (var doc = JsonDocument.Parse(line))
                            {
                                var root = doc.RootElement;
                                var message = new ClaudeSessionMessage();

                                // Get type
                                if (root.TryGetProperty("type", out var typeEl))
                                    message.Type = typeEl.GetString();

                                // Get uuid
                                if (root.TryGetProperty("uuid", out var uuidEl))
                                    message.Uuid = uuidEl.GetString();

                                // Get timestamp
                                if (root.TryGetProperty("timestamp", out var tsEl))
                                {
                                    var tsStr = tsEl.GetString();
                                    if (!string.IsNullOrEmpty(tsStr) && DateTime.TryParse(tsStr, out var ts))
                                        message.Timestamp = ts;
                                }

                                // Get message object which contains role and content
                                if (root.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.Object)
                                {
                                    if (msgEl.TryGetProperty("role", out var roleEl))
                                        message.Role = roleEl.GetString();

                                    if (msgEl.TryGetProperty("content", out var contentEl))
                                    {
                                        if (contentEl.ValueKind == JsonValueKind.String)
                                        {
                                            message.Content = contentEl.GetString();
                                        }
                                        else if (contentEl.ValueKind == JsonValueKind.Array)
                                        {
                                            // Extract text from content array
                                            var textParts = new List<string>();
                                            foreach (var block in contentEl.EnumerateArray())
                                            {
                                                if (block.TryGetProperty("type", out var blockType) &&
                                                    blockType.GetString() == "text" &&
                                                    block.TryGetProperty("text", out var textEl))
                                                {
                                                    var text = textEl.GetString();
                                                    if (!string.IsNullOrEmpty(text))
                                                        textParts.Add(text);
                                                }
                                            }
                                            message.Content = string.Join("\n", textParts);
                                        }
                                    }
                                }

                                messages.Add(message);
                            }
                        }
                        catch
                        {
                            // Skip malformed lines
                        }
                    }
                }
            }
            catch
            {
                // Return what we have
            }

            return messages;
        }

        #endregion
    }
}
