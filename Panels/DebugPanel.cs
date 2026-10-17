using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;
using MultiTerminal.Controls;
using MultiTerminal.Models;
using MultiTerminal.Services;

namespace MultiTerminal.Panels
{
    /// <summary>
    /// Debug log panel for real-time collaborative debugging.
    /// Displays internal debug messages from all system components.
    /// </summary>
    public class DebugPanel : DockContent
    {
        private ListView _logListView;
        private ToolStrip _toolStrip;

        // ToolStrip items added to _toolStrip.Items / listViewMenu.Items; disposed transitively when parent ToolStrip/ContextMenuStrip is disposed.
#pragma warning disable CA2213
        private ToolStripButton _clearButton;
        private ToolStripButton _copyButton;
        private ToolStripButton _exportButton;
        private ToolStripButton _pauseButton;
        private ToolStripButton _systemWideCaptureButton;
        private ToolStripMenuItem _copyMenuItem;
        private ToolStripLabel _countLabel;
#pragma warning restore CA2213
        private DebugLogService _logService;
        private bool _isPaused = false;
        private bool _isDisposed = false;

        // Set from any thread when an entry was skipped because the panel had no handle; read on the
        // UI thread in OnHandleCreated. Volatile so a set on a pool thread is seen there.
        private volatile bool _resyncPending;

        public DebugPanel()
        {
            InitializeComponent();
            DockAreas = DockAreas.DockBottom | DockAreas.DockTop | DockAreas.DockLeft | DockAreas.DockRight | DockAreas.Float | DockAreas.Document;
        }

        protected override string GetPersistString()
        {
            return "DebugPanel";
        }

        /// <summary>
        /// Initializes the panel with the debug log service.
        /// </summary>
        public void Initialize(DebugLogService logService)
        {
            _logService = logService;
            _logService.LogMessageAdded += OnLogMessageAdded;
            _logService.LogCleared += OnLogCleared;

            // Load any existing messages
            foreach (var entry in _logService.GetMessages())
            {
                AddLogEntry(entry);
            }

            UpdateCountLabel();
        }

        private void InitializeComponent()
        {
            Text = "Debug Log";
            CloseButton = false;
            CloseButtonVisible = false;

            // Create toolbar
            _toolStrip = new ToolStrip
            {
                GripStyle = ToolStripGripStyle.Hidden,
                Renderer = new DarkToolStripRenderer(),
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.White,
                Dock = DockStyle.Top
            };

            _clearButton = new ToolStripButton
            {
                Text = "Clear",
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ForeColor = Color.White
            };
            _clearButton.Click += OnClearClick;

            _copyButton = new ToolStripButton
            {
                Text = "Copy Selected to Clipboard",
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ForeColor = Color.White,
                ToolTipText = "Copy selected rows as tab-separated text (Ctrl+C)"
            };
            _copyButton.Click += (s, e) => CopySelectedToClipboard();

            _exportButton = new ToolStripButton
            {
                Text = "Export",
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ForeColor = Color.White
            };
            _exportButton.Click += OnExportClick;

            _pauseButton = new ToolStripButton
            {
                Text = "Pause",
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ForeColor = Color.White
            };
            _pauseButton.Click += OnPauseClick;

            _systemWideCaptureButton = new ToolStripButton
            {
                Text = "System-Wide: OFF",
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ForeColor = Color.FromArgb(180, 180, 180),
                ToolTipText = "Enable OutputDebugString capture from all processes"
            };
            _systemWideCaptureButton.Click += OnSystemWideCaptureClick;

            _countLabel = new ToolStripLabel
            {
                Text = "0 messages",
                Alignment = ToolStripItemAlignment.Right,
                ForeColor = Color.FromArgb(180, 180, 180)
            };

            _toolStrip.Items.Add(_clearButton);
            _toolStrip.Items.Add(new ToolStripSeparator());
            _toolStrip.Items.Add(_copyButton);
            _toolStrip.Items.Add(new ToolStripSeparator());
            _toolStrip.Items.Add(_exportButton);
            _toolStrip.Items.Add(new ToolStripSeparator());
            _toolStrip.Items.Add(_pauseButton);
            _toolStrip.Items.Add(new ToolStripSeparator());
            _toolStrip.Items.Add(_systemWideCaptureButton);
            _toolStrip.Items.Add(_countLabel);

            // Create ListView with double buffering to prevent flashing
            _logListView = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                BackColor = Color.FromArgb(37, 37, 38),
                ForeColor = Color.FromArgb(220, 220, 220),
                Font = new Font("Consolas", 9F),
                BorderStyle = BorderStyle.None
            };

