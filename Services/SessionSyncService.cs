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
            if (Directory.Exists(claudePath))
                return claudePath;

            // Case-insensitive search (Windows paths are case-insensitive)
            if (Directory.Exists(_claudeProjectsRoot))
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

        private ClaudeSessionMessage ParseJsonlLine(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith("{"))
                return null;

            var message = new ClaudeSessionMessage();
            int pos = 0;

            // Skip opening brace
            SkipWhitespace(json, ref pos);
            if (pos >= json.Length || json[pos] != '{')
                return null;
            pos++;

            while (pos < json.Length && json[pos] != '}')
            {
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length || json[pos] == '}')
                    break;

                string key = ParseJsonString(json, ref pos);
                SkipWhitespace(json, ref pos);

                if (pos < json.Length && json[pos] == ':')
                    pos++;

                SkipWhitespace(json, ref pos);

                if (key != null)
                {
                    switch (key.ToLowerInvariant())
                    {
                        case "type":
                            message.Type = ParseJsonString(json, ref pos);
                            break;
                        case "uuid":
                            message.Uuid = ParseJsonString(json, ref pos);
                            break;
                        case "parentuuid":
                            message.ParentUuid = ParseJsonString(json, ref pos);
                            break;
                        case "sessionid":
                            message.SessionId = ParseJsonString(json, ref pos);
                            break;
                        case "timestamp":
                            string timestampStr = ParseJsonString(json, ref pos);
                            if (DateTime.TryParse(timestampStr, out var timestamp))
                                message.Timestamp = timestamp;
                            break;
                        case "cwd":
                            message.Cwd = ParseJsonString(json, ref pos);
                            break;
                        case "gitbranch":
                            message.GitBranch = ParseJsonString(json, ref pos);
                            break;
                        case "issidechain":
                            message.IsSidechain = ParseJsonBool(json, ref pos);
                            break;
                        case "message":
                            ParseMessageObject(json, ref pos, message);
                            break;
                        default:
                            SkipJsonValue(json, ref pos);
                            break;
                    }
                }

                SkipWhitespace(json, ref pos);
                if (pos < json.Length && json[pos] == ',')
                    pos++;
            }

            return message;
        }

        private void ParseMessageObject(string json, ref int pos, ClaudeSessionMessage message)
        {
            SkipWhitespace(json, ref pos);
            if (pos >= json.Length || json[pos] != '{')
            {
                SkipJsonValue(json, ref pos);
                return;
            }

            pos++; // skip '{'

            while (pos < json.Length && json[pos] != '}')
            {
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length || json[pos] == '}')
                    break;

                string key = ParseJsonString(json, ref pos);
                SkipWhitespace(json, ref pos);

                if (pos < json.Length && json[pos] == ':')
                    pos++;

                SkipWhitespace(json, ref pos);

                if (key != null)
                {
                    switch (key.ToLowerInvariant())
                    {
                        case "role":
                            message.Role = ParseJsonString(json, ref pos);
                            break;
                        case "model":
                            message.Model = ParseJsonString(json, ref pos);
                            break;
                        case "content":
                            ParseMessageContent(json, ref pos, message);
                            break;
                        default:
                            SkipJsonValue(json, ref pos);
                            break;
                    }
                }

                SkipWhitespace(json, ref pos);
                if (pos < json.Length && json[pos] == ',')
                    pos++;
            }

            if (pos < json.Length && json[pos] == '}')
                pos++;
        }

        private void ParseMessageContent(string json, ref int pos, ClaudeSessionMessage message)
        {
            SkipWhitespace(json, ref pos);

            // Content can be a string or an array
            if (pos >= json.Length)
                return;

            if (json[pos] == '"')
            {
                // Simple string content
                message.Content = ParseJsonString(json, ref pos);
            }
            else if (json[pos] == '[')
            {
                // Array of content blocks
                ParseContentArray(json, ref pos, message);
            }
            else
            {
                SkipJsonValue(json, ref pos);
            }
        }

        private void ParseContentArray(string json, ref int pos, ClaudeSessionMessage message)
        {
            if (pos >= json.Length || json[pos] != '[')
                return;

            pos++; // skip '['
            var textContent = new StringBuilder();

            while (pos < json.Length && json[pos] != ']')
            {
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length || json[pos] == ']')
                    break;

                if (json[pos] == '{')
                {
                    // Parse content block
                    ParseContentBlock(json, ref pos, message, textContent);
                }
                else
                {
                    SkipJsonValue(json, ref pos);
                }

                SkipWhitespace(json, ref pos);
                if (pos < json.Length && json[pos] == ',')
                    pos++;
            }

            if (pos < json.Length && json[pos] == ']')
                pos++;

            message.Content = textContent.ToString().Trim();
        }

        private void ParseContentBlock(string json, ref int pos, ClaudeSessionMessage message, StringBuilder textContent)
        {
            if (pos >= json.Length || json[pos] != '{')
                return;

            pos++; // skip '{'

            string blockType = null;
            string text = null;
            string toolId = null;
            string toolName = null;
            string toolInput = null;
            bool isError = false;

            while (pos < json.Length && json[pos] != '}')
            {
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length || json[pos] == '}')
                    break;

                string key = ParseJsonString(json, ref pos);
                SkipWhitespace(json, ref pos);

                if (pos < json.Length && json[pos] == ':')
                    pos++;

                SkipWhitespace(json, ref pos);

                if (key != null)
                {
                    switch (key.ToLowerInvariant())
                    {
                        case "type":
                            blockType = ParseJsonString(json, ref pos);
                            break;
                        case "text":
                            text = ParseJsonString(json, ref pos);
                            break;
                        case "id":
                            toolId = ParseJsonString(json, ref pos);
                            break;
                        case "name":
                            toolName = ParseJsonString(json, ref pos);
                            break;
                        case "input":
                            toolInput = CaptureJsonValue(json, ref pos);
                            break;
                        case "is_error":
                            isError = ParseJsonBool(json, ref pos);
                            break;
                        case "content":
                            // Tool result content - check for file paths
                            string resultContent = ParseJsonString(json, ref pos);
                            if (resultContent != null)
                            {
                                ExtractFileReferences(resultContent, message.FileReferences);
                            }
                            break;
                        default:
                            SkipJsonValue(json, ref pos);
                            break;
                    }
                }

                SkipWhitespace(json, ref pos);
                if (pos < json.Length && json[pos] == ',')
                    pos++;
            }

            if (pos < json.Length && json[pos] == '}')
                pos++;

            // Process the content block based on type
            if (blockType == "text" && text != null)
            {
                if (textContent.Length > 0)
                    textContent.Append("\n");
                textContent.Append(text);
            }
            else if (blockType == "tool_use" && toolName != null)
            {
                message.ToolUses.Add(new ToolUseInfo
                {
                    Id = toolId,
                    Name = toolName,
                    Input = toolInput
                });

                // Extract file paths from tool input
                if (toolInput != null)
                {
                    ExtractFileReferences(toolInput, message.FileReferences);
                }
            }
            else if (blockType == "tool_result")
            {
                if (isError)
                {
                    message.IsError = true;
                    message.ErrorMessage = text;
                }
            }
        }

        private void ExtractFileReferences(string content, List<string> fileRefs)
        {
            if (string.IsNullOrEmpty(content))
                return;

            // Look for file paths in the content
            // Common patterns: absolute paths starting with drive letter or forward slash
            int i = 0;
            while (i < content.Length)
            {
                // Check for Windows path (e.g., H:\path\to\file)
                if (i + 2 < content.Length &&
                    char.IsLetter(content[i]) &&
                    content[i + 1] == ':' &&
                    (content[i + 2] == '\\' || content[i + 2] == '/'))
                {
                    int start = i;
                    i += 3;

                    // Continue until we hit a character that's not valid in a path
                    while (i < content.Length && IsValidPathChar(content[i]))
                    {
                        i++;
                    }

                    string path = content.Substring(start, i - start).TrimEnd('"', '\'', ',', ';', ')', ']', '}');
                    if (path.Length > 5 && !fileRefs.Contains(path))
                    {
                        fileRefs.Add(path);
                    }
                }
                else
                {
                    i++;
                }
            }
        }

        private bool IsValidPathChar(char c)
        {
            // Allow letters, digits, common path characters
            return char.IsLetterOrDigit(c) ||
                   c == '\\' || c == '/' || c == '.' || c == '-' || c == '_' ||
                   c == ' ' || c == '(' || c == ')';
        }

        private string CaptureJsonValue(string json, ref int pos)
        {
            SkipWhitespace(json, ref pos);
            if (pos >= json.Length)
                return null;

            int startPos = pos;
            SkipJsonValue(json, ref pos);
            return json.Substring(startPos, pos - startPos);
        }

        #endregion

        #region JSON Parsing Helpers

        private void SkipWhitespace(string json, ref int pos)
        {
            while (pos < json.Length && char.IsWhiteSpace(json[pos]))
                pos++;
        }

        private string ParseJsonString(string json, ref int pos)
        {
            SkipWhitespace(json, ref pos);

            if (pos >= json.Length)
                return null;

            if (pos + 3 < json.Length && json.Substring(pos, 4) == "null")
            {
                pos += 4;
                return null;
            }

            if (json[pos] != '"')
                return null;

            pos++;
            var sb = new StringBuilder();

            while (pos < json.Length && json[pos] != '"')
            {
                if (json[pos] == '\\' && pos + 1 < json.Length)
                {
                    pos++;
                    switch (json[pos])
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'u':
                            if (pos + 4 < json.Length)
                            {
                                string hex = json.Substring(pos + 1, 4);
                                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int code))
                                    sb.Append((char)code);
                                pos += 4;
                            }
                            break;
                        default:
                            sb.Append(json[pos]);
                            break;
                    }
                }
                else
                {
                    sb.Append(json[pos]);
                }
                pos++;
            }

            if (pos < json.Length && json[pos] == '"')
                pos++;

            return sb.ToString();
        }

        private bool ParseJsonBool(string json, ref int pos)
        {
            SkipWhitespace(json, ref pos);

            if (pos + 3 < json.Length && json.Substring(pos, 4) == "true")
            {
                pos += 4;
                return true;
            }

            if (pos + 4 < json.Length && json.Substring(pos, 5) == "false")
            {
                pos += 5;
                return false;
            }

            return false;
        }

        private int ParseJsonInt(string json, ref int pos)
        {
            SkipWhitespace(json, ref pos);
            int start = pos;

            if (pos < json.Length && (json[pos] == '-' || json[pos] == '+'))
                pos++;

            while (pos < json.Length && char.IsDigit(json[pos]))
                pos++;

            if (start < pos && int.TryParse(json.Substring(start, pos - start), out int result))
                return result;

            return 0;
        }

        private long ParseJsonLong(string json, ref int pos)
        {
            SkipWhitespace(json, ref pos);
            int start = pos;

            if (pos < json.Length && (json[pos] == '-' || json[pos] == '+'))
                pos++;

            while (pos < json.Length && char.IsDigit(json[pos]))
                pos++;

            if (start < pos && long.TryParse(json.Substring(start, pos - start), out long result))
                return result;

            return 0;
        }

        private void SkipJsonValue(string json, ref int pos)
        {
            SkipWhitespace(json, ref pos);
            if (pos >= json.Length)
                return;

            char c = json[pos];
            if (c == '"')
            {
                ParseJsonString(json, ref pos);
            }
            else if (c == '{')
            {
                int depth = 1;
                pos++;
                while (pos < json.Length && depth > 0)
                {
                    if (json[pos] == '{') depth++;
                    else if (json[pos] == '}') depth--;
                    else if (json[pos] == '"')
                        ParseJsonString(json, ref pos);
                    else
                        pos++;
                }
            }
            else if (c == '[')
            {
                int depth = 1;
                pos++;
                while (pos < json.Length && depth > 0)
                {
                    if (json[pos] == '[') depth++;
                    else if (json[pos] == ']') depth--;
                    else if (json[pos] == '"')
                        ParseJsonString(json, ref pos);
                    else
                        pos++;
                }
            }
            else
            {
                // Number, true, false, null
                while (pos < json.Length && json[pos] != ',' && json[pos] != '}' && json[pos] != ']')
                    pos++;
            }
        }

        #endregion
    }
}
