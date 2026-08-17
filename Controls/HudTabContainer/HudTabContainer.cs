using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MultiTerminal.Controls;

namespace MultiTerminal.Controls
{
    /// <summary>
    /// Tab container that hosts multiple permanent tabs (Dashboard, Tasks, Notes, etc.)
    /// and supports additional dynamic browser tabs. Tab strip is always visible when
    /// there are 2+ tabs (permanent or dynamic).
    /// </summary>
    public class HudTabContainer : UserControl
    {
        // Child controls; added to Controls collection and auto-disposed by base Control.Dispose().
#pragma warning disable CA2213
        private readonly Panel _tabStrip;
        private readonly Panel _contentArea;
        private readonly Panel _dirtyStrip;
        private readonly Label _dirtyLabel;
#pragma warning restore CA2213
        private readonly TaskHudRenderer _taskHud;
        private readonly List<TabEntry> _tabs = new List<TabEntry>();
        private int _activeTabIndex;
        private bool _isDarkTheme = true;
        private int _dirtyCount;

        private const int TabStripHeight = 28;
        private const int DirtyStripHeight = 22;
        private const int TabPadding = 12;
        private const int CloseButtonWidth = 18;

        /// <summary>
        /// Raised when any tab needs the container to be shown or hidden.
        /// Mirrors TaskHudRenderer.HudVisibilityRequested for TerminalDocument integration.
        /// </summary>
        public event EventHandler<bool> VisibilityRequested;

        /// <summary>
        /// Raised when a browser tab's close button is clicked.
        /// </summary>
        public event EventHandler<string> TabClosed;

        /// <summary>
        /// Gets the inner TaskHudRenderer (Tab 0) so callers can still call
        /// Initialize, SetTerminalName, etc.
        /// </summary>
        public TaskHudRenderer TaskHud => _taskHud;

        /// <summary>
        /// Number of browser tabs (excludes permanent tabs).
        /// </summary>
        public int BrowserTabCount => _tabs.Count(t => !t.IsPermanent);

        /// <summary>
        /// Total number of permanent tabs (Task HUD + any added via AddPermanentTab).
        /// </summary>
        public int PermanentTabCount => _tabs.Count(t => t.IsPermanent);

        /// <summary>
        /// The default id of the built-in Tasks tab — the one every terminal HUD uses.
        /// </summary>
        public const string DefaultTaskTabId = "__tasks__";

        /// <summary>
        /// This container's id for the built-in Tasks tab. Also its zoom persistence key.
        /// </summary>
        private readonly string _taskTabId;

        /// <summary>
        /// Initializes the container.
        /// </summary>
        /// <param name="taskHudRenderer">The always-present Tasks tab renderer.</param>
        /// <param name="taskTabId">
        /// Id for the built-in Tasks tab. Defaults to <see cref="DefaultTaskTabId"/>; the Tasks pane's
        /// board HUD passes its own so it does not share zoom with every terminal (task f5744489).
        /// </param>
        /// <remarks>
        /// The id is a parameter rather than a constant because it doubles as the ZOOM PERSISTENCE
        /// key (see <c>ZoomKeyFor</c>), and per-tab zoom is stored in one flat, app-wide settings
        /// file. Two containers sharing an id therefore share a stored zoom — and since MainForm fans
        /// a changed key out to every TerminalDocument, a board zoom would silently resize the same
        /// tab in every open terminal. Distinct ids make the two independent with no change to that
        /// propagation path.
        /// </remarks>
        public HudTabContainer(TaskHudRenderer taskHudRenderer, string taskTabId = DefaultTaskTabId)
        {
            _taskHud = taskHudRenderer ?? throw new ArgumentNullException(nameof(taskHudRenderer));
            _taskTabId = string.IsNullOrWhiteSpace(taskTabId) ? DefaultTaskTabId : taskTabId;

            SuspendLayout();

            _tabStrip = new Panel
            {
                Dock = DockStyle.Top,
                Height = TabStripHeight,
                BackColor = Color.FromArgb(30, 30, 30),
                Visible = false // hidden when only 1 tab
            };
            _tabStrip.Paint += OnTabStripPaint;
            _tabStrip.MouseClick += OnTabStripMouseClick;

            _contentArea = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(30, 30, 30)
            };