            // Enable double buffering to reduce flicker
            typeof(ListView).InvokeMember("DoubleBuffered",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.SetProperty,
                null, _logListView, new object[] { true });

            _logListView.Columns.Add("#", 40);
            _logListView.Columns.Add("Time", 100);
            _logListView.Columns.Add("Process", 150);
            _logListView.Columns.Add("Source", 120);
            _logListView.Columns.Add("Level", 70);
            _logListView.Columns.Add("Message", 500);

            // Right-click menu for copy
            var listViewMenu = new ContextMenuStrip
            {
                Renderer = new DarkToolStripRenderer(),
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.White
            };
            _copyMenuItem = new ToolStripMenuItem("Copy Selected to Clipboard")
            {
                ShortcutKeyDisplayString = "Ctrl+C",
                ForeColor = Color.White
            };
            _copyMenuItem.Click += (s, e) => CopySelectedToClipboard();
            listViewMenu.Items.Add(_copyMenuItem);
            listViewMenu.Opening += (s, e) =>
            {
                _copyMenuItem.Enabled = _logListView.SelectedItems.Count > 0;
            };
            _logListView.ContextMenuStrip = listViewMenu;

            // Ctrl+C keyboard shortcut
            _logListView.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.C)
                {
                    CopySelectedToClipboard();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };

            Controls.Add(_logListView);
            Controls.Add(_toolStrip);

