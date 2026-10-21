using System;
using System.Drawing;
using System.Windows.Forms;
using MultiTerminal.Services;
using MultiTerminal.Terminal;

namespace MultiTerminal.Dialogs
{
    /// <summary>
    /// The startup restore prompt (task 3e1c09b8): the Claude terminals the previous MultiTerminal run
    /// had open when it closed or crashed, every row ticked, with Resume checked and Don't restore.
    /// Helpers are indented under their PM. A thin view over <see cref="StartupRestoreModel"/>, which
    /// holds the rows and the tick rules (unit-tested); themed like <see cref="LaunchSessionDialog"/>.
    /// Closing the window (Esc, the close box) is Don't restore. The caller reads
    /// <see cref="StartupRestoreModel.SelectedTopLevel"/> after <see cref="DialogResult.OK"/>.
    /// </summary>
    public class StartupRestoreDialog : Form
    {
        private const float BaseFontSize = 9.5f;
        private const float ListFontSize = BaseFontSize + 2f;
        private const double RowHeightFactor = 1.6;
        private const int CellPadding = 8;
        private const int HelperIndent = 28;
        private const int ColTerminal = 0;
        private const int ColWhere = 2;
        private const int MinWhereWidth = 160;

        // Controls — added to Controls collection and auto-disposed by base Form.Dispose().
#pragma warning disable CA2213
        private ListView _list;
        private Button _resumeButton;
        private Button _dontRestoreButton;
#pragma warning restore CA2213

        // Not controls: disposed in Dispose below.
        private ImageList _rowHeightImages;
        private Font _listFont;
        private Font _listBoldFont;

        private readonly StartupRestoreModel _model;
        private bool _fittingColumns;

        private Color _listBack = SystemColors.Window;
        private Color _listFore = SystemColors.WindowText;
        private Color _selectionBack = SystemColors.Highlight;
        private Color _selectionFore = SystemColors.HighlightText;
        private Color _headerBack = SystemColors.Control;
        private Color _headerFore = SystemColors.ControlText;
        private Color _gridLine = SystemColors.ControlDark;

