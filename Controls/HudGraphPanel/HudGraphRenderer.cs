using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using MultiTerminal.Terminal;

namespace MultiTerminal.Controls
{
    /// <summary>
    /// WebView2 dependency-graph view of a task's checklist (task 60665c6c).
    /// <para>Shows the plan as a graph rather than a list, so parallel work and branches are
    /// visible, and explains each step on hover so a reader can learn what a plan is doing
    /// instead of approving it on trust.</para>
    /// <para>The graph is rebuilt from the live checklist on every refresh via
    /// <see cref="ChecklistGraphBuilder"/> — it is a derived view, never a stored artifact, so
    /// it cannot drift away from the card it describes.</para>
    /// </summary>
    public class HudGraphRenderer : UserControl
    {
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private bool _isDarkTheme = true;
        private double _pendingZoom = 1.0;

        private MessageBroker _broker;
        private string _terminalName;
        private string _pinnedTaskId;

        /// <summary>
        /// Initializes a new instance of the <see cref="HudGraphRenderer"/> class.
        /// </summary>
        public HudGraphRenderer()
        {
            SuspendLayout();
            BackColor = System.Drawing.Color.FromArgb(26, 26, 46);
            Name = "HudGraphRenderer";
            Visible = false;

            _webView = new WebView2 { Dock = DockStyle.Fill, Name = "graphWebView" };
            Controls.Add(_webView);
            ResumeLayout(false);

            VisibleChanged += (s, e) =>
            {
                if (Visible && !_isInitialized && !_isInitializing)
                {
                    InitializeWebView();
                }
            };
        }

        /// <summary>
        /// Raised when the user zooms the view, so the container can persist the factor.
        /// </summary>
        public event EventHandler<double> ZoomChanged;

        /// <summary>
        /// Wires the broker and subscribes to checklist changes so the graph follows the board.
        /// </summary>
        /// <param name="broker">The message broker.</param>
        public void Initialize(MessageBroker broker)
        {
            _broker = broker;
            if (_broker != null)
            {
                _broker.TasksUpdated -= OnTasksUpdated;
                _broker.TasksUpdated += OnTasksUpdated;
            }
        }

        /// <summary>
        /// Sets the terminal name whose active task the graph follows when no task is pinned.
        /// </summary>
        /// <param name="terminalName">The agent/terminal name.</param>
        public void SetTerminalName(string terminalName)
        {
            _terminalName = terminalName;
            RefreshGraph();
        }

        /// <summary>
        /// Pins the view to a specific task — used by the board's deep link, so opening the graph
        /// from a card shows THAT card rather than whatever happens to be active.
        /// </summary>
        /// <param name="taskId">Task to show, or null to resume following the active task.</param>
        public void SetTask(string taskId)
        {
            _pinnedTaskId = string.IsNullOrWhiteSpace(taskId) ? null : taskId;
            RefreshGraph();
        }

        /// <summary>
        /// Applies the light/dark theme.
        /// </summary>
        /// <param name="isDark">True for the dark palette.</param>
        public void ApplyTheme(bool isDark)
        {
            _isDarkTheme = isDark;
            Send(new { type = "theme", isDark });
        }

        /// <summary>
        /// Applies a zoom factor, matching the other HUD tabs.
        /// </summary>
        /// <param name="zoom">The zoom factor.</param>
        public void SetZoomFactor(double zoom)
        {
            _pendingZoom = zoom;
            if (_webView?.CoreWebView2 != null)
            {
                _webView.ZoomFactor = zoom;
            }
        }

        /// <summary>
        /// Rebuilds the graph from the live checklist and pushes it to the view.
        /// </summary>
        public void RefreshGraph()
        {
            // Nothing can be delivered before the view handshakes, and the ready handler always
            // refreshes — so returning early here is not just an optimization. TasksUpdated fires
            // on EVERY task write by ANY agent, and without this guard each broadcast cost two
            // broker/DB round-trips per terminal document for a tab that may never be opened.
            if (!_isInitialized)
            {
                return;
            }

            if (_broker == null)
            {
                // Also carries `pinned` — SetTask can pin before the broker is wired (the board
                // doorway calls it on a renderer that may never have been opened), so this path
                // can legitimately be reached while pinned.
                Send(new { type = "no_task", pinned = _pinnedTaskId != null });
                return;
            }

            try
            {
                KanbanTask task = null;

                if (!string.IsNullOrEmpty(_pinnedTaskId))
                {
                    task = _broker.GetTask(_pinnedTaskId);
                }
                else if (!string.IsNullOrEmpty(_terminalName))
                {
                    task = _broker.GetMyActiveTask(_terminalName);
                }

                if (task == null)
                {
                    // bool, not the id — `pinned` is read as a flag on the view side (both in
                    // renderEmpty and at `pinBadge.hidden = !data.pinned`), and the "graph"
                    // message below already sends a bool. One key, one type, on one channel.
                    Send(new { type = "no_task", pinned = _pinnedTaskId != null });
                    return;
                }

                var rels = _broker.GetRelationships(task.Id);
                var relList = rels != null && rels.Success ? rels.Relationships : new List<TaskRelationship>();

                var files = _broker.GetTaskFiles(task.Id);
                var fileList = files != null && files.Success ? files.Files : new List<TaskFileLink>();

                var graph = ChecklistGraphBuilder.Build(task, relList, fileList);

                Send(new
                {
                    type = "graph",
                    taskId = graph.TaskId,
                    taskTitle = graph.TaskTitle,
                    taskStatus = task.Status,
                    assignee = task.Assignee,
                    pinned = _pinnedTaskId != null,
                    nodes = graph.Nodes,
                    edges = graph.Edges,
                    warnings = graph.Warnings,
                });
            }
            catch (Exception)
            {
                // A malformed checklist must never take down the tab. The builder already
                // degrades on bad dependency data; this catches anything further upstream
                // (a broker call failing mid-refresh) and leaves the view in its empty state.
                //
                // MUST carry `pinned` like the other two sends. Omitting it made the empty state
                // claim "No active task / Activate a task to see its plan" on a tab that WAS
                // pinned — and, with the badge hidden, offered no way back. The failure told the
                // user the opposite of the truth about the tab's state.
                Send(new { type = "no_task", pinned = _pinnedTaskId != null });
            }
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_broker != null)
                {
                    _broker.TasksUpdated -= OnTasksUpdated;
                }