            BackColor = Color.FromArgb(37, 37, 38);
        }

        private void OnLogMessageAdded(object sender, DebugLogEntry entry)
        {
            if (_isDisposed) return;

            // Task d41637ab: InvokeRequired answers false when the panel has no handle (never shown,
            // or hidden before its handle was made), even on a thread-pool thread. The old code then
            // edited the ListView from that thread, and on 2026-10-16 a FileSystemWatcher error
            // logged that way corrupted it and crashed MultiTerminal. With no handle there is no
            // thread to marshal to, so leave the list alone and rebuild it from the service's buffer
            // once a handle exists (OnHandleCreated).
            if (!IsHandleCreated)
            {
                _resyncPending = true;
                return;
            }

            // Marshal to UI thread
            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action(() => OnLogMessageAdded(sender, entry)));
                }
                catch (InvalidOperationException)
                {
                    // The handle was destroyed between the check above and this call.
                    _resyncPending = true;
                }
                return;
            }

            if (!_isPaused)
            {
                AddLogEntry(entry);
                UpdateCountLabel();
            }
        }

        private void AddLogEntry(DebugLogEntry entry)
        {
            // Suspend drawing to prevent flashing
            _logListView.BeginUpdate();

            try
            {
                // Line number is current count + 1
                var lineNumber = (_logListView.Items.Count + 1).ToString();
                var item = new ListViewItem(lineNumber);
                item.SubItems.Add(entry.Timestamp.ToString("HH:mm:ss.fff"));

                // Add process info (if available)
                if (entry.ProcessId > 0 && !string.IsNullOrEmpty(entry.ProcessName))
                {
                    item.SubItems.Add($"{entry.ProcessName} ({entry.ProcessId})");
                }
                else
                {
                    item.SubItems.Add(""); // No process info for internal messages
                }

                item.SubItems.Add(entry.Source);
                item.SubItems.Add(entry.Level.ToString());
                item.SubItems.Add(entry.Message);

                // Color code by level
                switch (entry.Level)
                {
                    case DebugLogLevel.Error:
                        item.ForeColor = Color.FromArgb(244, 71, 71);
                        break;
                    case DebugLogLevel.Warning:
                        item.ForeColor = Color.FromArgb(255, 193, 7);
                        break;
                    case DebugLogLevel.Info:
                        item.ForeColor = Color.FromArgb(97, 175, 239);
                        break;
                    case DebugLogLevel.Trace:
                        item.ForeColor = Color.FromArgb(180, 180, 180);
                        break;
                }

                _logListView.Items.Add(item);

                // Auto-scroll to bottom if not paused
                if (!_isPaused && _logListView.Items.Count > 0)
                {
                    _logListView.EnsureVisible(_logListView.Items.Count - 1);
                }
            }
            finally
            {
                // Resume drawing
                _logListView.EndUpdate();
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (_resyncPending) ResyncFromService();
        }

        /// <summary>
        /// Rebuilds the list from the service's in-memory buffer, replacing entries that were skipped
        /// while the panel had no handle. UI thread only.
        /// </summary>
        private void ResyncFromService()
        {
            _resyncPending = false;
            if (_logService == null || _isDisposed) return;

            _logListView.Items.Clear();
            foreach (var entry in _logService.GetMessages())
            {
                AddLogEntry(entry);
            }
            UpdateCountLabel();
        }

        /// <summary>Test seam (InternalsVisibleTo): rows currently in the list.</summary>
        internal int DisplayedEntryCount => _logListView.Items.Count;

        private void UpdateCountLabel()
        {
            if (_logService != null)
            {
                _countLabel.Text = $"{_logService.Count} messages";
            }
        }

        private void OnLogCleared(object sender, EventArgs e)
        {
            if (_isDisposed) return;

            // Same no-handle hazard as OnLogMessageAdded; the resync rebuilds from the emptied buffer.
            if (!IsHandleCreated)
            {
                _resyncPending = true;
                return;
            }

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action(() => OnLogCleared(sender, e)));
                }
                catch (InvalidOperationException)
                {
                    _resyncPending = true;
                }
                return;
            }

            _logListView.Items.Clear();
            UpdateCountLabel();
        }

        private void OnClearClick(object sender, EventArgs e)
        {
            _logService?.Clear();
        }

        private void CopySelectedToClipboard()
        {
            var selected = _logListView.SelectedItems;
            if (selected.Count == 0) return;

            var sb = new StringBuilder();
            foreach (ListViewItem item in selected)
            {
                for (int i = 0; i < item.SubItems.Count; i++)
                {
                    if (i > 0) sb.Append('\t');
                    sb.Append(item.SubItems[i].Text);
                }
                sb.Append("\r\n");
            }

            try
            {
                Clipboard.SetText(sb.ToString());
            }
            catch
            {
                // Clipboard can fail if another process is holding it; ignore.
            }
        }

        private void OnExportClick(object sender, EventArgs e)
        {
            using (var dialog = new SaveFileDialog
            {
                Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
                FileName = $"debug-log-{DateTime.Now:yyyy-MM-dd-HHmmss}.txt"
            })
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine($"Debug Log Export - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                        sb.AppendLine(new string('=', 80));
                        sb.AppendLine();

                        foreach (var entry in _logService.GetMessages())
                        {
                            sb.AppendLine(entry.ToFullString());
                        }

                        File.WriteAllText(dialog.FileName, sb.ToString());
                        MessageBox.Show($"Debug log exported to:\n{dialog.FileName}", "Export Successful",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to export debug log:\n{ex.Message}", "Export Failed",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void OnPauseClick(object sender, EventArgs e)
        {
            _isPaused = !_isPaused;
            _pauseButton.Text = _isPaused ? "Resume" : "Pause";
            _pauseButton.ForeColor = _isPaused ? Color.FromArgb(255, 193, 7) : Color.White;
        }

        private void OnSystemWideCaptureClick(object sender, EventArgs e)
        {
            if (_logService == null)
                return;

            if (_logService.IsSystemWideCapture)
            {
                // Stop system-wide capture
                _logService.StopSystemWideCapture();
                _systemWideCaptureButton.Text = "System-Wide: OFF";
                _systemWideCaptureButton.ForeColor = Color.FromArgb(180, 180, 180);
            }
            else
            {
                // Start system-wide capture
                _logService.StartSystemWideCapture();
                _systemWideCaptureButton.Text = "System-Wide: ON";
                _systemWideCaptureButton.ForeColor = Color.FromArgb(97, 175, 239); // Blue when active
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_isDisposed)
            {
                _isDisposed = true;
                if (_logService != null)
                {
                    _logService.LogMessageAdded -= OnLogMessageAdded;
                    _logService.LogCleared -= OnLogCleared;
                }
                _logListView?.Dispose();
                _toolStrip?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
