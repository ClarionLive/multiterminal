using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MultiTerminal.Services;
using MultiTerminal.Terminal;

namespace MultiTerminal.Dialogs
{
    /// <summary>
    /// Dialog for viewing Claude Code session history from JSONL files.
    /// </summary>
    public class SessionViewerDialog : Form
    {
        private readonly string _sessionId;
        private readonly string _sessionPath;
        private readonly TerminalTheme _theme;
        private readonly SessionSyncService _syncService;

        // Controls
        private Panel _headerPanel;
        private Label _sessionDateLabel;
        private Label _messageCountLabel;
        private Label _branchLabel;
        private WebView2 _webView;
        private Button _closeButton;

        // Session data
        private List<SessionMessage> _messages;
        private DateTime _sessionDate;
        private string _gitBranch;

        /// <summary>
        /// Creates a new SessionViewerDialog instance.
        /// </summary>
        /// <param name="sessionId">The session ID.</param>
        /// <param name="sessionPath">Path to the session JSONL file.</param>
        /// <param name="theme">The terminal theme to apply.</param>
        public SessionViewerDialog(string sessionId, string sessionPath, TerminalTheme theme)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _sessionPath = sessionPath ?? throw new ArgumentNullException(nameof(sessionPath));
            _theme = theme ?? TerminalTheme.Dark;
            _syncService = new SessionSyncService();
            _messages = new List<SessionMessage>();

            InitializeComponent();
            ApplyTheme();
            _ = InitializeWebViewAndLoadSessionAsync();
        }

        private void InitializeComponent()
        {
            Text = $"Session History - {_sessionId}";
            Size = new Size(900, 700);
            MinimumSize = new Size(600, 400);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 10f);

