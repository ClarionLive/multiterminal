using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Models;
using MultiTerminal.Services;
using MultiTerminal.Terminal;

namespace MultiTerminal.Dialogs
{
    /// <summary>
    /// The project-launch session prompt (task ec2ed636): New session, Continue last (with a preview),
    /// or resume a session picked from the project's recent sessions, helpers and worktree sessions
    /// included. Themed like <see cref="IdentityPickerDialog"/>.
    ///
    /// <para>The Form is a thin view. What is offered, what needs a confirm, which name a choice
    /// launches under and what "Don't ask again" persists all live in
    /// <see cref="LaunchSessionChoiceModel"/>, which is unit-tested, as are the context-used text and
    /// level (<see cref="LaunchSessionChoiceModel.FormatContext"/>). Cancel (Esc, the close box or the
    /// Cancel button) returns <see cref="DialogResult.Cancel"/> and the caller launches nothing.</para>
    ///
    /// <para>The past-session list is an owner-drawn details ListView (item 7): columns Agent, Where,
    /// Age, Context and Summary, the last filling the remaining width. Owner drawing is what lets the
    /// header and selection follow the dark/light theme and the Context cell take a warning colour.</para>
    /// </summary>
    public class LaunchSessionDialog : Form
    {
        private const float BaseFontSize = 9.5f;
        private const float ListFontSize = BaseFontSize + 2f;
        private const double RowHeightFactor = 1.6;
        private const int CellPadding = 8;

        private const int ColAgent = 0;
        private const int ColWhere = 1;
        private const int ColAge = 2;
        private const int ColContext = 3;
        private const int ColSummary = 4;
        private const int MinSummaryWidth = 160;

        // Controls — added to Controls collection and auto-disposed by base Form.Dispose().
#pragma warning disable CA2213
        private TextBox _nameTextBox;
        private Button _newButton;
        private Button _continueButton;
        private Label _continueContext;
        private Label _continuePreview;
        private ListView _sessionList;
        private Label _helperHeader;
        private ListView _helperList;
        private Button _resumeSelectedButton;
        private CheckBox _dontAskCheckBox;
        private Button _cancelButton;
#pragma warning restore CA2213

        // Not controls: disposed in Dispose below.
        private ToolTip _toolTip;
        private ImageList _rowHeightImages;
        private Font _listFont;
        private Font _listBoldFont;
        private Font _continueContextBoldFont;

        // Item 8: one per list header, painting the strip right of the last column (see HeaderRemainderPainter).
        private readonly List<HeaderRemainderPainter> _headerPainters = new List<HeaderRemainderPainter>();

        private readonly LaunchSessionChoiceModel _model;
        private readonly string _newSessionName;
        private readonly DateTime _nowUtc;
        private readonly LaunchSessionChoice _defaultChoice;
        private bool _settingNameProgrammatically;
        private bool _nameEdited;
        private bool _fittingColumns;

        // "Also resume helpers" (task 7d20e94c item 5): the ticks, and the session whose helpers the
        // list shows now (the selected row, else the Continue last candidate).
        private readonly HelperTickState _helperTicks = new HelperTickState();
        private LaunchSessionCandidate _helperTarget;

        // Theme-derived colours for the owner-drawn list (system colours when no theme is given).
        private Color _listBack = SystemColors.Window;
        private Color _listFore = SystemColors.WindowText;
        private Color _selectionBack = SystemColors.Highlight;
        private Color _selectionFore = SystemColors.HighlightText;
        private Color _headerBack = SystemColors.Control;
        private Color _headerFore = SystemColors.ControlText;
        private Color _gridLine = SystemColors.ControlDark;
        private bool _isDark;

        public LaunchSessionDialog(string projectName, LaunchSessionChoiceModel model, string newSessionName, TerminalTheme theme, DateTime nowUtc,
            LaunchSessionChoice defaultChoice = LaunchSessionChoice.NewSession)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _newSessionName = newSessionName;
            _nowUtc = nowUtc;
            // Continue last can be the default only when there is something to continue.
            _defaultChoice = defaultChoice == LaunchSessionChoice.ContinueLast && model.ContinueCandidate != null
                ? LaunchSessionChoice.ContinueLast
                : LaunchSessionChoice.NewSession;

            Text = string.IsNullOrWhiteSpace(projectName) ? "Launch project" : $"Launch {projectName}";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            SizeGripStyle = SizeGripStyle.Show;

            BuildLayout();
            ApplyTheme(theme);
        }

        /// <summary>What the user chose. Meaningful only after <see cref="DialogResult.OK"/>.</summary>
        public LaunchSessionChoice Choice { get; private set; }