                if (_webView != null)
                {
                    if (_webView.CoreWebView2 != null)
                    {
                        _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                    }

                    _webView.Dispose();
                    _webView = null;
                }
            }

            base.Dispose(disposing);
        }

        private async void InitializeWebView()
        {
            if (_isInitializing || _isInitialized)
            {
                return;
            }

            _isInitializing = true;
            try
            {
                var env = await WebView2EnvironmentCache.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(env);
                _webView.DefaultBackgroundColor = _isDarkTheme
                    ? System.Drawing.Color.FromArgb(26, 26, 46)
                    : System.Drawing.Color.FromArgb(245, 245, 245);
                var s = _webView.CoreWebView2.Settings;
                s.IsScriptEnabled = true;
                s.AreDefaultContextMenusEnabled = false;
                s.AreDevToolsEnabled = false;
                s.IsStatusBarEnabled = false;
                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

                string htmlPath = FindHtml("Controls/HudGraphPanel/hud-graph.html", "HudGraphPanel/hud-graph.html");
                if (File.Exists(htmlPath))
                {
                    _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
                }
                else
                {
                    _isInitializing = false;
                }
            }
            catch (Exception)
            {
                _isInitializing = false;
            }
        }

        private string FindHtml(params string[] relativePaths)
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            foreach (var rel in relativePaths)
            {
                string p = Path.Combine(dir, rel);
                if (File.Exists(p))
                {
                    return p;
                }
            }

            return Path.Combine(dir, relativePaths[0]);
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var doc = JsonDocument.Parse(e.WebMessageAsJson);
                if (!doc.RootElement.TryGetProperty("type", out var t))
                {
                    return;
                }

                string msgType = t.GetString();

                if (msgType == "ready")
                {
                    _isInitialized = true;
                    _isInitializing = false;
                    Send(new { type = "theme", isDark = _isDarkTheme });

                    // ALWAYS rebuild — never replay a queued snapshot. The sibling renderers buffer
                    // a pending payload because their data is expensive to re-fetch; this graph is
                    // derived from live state, so a queued copy is strictly worse than a fresh read.
                    // Buffering here also caused a real bug: a TasksUpdated firing in the window
                    // between Initialize(broker) and the late CustomTitle would queue {"no_task"},
                    // and flushing it on ready pinned the tab to "No active task" for a terminal
                    // that had one.
                    RefreshGraph();

                    _webView.ZoomFactorChanged += (s, ev) => ZoomChanged?.Invoke(this, _webView.ZoomFactor);
                    if (Math.Abs(_pendingZoom - 1.0) > 0.01)
                    {
                        _webView.ZoomFactor = _pendingZoom;
                    }
                }
                else if (msgType == "refresh")
                {
                    RefreshGraph();
                }
                else if (msgType == "unpin")
                {
                    SetTask(null);
                }
            }
            catch (JsonException)
            {
            }
        }

        private void OnTasksUpdated(object sender, List<KanbanTask> tasks)
        {
            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action(() => OnTasksUpdated(sender, tasks)));
                }
                catch (InvalidOperationException)
                {
                    // Handle destroyed between the check and the post — nothing to refresh.
                }

                return;
            }

            RefreshGraph();
        }

        /// <summary>
        /// Serializes with <see cref="ChecklistGraphBuilder.ViewJsonOptions"/> (camelCase — the
        /// view depends on it; see that field's docs) and posts to the WebView. Drops the message
        /// when the view has not handshaked: callers never need to pre-check, because the ready
        /// handler rebuilds from live state rather than replaying anything queued.
        /// </summary>
        private void Send(object data)
        {
            if (!_isInitialized || _webView?.CoreWebView2 == null)
            {
                return;
            }

            try
            {
                _webView.CoreWebView2.PostWebMessageAsJson(
                    JsonSerializer.Serialize(data, ChecklistGraphBuilder.ViewJsonOptions));
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
