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
        private readonly Panel _tabStrip;
        private readonly Panel _contentArea;
        private readonly TaskHudRenderer _taskHud;
        private readonly List<TabEntry> _tabs = new List<TabEntry>();
        private int _activeTabIndex;
        private bool _isDarkTheme = true;

        private const int TabStripHeight = 28;
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

        public HudTabContainer(TaskHudRenderer taskHudRenderer)
        {
            _taskHud = taskHudRenderer ?? throw new ArgumentNullException(nameof(taskHudRenderer));

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

            // Order matters: Fill must be added before Top for DockStyle layout
            Controls.Add(_contentArea);
            Controls.Add(_tabStrip);

            ResumeLayout(false);

            // Install TaskHudRenderer as permanent "Tasks" tab
            _taskHud.Dock = DockStyle.Fill;
            var hudEntry = new TabEntry
            {
                Id = "__tasks__",
                Title = "\ud83d\udccb Tasks",
                Control = _taskHud,
                IsPermanent = true
            };
            _tabs.Add(hudEntry);
            _contentArea.Controls.Add(_taskHud);
            _activeTabIndex = 0;
            _taskHud.Visible = true; // Active tab starts visible

            // HUD is always visible now — forward visibility requests to keep
            // Panel2 expanded, but don't hide/show based on task presence.
            _taskHud.HudVisibilityRequested += (s, show) =>
            {
                // Always keep the container visible regardless of task state.
                // If another tab is active, just update TaskHud data silently.
                if (_activeTabIndex != GetTabIndex("__tasks__"))
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
                if (_taskHud.Visible && _activeTabIndex != GetTabIndex("__tasks__"))
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
        public void AddPermanentTab(string tabId, string title, Control control)
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
        /// Applies theme to the container and all tabs.
        /// </summary>
        public void ApplyTheme(bool isDark)
        {
            _isDarkTheme = isDark;
            _tabStrip.BackColor = isDark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(235, 235, 235);
            _contentArea.BackColor = isDark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(245, 245, 245);
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
                else if (tab.Control is HudSessionsRenderer sessions)
                    sessions.ApplyTheme(isDark);
            }
            _tabStrip.Invalidate();
        }

        /// <summary>
        /// Propagates zoom to all WebView2 tabs.
        /// </summary>
        public void SetZoomFactor(double zoom)
        {
            _taskHud.SetZoomFactor(zoom);
            foreach (var tab in _tabs)
            {
                if (tab.Control is BrowserTabPage page)
                    page.SetZoomFactor(zoom);
                else if (tab.Control is HudDashboardRenderer dashboard)
                    dashboard.SetZoomFactor(zoom);
                else if (tab.Control is HudNotesRenderer notes)
                    notes.SetZoomFactor(zoom);
                else if (tab.Control is HudKnowledgeRenderer knowledge)
                    knowledge.SetZoomFactor(zoom);
                else if (tab.Control is HudSessionsRenderer sessions)
                    sessions.SetZoomFactor(zoom);
            }
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