        /// <summary>The session to resume; null for <see cref="LaunchSessionChoice.NewSession"/>.</summary>
        public LaunchSessionCandidate SelectedCandidate { get; private set; }

        /// <summary>The name to launch under, before the broker uniquifies it.</summary>
        public string ChosenName { get; private set; }

        /// <summary>True when the user typed a name rather than accepting the prefill.</summary>
        public bool NameWasEdited { get; private set; }

        /// <summary>The launch mode to persist for the project, or null for no change.</summary>
        public ProjectLaunchMode? ModeToPersist { get; private set; }

        /// <summary>
        /// The helpers to resume with the chosen session (task 7d20e94c item 5): the ticked helpers of
        /// <see cref="SelectedCandidate"/>, all of them for a session whose list the user never changed.
        /// Empty for a new session. Meaningful only after <see cref="DialogResult.OK"/>.
        /// </summary>
        public IReadOnlyList<OpenHelper> HelpersToResume { get; private set; } = Array.Empty<OpenHelper>();

        private void BuildLayout()
        {
            const int left = 16;
            const int width = 1040;
            const int listHeight = 300;
            const int helperListHeight = 132;
            const int helperSectionHeight = 24 + helperListHeight + 10;
            int y = 16;
            _toolTip = new ToolTip();

            // The final size is set BEFORE any control is added: anchors record their distance to the
            // edges at add time, so adding first and resizing after would move every anchored control.
            var listTop = y + 40 + 40 + 44 + 24;
            var bottomRowTop = listTop + listHeight + 10 + helperSectionHeight;
            ClientSize = new Size(width + (2 * left), bottomRowTop + 46);
            MinimumSize = new Size(760, 560);

            Controls.Add(new Label
            {
                Text = "Agent name:",
                Location = new Point(left, y + 4),
                Size = new Size(100, 20),
                Font = new Font("Segoe UI", BaseFontSize, FontStyle.Bold),
            });
            _nameTextBox = new TextBox
            {
                Location = new Point(left + 104, y),
                Size = new Size(260, 26),
                Font = new Font("Segoe UI", BaseFontSize),
            };
            _nameTextBox.TextChanged += (s, e) =>
            {
                if (!_settingNameProgrammatically) _nameEdited = true;
            };
            Controls.Add(_nameTextBox);
            _toolTip.SetToolTip(_nameTextBox,
                "The MultiTerminal name the pane registers under. Prefilled with the chosen session's original name; " +
                "if another pane holds it, the name comes back with a suffix.");
            y += 40;

            // --- New session ---
            _newButton = new Button
            {
                Text = "New session",
                Location = new Point(left, y),
                Size = new Size(140, 30),
                Font = new Font("Segoe UI", BaseFontSize),
            };
            _newButton.Click += (s, e) => Finish(LaunchSessionChoice.NewSession, null);
            Controls.Add(_newButton);
            Controls.Add(new Label
            {
                Text = "Start a fresh Claude session.",
                Location = new Point(left + 150, y + 6),
                Size = new Size(width - 150, 20),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            });
            y += 40;

            // --- Continue last: context used, then agent / age / summary ---
            var cont = _model.ContinueCandidate;
            _continueButton = new Button
            {
                Text = "Continue last",
                Location = new Point(left, y),
                Size = new Size(140, 30),
                Font = new Font("Segoe UI", BaseFontSize),
                Enabled = cont != null,
            };
            _continueButton.Click += (s, e) => Finish(LaunchSessionChoice.ContinueLast, _model.ContinueCandidate);
            Controls.Add(_continueButton);
            // The context part is an AutoSize label (its own colour, maybe bold) and the preview
            // starts right after it (LayoutContinueRow), so the two read as one line. A fixed-width
            // context label left a wide gap before the agent name (Owner's live test).
            _continueContext = new Label
            {
                Text = cont == null ? string.Empty : "Context " + ContextText(cont),
                Location = new Point(left + 150, y + 6),
                AutoSize = true,
                Visible = cont != null,
            };
            Controls.Add(_continueContext);
            _continuePreview = new Label
            {
                Text = cont == null ? "No resumable session." : "·  " + FormatPreview(cont),
                Location = new Point(left + 150, y + 6),
                Size = new Size(width - 150, 20),
                AutoEllipsis = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            Controls.Add(_continuePreview);
            _continueContext.SizeChanged += (s, e) => LayoutContinueRow();
            LayoutContinueRow();
            if (cont != null)
            {
                _toolTip.SetToolTip(_continuePreview, FormatTooltip(cont));
                _toolTip.SetToolTip(_continueContext, ContextTooltip(cont));
            }
            y += 44;

            // --- Pick a past session ---
            Controls.Add(new Label
            {
                Text = "Or resume a past session (helpers and worktree sessions included):",
                Location = new Point(left, y),
                Size = new Size(width, 20),
                Font = new Font("Segoe UI", BaseFontSize, FontStyle.Bold),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            });
            y += 24;

            _listFont = new Font("Segoe UI", ListFontSize);
            _listBoldFont = new Font("Segoe UI", ListFontSize, FontStyle.Bold);
            var rowHeight = (int)Math.Round(TextRenderer.MeasureText("Ag", _listFont).Height * RowHeightFactor);
            // A details ListView's row height follows its small image list: a 1px-wide transparent
            // image of the wanted height is the standard way to get taller rows.
            _rowHeightImages = new ImageList { ImageSize = new Size(1, Math.Min(256, Math.Max(16, rowHeight))) };

            _sessionList = new ListView
            {
                Location = new Point(left, y),
                Size = new Size(width, listHeight),
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                OwnerDraw = true,
                ShowItemToolTips = true,
                BorderStyle = BorderStyle.FixedSingle,
                Font = _listFont,
                SmallImageList = _rowHeightImages,
                Activation = ItemActivation.Standard,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            };
            _sessionList.Columns.Add("Agent", 210);
            _sessionList.Columns.Add("Where", 190);
            _sessionList.Columns.Add("Age", 105);
            _sessionList.Columns.Add("Context", 180);
            _sessionList.Columns.Add("Summary", MinSummaryWidth);

            foreach (var candidate in _model.Candidates)
            {
                var e = candidate.Entry;
                var item = new ListViewItem(AgentCellText(candidate))
                {
                    Tag = candidate,
                    ToolTipText = FormatTooltip(candidate),
                };
                item.SubItems.Add(string.IsNullOrEmpty(e.WorktreeId) ? "project folder" : $"worktree {SessionCatalog.ToDisplayText(e.WorktreeId)}");
                item.SubItems.Add(LaunchSessionChoiceModel.FormatRelativeAge(e.LastActivityUtc, _nowUtc));
                item.SubItems.Add(ContextText(candidate));
                item.SubItems.Add(e.Summary ?? "(no summary)");
                _sessionList.Items.Add(item);
            }

            // Age is sized from the list font, not a fixed pixel width: at the larger font a fixed 105px
            // clipped "15 d ago" to "15 d a...". Measured with the flags the cell is drawn with; the
            // padding is the cell's own on both sides plus a few pixels for the header's bold font.
            const TextFormatFlags measureFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            var ageTexts = LaunchSessionChoiceModel.AgeWidthSamples()
                .Concat(_sessionList.Items.Cast<ListViewItem>().Select(i => i.SubItems[ColAge].Text));
            var ageWidth = LaunchSessionChoiceModel.FitColumnWidth(
                ageTexts, t => TextRenderer.MeasureText(t, _listFont, Size.Empty, measureFlags).Width, (2 * CellPadding) + 6,
                TextRenderer.MeasureText("Age", _listBoldFont, Size.Empty, measureFlags).Width + (2 * CellPadding) + 6);
            _sessionList.Columns[ColAge].Width = ageWidth;

            _sessionList.DrawColumnHeader += OnDrawColumnHeader;
            _sessionList.DrawItem += (s, e) => { /* every cell is drawn in DrawSubItem */ };
            _sessionList.DrawSubItem += OnDrawSubItem;
            _sessionList.SelectedIndexChanged += (s, e) =>
            {
                var candidate = SelectedListCandidate();
                _resumeSelectedButton.Enabled = candidate != null;
                if (candidate != null && !_nameEdited)
                    SetNameProgrammatically(LaunchSessionChoiceModel.DefaultNameFor(LaunchSessionChoice.PickedSession, candidate, _newSessionName));
                _sessionList.Invalidate(); // owner-drawn rows: repaint the old and new selection fully
                RefreshHelperList();
            };
            // ONE path per gesture (review of 702e4fa): a double-click resumes through MouseDoubleClick,
            // and Enter through the AcceptButton swap below. ItemActivate is deliberately NOT handled:
            // it also fires for Enter, so with both wired one keypress could reach Finish twice and,
            // after the user answered No to the "may be open elsewhere" confirm, ask again.
            _sessionList.MouseDoubleClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left && _sessionList.HitTest(e.Location).Item != null)
                    ResumeSelected();
            };
            // While the list has focus, Enter means "resume selected", not the dialog's default button.
            _sessionList.Enter += (s, e) => AcceptButton = _resumeSelectedButton;
            _sessionList.Leave += (s, e) => AcceptButton = DefaultButton;
            _sessionList.Resize += (s, e) => FitSummaryColumn();
            _sessionList.ColumnWidthChanged += (s, e) =>
            {
                if (e.ColumnIndex != ColSummary) FitSummaryColumn();
            };
            Controls.Add(_sessionList);
            PaintHeaderRemainder(_sessionList);
            y += listHeight + 10;

