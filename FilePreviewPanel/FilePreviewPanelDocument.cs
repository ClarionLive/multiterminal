using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MultiTerminal.Terminal;
using WeifenLuo.WinFormsUI.Docking;

namespace MultiTerminal.FilePreviewPanel
{
    /// <summary>
    /// Dockable file preview panel. Displays file content with syntax highlighting
    /// and image preview. Designed to be opened from the Project Panel's file explorer.
    /// </summary>
    public class FilePreviewPanelDocument : DockContent
    {
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private string _pendingMessage;
        private bool _isDarkTheme = true;
        private string _currentFilePath;
        private Services.DebugLogService _debugLog;

        /// <summary>
        /// The file path currently being displayed.
        /// </summary>
        public string CurrentFilePath => _currentFilePath;

        /// <summary>
        /// Set the debug log service for logging to the app's debug panel.
        /// </summary>
        public Services.DebugLogService DebugLogService { set => _debugLog = value; }

        public FilePreviewPanelDocument()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            Text = "File Preview";
            TabText = "File Preview";
            DockAreas = DockAreas.DockLeft | DockAreas.DockRight | DockAreas.DockBottom |
                        DockAreas.DockTop | DockAreas.Float | DockAreas.Document;
            ShowHint = DockState.DockBottom;
            Icon = SystemIcons.Application;
            CloseButtonVisible = true;
            HideOnClose = true;

            BackColor = Color.FromArgb(30, 30, 30);

            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                Name = "filePreviewWebView"
            };

