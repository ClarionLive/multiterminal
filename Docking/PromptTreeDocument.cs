using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MultiTerminal.Controls;
using MultiTerminal.Services;
using MultiTerminal.Terminal;
using WeifenLuo.WinFormsUI.Docking;

namespace MultiTerminal.Docking
{
    /// <summary>
    /// Event arguments for prompt operations.
    /// </summary>
    public class PromptEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the prompt associated with the event.
        /// </summary>
        public Prompt Prompt { get; }

        public PromptEventArgs(Prompt prompt)
        {
            Prompt = prompt;
        }
    }

    /// <summary>
    /// Event arguments for creating a new prompt in a specific category.
    /// </summary>
    public class NewPromptInCategoryEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the category name for the new prompt.
        /// </summary>
        public string Category { get; }

        public NewPromptInCategoryEventArgs(string category)
        {
            Category = category;
        }
    }

    /// <summary>
    /// DockContent panel for displaying and managing saved prompts in a TreeView.
    /// Provides context menus for prompt operations and category management.
    /// </summary>
    public class PromptTreeDocument : DockContent
    {
        private TreeView _treeView;
        private PromptService _promptService;
        private string _currentWorkingDirectory;
        private TerminalTheme _currentTheme;

        // Context menus
        private ContextMenuStrip _promptContextMenu;
        private ContextMenuStrip _categoryContextMenu;
        private ContextMenuStrip _backgroundContextMenu;

        // Font styles for local vs global prompts
        private Font _regularFont;
        private Font _italicFont;

        /// <summary>
        /// Event fired when a prompt should be pasted to the active terminal.
        /// </summary>
        public event EventHandler<PromptEventArgs> PromptPasteRequested;

        /// <summary>
        /// Event fired when a prompt should be edited.
        /// </summary>
        public event EventHandler<PromptEventArgs> PromptEditRequested;

        /// <summary>
        /// Event fired when a prompt should be deleted.
        /// </summary>
        public event EventHandler<PromptEventArgs> PromptDeleteRequested;

        /// <summary>
        /// Event fired when a new prompt should be created.
        /// </summary>
        public event EventHandler NewPromptRequested;

        /// <summary>
        /// Event fired when a new prompt should be created in a specific category.
        /// </summary>
        public event EventHandler<NewPromptInCategoryEventArgs> NewPromptInCategoryRequested;

        public PromptTreeDocument()
        {
            Text = "Prompts";
            TabText = "Prompts";

            InitializeComponent();
            InitializeContextMenus();
        }

        private void InitializeComponent()
        {
            // Initialize fonts
            _regularFont = new Font("Segoe UI", 9f, FontStyle.Regular);
            _italicFont = new Font("Segoe UI", 9f, FontStyle.Italic);

            // Create TreeView
            _treeView = new TreeView
            {
                Dock = DockStyle.Fill,
                ShowNodeToolTips = true,
                ShowLines = true,
                ShowPlusMinus = true,
                ShowRootLines = true,
                HideSelection = false,
                Font = _regularFont,
                BorderStyle = BorderStyle.None,
                FullRowSelect = true,
                ImageList = null // Could add icons later
            };

            // Subscribe to TreeView events
            _treeView.NodeMouseDoubleClick += OnTreeViewNodeMouseDoubleClick;
            _treeView.NodeMouseClick += OnTreeViewNodeMouseClick;
            _treeView.MouseDown += OnTreeViewMouseDown;

            Controls.Add(_treeView);

            // Configure dock behavior
            DockAreas = DockAreas.DockLeft | DockAreas.DockRight | DockAreas.Float;
            CloseButton = true;
            CloseButtonVisible = true;
            HideOnClose = true;  // Prevent disposal when user clicks close button
            ShowHint = DockState.DockLeft;
        }

        private void InitializeContextMenus()
        {
            // Prompt context menu (right-click on a prompt node)
            _promptContextMenu = new ContextMenuStrip();

            var pasteToTerminalItem = new ToolStripMenuItem("Paste to Terminal");
            pasteToTerminalItem.Click += OnPasteToTerminalClick;
            _promptContextMenu.Items.Add(pasteToTerminalItem);

            var copyToClipboardItem = new ToolStripMenuItem("Copy to Clipboard");
            copyToClipboardItem.Click += OnCopyToClipboardClick;
            _promptContextMenu.Items.Add(copyToClipboardItem);

            _promptContextMenu.Items.Add(new ToolStripSeparator());

            var editPromptItem = new ToolStripMenuItem("Edit Prompt...");
            editPromptItem.Click += OnEditPromptClick;
            _promptContextMenu.Items.Add(editPromptItem);

            var deletePromptItem = new ToolStripMenuItem("Delete Prompt");
            deletePromptItem.Click += OnDeletePromptClick;
            _promptContextMenu.Items.Add(deletePromptItem);

            // Category context menu (right-click on a category node)
            _categoryContextMenu = new ContextMenuStrip();

            var newPromptInCategoryItem = new ToolStripMenuItem("New Prompt...");
            newPromptInCategoryItem.Click += OnNewPromptInCategoryClick;
            _categoryContextMenu.Items.Add(newPromptInCategoryItem);

            _categoryContextMenu.Items.Add(new ToolStripSeparator());

            var collapseAllItem = new ToolStripMenuItem("Collapse All");
            collapseAllItem.Click += OnCollapseAllClick;
            _categoryContextMenu.Items.Add(collapseAllItem);

            var expandAllItem = new ToolStripMenuItem("Expand All");
            expandAllItem.Click += OnExpandAllClick;
            _categoryContextMenu.Items.Add(expandAllItem);

            // Background context menu (right-click on empty area)
            _backgroundContextMenu = new ContextMenuStrip();

            var newPromptItem = new ToolStripMenuItem("New Prompt...");
            newPromptItem.Click += OnNewPromptClick;
            _backgroundContextMenu.Items.Add(newPromptItem);

            var refreshItem = new ToolStripMenuItem("Refresh");
            refreshItem.Click += OnRefreshClick;
            _backgroundContextMenu.Items.Add(refreshItem);
        }

        /// <summary>
        /// Sets the prompt service for loading and managing prompts.
        /// </summary>
        public void SetPromptService(PromptService service)
        {
            _promptService = service;
        }

        /// <summary>
        /// Applies a theme to the prompt tree panel.
        /// </summary>
        public void SetTheme(TerminalTheme theme)
        {
            _currentTheme = theme;

            if (theme.IsDark)
            {
                // Dark theme colors
                _treeView.BackColor = Color.FromArgb(37, 37, 38);
                _treeView.ForeColor = Color.White;
                BackColor = Color.FromArgb(37, 37, 38);
                ForeColor = Color.White;

                // Apply dark renderer to context menus
                _promptContextMenu.Renderer = new DarkToolStripRenderer();
                _promptContextMenu.BackColor = Color.FromArgb(45, 45, 48);
                _promptContextMenu.ForeColor = Color.White;
                ApplyDarkThemeToMenuItems(_promptContextMenu.Items);

                _categoryContextMenu.Renderer = new DarkToolStripRenderer();
                _categoryContextMenu.BackColor = Color.FromArgb(45, 45, 48);
                _categoryContextMenu.ForeColor = Color.White;
                ApplyDarkThemeToMenuItems(_categoryContextMenu.Items);

                _backgroundContextMenu.Renderer = new DarkToolStripRenderer();
                _backgroundContextMenu.BackColor = Color.FromArgb(45, 45, 48);
                _backgroundContextMenu.ForeColor = Color.White;
                ApplyDarkThemeToMenuItems(_backgroundContextMenu.Items);
            }
            else
            {
                // Light theme colors
                _treeView.BackColor = Color.FromArgb(246, 246, 246);
                _treeView.ForeColor = Color.FromArgb(30, 30, 30);
                BackColor = Color.FromArgb(246, 246, 246);
                ForeColor = Color.FromArgb(30, 30, 30);

                // Apply light renderer to context menus
                _promptContextMenu.Renderer = new LightToolStripRenderer();
                _promptContextMenu.BackColor = Color.FromArgb(246, 246, 246);
                _promptContextMenu.ForeColor = Color.FromArgb(30, 30, 30);
                ApplyLightThemeToMenuItems(_promptContextMenu.Items);

                _categoryContextMenu.Renderer = new LightToolStripRenderer();
                _categoryContextMenu.BackColor = Color.FromArgb(246, 246, 246);
                _categoryContextMenu.ForeColor = Color.FromArgb(30, 30, 30);
                ApplyLightThemeToMenuItems(_categoryContextMenu.Items);

                _backgroundContextMenu.Renderer = new LightToolStripRenderer();
                _backgroundContextMenu.BackColor = Color.FromArgb(246, 246, 246);
                _backgroundContextMenu.ForeColor = Color.FromArgb(30, 30, 30);
                ApplyLightThemeToMenuItems(_backgroundContextMenu.Items);
            }
        }

        private void ApplyDarkThemeToMenuItems(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                item.BackColor = Color.FromArgb(45, 45, 48);
                item.ForeColor = Color.White;
            }
        }

        private void ApplyLightThemeToMenuItems(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                item.BackColor = Color.FromArgb(246, 246, 246);
                item.ForeColor = Color.FromArgb(30, 30, 30);
            }
        }

        /// <summary>
        /// Sets the font size for the prompts tree view.
        /// </summary>
        public void SetFontSize(float size)
        {
            // Store old fonts for disposal after replacement
            var oldRegularFont = _regularFont;
            var oldItalicFont = _italicFont;

            // Create new fonts with updated size
            _regularFont = new Font("Segoe UI", size, FontStyle.Regular);
            _italicFont = new Font("Segoe UI", size, FontStyle.Italic);

            // Update TreeView font BEFORE disposing old fonts
            _treeView.Font = _regularFont;

            // Now safe to dispose old fonts
            oldRegularFont?.Dispose();
            oldItalicFont?.Dispose();

            // Refresh nodes to apply new fonts (only if we have prompts loaded)
            if (_promptService != null)
            {
                RefreshPrompts(_currentWorkingDirectory);
            }
        }

        /// <summary>
        /// Gets the current font size.
        /// </summary>
        public float GetFontSize()
        {
            return _regularFont?.Size ?? 9f;
        }

        /// <summary>
        /// Refreshes the prompt tree from the prompt service.
        /// </summary>
        /// <param name="workingDirectory">The current working directory for local prompt scope.</param>
        public void RefreshPrompts(string workingDirectory)
        {
            _currentWorkingDirectory = workingDirectory;

            _treeView.BeginUpdate();
            try
            {
                _treeView.Nodes.Clear();

                if (_promptService == null)
                {
                    return;
                }

                // Get prompts from service
                var prompts = _promptService.GetAllPrompts(workingDirectory);
                if (prompts == null || !prompts.Any())
                {
                    return;
                }

                // Group prompts by category
                var groupedPrompts = prompts
                    .GroupBy(p => p.Category ?? "Uncategorized")
                    .OrderBy(g => g.Key);

                foreach (var group in groupedPrompts)
                {
                    // Create category node
                    var categoryNode = new TreeNode(group.Key)
                    {
                        Tag = $"category:{group.Key}",
                        NodeFont = _regularFont
                    };

                    // Add prompt nodes under category
                    foreach (var prompt in group.OrderBy(p => p.Description))
                    {
                        var promptNode = CreatePromptNode(prompt);
                        categoryNode.Nodes.Add(promptNode);
                    }

                    _treeView.Nodes.Add(categoryNode);
                }

                // Expand all categories by default
                _treeView.ExpandAll();
            }
            finally
            {
                _treeView.EndUpdate();
            }
        }

        private TreeNode CreatePromptNode(Prompt prompt)
        {
            // Truncate tooltip text to 200 characters
            string tooltipText = prompt.Text ?? string.Empty;
            if (tooltipText.Length > 200)
            {
                tooltipText = tooltipText.Substring(0, 197) + "...";
            }

            // Use Description as the display name
            string displayName = prompt.Description ?? "Unnamed Prompt";

            var node = new TreeNode(displayName)
            {
                Tag = prompt,
                ToolTipText = tooltipText,
                // Local prompts (IsGlobal=false) are italic, global prompts are regular
                NodeFont = prompt.IsGlobal ? _regularFont : _italicFont
            };

            return node;
        }

        private void OnTreeViewNodeMouseDoubleClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            // Double-click on prompt node fires paste request
            if (e.Node?.Tag is Prompt prompt)
            {
                PromptPasteRequested?.Invoke(this, new PromptEventArgs(prompt));
            }
        }

        private void OnTreeViewNodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            // Right-click handling
            if (e.Button == MouseButtons.Right)
            {
                // Select the node that was clicked
                _treeView.SelectedNode = e.Node;

                if (e.Node?.Tag is Prompt)
                {
                    // Show prompt context menu
                    _promptContextMenu.Show(_treeView, e.Location);
                }
                else if (e.Node?.Tag is string tagString && tagString.StartsWith("category:"))
                {
                    // Show category context menu
                    _categoryContextMenu.Show(_treeView, e.Location);
                }
            }
        }

        private void OnTreeViewMouseDown(object sender, MouseEventArgs e)
        {
            // Right-click on empty area (background)
            if (e.Button == MouseButtons.Right)
            {
                var hitTest = _treeView.HitTest(e.Location);
                if (hitTest.Node == null)
                {
                    // Clicked on empty area, show background context menu
                    _treeView.SelectedNode = null;
                    _backgroundContextMenu.Show(_treeView, e.Location);
                }
            }
        }

        private void OnPasteToTerminalClick(object sender, EventArgs e)
        {
            if (_treeView.SelectedNode?.Tag is Prompt prompt)
            {
                PromptPasteRequested?.Invoke(this, new PromptEventArgs(prompt));
            }
        }

        private void OnCopyToClipboardClick(object sender, EventArgs e)
        {
            if (_treeView.SelectedNode?.Tag is Prompt prompt)
            {
                try
                {
                    if (!string.IsNullOrEmpty(prompt.Text))
                    {
                        Clipboard.SetText(prompt.Text);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[PromptTreeDocument] Failed to copy to clipboard: {ex.Message}");
                }
            }
        }

        private void OnEditPromptClick(object sender, EventArgs e)
        {
            if (_treeView.SelectedNode?.Tag is Prompt prompt)
            {
                PromptEditRequested?.Invoke(this, new PromptEventArgs(prompt));
            }
        }

        private void OnDeletePromptClick(object sender, EventArgs e)
        {
            if (_treeView.SelectedNode?.Tag is Prompt prompt)
            {
                PromptDeleteRequested?.Invoke(this, new PromptEventArgs(prompt));
            }
        }

        private void OnNewPromptClick(object sender, EventArgs e)
        {
            NewPromptRequested?.Invoke(this, EventArgs.Empty);
        }

        private void OnNewPromptInCategoryClick(object sender, EventArgs e)
        {
            if (_treeView.SelectedNode?.Tag is string tagString && tagString.StartsWith("category:"))
            {
                string category = tagString.Substring("category:".Length);
                NewPromptInCategoryRequested?.Invoke(this, new NewPromptInCategoryEventArgs(category));
            }
        }

        private void OnCollapseAllClick(object sender, EventArgs e)
        {
            _treeView.CollapseAll();
        }

        private void OnExpandAllClick(object sender, EventArgs e)
        {
            _treeView.ExpandAll();
        }

        private void OnRefreshClick(object sender, EventArgs e)
        {
            RefreshPrompts(_currentWorkingDirectory);
        }

        /// <summary>
        /// Gets the persist string for layout serialization.
        /// </summary>
        protected override string GetPersistString()
        {
            return typeof(PromptTreeDocument).FullName;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _regularFont?.Dispose();
                _italicFont?.Dispose();
                _promptContextMenu?.Dispose();
                _categoryContextMenu?.Dispose();
                _backgroundContextMenu?.Dispose();
                _treeView?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
