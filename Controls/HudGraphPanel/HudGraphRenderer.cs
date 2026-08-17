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
    public class HudGraphRenderer : UserControl, IZoomableTab
    {
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private bool _isDarkTheme = true;
        private double _pendingZoom = 1.0;

        private MessageBroker _broker;
        private string _terminalName;
        private GraphViewMode _mode = GraphViewMode.Terminal;
        private string _boardTaskId;

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
        /// Sets the terminal whose active task the graph follows. TERMINAL MODE ONLY.
        /// </summary>
        /// <param name="terminalName">The agent/terminal name.</param>
        public void SetTerminalName(string terminalName)
        {
            if (_mode != GraphViewMode.Terminal) return;
            _terminalName = terminalName;
            RefreshGraph();
        }

        /// <summary>
        /// Switches this renderer into BOARD mode: it stops following any terminal's active task and
        /// shows only what <see cref="SetTask"/> gives it.
        /// </summary>
        /// <remarks>
        /// <para>Task f5744489 replaced the old "pinning" design with this two-value mode, and the
        /// difference is not cosmetic. Pinning was a nullable id on a renderer that otherwise followed
        /// a terminal, which meant ANY instance could be pointed at ANY ticket — the board's Plan
        /// glyph did exactly that, reaching into whichever terminal it could resolve (assignee's, else
        /// merely the last active one) and pinning it to a card that terminal had nothing to do with.
        /// The defence was that the borrow was reversible via a "pinned" badge; a reversible borrow is
        /// still a borrow.</para>
        /// <para>With a mode, a terminal-mode renderer has NO code path that can bind it to a foreign
        /// task: <see cref="SetTask"/> is inert unless the host declared board mode. The invariant is
        /// structural rather than conventional, which is the whole reason for the change.</para>
        /// </remarks>
        public void SetBoardMode()
        {
            _mode = GraphViewMode.Board;
            _terminalName = null;
            RefreshGraph();
        }

        /// <summary>
        /// Shows a specific task. BOARD MODE ONLY — ignored in terminal mode.
        /// </summary>
        /// <param name="taskId">The selected card's task id, or null for "nothing selected".</param>
        /// <remarks>
        /// The early return is the enforcement point for the invariant described on
        /// <see cref="SetBoardMode"/>. It is deliberately silent rather than throwing: this is driven
        /// by UI selection, and a mis-wired host should degrade to "the terminal keeps showing its own
        /// task" rather than take down the tab.
        /// </remarks>
        public void SetTask(string taskId)
        {
            if (_mode != GraphViewMode.Board) return;
            _boardTaskId = string.IsNullOrWhiteSpace(taskId) ? null : taskId;
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
                // Carries `context` for the same reason the old code carried `pinned`: the host can
                // select a card before the broker is wired, so this path is legitimately reachable
                // in board mode and the view needs to know which empty-state copy to show.
                Send(NoTask());
                return;
            }

            try
            {
                KanbanTask task = null;

                if (_mode == GraphViewMode.Board)
                {
                    // Board mode NEVER falls back to a terminal's active task. Nothing selected means
                    // nothing selected — showing some terminal's work instead would be the borrowing
                    // behaviour this ticket removed, running in the opposite direction.
                    if (!string.IsNullOrEmpty(_boardTaskId))
                    {
                        task = _broker.GetTask(_boardTaskId);
                    }
                }
                else if (!string.IsNullOrEmpty(_terminalName))
                {
                    task = _broker.GetMyActiveTask(_terminalName);
                }

                if (task == null)
                {
                    Send(NoTask());
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
                    context = ContextName,
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
                // MUST go through NoTask() like the other two sends. The pre-f5744489 version of
                // this catch hand-built its payload and omitted the context field, so a board-bound
                // tab announced "No active task" — telling the user the opposite of the truth about
                // what the tab was showing. Routing all three sends through one helper is what stops
                // that recurring, rather than three sites remembering to agree.
                Send(NoTask());
            }
        }

        /// <summary>
        /// The single empty-state payload, so all three send sites cannot disagree about the fields
        /// the view needs. See the note in the catch block above for why that matters.
        /// </summary>
        /// <remarks>
        /// <c>hasSelection</c> is what separates "nothing is selected" from "the selected card is
        /// gone" — two different truths that must not share one message. Without it a deleted card
        /// would render as "No card selected", which is false: the user DID select something and it
        /// vanished, and saying otherwise hides the deletion instead of reporting it.
        /// </remarks>
        private object NoTask() => new
        {
            type = "no_task",
            context = ContextName,
            hasSelection = _mode == GraphViewMode.Board && _boardTaskId != null,
        };

        /// <summary>
        /// The wire name for the current mode. The view keys its empty-state copy off this: a
        /// terminal with nothing active reads "No active task", a board with nothing selected reads
        /// "No card selected" — two genuinely different situations that the old single `pinned`
        /// boolean could not tell apart.
        /// </summary>
        private string ContextName => _mode == GraphViewMode.Board ? "board" : "terminal";

        /// <summary>
        /// What this renderer is bound to. Two values, chosen at wiring time and never changed after.
        /// </summary>
        private enum GraphViewMode
        {
            /// <summary>Follows its own terminal's active task. Cannot be pointed at another ticket.</summary>
            Terminal,

            /// <summary>Shows whatever card the board selected, and never falls back to a terminal.</summary>
            Board,
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

                // There is deliberately no "unpin" case any more (task f5744489). It existed to undo
                // a borrow that can no longer happen: a terminal-mode graph cannot be pointed at
                // another ticket, and a board-mode graph has nothing to fall back TO. Keeping a
                // handler for a message the view no longer sends would just be a second way in.
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
