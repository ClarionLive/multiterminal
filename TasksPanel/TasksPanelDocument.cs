using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;
using MultiTerminal.ChatPanel;
using MultiTerminal.Controls;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;

namespace MultiTerminal.TasksPanel
{
    /// <summary>
    /// Dockable document for the Tasks Panel - Kanban board for task management.
    /// </summary>
    public class TasksPanelDocument : DockContent
    {
        private TasksPanelControl _control;
        private MessageBroker _broker;
        private bool _isDarkTheme = true;

        // ---- Board HUD (task f5744489) ----

        /// <summary>Zoom/persistence id for the board HUD's Tasks tab.</summary>
        public const string BoardTasksTabId = "__board_tasks__";

        /// <summary>Zoom/persistence id for the board HUD's Plan tab.</summary>
        public const string BoardGraphTabId = "__board_graph__";

        private SplitContainer _boardSplit;
        private HudTabContainer _boardHud;
        private TaskHudRenderer _boardTaskHud;
        private HudGraphRenderer _boardGraph;
        private double _hudSplitRatio = 0.60;
        private bool _suppressSplitterEvents;
        private bool _isDisposing;

        /// <summary>
        /// Raised when the user requests to inject a task into a terminal.
        /// </summary>
        public event EventHandler<InjectMessageEventArgs> InjectRequested;

        /// <summary>
        /// Raised when the WebView2 zoom factor changes (e.g. Ctrl+wheel).
        /// </summary>
        public event EventHandler<double> ZoomChanged;

        /// <summary>
        /// Raised when the user zooms one of the board HUD's tabs, carrying which tab moved.
        /// </summary>
        public event EventHandler<HudTabZoomChangedEventArgs> HudTabZoomChanged;

        /// <summary>
        /// Raised when the user drags the board/HUD splitter. Arg is the new ratio.
        /// </summary>
        public event EventHandler<double> HudSplitRatioChanged;

        /// <summary>
        /// Set the zoom factor for this panel. Forwards to the inner control.
        /// </summary>
        public void SetZoomFactor(double zoom) => _control?.SetZoomFactor(zoom);

        public TasksPanelDocument()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            Text = "Tasks";
            TabText = "Tasks";
            DockAreas = DockAreas.DockLeft | DockAreas.DockRight | DockAreas.DockBottom |
                        DockAreas.DockTop | DockAreas.Float | DockAreas.Document;
            ShowHint = DockState.DockRight;
            Icon = SystemIcons.Application;
            CloseButtonVisible = true;
            HideOnClose = true; // Prevent disposal when closed - allows reopening via toggle button

            _control = new TasksPanelControl
            {
                Dock = DockStyle.Fill
            };

            // Wire up inject event from control
            _control.InjectRequested += (s, e) => InjectRequested?.Invoke(this, e);
            _control.ZoomChanged += (s, zoom) => ZoomChanged?.Invoke(this, zoom);
            _control.TaskSelected += (s, taskId) => SetSelectedTask(taskId);

            BuildBoardHud();

            Controls.Add(_boardSplit);
        }

