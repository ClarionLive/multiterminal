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
        private ToolStripButton _clearButton;
        private ToolStripButton _exportButton;
        private ToolStripButton _pauseButton;
        private ToolStripButton _systemWideCaptureButton;
        private ToolStripLabel _countLabel;
        private DebugLogService _logService;
        private bool _isPaused = false;
        private bool _isDisposed = false;

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

            Controls.Add(_logListView);
            Controls.Add(_toolStrip);

            BackColor = Color.FromArgb(37, 37, 38);
        }

        private void OnLogMessageAdded(object sender, DebugLogEntry entry)
        {
            if (_isDisposed) return;

            // Marshal to UI thread
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnLogMessageAdded(sender, entry)));
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

            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnLogCleared(sender, e)));
                return;
            }

            _logListView.Items.Clear();
            UpdateCountLabel();
        }

        private void OnClearClick(object sender, EventArgs e)
        {
            _logService?.Clear();
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