        public StartupRestoreDialog(StartupRestoreModel model, TerminalTheme theme)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));

            Text = "Restore terminals";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            SizeGripStyle = SizeGripStyle.Show;

            BuildLayout();
            ApplyTheme(theme);
            UpdateResumeText();
        }

        private void BuildLayout()
        {
            const int left = 16;
            const int width = 760;
            int y = 16;

            _listFont = new Font("Segoe UI", ListFontSize);
            _listBoldFont = new Font("Segoe UI", ListFontSize, FontStyle.Bold);
            var rowHeight = (int)Math.Round(TextRenderer.MeasureText("Ag", _listFont).Height * RowHeightFactor);
            int visibleRows = Math.Min(12, Math.Max(3, _model.Rows.Count));
            int listHeight = (visibleRows * rowHeight) + rowHeight + 6; // rows + header

            // Size first: anchors record their distance to the edges when a control is added.
            int listTop = y + 52;
            int bottomTop = listTop + listHeight + 12;
            ClientSize = new Size(width + (2 * left), bottomTop + 46);
            MinimumSize = new Size(520, 300);

            Controls.Add(new Label
            {
                Text = "The following terminals were open when you closed MultiTerminal.\nWould you like to resume the checked sessions?",
                Location = new Point(left, y),
                Size = new Size(width, 44),
                Font = new Font("Segoe UI", BaseFontSize + 0.5f),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            });

            _rowHeightImages = new ImageList { ImageSize = new Size(1, Math.Min(256, Math.Max(16, rowHeight))) };
            _list = new ListView
            {
                Location = new Point(left, listTop),
                Size = new Size(width, listHeight),
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                OwnerDraw = true,
                ShowItemToolTips = true,
                BorderStyle = BorderStyle.FixedSingle,
                Font = _listFont,
                SmallImageList = _rowHeightImages,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            };
            _list.Columns.Add("Terminal", 300);
            _list.Columns.Add("Project", 220);
            _list.Columns.Add("Where", MinWhereWidth);

            foreach (var row in _model.Rows)
            {
                var item = new ListViewItem(row.IsHelper ? $"{row.DisplayName} [Helper]" : row.DisplayName)
                {
                    Tag = row,
                    ToolTipText = $"{row.Pane.SessionId}\n{MCPServer.Services.SessionCatalog.ToDisplayText(row.Pane.Cwd)}",
                };
                item.SubItems.Add(MCPServer.Services.SessionCatalog.ToDisplayText(row.ProjectName) ?? string.Empty);
                item.SubItems.Add(row.Where);
                _list.Items.Add(item);
            }

            _list.DrawColumnHeader += OnDrawColumnHeader;
            _list.DrawItem += (s, e) => { /* every cell is drawn in DrawSubItem */ };
            _list.DrawSubItem += OnDrawSubItem;
            _list.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                    Toggle(_list.HitTest(e.Location).Item);
            };
            _list.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Space && _list.SelectedItems.Count == 1)
                {
                    Toggle(_list.SelectedItems[0]);
                    e.Handled = true;
                }
            };
            _list.SelectedIndexChanged += (s, e) => _list.Invalidate();
            _list.Enter += (s, e) => _list.Invalidate();
            _list.Leave += (s, e) => _list.Invalidate();
            _list.Resize += (s, e) => FitWhereColumn();
            _list.ColumnWidthChanged += (s, e) =>
            {
                if (e.ColumnIndex != ColWhere) FitWhereColumn();
            };
            Controls.Add(_list);

            _resumeButton = new Button
            {
                Location = new Point(left + width - 330, bottomTop),
                Size = new Size(190, 30),
                Font = new Font("Segoe UI", BaseFontSize),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            };
            _resumeButton.Click += (s, e) => DialogResult = DialogResult.OK;
            Controls.Add(_resumeButton);

            _dontRestoreButton = new Button
            {
                Text = "Don't restore",
                Location = new Point(left + width - 130, bottomTop),
                Size = new Size(130, 30),
                Font = new Font("Segoe UI", BaseFontSize),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            };
            _dontRestoreButton.Click += (s, e) => DialogResult = DialogResult.Cancel;
            Controls.Add(_dontRestoreButton);

            AcceptButton = _resumeButton;
            CancelButton = _dontRestoreButton;
            FitWhereColumn();
        }

        /// <summary>
        /// The dialog's answer is the model: OK resumes <see cref="StartupRestoreModel.SelectedTopLevel"/>
        /// (nothing ticked behaves as Don't restore); any other close unticks everything first, so a
        /// caller that reads the model after Cancel cannot restore anything.
        /// </summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (DialogResult != DialogResult.OK)
                _model.UncheckAll();
            base.OnFormClosing(e);
        }

        private void Toggle(ListViewItem item)
        {
            if (item?.Tag is not StartupRestoreEntry row)
                return;
            _model.Toggle(row);
            UpdateResumeText();
            _list.Invalidate();
        }

        private void UpdateResumeText()
        {
            int count = _model.CheckedCount;
            _resumeButton.Text = count == 1 ? "Resume 1 terminal" : $"Resume {count} terminals";
        }

        // Where takes the width the other columns leave, so the header has no unpainted strip.
        private void FitWhereColumn()
        {
            if (_fittingColumns || _list == null || _list.Columns.Count <= ColWhere)
                return;
            _fittingColumns = true;
            try
            {
                var others = _list.Columns[0].Width + _list.Columns[1].Width;
                var width = Math.Max(MinWhereWidth, _list.ClientSize.Width - others - 1);
                if (_list.Columns[ColWhere].Width != width)
                    _list.Columns[ColWhere].Width = width;
            }
            finally
            {
                _fittingColumns = false;
            }
        }

        private void OnDrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            using (var back = new SolidBrush(_headerBack))
                e.Graphics.FillRectangle(back, e.Bounds);
            var textRect = new Rectangle(e.Bounds.X + CellPadding, e.Bounds.Y, Math.Max(0, e.Bounds.Width - (2 * CellPadding)), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, _listBoldFont, textRect, _headerFore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            using var pen = new Pen(_gridLine);
            e.Graphics.DrawLine(pen, e.Bounds.Right - 1, e.Bounds.Top + 4, e.Bounds.Right - 1, e.Bounds.Bottom - 5);
            e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        }

        private void OnDrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            if (e.Item?.Tag is not StartupRestoreEntry row || e.SubItem == null)
                return;

            // For column 0 some WinForms versions report the whole row as the bounds; clip to the column.
            var bounds = e.ColumnIndex == 0
                ? new Rectangle(e.Bounds.X, e.Bounds.Y, _list.Columns[0].Width, e.Bounds.Height)
                : e.Bounds;
            var selected = e.Item.Selected && _list.Focused;
            using (var back = new SolidBrush(selected ? _selectionBack : _listBack))
                e.Graphics.FillRectangle(back, bounds);

            // A helper whose PM is unticked is disabled: dimmed like a disabled button, still readable.
            var fore = !row.Enabled ? LaunchSessionDialog.Blend(_listFore, _listBack, 0.45) : selected ? _selectionFore : _listFore;
            var font = row.IsHelper ? _listFont : _listBoldFont;
            var textLeft = bounds.X + CellPadding;
            if (e.ColumnIndex == ColTerminal)
            {
                if (row.IsHelper)
                    textLeft += HelperIndent;
                var box = Math.Min(14, Math.Max(8, bounds.Height - 10));
                var boxRect = new Rectangle(textLeft, bounds.Y + ((bounds.Height - box) / 2), box, box);
                using (var pen = new Pen(selected ? _selectionFore : !row.Enabled ? fore : _gridLine))
                    e.Graphics.DrawRectangle(pen, boxRect);
                if (row.Checked)
                {
                    var smoothing = e.Graphics.SmoothingMode;
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (var check = new Pen(fore, 2f))
                    {
                        e.Graphics.DrawLines(check, new[]
                        {
                            new PointF(boxRect.Left + (box * 0.2f), boxRect.Top + (box * 0.5f)),
                            new PointF(boxRect.Left + (box * 0.42f), boxRect.Top + (box * 0.75f)),
                            new PointF(boxRect.Left + (box * 0.82f), boxRect.Top + (box * 0.25f)),
                        });
                    }

                    e.Graphics.SmoothingMode = smoothing;
                }

                textLeft = boxRect.Right + 8;
            }
            else
            {
                font = _listFont;
            }

            var textRect = new Rectangle(textLeft, bounds.Y, Math.Max(0, bounds.Right - CellPadding - textLeft), bounds.Height);
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, font, textRect, fore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        private void ApplyTheme(TerminalTheme theme)
        {
            if (theme == null)
                return;
            BackColor = theme.ToolbarBackground;
            ForeColor = theme.ToolbarForeground;
            _listBack = theme.Background;
            _listFore = theme.Foreground;
            _selectionBack = theme.SelectionBackground;
            _selectionFore = theme.SelectionForeground;
            _headerBack = theme.TabInactiveBackground;
            _headerFore = theme.ToolbarForeground;
            _gridLine = theme.StatusForeground;

            foreach (Control ctrl in Controls)
            {
                if (ctrl is Label lbl)
                {
                    lbl.ForeColor = theme.ToolbarForeground;
                }
                else if (ctrl is ListView lv)
                {
                    lv.BackColor = theme.Background;
                    lv.ForeColor = theme.Foreground;
                }
                else if (ctrl is Button btn)
                {
                    btn.BackColor = theme.TabInactiveBackground;
                    btn.ForeColor = theme.ToolbarForeground;
                    btn.FlatStyle = FlatStyle.Flat;
                    btn.FlatAppearance.BorderColor = theme.StatusForeground;
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            // Controls first (the list still references the fonts and image list), then what they used.
            base.Dispose(disposing);
            if (disposing)
            {
                _rowHeightImages?.Dispose();
                _listFont?.Dispose();
                _listBoldFont?.Dispose();
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            FitWhereColumn();
            _resumeButton.Focus();
        }
    }
}
