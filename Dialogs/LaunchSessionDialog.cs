using System;
using System.Drawing;
using System.Linq;
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

        private readonly LaunchSessionChoiceModel _model;
        private readonly string _newSessionName;
        private readonly DateTime _nowUtc;
        private readonly LaunchSessionChoice _defaultChoice;
        private bool _settingNameProgrammatically;
        private bool _nameEdited;
        private bool _fittingColumns;

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

        private void BuildLayout()
        {
            const int left = 16;
            const int width = 1040;
            const int listHeight = 380;
            int y = 16;
            _toolTip = new ToolTip();

            // The final size is set BEFORE any control is added: anchors record their distance to the
            // edges at add time, so adding first and resizing after would move every anchored control.
            var listTop = y + 40 + 40 + 44 + 24;
            var bottomRowTop = listTop + listHeight + 10;
            ClientSize = new Size(width + (2 * left), bottomRowTop + 46);
            MinimumSize = new Size(760, 480);

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
            const int contextWidth = 170;
            _continueContext = new Label
            {
                Text = cont == null ? string.Empty : "Context " + ContextText(cont),
                Location = new Point(left + 150, y + 6),
                Size = new Size(contextWidth, 20),
                AutoEllipsis = true,
                Visible = cont != null,
            };
            Controls.Add(_continueContext);
            var previewLeft = cont == null ? left + 150 : left + 150 + contextWidth + 10;
            _continuePreview = new Label
            {
                Text = cont == null ? "No resumable session." : FormatPreview(cont),
                Location = new Point(previewLeft, y + 6),
                Size = new Size(left + width - previewLeft, 20),
                AutoEllipsis = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            Controls.Add(_continuePreview);
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
            y += listHeight + 10;

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

            // The name box shows the name the default (Enter) action launches under: the continue
            // candidate's agent when Continue last is the default (pipeline Run 1), else the new name.
            // Focusing either button re-prefills for that button, unless the user has typed a name.
            SetNameProgrammatically(_model.InitialNameFor(_defaultChoice, _newSessionName));
            _newButton.Enter += (s, e) => PrefillFor(LaunchSessionChoice.NewSession);
            _continueButton.Enter += (s, e) => PrefillFor(LaunchSessionChoice.ContinueLast);
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
            NameWasEdited = _nameEdited && !string.IsNullOrWhiteSpace(_nameTextBox.Text);
            ChosenName = LaunchSessionChoiceModel.ResolveName(
                choice, SelectedCandidate, _newSessionName, NameWasEdited ? _nameTextBox.Text : null);
            ModeToPersist = LaunchSessionChoiceModel.ModeToPersist(choice, _dontAskCheckBox.Checked);
            DialogResult = DialogResult.OK;
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