            Controls.Add(_webView);
            HandleCreated += OnHandleCreated;
        }

        private async void OnHandleCreated(object sender, EventArgs e)
        {
            if (_isInitializing || _isInitialized) return;
            _isInitializing = true;

            try
            {
                var environment = await WebView2EnvironmentCache.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(environment);

                var settings = _webView.CoreWebView2.Settings;
                settings.IsScriptEnabled = true;
                settings.AreDefaultContextMenusEnabled = false;
                settings.AreDevToolsEnabled = true;
                settings.IsStatusBarEnabled = false;

                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

                string htmlPath = GetHtmlPath();
                if (File.Exists(htmlPath))
                {
                    _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
                }
                else
                {
                    ShowError("File preview HTML not found: " + htmlPath);
                    _isInitializing = false;
                }
            }
            catch (Exception ex)
            {
                ShowError("Failed to initialize WebView2: " + ex.Message);
            }
        }

        private string GetHtmlPath()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            string path = Path.Combine(assemblyDir, "FilePreviewPanel", "file-preview.html");
            if (File.Exists(path)) return path;

            path = Path.Combine(assemblyDir, "file-preview.html");
            if (File.Exists(path)) return path;

            return Path.Combine(assemblyDir, "FilePreviewPanel", "file-preview.html");
        }

        private void ShowError(string message)
        {
            var errorLabel = new Label
            {
                Text = message,
                ForeColor = Color.Red,
                BackColor = Color.FromArgb(30, 30, 30),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter
            };
            Controls.Clear();
            Controls.Add(errorLabel);
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                ShowError("Failed to load file preview: " + e.WebErrorStatus);
                _isInitializing = false;
            }
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.WebMessageAsJson;
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("type", out var typeEl))
                {
                    var type = typeEl.GetString();
                    if (type == "ready")
                    {
                        _debugLog?.Info("FilePreview", $"WebView2 ready, hasPending={!string.IsNullOrEmpty(_pendingMessage)}");
                        _isInitialized = true;
                        _isInitializing = false;
                        SendMessage($"theme:{(_isDarkTheme ? "dark" : "light")}");
                        if (!string.IsNullOrEmpty(_pendingMessage))
                        {
                            _debugLog?.Info("FilePreview", "Sending pending message");
                            SendMessage(_pendingMessage);
                            _pendingMessage = null;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _debugLog?.Error("FilePreview", $"Error handling message: {ex.Message}");
            }
        }

        private void SendMessage(string message)
        {
            var msgPreview = message.Length > 80 ? message.Substring(0, 80) + "..." : message;
            if (_webView?.CoreWebView2 != null && _isInitialized)
            {
                _debugLog?.Info("FilePreview", $"SendMessage (direct): {msgPreview}");
                _webView.CoreWebView2.PostWebMessageAsString(message);
            }
            else
            {
                _debugLog?.Info("FilePreview", $"SendMessage (queued): {msgPreview}");
                _pendingMessage = message;
            }
        }

        /// <summary>
        /// Display a file in the preview panel. Reads the file from disk and sends
        /// the content to the WebView2 panel.
        /// </summary>
        /// <param name="filePath">Full path to the file.</param>
        public void PreviewFile(string filePath)
        {
            _debugLog?.Info("FilePreview", $"PreviewFile called: {filePath}, isInit={_isInitialized}, isIniting={_isInitializing}");
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                _debugLog?.Warning("FilePreview", $"PreviewFile: path empty or file not found: {filePath}");
                return;
            }

            _currentFilePath = filePath;

            // Show loading state
            SendMessage($"loading:{filePath}");

            try
            {
                var ext = Path.GetExtension(filePath).ToLowerInvariant();
                var imageExts = new HashSet<string> { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".svg", ".webp" };

                if (imageExts.Contains(ext))
                {
                    var imageInfo = new FileInfo(filePath);
                    if (imageInfo.Length > 5 * 1024 * 1024)
                    {
                        SendFileContent(filePath, "[Image too large to display (>5MB)]", false);
                        return;
                    }
                    var bytes = File.ReadAllBytes(filePath);
                    var mime = ext switch
                    {
                        ".png" => "image/png",
                        ".jpg" or ".jpeg" => "image/jpeg",
                        ".gif" => "image/gif",
                        ".bmp" => "image/bmp",
                        ".ico" => "image/x-icon",
                        ".svg" => "image/svg+xml",
                        ".webp" => "image/webp",
                        _ => "application/octet-stream"
                    };
                    var base64 = Convert.ToBase64String(bytes);
                    SendFileContent(filePath, $"data:{mime};base64,{base64}", true);
                }
                else
                {
                    var fileInfo = new FileInfo(filePath);
                    if (fileInfo.Length > 512 * 1024)
                    {
                        SendFileContent(filePath, "[File too large to display (>512KB)]", false);
                        return;
                    }
                    var content = File.ReadAllText(filePath);
                    SendFileContent(filePath, content, false);
                }
            }
            catch (Exception ex)
            {
                _debugLog?.Error("FilePreview", $"PreviewFile error: {ex.Message}");
                SendFileContent(filePath, "[Unable to read file]", false);
            }
        }

        private void SendFileContent(string filePath, string content, bool isBinary)
        {
            try
            {
                var payload = new { path = filePath ?? "", isBinary = isBinary, content = content ?? "" };
                string json = JsonSerializer.Serialize(payload);
                _debugLog?.Info("FilePreview", $"SendFileContent: path={filePath}, isBinary={isBinary}, jsonLen={json.Length}");
                SendMessage($"fileContent:{json}");
            }
            catch (Exception ex)
            {
                _debugLog?.Error("FilePreview", $"SendFileContent JSON error: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[FilePreviewPanel] SendFileContent error: {ex.Message}");
            }
        }

        /// <summary>
        /// Apply theme (dark or light).
        /// </summary>
        public void ApplyTheme(bool isDark)
        {
            _isDarkTheme = isDark;
            BackColor = isDark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(243, 243, 243);
            SendMessage($"theme:{(isDark ? "dark" : "light")}");
        }

        /// <summary>
        /// Set the font size for the file preview panel.
        /// </summary>
        public void SetFontSize(float size)
        {
            size = Math.Max(8f, Math.Min(14f, size));
            SendMessage($"fontSize:{size}");
        }

        protected override string GetPersistString()
        {
            return "FilePreviewPanel";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_webView != null)
                {
                    _webView.CoreWebView2?.Stop();
                    _webView.Dispose();
                    _webView = null;
                }
            }
            base.Dispose(disposing);
        }
    }
}