        /// <summary>
        /// Builds the board's own two-tab HUD (Tasks + Plan) below the kanban board.
        /// </summary>
        /// <remarks>
        /// <para>Task f5744489. This HUD answers questions about the SELECTED CARD, which is what
        /// lets a terminal's HUD go back to only ever describing that terminal.</para>
        /// <para>It reuses <see cref="HudTabContainer"/> rather than a purpose-built two-tab host.
        /// A lighter host would mean re-implementing the tab strip, its painting, theming and the
        /// zoom wiring — a SECOND hand-maintained copy of exactly the shape that caused task
        /// 0d72698a, where six of seven renderers were silently left out of one such list. The
        /// container's unused browser-tab machinery costs nothing here because nothing calls it, and
        /// the dirty strip stays hidden because <c>SetWorkingTreeDirty</c> is never called.</para>
        /// <para>Both renderers are put into board mode BEFORE any broker wiring. That ordering is
        /// load-bearing for the Tasks renderer: <c>SetBoardMode</c> clears the pending terminal name,
        /// and a name queued first would otherwise be adopted later by <c>Initialize</c> and quietly
        /// re-arm the activate path this panel must never have.</para>
        /// </remarks>
        private void BuildBoardHud()
        {
            _boardTaskHud = new TaskHudRenderer();
            _boardTaskHud.SetBoardMode();

            _boardGraph = new HudGraphRenderer();
            _boardGraph.SetBoardMode();

            _boardHud = new HudTabContainer(_boardTaskHud, BoardTasksTabId)
            {
                Dock = DockStyle.Fill
            };
            _boardHud.AddPermanentTab(BoardGraphTabId, "🔗 Plan", _boardGraph);
            _boardHud.ReorderPermanentTabs(BoardTasksTabId, BoardGraphTabId);
            _boardHud.TabZoomChanged += (s, e) => HudTabZoomChanged?.Invoke(this, e);

            _boardSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                Panel1MinSize = 120,
                Panel2MinSize = 80,
                SplitterWidth = 4,
            };
            _boardSplit.Panel1.Controls.Add(_control);
            _boardSplit.Panel2.Controls.Add(_boardHud);
            _boardSplit.SplitterMoved += OnHudSplitterMoved;
        }

        /// <summary>
        /// Points the board HUD at a card. Null clears the selection to the honest empty state.
        /// </summary>
        /// <remarks>
        /// Both tabs are driven from the one call so they can never disagree about which ticket the
        /// panel is describing.
        /// </remarks>
        public void SetSelectedTask(string taskId)
        {
            _boardTaskHud?.SetTask(taskId);
            _boardGraph?.SetTask(taskId);
        }

        private void OnHudSplitterMoved(object sender, SplitterEventArgs e)
        {
            if (_suppressSplitterEvents || _isDisposing) return;
            if (_boardSplit == null || _boardSplit.Height <= 0) return;

            double ratio = (double)_boardSplit.SplitterDistance / _boardSplit.Height;
            _hudSplitRatio = ratio;
            HudSplitRatioChanged?.Invoke(this, ratio);
        }

        /// <summary>
        /// Applies a saved board/HUD split ratio, clamped to the splitter's own minimum sizes.
        /// </summary>
        /// <remarks>
        /// Mirrors <c>TerminalDocument.ApplyHudSplitRatio</c>, including the suppression flag: the
        /// assignment below raises SplitterMoved, and without suppression the restore would be
        /// re-saved as though the user had dragged it — rounding the stored value a little further
        /// on every launch.
        /// </remarks>
        public void ApplyHudSplitRatio(double ratio)
        {
            _hudSplitRatio = ratio;
            if (_isDisposing || _boardSplit == null || _boardSplit.Height <= 0) return;

            bool prevSuppress = _suppressSplitterEvents;
            try
            {
                _suppressSplitterEvents = true;
                int distance = (int)(_boardSplit.Height * ratio);
                int maxDistance = _boardSplit.Height - _boardSplit.Panel2MinSize;
                if (distance > maxDistance) distance = maxDistance;
                if (distance > _boardSplit.Panel1MinSize && distance < maxDistance)
                {
                    _boardSplit.SplitterDistance = distance;
                }
            }
            catch (InvalidOperationException)
            {
                // SplitterDistance throws if the container is mid-layout or too small for the
                // minimums. Losing the restore is acceptable; taking down the panel is not.
            }
            finally
            {
                _suppressSplitterEvents = prevSuppress;
            }
        }

        /// <summary>
        /// Applies a saved zoom to one board HUD tab.
        /// </summary>
        public void ApplyHudTabZoom(string zoomKey, double zoom) =>
            _boardHud?.SetZoomFactorForKey(zoomKey, zoom);

        /// <summary>
        /// The board HUD's distinct zoom keys, for the host's restore loop.
        /// </summary>
        public IEnumerable<string> HudZoomKeys =>
            _boardHud?.ZoomKeys ?? Enumerable.Empty<string>();

        /// <summary>
        /// Initialize the tasks panel with the MessageBroker for task operations.
        /// </summary>
        public void Initialize(MessageBroker broker, ActivityService activityService = null, SettingsService settings = null)
        {
            _broker = broker;
            _control?.Initialize(broker, activityService, settings);

            // Board HUD. The terminal name is deliberately null: this panel belongs to no terminal,
            // which is the whole point, and both renderers were switched to board mode at
            // construction so neither will try to resolve one.
            _boardTaskHud?.Initialize(broker, null);
            _boardGraph?.Initialize(broker);

            if (settings != null)
            {
                ApplyHudSplitRatio(settings.GetTasksPanelHudSplitRatio());
                foreach (var key in HudZoomKeys.ToList())
                {
                    ApplyHudTabZoom(key, settings.GetHudTabZoom(key));
                }
            }
        }

        /// <summary>
        /// Set the debug log service for internal debug panel logging.
        /// </summary>
        public void SetDebugLogService(DebugLogService debugLogService)
        {
            _control?.SetDebugLogService(debugLogService);
        }

        /// <summary>
        /// Apply theme to the panel.
        /// </summary>
        public void ApplyTheme(bool isDark)
        {
            _isDarkTheme = isDark;
            BackColor = isDark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(240, 240, 240);
            _control?.ApplyTheme(isDark);
            _boardHud?.ApplyTheme(isDark);
        }

        /// <summary>
        /// Refresh the projects dropdown after ProjectService is available.
        /// </summary>
        public void RefreshProjects()
        {
            _control?.RefreshProjects();
        }

        /// <summary>
        /// Sets the font size for the tasks panel.
        /// </summary>
        public void SetFontSize(float size)
        {
            _control?.SetFontSize(size);
        }

        /// <summary>
        /// Open the Code Review overlay for a task. Triggered externally (e.g. from
        /// the Git tab's diff popup escalation button). The webview JS handles the
        /// actual overlay open via its existing <c>openCodeReview(taskId)</c> path.
        /// When <paramref name="filePath"/> is non-empty, the overlay pre-selects
        /// the matching linked-file tab instead of defaulting to the first file.
        /// </summary>
        public void OpenCodeReview(string taskId, string filePath = null)
        {
            _control?.OpenCodeReview(taskId, filePath);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Set before disposing anything: teardown resizes the splitter, and an unsuppressed
                // SplitterMoved during that would persist a meaningless ratio over the user's.
                _isDisposing = true;
                _suppressSplitterEvents = true;

                if (_boardSplit != null)
                {
                    _boardSplit.SplitterMoved -= OnHudSplitterMoved;
                }

                _control?.Dispose();

                // The two renderers are children of the container's content area, so disposing
                // _boardHud already reaches them — but they are disposed explicitly anyway. CA2213
                // cannot see transitive ownership through a control tree, and Control.Dispose is
                // idempotent, so being explicit costs nothing and keeps the ownership readable.
                _boardTaskHud?.Dispose();
                _boardGraph?.Dispose();
                _boardHud?.Dispose();
                _boardSplit?.Dispose();
            }

            base.Dispose(disposing);
        }

        protected override string GetPersistString()
        {
            return "TasksPanel";
        }
    }
}