            // Header Panel
            _headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 60,
                Padding = new Padding(15, 10, 15, 10)
            };

            _sessionDateLabel = new Label
            {
                AutoSize = true,
                Location = new Point(15, 10),
                Font = new Font("Segoe UI", 11f, FontStyle.Bold)
            };

            _messageCountLabel = new Label
            {
                AutoSize = true,
                Location = new Point(15, 35)
            };

            _branchLabel = new Label
            {
                AutoSize = true,
                Location = new Point(250, 35)
            };

            _headerPanel.Controls.AddRange(new Control[] { _sessionDateLabel, _messageCountLabel, _branchLabel });

            // WebView2 for content rendering
            _webView = new WebView2
            {
                Dock = DockStyle.Fill
            };

            // Bottom panel with close button
            var bottomPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 50,
                Padding = new Padding(15, 10, 15, 10)
            };

            _closeButton = new Button
            {
                Text = "Close",
                Size = new Size(80, 30),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                DialogResult = DialogResult.Cancel
            };
            _closeButton.Location = new Point(bottomPanel.Width - _closeButton.Width - 15, 10);
            _closeButton.Click += (s, e) => Close();

            bottomPanel.Controls.Add(_closeButton);

            // Content panel to hold WebView
            var contentPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10, 5, 10, 5)
            };
            contentPanel.Controls.Add(_webView);

            // Add controls to form
            Controls.Add(contentPanel);
            Controls.Add(bottomPanel);
            Controls.Add(_headerPanel);

            CancelButton = _closeButton;

            // Handle resize for close button position
            bottomPanel.Resize += (s, e) =>
            {
                _closeButton.Location = new Point(bottomPanel.Width - _closeButton.Width - 15, 10);
            };
        }

        private void ApplyTheme()
        {
            bool isDark = _theme.IsDark;

            BackColor = isDark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(240, 240, 240);
            ForeColor = isDark ? Color.White : Color.FromArgb(30, 30, 30);

            _headerPanel.BackColor = isDark ? Color.FromArgb(45, 45, 48) : Color.FromArgb(230, 230, 230);

            _sessionDateLabel.ForeColor = isDark ? Color.White : Color.FromArgb(30, 30, 30);
            _messageCountLabel.ForeColor = isDark ? Color.FromArgb(180, 180, 180) : Color.FromArgb(80, 80, 80);
            _branchLabel.ForeColor = isDark ? Color.FromArgb(86, 156, 214) : Color.FromArgb(0, 100, 180);

            ApplyThemeToButton(_closeButton, isDark);
        }

        private void ApplyThemeToButton(Button button, bool isDark)
        {
            button.BackColor = isDark ? Color.FromArgb(62, 62, 66) : Color.FromArgb(225, 225, 225);
            button.ForeColor = isDark ? Color.White : Color.FromArgb(30, 30, 30);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = isDark ? Color.FromArgb(86, 86, 86) : Color.FromArgb(173, 173, 173);
            button.FlatAppearance.MouseOverBackColor = isDark ? Color.FromArgb(80, 80, 84) : Color.FromArgb(200, 220, 240);
            button.FlatAppearance.MouseDownBackColor = isDark ? Color.FromArgb(90, 90, 94) : Color.FromArgb(180, 200, 220);
        }

        private async Task InitializeWebViewAndLoadSessionAsync()
        {
            try
            {
                // Get the shared WebView2 environment
                var environment = await WebView2EnvironmentCache.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(environment);

                // Load and parse the session
                LoadSession();

                // Update header info
                UpdateHeaderInfo();

                // Render the conversation
                string html = RenderConversationHtml();
                _webView.NavigateToString(html);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to load session: {ex.Message}",
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void LoadSession()
        {
            _messages.Clear();

            if (!File.Exists(_sessionPath))
            {
                throw new FileNotFoundException($"Session file not found: {_sessionPath}");
            }

            // Get session date from file creation/modification time
            var fileInfo = new FileInfo(_sessionPath);
            _sessionDate = fileInfo.LastWriteTime;

            // Use SessionSyncService to parse the JSONL file
            var claudeMessages = _syncService.ParseSessionFile(_sessionPath);

            foreach (var claudeMsg in claudeMessages)
            {
                var message = new SessionMessage
                {
                    Type = claudeMsg.Type,
                    Role = claudeMsg.Role,
                    Content = claudeMsg.Content,
                    Cwd = claudeMsg.Cwd,
                    Timestamp = claudeMsg.Timestamp
                };

                // Convert tool uses
                if (claudeMsg.ToolUses != null && claudeMsg.ToolUses.Count > 0)
                {
                    message.ToolUses = claudeMsg.ToolUses.Select(t => new ToolUse
                    {
                        Name = t.Name,
                        Input = t.Input
                    }).ToList();
                }

                _messages.Add(message);

                // Try to extract git branch from first message if available
                if (string.IsNullOrEmpty(_gitBranch) && !string.IsNullOrEmpty(claudeMsg.GitBranch))
                {
                    _gitBranch = claudeMsg.GitBranch;
                }
            }
        }

        private void UpdateHeaderInfo()
        {
            _sessionDateLabel.Text = $"Session: {_sessionDate:MMMM d, yyyy 'at' h:mm tt}";

            int userMessages = _messages.Count(m => m.Role == "user");
            int assistantMessages = _messages.Count(m => m.Role == "assistant");
            _messageCountLabel.Text = $"Messages: {userMessages} user, {assistantMessages} assistant";

            if (!string.IsNullOrEmpty(_gitBranch))
            {
                _branchLabel.Text = $"Branch: {_gitBranch}";
            }
            else
            {
                _branchLabel.Text = "";
            }

            // Update window title with first user message if available
            var firstUserMessage = _messages.FirstOrDefault(m => m.Role == "user");
            if (firstUserMessage != null && !string.IsNullOrEmpty(firstUserMessage.Content))
            {
                string summary = firstUserMessage.Content;
                if (summary.Length > 60)
                    summary = summary.Substring(0, 57) + "...";
                Text = $"Session History - {summary}";
            }
        }

        private string RenderConversationHtml()
        {
            bool isDark = _theme.IsDark;

            // CSS colors based on theme
            string bgColor = isDark ? "#1e1e1e" : "#ffffff";
            string textColor = isDark ? "#d4d4d4" : "#333333";
            string userBgColor = isDark ? "#264f78" : "#e3f2fd";
            string userBorderColor = isDark ? "#3c7ab5" : "#90caf9";
            string assistantBgColor = isDark ? "#2d2d30" : "#f5f5f5";
            string assistantBorderColor = isDark ? "#3c3c3c" : "#e0e0e0";
            string toolBgColor = isDark ? "#1a1a1a" : "#fafafa";
            string toolBorderColor = isDark ? "#444444" : "#cccccc";
            string codeBlockBg = isDark ? "#0d0d0d" : "#f4f4f4";
            string codeBorderColor = isDark ? "#333333" : "#dddddd";
            string linkColor = isDark ? "#4fc3f7" : "#1976d2";
            string toolHeaderBg = isDark ? "#333333" : "#e8e8e8";

            var html = new StringBuilder();
            html.AppendLine("<!DOCTYPE html>");
            html.AppendLine("<html>");
            html.AppendLine("<head>");
            html.AppendLine("<meta charset=\"UTF-8\">");
            html.AppendLine("<style>");
            html.AppendLine($@"
                * {{
                    box-sizing: border-box;
                }}
                body {{
                    font-family: 'Segoe UI', -apple-system, BlinkMacSystemFont, sans-serif;
                    font-size: 14px;
                    line-height: 1.6;
                    margin: 0;
                    padding: 20px;
                    background-color: {bgColor};
                    color: {textColor};
                }}
                .message {{
                    margin-bottom: 16px;
                    padding: 12px 16px;
                    border-radius: 8px;
                    max-width: 90%;
                }}
                .user-message {{
                    background-color: {userBgColor};
                    border-left: 4px solid {userBorderColor};
                    margin-left: auto;
                    margin-right: 0;
                }}
                .assistant-message {{
                    background-color: {assistantBgColor};
                    border-left: 4px solid {assistantBorderColor};
                }}
                .message-role {{
                    font-weight: 600;
                    font-size: 12px;
                    text-transform: uppercase;
                    letter-spacing: 0.5px;
                    margin-bottom: 8px;
                    opacity: 0.7;
                }}
                .message-content {{
                    white-space: pre-wrap;
                    word-wrap: break-word;
                }}
                .tool-section {{
                    margin-top: 12px;
                    border: 1px solid {toolBorderColor};
                    border-radius: 6px;
                    overflow: hidden;
                }}
                .tool-header {{
                    background-color: {toolHeaderBg};
                    padding: 8px 12px;
                    cursor: pointer;
                    font-weight: 500;
                    font-size: 13px;
                    display: flex;
                    align-items: center;
                    gap: 8px;
                }}
                .tool-header:hover {{
                    opacity: 0.8;
                }}
                .tool-content {{
                    display: none;
                    padding: 12px;
                    background-color: {toolBgColor};
                    max-height: 300px;
                    overflow: auto;
                }}
                .tool-content.expanded {{
                    display: block;
                }}
                .tool-icon {{
                    font-size: 14px;
                }}
                .expand-icon {{
                    margin-left: auto;
                    transition: transform 0.2s;
                }}
                .expanded .expand-icon {{
                    transform: rotate(90deg);
                }}
                pre {{
                    background-color: {codeBlockBg};
                    border: 1px solid {codeBorderColor};
                    border-radius: 4px;
                    padding: 12px;
                    overflow-x: auto;
                    font-family: 'Cascadia Code', 'Consolas', 'Monaco', monospace;
                    font-size: 13px;
                    margin: 8px 0;
                }}
                code {{
                    font-family: 'Cascadia Code', 'Consolas', 'Monaco', monospace;
                    font-size: 13px;
                    background-color: {codeBlockBg};
                    padding: 2px 4px;
                    border-radius: 3px;
                }}
                pre code {{
                    background: none;
                    padding: 0;
                }}
                a {{
                    color: {linkColor};
                }}
                .summary-message {{
                    background-color: {(isDark ? "#2a2a5a" : "#e8eaf6")};
                    border-left: 4px solid {(isDark ? "#5c6bc0" : "#3f51b5")};
                    font-style: italic;
                }}
                /* Syntax highlighting - basic */
                .keyword {{ color: {(isDark ? "#569cd6" : "#0000ff")}; }}
                .string {{ color: {(isDark ? "#ce9178" : "#a31515")}; }}
                .comment {{ color: {(isDark ? "#6a9955" : "#008000")}; }}
                .number {{ color: {(isDark ? "#b5cea8" : "#098658")}; }}
            ");
            html.AppendLine("</style>");
            html.AppendLine("<script>");
            html.AppendLine(@"
                function toggleTool(element) {
                    var content = element.nextElementSibling;
                    var header = element;
                    if (content.classList.contains('expanded')) {
                        content.classList.remove('expanded');
                        header.classList.remove('expanded');
                    } else {
                        content.classList.add('expanded');
                        header.classList.add('expanded');
                    }
                }
            ");
            html.AppendLine("</script>");
            html.AppendLine("</head>");
            html.AppendLine("<body>");

            foreach (var message in _messages)
            {
                if (message.Type == "summary")
                {
                    html.AppendLine("<div class=\"message summary-message\">");
                    html.AppendLine("<div class=\"message-role\">Summary</div>");
                    html.AppendLine($"<div class=\"message-content\">{HtmlEncode(message.Content)}</div>");
                    html.AppendLine("</div>");
                    continue;
                }

                if (string.IsNullOrEmpty(message.Role))
                    continue;

                string roleClass = message.Role == "user" ? "user-message" : "assistant-message";
                string roleLabel = message.Role == "user" ? "User" : "Assistant";

                html.AppendLine($"<div class=\"message {roleClass}\">");
                html.AppendLine($"<div class=\"message-role\">{roleLabel}</div>");

                if (!string.IsNullOrEmpty(message.Content))
                {
                    string formattedContent = FormatContent(message.Content);
                    html.AppendLine($"<div class=\"message-content\">{formattedContent}</div>");
                }

                // Render tool uses
                if (message.ToolUses != null && message.ToolUses.Count > 0)
                {
                    foreach (var toolUse in message.ToolUses)
                    {
                        html.AppendLine("<div class=\"tool-section\">");
                        html.AppendLine($"<div class=\"tool-header\" onclick=\"toggleTool(this)\">");
                        html.AppendLine($"<span class=\"tool-icon\">&#128295;</span>");
                        html.AppendLine($"<span>{HtmlEncode(toolUse.Name)}</span>");
                        html.AppendLine("<span class=\"expand-icon\">&#9654;</span>");
                        html.AppendLine("</div>");
                        html.AppendLine("<div class=\"tool-content\">");
                        html.AppendLine($"<pre><code>{HtmlEncode(FormatJson(toolUse.Input))}</code></pre>");
                        html.AppendLine("</div>");
                        html.AppendLine("</div>");
                    }
                }

                html.AppendLine("</div>");
            }

            html.AppendLine("</body>");
            html.AppendLine("</html>");

            return html.ToString();
        }

        private string HtmlEncode(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";

            return System.Net.WebUtility.HtmlEncode(text);
        }

        private string FormatContent(string content)
        {
            if (string.IsNullOrEmpty(content))
                return "";

            // HTML encode first
            string encoded = HtmlEncode(content);

            // Convert markdown-style code blocks to HTML
            encoded = FormatCodeBlocks(encoded);

            // Convert inline code
            encoded = System.Text.RegularExpressions.Regex.Replace(
                encoded,
                @"`([^`]+)`",
                "<code>$1</code>");

            // Convert markdown bold
            encoded = System.Text.RegularExpressions.Regex.Replace(
                encoded,
                @"\*\*([^*]+)\*\*",
                "<strong>$1</strong>");

            // Convert markdown italic (single asterisks)
            encoded = System.Text.RegularExpressions.Regex.Replace(
                encoded,
                @"(?<!\*)\*([^*]+)\*(?!\*)",
                "<em>$1</em>");

            return encoded;
        }

        private string FormatCodeBlocks(string content)
        {
            // Match ```language\ncode\n``` patterns
            return System.Text.RegularExpressions.Regex.Replace(
                content,
                @"```(\w*)\n?([\s\S]*?)```",
                match =>
                {
                    string language = match.Groups[1].Value;
                    string code = match.Groups[2].Value.Trim();
                    return $"<pre><code>{code}</code></pre>";
                });
        }

        private string FormatJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                return "";

            // Simple JSON formatting without System.Text.Json
            // Just do basic indentation
            try
            {
                var sb = new StringBuilder();
                int indent = 0;
                bool inString = false;
                bool escaped = false;

                foreach (char c in json)
                {
                    if (escaped)
                    {
                        sb.Append(c);
                        escaped = false;
                        continue;
                    }

                    if (c == '\\')
                    {
                        sb.Append(c);
                        escaped = true;
                        continue;
                    }

                    if (c == '"')
                    {
                        inString = !inString;
                        sb.Append(c);
                        continue;
                    }

                    if (inString)
                    {
                        sb.Append(c);
                        continue;
                    }

                    switch (c)
                    {
                        case '{':
                        case '[':
                            sb.Append(c);
                            sb.AppendLine();
                            indent++;
                            sb.Append(new string(' ', indent * 2));
                            break;
                        case '}':
                        case ']':
                            sb.AppendLine();
                            indent--;
                            sb.Append(new string(' ', indent * 2));
                            sb.Append(c);
                            break;
                        case ',':
                            sb.Append(c);
                            sb.AppendLine();
                            sb.Append(new string(' ', indent * 2));
                            break;
                        case ':':
                            sb.Append(c);
                            sb.Append(' ');
                            break;
                        case ' ':
                        case '\t':
                        case '\n':
                        case '\r':
                            // Skip whitespace outside strings
                            break;
                        default:
                            sb.Append(c);
                            break;
                    }
                }

                return sb.ToString();
            }
            catch
            {
                // Return original if formatting fails
                return json;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _webView?.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Internal Classes

        private class SessionMessage
        {
            public string Type { get; set; }
            public string Role { get; set; }
            public string Content { get; set; }
            public List<ToolUse> ToolUses { get; set; }
            public string Cwd { get; set; }
            public string GitBranch { get; set; }
            public DateTime? Timestamp { get; set; }
        }

        private class ToolUse
        {
            public string Name { get; set; }
            public string Input { get; set; }
        }

        #endregion
    }
}
