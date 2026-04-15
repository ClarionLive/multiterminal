using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using MultiTerminal.Terminal;

namespace MultiTerminal.Controls
{
    /// <summary>
    /// WebView2-based knowledge browser showing entries relevant to the current project.
    /// Searchable cards with category badges.
    /// </summary>
    public class HudKnowledgeRenderer : UserControl
    {
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private bool _isDarkTheme = true;
        private double _pendingZoom = 1.0;

        private MessageBroker _broker;
        private string _projectId;
        private string _pendingJson;

        public event EventHandler<double> ZoomChanged;

        public HudKnowledgeRenderer()
        {
            SuspendLayout();
            BackColor = System.Drawing.Color.FromArgb(26, 26, 46);
            Name = "HudKnowledgeRenderer";
            Visible = false;

            _webView = new WebView2 { Dock = DockStyle.Fill, Name = "knowledgeWebView" };
            Controls.Add(_webView);
            ResumeLayout(false);

            VisibleChanged += (s, e) => { if (Visible && !_isInitialized && !_isInitializing) InitializeWebView(); };
        }

        private async void InitializeWebView()
        {
            if (_isInitializing || _isInitialized) return;
            _isInitializing = true;
            try
            {
                var env = await WebView2EnvironmentCache.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(env);
                _webView.DefaultBackgroundColor = _isDarkTheme
                    ? System.Drawing.Color.FromArgb(26, 26, 46) : System.Drawing.Color.FromArgb(245, 245, 245);
                var s = _webView.CoreWebView2.Settings;
                s.IsScriptEnabled = true; s.AreDefaultContextMenusEnabled = false;
                s.AreDevToolsEnabled = false; s.IsStatusBarEnabled = false;
                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                string htmlPath = FindHtml("Controls/HudKnowledgePanel/hud-knowledge.html", "HudKnowledgePanel/hud-knowledge.html");
                if (File.Exists(htmlPath)) _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
                else _isInitializing = false;
            }
            catch { _isInitializing = false; }
        }

        private string FindHtml(params string[] relativePaths)
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            foreach (var rel in relativePaths)
            {
                string p = Path.Combine(dir, rel);
                if (File.Exists(p)) return p;
            }
            return Path.Combine(dir, relativePaths[0]);
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var doc = JsonDocument.Parse(e.WebMessageAsJson);
                if (doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "ready")
                {
                    _isInitialized = true; _isInitializing = false;
                    PostJson(new { type = "theme", isDark = _isDarkTheme });
                    if (_pendingJson != null) { PostRaw(_pendingJson); _pendingJson = null; }
                    else RefreshKnowledge();
                    _webView.ZoomFactorChanged += (s, ev) => ZoomChanged?.Invoke(this, _webView.ZoomFactor);
                    if (Math.Abs(_pendingZoom - 1.0) > 0.01) _webView.ZoomFactor = _pendingZoom;
                }
            }
            catch { }
        }

        public void Initialize(MessageBroker broker) { _broker = broker; }

        public void SetProject(string projectId)
        {
            _projectId = projectId;
            if (_isInitialized) RefreshKnowledge();
        }

        public void ApplyTheme(bool isDark)
        {
            _isDarkTheme = isDark;
            if (_isInitialized) PostJson(new { type = "theme", isDark });
        }

        public void SetZoomFactor(double zoom)
        {
            _pendingZoom = zoom;
            if (_webView?.CoreWebView2 != null) _webView.ZoomFactor = zoom;
        }

        public void RefreshKnowledge()
        {
            var kb = _broker?.KnowledgeDb;
            if (kb == null) { Send(new { type = "no_project" }); return; }

            // Get entries for project + global entries
            var entries = kb.SearchKnowledge(null, projectId: _projectId, limit: 50);
            var items = entries.Select(e => new
            {
                id = e.Id, title = e.Title, content = e.Content,
                category = e.Category, confidence = e.Confidence,
                tags = e.Tags, sourceAgent = e.SourceAgent
            }).ToArray();

            Send(new { type = "knowledge_entries", entries = items });
        }

        private void Send(object data)
        {
            string json = JsonSerializer.Serialize(data);
            if (_isInitialized) PostRaw(json); else _pendingJson = json;
        }

        private void PostJson(object d)
        {
            if (!_isInitialized || _webView?.CoreWebView2 == null) return;
            try { _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(d)); } catch { }
        }

        private void PostRaw(string json)
        {
            if (!_isInitialized || _webView?.CoreWebView2 == null) return;
            try { _webView.CoreWebView2.PostWebMessageAsJson(json); } catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _webView != null)
            {
                if (_webView.CoreWebView2 != null) _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                _webView.Dispose(); _webView = null;
            }
            base.Dispose(disposing);
        }
    }
}