            // Uncommitted-changes header strip — surfaces working-tree dirt that
            // doesn't touch .git/ (so the existing RepoStateChanged watcher
            // misses it). Hidden when count == 0, clickable when shown (deep
            // links to the Git tab). Polled by TerminalDocument via
            // SetWorkingTreeDirty().
            _dirtyStrip = new Panel
            {
                Dock = DockStyle.Top,
                Height = DirtyStripHeight,
                BackColor = Color.FromArgb(80, 50, 30),
                Visible = false,
                Cursor = Cursors.Hand
            };
            _dirtyLabel = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0),
                ForeColor = Color.FromArgb(255, 200, 140),
                Font = new Font("Segoe UI", 9F, FontStyle.Regular),
                Cursor = Cursors.Hand,
                Text = string.Empty
            };
            _dirtyStrip.Controls.Add(_dirtyLabel);
            _dirtyStrip.Click += OnDirtyStripClick;
            _dirtyLabel.Click += OnDirtyStripClick;

            // Docking order: Fill first, then Top-docked controls in the order
            // they should appear from bottom-to-top. Tab strip sits just above
            // content; dirty strip sits above the tab strip (added last =>
            // closest to the top edge).
            Controls.Add(_contentArea);
            Controls.Add(_tabStrip);
            Controls.Add(_dirtyStrip);

            ResumeLayout(false);

            // Install TaskHudRenderer as permanent "Tasks" tab
            _taskHud.Dock = DockStyle.Fill;
            var hudEntry = new TabEntry
            {
                Id = _taskTabId,
                Title = "\ud83d\udccb Tasks",
                Control = _taskHud,
                IsPermanent = true
            };
            _tabs.Add(hudEntry);
            HookTabZoom(hudEntry);
            _contentArea.Controls.Add(_taskHud);
            _activeTabIndex = 0;
            _taskHud.Visible = true; // Active tab starts visible

            // HUD is always visible now — forward visibility requests to keep
            // Panel2 expanded, but don't hide/show based on task presence.
            _taskHud.HudVisibilityRequested += (s, show) =>
            {
                // Always keep the container visible regardless of task state.
                // If another tab is active, just update TaskHud data silently.
                if (_activeTabIndex != GetTabIndex(_taskTabId))
                {
                    _taskHud.Visible = false;
                    return;
                }
                VisibilityRequested?.Invoke(this, true);
            };

            // Prevent TaskHudRenderer from setting Visible=true directly when
            // a different tab is active — that would overlay the HUD on
            // top of the other tab's content.
            _taskHud.VisibleChanged += (s, ev) =>
            {
                if (_taskHud.Visible && _activeTabIndex != GetTabIndex(_taskTabId))
                {
                    _taskHud.Visible = false;
                }
            };
        }

        /// <summary>
        /// Adds a permanent tab (not closable) with an existing control.
        /// Permanent tabs stay in the tab strip and cannot be removed by the user.
        /// Insert position is after existing permanent tabs but before dynamic tabs.
        /// </summary>
        /// <typeparam name="TControl">
        /// The tab's control type. It MUST implement <see cref="IZoomableTab"/> — that constraint is
        /// the whole guarantee, and it is checked by the compiler rather than hoped for.
        /// </typeparam>
        /// <remarks>
        /// Generic solely to carry the <c>IZoomableTab</c> constraint (task 0d72698a, cross-model
        /// adversary gate). Taking a plain <c>Control</c> let a new permanent tab compile in, render
        /// normally, and silently never persist or report zoom — because <see cref="HookTabZoom"/>
        /// returns early for a non-zoomable control. The interface alone did NOT make the omission a
        /// compile error, despite being documented as if it did; this signature is what makes that
        /// claim true.
        /// </remarks>
        public void AddPermanentTab<TControl>(string tabId, string title, TControl control)
            where TControl : Control, IZoomableTab
        {
            // Check if tab already exists
            var existing = _tabs.FirstOrDefault(t => t.Id == tabId);
            if (existing != null) return;

            control.Dock = DockStyle.Fill;
            control.Visible = false; // SwitchToTab manages visibility

            var entry = new TabEntry
            {
                Id = tabId,
                Title = title,
                Control = control,
                IsPermanent = true
            };

            // Insert after existing permanent tabs
            int insertIndex = _tabs.Count(t => t.IsPermanent);
            _tabs.Insert(insertIndex, entry);
            HookTabZoom(entry);
            _contentArea.Controls.Add(control);

            UpdateTabStripVisibility();
        }

        /// <summary>
        /// Reorders permanent tabs to match the specified ID order.
        /// Dynamic (non-permanent) tabs keep their relative order after permanents.
        /// Call after all permanent tabs have been added.
        /// </summary>
        public void ReorderPermanentTabs(params string[] tabIdOrder)
        {
            var permanentTabs = _tabs.Where(t => t.IsPermanent).ToList();
            var dynamicTabs = _tabs.Where(t => !t.IsPermanent).ToList();

            var ordered = new List<TabEntry>();
            foreach (var id in tabIdOrder)
            {
                var tab = permanentTabs.FirstOrDefault(t => t.Id == id);
                if (tab != null)
                {
                    ordered.Add(tab);
                    permanentTabs.Remove(tab);
                }
            }
            // Append any permanents not in the order list
            ordered.AddRange(permanentTabs);
            // Append dynamic tabs
            ordered.AddRange(dynamicTabs);

            _tabs.Clear();
            _tabs.AddRange(ordered);
            _activeTabIndex = 0;
            SwitchToTab(0);
            UpdateTabStripVisibility();
        }

        /// <summary>
        /// Gets the index of a tab by its ID, or -1 if not found.
        /// </summary>
        public int GetTabIndex(string tabId)
        {
            for (int i = 0; i < _tabs.Count; i++)
                if (_tabs[i].Id == tabId) return i;
            return -1;
        }

        /// <summary>
        /// Switches to the tab with the given ID.
        /// </summary>
        public void SwitchToTabById(string tabId)
        {
            int index = GetTabIndex(tabId);
            if (index >= 0) SwitchToTab(index);
        }

        /// <summary>
        /// Adds a new browser tab. Returns the BrowserTabPage instance.
        /// </summary>
        public BrowserTabPage AddBrowserTab(string tabId, string title, string url, string htmlContent)
        {
            // Check if tab already exists
            var existing = _tabs.FirstOrDefault(t => t.Id == tabId);
            if (existing != null)
            {
                SetBrowserContent(tabId, title, url, htmlContent);
                SwitchToTab(_tabs.IndexOf(existing));
                return existing.Control as BrowserTabPage;
            }

            var page = new BrowserTabPage(tabId, title)
            {
                Dock = DockStyle.Fill,
                Visible = false
            };
            page.ApplyTheme(_isDarkTheme);
            page.TitleChanged += (s, ev) =>
            {
                var tab = _tabs.FirstOrDefault(t => t.Id == tabId);
                if (tab != null)
                {
                    tab.Title = page.Title;
                    _tabStrip.Invalidate();
                }
            };

            var entry = new TabEntry
            {
                Id = tabId,
                Title = title,
                Control = page,
                IsPermanent = false
            };
            _tabs.Add(entry);
            HookTabZoom(entry);
            _contentArea.Controls.Add(page);

            // Navigate after adding to content area
            if (!string.IsNullOrEmpty(url))
                page.NavigateToUrl(url);
            else if (!string.IsNullOrEmpty(htmlContent))
                page.LoadHtmlContent(htmlContent);

            UpdateTabStripVisibility();
            SwitchToTab(_tabs.Count - 1);

            // Request visibility if container is not shown
            VisibilityRequested?.Invoke(this, true);

            return page;
        }

        /// <summary>
        /// Removes a browser tab by ID. Switches to Task HUD if it was active.
        /// </summary>
        public void RemoveBrowserTab(string tabId)
        {
            var entry = _tabs.FirstOrDefault(t => t.Id == tabId);
            if (entry == null || entry.IsPermanent) return;

            int index = _tabs.IndexOf(entry);
            _contentArea.Controls.Remove(entry.Control);
            entry.Control.Dispose();
            _tabs.Remove(entry);

            // Drop the per-tab echo entry; a closed browser tab never returns under the same id, so
            // keeping it would leak one entry per open/close for the life of the process. The tab's
            // persistence KEY is deliberately kept — the browser bucket must survive a moment with no
            // browser tab open so the next one can adopt it.
            _zoomTracker.Forget(entry.Id);

            if (_activeTabIndex >= index)
            {
                _activeTabIndex = Math.Max(0, _activeTabIndex - 1);
            }
            SwitchToTab(_activeTabIndex);
            UpdateTabStripVisibility();

            // HUD is always-on — never hide the container when dynamic tabs close

            TabClosed?.Invoke(this, tabId);
        }

        /// <summary>
        /// Updates content of an existing browser tab.
        /// </summary>
        public void SetBrowserContent(string tabId, string title, string url, string htmlContent)
        {
            var entry = _tabs.FirstOrDefault(t => t.Id == tabId);
            if (entry == null || entry.IsPermanent) return;

            if (!string.IsNullOrEmpty(title))
                entry.Title = title;

            var page = entry.Control as BrowserTabPage;
            if (page == null) return;

            if (!string.IsNullOrEmpty(title))
                page.Title = title;

            if (!string.IsNullOrEmpty(url))
                page.NavigateToUrl(url);
            else if (!string.IsNullOrEmpty(htmlContent))
                page.LoadHtmlContent(htmlContent);

            _tabStrip.Invalidate();
        }

        /// <summary>
        /// Gets a browser tab by ID, or null if not found.
        /// </summary>
        public BrowserTabPage GetBrowserTab(string tabId)
        {
            var entry = _tabs.FirstOrDefault(t => t.Id == tabId);
            return entry?.Control as BrowserTabPage;
        }

        /// <summary>
        /// Updates the persistent "uncommitted changes" strip at the top of the HUD.
        /// Hidden when <paramref name="count"/> is 0; otherwise shows a clickable
        /// warning ("N uncommitted file(s) on &lt;branch&gt;") that deep-links to the
        /// Git tab. When <paramref name="aggregateText"/> is supplied it replaces
        /// the default per-worktree body — used by the multi-worktree roll-up so
        /// the strip mirrors the Git tab's aggregate header
        /// ("5 uncommitted · 1 master · 4 worktrees (2)"). Safe to call from any thread.
        /// </summary>
        public void SetWorkingTreeDirty(int count, string branch, string aggregateText = null)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => SetWorkingTreeDirty(count, branch, aggregateText))); }
                catch { }
                return;
            }

            _dirtyCount = count;
            if (count <= 0)
            {
                if (_dirtyStrip.Visible) _dirtyStrip.Visible = false;
                return;
            }

            string body;
            if (!string.IsNullOrEmpty(aggregateText))
            {
                body = $"⚠ {aggregateText} — click to view in Git tab";
            }
            else
            {
                string branchPart = string.IsNullOrEmpty(branch) ? "" : " on " + branch;
                string fileWord = count == 1 ? "file" : "files";
                body = $"⚠ {count} uncommitted {fileWord}{branchPart} — click to view in Git tab";
            }
            _dirtyLabel.Text = body;
            if (!_dirtyStrip.Visible) _dirtyStrip.Visible = true;
        }

        private void OnDirtyStripClick(object sender, EventArgs e)
        {
            if (_dirtyCount <= 0) return;
            // Deep-link to the Git tab. The tab is registered by TerminalDocument
            // with the literal id "__git__" — silent no-op if not present.
            try { SwitchToTabById("__git__"); }
            catch { }
        }

        /// <summary>
        /// Applies theme to the container and all tabs.
        /// </summary>
        public void ApplyTheme(bool isDark)
        {
            _isDarkTheme = isDark;
            _tabStrip.BackColor = isDark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(235, 235, 235);
            _contentArea.BackColor = isDark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(245, 245, 245);
            // Dirty strip stays warm-orange in both themes so it remains a
            // loud, unmissable signal regardless of the surrounding palette.
            _dirtyStrip.BackColor = isDark ? Color.FromArgb(80, 50, 30) : Color.FromArgb(255, 230, 200);
            _dirtyLabel.ForeColor = isDark ? Color.FromArgb(255, 200, 140) : Color.FromArgb(120, 60, 0);
            _taskHud.ApplyTheme(isDark);

            foreach (var tab in _tabs)
            {
                if (tab.Control is BrowserTabPage page)
                    page.ApplyTheme(isDark);
                else if (tab.Control is HudDashboardRenderer dashboard)
                    dashboard.ApplyTheme(isDark);
                else if (tab.Control is HudNotesRenderer notes)
                    notes.ApplyTheme(isDark);
                else if (tab.Control is HudKnowledgeRenderer knowledge)
                    knowledge.ApplyTheme(isDark);
                else if (tab.Control is HudGitRenderer git)
                    git.ApplyTheme(isDark);
                else if (tab.Control is HudSessionsRenderer sessions)
                    sessions.ApplyTheme(isDark);
                else if (tab.Control is HudGraphRenderer graph)
                    graph.ApplyTheme(isDark);
            }
            _tabStrip.Invalidate();
        }

        // NOTE: there is deliberately no "SetZoomFactor(double)" that applies one factor to every tab.
        // That method existed before task 0d72698a and was the restore half of a global-zoom design; a
        // single call able to move every tab at once is how Option A behaviour would quietly return.
        // Callers name the tab they mean via SetZoomFactorForKey. The per-tab apply loop that replaced
        // the old "is XRenderer" chain now lives there.

        /// <summary>
        /// The shared persistence key used by every dynamic browser tab.
        /// </summary>
        public const string BrowserZoomKey = HudTabZoomTracker.BrowserZoomKey;

        /// <summary>
        /// Key mapping and echo suppression. Pure state, extracted so it is testable without a
        /// WinForms control tree — see <see cref="HudTabZoomTracker"/>.
        /// </summary>
        private readonly HudTabZoomTracker _zoomTracker = new HudTabZoomTracker();

        /// <summary>
        /// Last zoom applied for each PERSISTENCE key, so a tab added after a restore adopts it.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="_zoomTracker"/>, which is keyed per TAB and answers "was this an
        /// echo". This one answers "what should a newly-opened tab in this bucket start at", which is
        /// a per-key question and must survive a moment when no tab carries the key at all.
        /// </remarks>
        private readonly Dictionary<string, double> _lastZoomByKey =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Raised when the user zooms a tab, carrying WHICH tab so the value can be stored per tab.
        /// </summary>
        public event EventHandler<HudTabZoomChangedEventArgs> TabZoomChanged;

        /// <summary>
        /// Gets the distinct persistence keys currently represented by the open tabs.
        /// </summary>
        public IEnumerable<string> ZoomKeys =>
            _tabs.Select(ZoomKeyFor).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>
        /// Applies a zoom factor to every tab sharing <paramref name="zoomKey"/>.
        /// </summary>
        /// <param name="zoomKey">A permanent tab's id, or <see cref="BrowserZoomKey"/>.</param>
        /// <param name="zoom">The zoom factor to apply.</param>
        /// <remarks>
        /// Keyed rather than tab-id'd because one key can cover several live tabs: every open browser
        /// tab shares <see cref="BrowserZoomKey"/>, so restoring it must reach all of them.
        /// </remarks>
        public void SetZoomFactorForKey(string zoomKey, double zoom)
        {
            if (string.IsNullOrEmpty(zoomKey)) return;

            // The guard is seeded PER TAB, for each tab actually touched — not once against the key.
            // Several browser tabs share one key, so a key-seeded guard would compare one tab's next
            // change against a different tab's value and drop a genuine user zoom as an echo.
            // Record BEFORE applying: the renderers differ in whether they subscribe to WebView2's
            // ZoomFactorChanged before or after applying a pending zoom, so applying here can echo
            // straight back as a "change". Seeding first makes that echo a no-op.
            foreach (var tab in _tabs)
            {
                if (string.Equals(ZoomKeyFor(tab), zoomKey, StringComparison.OrdinalIgnoreCase)
                    && tab.Control is IZoomableTab zoomable)
                {
                    _zoomTracker.Record(tab.Id, zoom);
                    zoomable.SetZoomFactor(zoom);
                }
            }

            // Remember the key's value even when no tab currently carries it, so a tab opened later
            // (any browser tab, in practice) still adopts it via HookTabZoom.
            _lastZoomByKey[zoomKey] = zoom;
        }

        /// <summary>
        /// The persistence key for a tab: its own id when permanent, the shared browser bucket otherwise.
        /// </summary>
        private static string ZoomKeyFor(TabEntry entry) =>
            HudTabZoomTracker.KeyFor(entry.IsPermanent, entry.Id);

        /// <summary>
        /// Subscribes a newly added tab so its zoom is observed, and applies the key's current zoom.
        /// </summary>
        /// <remarks>
        /// Called from every site that adds a NEW entry to <c>_tabs</c>. Note that
        /// <see cref="ReorderPermanentTabs"/> clears and re-adds the SAME TabEntry instances and must
        /// NOT call this — doing so would subscribe every permanent tab a second time. Centralised on
        /// purpose: a per-site subscription is the same hand-maintained-list shape that caused this
        /// ticket, one layer up.
        /// </remarks>
        private void HookTabZoom(TabEntry entry)
        {
            if (!(entry?.Control is IZoomableTab zoomable)) return;

            zoomable.ZoomChanged += (s, zoom) => OnTabZoomChanged(entry, zoom);

            // A tab opened after a restore (any browser tab, in practice) still adopts the remembered
            // zoom rather than opening at 1.0 and looking like the bug this ticket fixes.
            if (_lastZoomByKey.TryGetValue(ZoomKeyFor(entry), out var known))
            {
                _zoomTracker.Record(entry.Id, known);
                zoomable.SetZoomFactor(known);
            }
        }

        /// <summary>
        /// Re-fires a tab's zoom change on the container, tagged with the tab's persistence key.
        /// </summary>
        /// <remarks>
        /// The value guard drops changes that merely repeat what we last applied. That covers the echo
        /// a renderer produces when it restores a saved zoom during WebView2 initialisation — which can
        /// arrive long after the call that caused it, so a synchronous "suppress" flag would miss it.
        /// Comparing values instead is independent of ordering and of when the echo lands.
        /// </remarks>
        private void OnTabZoomChanged(TabEntry entry, double zoom)
        {
            // Echo check is per TAB; persistence is per KEY. Conflating them dropped a genuine zoom on
            // a sibling browser tab that happened to match what another tab last reported.
            if (!_zoomTracker.ShouldReport(entry.Id, zoom))
            {
                return;
            }

            string key = ZoomKeyFor(entry);
            _lastZoomByKey[key] = zoom;
            TabZoomChanged?.Invoke(this, new HudTabZoomChangedEventArgs(key, zoom));
        }

        // -----------------------------------------------------------------
        // Tab strip rendering
        // -----------------------------------------------------------------

        private void SwitchToTab(int index)
        {
            if (index < 0 || index >= _tabs.Count) return;
            _activeTabIndex = index;

            for (int i = 0; i < _tabs.Count; i++)
                _tabs[i].Control.Visible = (i == index);

            _tabStrip.Invalidate();
        }

        private void UpdateTabStripVisibility()
        {
            // Always show tab strip when there are 2+ tabs (permanent or dynamic)
            bool shouldShow = _tabs.Count > 1;
            if (_tabStrip.Visible != shouldShow)
            {
                _tabStrip.Visible = shouldShow;
            }
        }

        private void OnTabStripPaint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var activeBg = _isDarkTheme ? Color.FromArgb(50, 50, 70) : Color.FromArgb(220, 220, 235);
            var inactiveBg = _isDarkTheme ? Color.FromArgb(35, 35, 45) : Color.FromArgb(230, 230, 230);
            var textColor = _isDarkTheme ? Color.FromArgb(200, 200, 210) : Color.FromArgb(40, 40, 40);
            var activeTextColor = _isDarkTheme ? Color.White : Color.Black;
            var closeColor = _isDarkTheme ? Color.FromArgb(150, 150, 160) : Color.FromArgb(100, 100, 100);

            int x = 2;
            using (var font = new Font("Segoe UI", 8.5f))
            using (var textBrush = new SolidBrush(textColor))
            using (var activeTextBrush = new SolidBrush(activeTextColor))
            using (var closeBrush = new SolidBrush(closeColor))
            {
                for (int i = 0; i < _tabs.Count; i++)
                {
                    var tab = _tabs[i];
                    var titleSize = g.MeasureString(tab.Title, font);
                    int tabWidth = (int)titleSize.Width + TabPadding * 2;
                    if (!tab.IsPermanent)
                        tabWidth += CloseButtonWidth;

                    var tabRect = new Rectangle(x, 2, tabWidth, TabStripHeight - 4);
                    tab.Bounds = tabRect;

                    bool isActive = (i == _activeTabIndex);
                    using (var bg = new SolidBrush(isActive ? activeBg : inactiveBg))
                    {
                        g.FillRoundedRectangle(bg, tabRect, 4);
                    }

                    // Tab title
                    var titleRect = new RectangleF(x + TabPadding, 5, titleSize.Width, TabStripHeight - 8);
                    g.DrawString(tab.Title, font, isActive ? activeTextBrush : textBrush, titleRect);

                    // Close button for non-permanent tabs
                    if (!tab.IsPermanent)
                    {
                        var closeRect = new Rectangle(x + tabWidth - CloseButtonWidth - 2, 5, CloseButtonWidth - 4, TabStripHeight - 10);
                        tab.CloseButtonBounds = closeRect;
                        using (var closeFont = new Font("Segoe UI", 8f))
                        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        {
                            g.DrawString("\u2715", closeFont, closeBrush, closeRect, sf);
                        }
                    }

                    x += tabWidth + 3;
                }
            }
        }

        private void OnTabStripMouseClick(object sender, MouseEventArgs e)
        {
            for (int i = 0; i < _tabs.Count; i++)
            {
                var tab = _tabs[i];
                if (!tab.IsPermanent && tab.CloseButtonBounds.Contains(e.Location))
                {
                    RemoveBrowserTab(tab.Id);
                    return;
                }
                if (tab.Bounds.Contains(e.Location))
                {
                    SwitchToTab(i);
                    return;
                }
            }
        }

        // -----------------------------------------------------------------
        // Dispose
        // -----------------------------------------------------------------

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var tab in _tabs)
                {
                    tab.Control?.Dispose();
                }
                _tabs.Clear();
            }
            base.Dispose(disposing);
        }

        // -----------------------------------------------------------------
        // Internal tab tracking
        // -----------------------------------------------------------------

        private class TabEntry
        {
            public string Id;
            public string Title;
            public Control Control;
            public bool IsPermanent;
            public Rectangle Bounds;
            public Rectangle CloseButtonBounds;
        }
    }

    /// <summary>
    /// Extension to draw rounded rectangles.
    /// </summary>
    internal static class GraphicsExtensions
    {
        public static void FillRoundedRectangle(this Graphics g, Brush brush, Rectangle rect, int radius)
        {
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                int d = radius * 2;
                path.AddArc(rect.X, rect.Y, d, d, 180, 90);
                path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
                path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
                path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                g.FillPath(brush, path);
            }
        }
    }
}