            // --- Also resume helpers (task 7d20e94c item 5) ---
            _helperHeader = new Label
            {
                Location = new Point(left, y),
                Size = new Size(width, 20),
                AutoEllipsis = true,
                Font = new Font("Segoe UI", BaseFontSize, FontStyle.Bold),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            };
            _toolTip.SetToolTip(_helperHeader,
                "Helpers this session had open when it last ended (closed helpers are not listed).\n" +
                "Ticked helpers reopen with it, each in its own folder under its own name.\n" +
                "Click a row or press Space to tick or untick it. New session never reopens helpers.");
            Controls.Add(_helperHeader);
            y += 24;

            _helperList = new ListView
            {
                Location = new Point(left, y),
                Size = new Size(width, helperListHeight),
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
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            };
            _helperList.Columns.Add("Helper", 300);
            _helperList.Columns.Add("Where", 260);
            _helperList.Columns.Add("Started", 160);
            _helperList.DrawColumnHeader += OnDrawColumnHeader;
            _helperList.DrawItem += (s, e) => { /* every cell is drawn in DrawSubItem */ };
            _helperList.DrawSubItem += OnDrawHelperSubItem;
            _helperList.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                    ToggleHelper(_helperList.HitTest(e.Location).Item);
            };
            _helperList.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Space && _helperList.SelectedItems.Count == 1)
                {
                    ToggleHelper(_helperList.SelectedItems[0]);
                    e.Handled = true;
                }
            };
            _helperList.SelectedIndexChanged += (s, e) => _helperList.Invalidate();
            _helperList.Enter += (s, e) => _helperList.Invalidate();
            _helperList.Leave += (s, e) => _helperList.Invalidate();
            Controls.Add(_helperList);
            PaintHeaderRemainder(_helperList);
            y += helperListHeight + 10;

            // --- Bottom row ---
            _dontAskCheckBox = new CheckBox
            {
                Text = "Don't ask again for this project",
                Location = new Point(left, y + 6),
                Size = new Size(260, 22),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            };
            _toolTip.SetToolTip(_dontAskCheckBox,
                "With New session: always start new for this project.\n" +
                "With Continue last: always resume the newest session.\n" +
                "With a session picked from the list: no change; you will be asked next time\n" +
                "(there is no \"always resume this one session\" setting).\n" +
                "Hold Shift while launching to start new without being asked.");
            Controls.Add(_dontAskCheckBox);

            _resumeSelectedButton = new Button
            {
                Text = "Resume selected",
                Location = new Point(left + width - 228, y),
                Size = new Size(140, 30),
                Font = new Font("Segoe UI", BaseFontSize),
                Enabled = false,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            };
            _resumeSelectedButton.Click += (s, e) => ResumeSelected();
            Controls.Add(_resumeSelectedButton);

            _cancelButton = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(left + width - 80, y),
                Size = new Size(80, 30),
                Font = new Font("Segoe UI", BaseFontSize),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            };
            Controls.Add(_cancelButton);

            AcceptButton = DefaultButton;
            CancelButton = _cancelButton;
            FitSummaryColumn();
            RefreshHelperList();

            // The name box shows the name the default (Enter) action launches under: the continue
            // candidate's agent when Continue last is the default (pipeline Run 1), else the new name.
            // Focusing either button re-prefills for that button, unless the user has typed a name.
            SetNameProgrammatically(_model.InitialNameFor(_defaultChoice, _newSessionName));
            _newButton.Enter += (s, e) => PrefillFor(LaunchSessionChoice.NewSession);
            _continueButton.Enter += (s, e) => PrefillFor(LaunchSessionChoice.ContinueLast);
        }

        // Puts the preview right after the context label, keeping its right edge (it is anchored right).
        private void LayoutContinueRow()
        {
            // Not _continueContext.Visible: that reads false until the form itself is shown.
            if (_continueContext == null || _continuePreview == null || _model.ContinueCandidate == null)
                return;
            var newLeft = _continueContext.Right + 4;
            var right = _continuePreview.Right;
            _continuePreview.Bounds = new Rectangle(newLeft, _continuePreview.Top, Math.Max(40, right - newLeft), _continuePreview.Height);
        }

        private Button DefaultButton => _defaultChoice == LaunchSessionChoice.ContinueLast ? _continueButton : _newButton;

        private LaunchSessionCandidate SelectedListCandidate() =>
            _sessionList.SelectedItems.Count == 1 ? _sessionList.SelectedItems[0].Tag as LaunchSessionCandidate : null;

        // Summary takes whatever width the other four columns leave.
        private void FitSummaryColumn()
        {
            if (_fittingColumns || _sessionList == null || _sessionList.Columns.Count <= ColSummary)
                return;
            _fittingColumns = true;
            try
            {
                var others = 0;
                for (var i = 0; i < ColSummary; i++)
                    others += _sessionList.Columns[i].Width;
                var width = Math.Max(MinSummaryWidth, _sessionList.ClientSize.Width - others - 1);
                if (_sessionList.Columns[ColSummary].Width != width)
                    _sessionList.Columns[ColSummary].Width = width;
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

        /// <summary>
        /// Item 8: owner drawing covers the column headers only; the header strip right of the last
        /// column is painted by the native header control, which left a white block on the dark theme
        /// (the helper list's columns do not fill its width, and a vertical scroll bar can open the same
        /// gap in the session list). The header is subclassed so that strip is painted in the header
        /// colours after every native paint, in both themes.
        /// </summary>
        private void PaintHeaderRemainder(ListView list)
        {
            void Attach()
            {
                var header = HeaderRemainderPainter.SendMessage(list.Handle, HeaderRemainderPainter.LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
                if (header == IntPtr.Zero)
                    return;
                var painter = new HeaderRemainderPainter(list, this);
                painter.AssignHandle(header);
                _headerPainters.Add(painter);
                list.HandleDestroyed += (s, e) =>
                {
                    painter.ReleaseHandle();
                    _headerPainters.Remove(painter);
                };
            }

            if (list.IsHandleCreated)
                Attach();
            else
                list.HandleCreated += (s, e) => Attach();
        }

        /// <summary>Paints a ListView header's strip right of its last column; see <see cref="PaintHeaderRemainder"/>.</summary>
        private sealed class HeaderRemainderPainter : NativeWindow
        {
            internal const int LVM_GETHEADER = 0x101F;
            private const int HDM_GETITEMRECT = 0x1207;
            private const int WM_PAINT = 0x000F;

            private readonly ListView _list;
            private readonly LaunchSessionDialog _owner;

            public HeaderRemainderPainter(ListView list, LaunchSessionDialog owner)
            {
                _list = list;
                _owner = owner;
            }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if (m.Msg == WM_PAINT)
                    PaintRemainder();
            }

            private void PaintRemainder()
            {
                if (Handle == IntPtr.Zero || !GetClientRect(Handle, out var client))
                    return;

                // Columns cannot be reordered here, so the last index is the last column on screen.
                int right = 0;
                int count = _list.Columns.Count;
                if (count > 0)
                {
                    var item = default(RECT);
                    if (SendMessage(Handle, HDM_GETITEMRECT, (IntPtr)(count - 1), ref item) != IntPtr.Zero)
                        right = item.Right;
                }

                if (right >= client.Right || client.Bottom <= client.Top)
                    return;

                var strip = Rectangle.FromLTRB(right, client.Top, client.Right, client.Bottom);
                using var g = Graphics.FromHwnd(Handle);
                using (var back = new SolidBrush(_owner._headerBack))
                    g.FillRectangle(back, strip);
                using var pen = new Pen(_owner._gridLine);
                g.DrawLine(pen, strip.Left, strip.Bottom - 1, strip.Right, strip.Bottom - 1);
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct RECT
            {
                public int Left;
                public int Top;
                public int Right;
                public int Bottom;
            }

            [DllImport("user32.dll")]
            internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll")]
            private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref RECT lParam);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
        }

        private void OnDrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            if (e.Item?.Tag is not LaunchSessionCandidate candidate || e.SubItem == null)
                return;

            // For column 0 some WinForms versions report the whole row as the bounds; clip to the column.
            var bounds = e.ColumnIndex == 0
                ? new Rectangle(e.Bounds.X, e.Bounds.Y, _sessionList.Columns[0].Width, e.Bounds.Height)
                : e.Bounds;
            var selected = e.Item.Selected;
            using (var back = new SolidBrush(selected ? _selectionBack : _listBack))
                e.Graphics.FillRectangle(back, bounds);

            var fore = selected ? _selectionFore : _listFore;
            var font = _listFont;
            if (e.ColumnIndex == ColContext)
            {
                var level = LaunchSessionChoiceModel.ContextLevelOf(candidate.Entry.ContextTokens, candidate.Entry.ContextWindow);
                fore = ContextColor(level, fore);
                if (level == ContextLevel.Critical)
                    font = _listBoldFont;
            }
            else if (e.ColumnIndex == ColAgent && candidate.MayBeOpenElsewhere)
            {
                fore = ContextColor(ContextLevel.Warning, fore);
            }

            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            flags |= e.ColumnIndex == ColContext || e.ColumnIndex == ColAge ? TextFormatFlags.Right : TextFormatFlags.Left;
            var textRect = new Rectangle(bounds.X + CellPadding, bounds.Y, Math.Max(0, bounds.Width - (2 * CellPadding)), bounds.Height);
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, font, textRect, fore, flags);

            // The focus rectangle spans the whole row. It is drawn with the LAST column, after every
            // other cell of the row has been painted, from the first column's left edge to this one's
            // right edge (Item.Bounds is not used: like column 0's bounds, it varies by version).
            if (e.ColumnIndex == ColSummary && e.Item.Focused && _sessionList.Focused)
            {
                var rowLeft = bounds.X;
                for (var i = 0; i < ColSummary; i++)
                    rowLeft -= _sessionList.Columns[i].Width;
                var row = Rectangle.FromLTRB(rowLeft, bounds.Top, bounds.Right, bounds.Bottom);
                ControlPaint.DrawFocusRectangle(e.Graphics, row, fore, selected ? _selectionBack : _listBack);
            }
        }

        // Warning and critical colours readable on both the theme's list background and its selection.
        private Color ContextColor(ContextLevel level, Color normal) => level switch
        {
            ContextLevel.Critical => _isDark ? Color.FromArgb(255, 110, 100) : Color.FromArgb(190, 20, 20),
            ContextLevel.Warning => _isDark ? Color.FromArgb(240, 190, 80) : Color.FromArgb(170, 95, 0),
            _ => normal,
        };

        private void PrefillFor(LaunchSessionChoice choice)
        {
            if (!_nameEdited)
                SetNameProgrammatically(_model.InitialNameFor(choice, _newSessionName));
        }

        private void SetNameProgrammatically(string name)
        {
            _settingNameProgrammatically = true;
            try { _nameTextBox.Text = name ?? string.Empty; }
            finally { _settingNameProgrammatically = false; }
        }

        private void ResumeSelected()
        {
            var candidate = SelectedListCandidate();
            if (candidate != null)
                Finish(LaunchSessionChoice.PickedSession, candidate);
        }

        private void Finish(LaunchSessionChoice choice, LaunchSessionCandidate candidate)
        {
            if (DialogResult == DialogResult.OK)
                return; // already finished
            if (choice != LaunchSessionChoice.NewSession)
            {
                if (candidate == null)
                    return;
                if (candidate.MayBeOpenElsewhere)
                {
                    var answer = MessageBox.Show(this,
                        $"{candidate.MayBeOpenReason}\n\nResuming a session that is still open makes two Claude processes write one transcript, which corrupts it.\n\nResume it anyway?",
                        "Session may be open elsewhere",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);
                    if (answer != DialogResult.Yes)
                        return;
                }
            }

            Choice = choice;
            SelectedCandidate = choice == LaunchSessionChoice.NewSession ? null : candidate;
            HelpersToResume = _helperTicks.TickedFor(_model, SelectedCandidate);
            NameWasEdited = _nameEdited && !string.IsNullOrWhiteSpace(_nameTextBox.Text);
            ChosenName = LaunchSessionChoiceModel.ResolveName(
                choice, SelectedCandidate, _newSessionName, NameWasEdited ? _nameTextBox.Text : null);
            ModeToPersist = LaunchSessionChoiceModel.ModeToPersist(choice, _dontAskCheckBox.Checked);
            DialogResult = DialogResult.OK;
        }

        // Shows the helpers of the selected row, else of the Continue last candidate. The lookup is the
        // model's (cached per session), made here on a selection change, never while painting.
        private void RefreshHelperList()
        {
            if (_helperList == null)
                return;
            var listSelected = SelectedListCandidate();
            _helperTarget = listSelected ?? _model.ContinueCandidate;
            var helpers = _model.HelpersFor(_helperTarget);

            _helperHeader.Text = _helperTarget == null
                ? "Also resume helpers: —"
                : $"Also resume helpers of {AgentLabel(_helperTarget)} ({(listSelected != null ? "selected session" : "Continue last")}):";

            _helperList.BeginUpdate();
            try
            {
                _helperList.Items.Clear();
                foreach (var h in helpers)
                {
                    var name = SessionCatalog.ToDisplayText(h.TerminalName);
                    var item = new ListViewItem(string.IsNullOrEmpty(name) ? "(unnamed)" : name)
                    {
                        Tag = h,
                        ToolTipText = $"{h.SessionId}\n{SessionCatalog.ToDisplayText(h.Cwd)}",
                    };
                    item.SubItems.Add(LaunchSessionChoiceModel.HelperWhere(h.Cwd));
                    item.SubItems.Add(LaunchSessionChoiceModel.FormatRelativeAge(h.LastStartedAt, _nowUtc));
                    _helperList.Items.Add(item);
                }

                if (helpers.Count == 0)
                {
                    // One inert row, drawn dimmed: the list reads as empty without being blank.
                    var none = new ListViewItem("—") { Tag = null };
                    none.SubItems.Add(string.Empty);
                    none.SubItems.Add(string.Empty);
                    _helperList.Items.Add(none);
                }
            }
            finally
            {
                _helperList.EndUpdate();
            }
        }

        private void ToggleHelper(ListViewItem item)
        {
            if (item?.Tag is not OpenHelper helper || _helperTarget == null)
                return;
            _helperTicks.Toggle(_helperTarget.Entry.SessionId, helper.SessionId);
            _helperList.Invalidate();
        }

        private void OnDrawHelperSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            if (e.Item == null || e.SubItem == null)
                return;
            var helper = e.Item.Tag as OpenHelper;
            var bounds = e.ColumnIndex == 0
                ? new Rectangle(e.Bounds.X, e.Bounds.Y, _helperList.Columns[0].Width, e.Bounds.Height)
                : e.Bounds;
            var selected = helper != null && e.Item.Selected && _helperList.Focused;
            using (var back = new SolidBrush(selected ? _selectionBack : _listBack))
                e.Graphics.FillRectangle(back, bounds);

            // The "—" row is the disabled state: dimmed like a disabled button, still readable.
            var fore = helper == null ? Blend(_listFore, _listBack, 0.45) : selected ? _selectionFore : _listFore;
            var textLeft = bounds.X + CellPadding;
            if (e.ColumnIndex == 0 && helper != null)
            {
                var box = Math.Min(14, Math.Max(8, bounds.Height - 10));
                var boxRect = new Rectangle(textLeft, bounds.Y + ((bounds.Height - box) / 2), box, box);
                using (var pen = new Pen(selected ? _selectionFore : _gridLine))
                    e.Graphics.DrawRectangle(pen, boxRect);
                if (_helperTarget != null && _helperTicks.IsTicked(_helperTarget.Entry.SessionId, helper.SessionId))
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

            var textRect = new Rectangle(textLeft, bounds.Y, Math.Max(0, bounds.Right - CellPadding - textLeft), bounds.Height);
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, _listFont, textRect, fore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        private static string ContextText(LaunchSessionCandidate c) =>
            LaunchSessionChoiceModel.FormatContext(c.Entry.ContextTokens, c.Entry.ContextWindow);

        private static string ContextTooltip(LaunchSessionCandidate c)
        {
            var e = c.Entry;
            if (!e.ContextTokens.HasValue)
                return "No token usage was found near the end of this session's transcript.";
            var tip = $"{e.ContextTokens.Value:N0} tokens in context at the session's last reply";
            return e.ContextWindow.HasValue
                ? tip + $", of a {e.ContextWindow.Value:N0}-token window."
                : tip + " (context window unknown for this model).";
        }

        private string FormatPreview(LaunchSessionCandidate c)
        {
            var e = c.Entry;
            var parts = new[]
            {
                c.MayBeOpenElsewhere ? "⚠ may be open elsewhere" : null,
                AgentLabel(c),
                LaunchSessionChoiceModel.FormatRelativeAge(e.LastActivityUtc, _nowUtc),
                e.Summary,
            };
            return string.Join("  ·  ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        private static string AgentCellText(LaunchSessionCandidate c) =>
            (c.MayBeOpenElsewhere ? "⚠ " : string.Empty) + AgentLabel(c);

        private static string AgentLabel(LaunchSessionCandidate c)
        {
            var name = string.IsNullOrWhiteSpace(c.Entry.AgentName) ? "(unknown agent)" : c.Entry.AgentName;
            return c.Entry.IsHelper ? $"{name} [Helper]" : name;
        }

        // Internal for tests. The cwd is transcript text: the tooltip shows a sanitised COPY; the
        // entry's Cwd, which the resume is launched from, is never altered.
        internal static string FormatTooltip(LaunchSessionCandidate c)
        {
            var tip = $"{c.Entry.SessionId}\n{SessionCatalog.ToDisplayText(c.Entry.Cwd)}\nContext: {ContextText(c)}";
            if (!string.IsNullOrEmpty(c.Entry.Summary))
                tip += "\n" + c.Entry.Summary;
            if (c.MayBeOpenElsewhere)
                tip += "\n\n" + c.MayBeOpenReason;
            return tip;
        }

        private void ApplyTheme(TerminalTheme theme)
        {
            if (theme != null)
            {
                BackColor = theme.ToolbarBackground;
                ForeColor = theme.ToolbarForeground;
                _isDark = theme.IsDark;
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
                    else if (ctrl is CheckBox cb)
                    {
                        cb.ForeColor = theme.ToolbarForeground;
                    }
                    else if (ctrl is ListView lv)
                    {
                        lv.BackColor = theme.Background;
                        lv.ForeColor = theme.Foreground;
                    }
                    else if (ctrl is TextBox tb)
                    {
                        tb.BackColor = theme.Background;
                        tb.ForeColor = theme.Foreground;
                    }
                    else if (ctrl is Button btn)
                    {
                        btn.BackColor = theme.TabInactiveBackground;
                        btn.ForeColor = theme.ToolbarForeground;
                        btn.FlatStyle = FlatStyle.Flat;
                        btn.FlatAppearance.BorderColor = theme.StatusForeground;
                        btn.Paint += PaintDisabledButton;
                    }
                }
            }

            // After the theme loop, which paints every label with the toolbar colour.
            var cont = _model.ContinueCandidate;
            if (cont != null)
            {
                var level = LaunchSessionChoiceModel.ContextLevelOf(cont.Entry.ContextTokens, cont.Entry.ContextWindow);
                _continueContext.ForeColor = ContextColor(level, _continueContext.ForeColor);
                if (level == ContextLevel.Critical)
                {
                    _continueContextBoldFont = new Font("Segoe UI", BaseFontSize, FontStyle.Bold);
                    _continueContext.Font = _continueContextBoldFont;
                }
            }
        }

        // A disabled FlatStyle button draws its text in a system grey that vanishes on the dark theme
        // (Resume selected read as an empty black box). Only the DISABLED state is painted here, over
        // the native drawing: dim text and a dim but visible border. Enabled buttons keep the native
        // flat drawing at full strength, focus rectangle and all; a disabled button cannot hold focus,
        // so no focus cue is lost. Hooked only when a theme is applied (system drawing is fine without).
        private void PaintDisabledButton(object sender, PaintEventArgs e)
        {
            if (sender is not Button btn || btn.Enabled)
                return;
            var back = btn.BackColor;
            var text = Blend(btn.ForeColor, back, 0.45);
            var border = Blend(btn.FlatAppearance.BorderColor, back, 0.4);
            var rect = btn.ClientRectangle;
            using (var brush = new SolidBrush(back))
                e.Graphics.FillRectangle(brush, rect);
            using (var pen = new Pen(border))
                e.Graphics.DrawRectangle(pen, 0, 0, rect.Width - 1, rect.Height - 1);
            TextRenderer.DrawText(e.Graphics, btn.Text, btn.Font, rect, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.HidePrefix);
        }

        /// <summary>
        /// <paramref name="from"/> moved <paramref name="towards"/> by <paramref name="amount"/> (0 = unchanged,
        /// 1 = <paramref name="towards"/>). Internal for tests.
        /// </summary>
        internal static Color Blend(Color from, Color towards, double amount)
        {
            amount = Math.Clamp(amount, 0, 1);
            int Mix(int a, int b) => (int)Math.Round(a + ((b - a) * amount));
            return Color.FromArgb(Mix(from.R, towards.R), Mix(from.G, towards.G), Mix(from.B, towards.B));
        }

        protected override void Dispose(bool disposing)
        {
            // Controls first (the list still references the fonts and image list), then what they used.
            base.Dispose(disposing);
            if (disposing)
            {
                _toolTip?.Dispose();
                _rowHeightImages?.Dispose();
                _listFont?.Dispose();
                _listBoldFont?.Dispose();
                _continueContextBoldFont?.Dispose();
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            FitSummaryColumn();
            DefaultButton.Focus();
        }
    }
}
