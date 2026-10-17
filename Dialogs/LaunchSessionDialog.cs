using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
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
    /// <see cref="LaunchSessionChoiceModel"/>, which is unit-tested. Cancel (Esc, the close box or the
    /// Cancel button) returns <see cref="DialogResult.Cancel"/> and the caller launches nothing.</para>
    /// </summary>
    public class LaunchSessionDialog : Form
    {
        private const int SummaryListChars = 90;

        // Controls — added to Controls collection and auto-disposed by base Form.Dispose().
#pragma warning disable CA2213
        private TextBox _nameTextBox;
        private Button _newButton;
        private Button _continueButton;
        private Label _continuePreview;
        private ListBox _sessionList;
        private Button _resumeSelectedButton;
        private CheckBox _dontAskCheckBox;
        private Button _cancelButton;
#pragma warning restore CA2213
        private ToolTip _toolTip;   // a component, not a control: disposed in Dispose below

        private readonly LaunchSessionChoiceModel _model;
        private readonly string _newSessionName;
        private readonly DateTime _nowUtc;
        private bool _settingNameProgrammatically;
        private bool _nameEdited;

        public LaunchSessionDialog(string projectName, LaunchSessionChoiceModel model, string newSessionName, TerminalTheme theme, DateTime nowUtc)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _newSessionName = newSessionName;
            _nowUtc = nowUtc;

            Text = string.IsNullOrWhiteSpace(projectName) ? "Launch project" : $"Launch {projectName}";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

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
            const int width = 688;
            int y = 16;
            _toolTip = new ToolTip();

            Controls.Add(new Label
            {
                Text = "Agent name:",
                Location = new Point(left, y + 4),
                Size = new Size(100, 20),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            });
            _nameTextBox = new TextBox
            {
                Location = new Point(left + 104, y),
                Size = new Size(260, 26),
                Font = new Font("Segoe UI", 9.5f),
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
                Font = new Font("Segoe UI", 9.5f),
            };
            _newButton.Click += (s, e) => Finish(LaunchSessionChoice.NewSession, null);
            Controls.Add(_newButton);
            Controls.Add(new Label
            {
                Text = "Start a fresh Claude session.",
                Location = new Point(left + 150, y + 6),
                Size = new Size(width - 150, 20),
            });
            y += 40;

            // --- Continue last ---
            var cont = _model.ContinueCandidate;
            _continueButton = new Button
            {
                Text = "Continue last",
                Location = new Point(left, y),
                Size = new Size(140, 30),
                Font = new Font("Segoe UI", 9.5f),
                Enabled = cont != null,
            };
            _continueButton.Click += (s, e) => Finish(LaunchSessionChoice.ContinueLast, _model.ContinueCandidate);
            Controls.Add(_continueButton);
            _continuePreview = new Label
            {
                Text = cont == null ? "No resumable session." : FormatPreview(cont),
                Location = new Point(left + 150, y + 6),
                Size = new Size(width - 150, 20),
                AutoEllipsis = true,
            };
            Controls.Add(_continuePreview);
            if (cont != null)
                _toolTip.SetToolTip(_continuePreview, FormatTooltip(cont));
            y += 44;

            // --- Pick a past session ---
            Controls.Add(new Label
            {
                Text = "Or resume a past session (helpers and worktree sessions included):",
                Location = new Point(left, y),
                Size = new Size(width, 20),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            });
            y += 24;

            _sessionList = new ListBox
            {
                Location = new Point(left, y),
                Size = new Size(width, 260),
                IntegralHeight = false,
                HorizontalScrollbar = true,
                Font = new Font("Segoe UI", 9.5f),
            };
            foreach (var candidate in _model.Candidates)
                _sessionList.Items.Add(new SessionItem(candidate, FormatListLine(candidate)));
            _sessionList.SelectedIndexChanged += (s, e) =>
            {
                var item = _sessionList.SelectedItem as SessionItem;
                _resumeSelectedButton.Enabled = item != null;
                if (item != null && !_nameEdited)
                    SetNameProgrammatically(LaunchSessionChoiceModel.DefaultNameFor(LaunchSessionChoice.PickedSession, item.Candidate, _newSessionName));
            };
            _sessionList.DoubleClick += (s, e) => ResumeSelected();
            Controls.Add(_sessionList);
            y += 270;

            // --- Bottom row ---
            _dontAskCheckBox = new CheckBox
            {
                Text = "Don't ask again for this project",
                Location = new Point(left, y + 6),
                Size = new Size(260, 22),
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
                Font = new Font("Segoe UI", 9.5f),
                Enabled = false,
            };
            _resumeSelectedButton.Click += (s, e) => ResumeSelected();
            Controls.Add(_resumeSelectedButton);

            _cancelButton = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(left + width - 80, y),
                Size = new Size(80, 30),
                Font = new Font("Segoe UI", 9.5f),
            };
            Controls.Add(_cancelButton);

            AcceptButton = _newButton;
            CancelButton = _cancelButton;
            ClientSize = new Size(width + (2 * left), y + 46);

            SetNameProgrammatically(_newSessionName);
        }

        private void SetNameProgrammatically(string name)
        {
            _settingNameProgrammatically = true;
            try { _nameTextBox.Text = name ?? string.Empty; }
            finally { _settingNameProgrammatically = false; }
        }

        private void ResumeSelected()
        {
            if (_sessionList.SelectedItem is SessionItem item)
                Finish(LaunchSessionChoice.PickedSession, item.Candidate);
        }

        private void Finish(LaunchSessionChoice choice, LaunchSessionCandidate candidate)
        {
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

        private string FormatListLine(LaunchSessionCandidate c)
        {
            var e = c.Entry;
            var summary = e.Summary;
            if (!string.IsNullOrEmpty(summary) && summary.Length > SummaryListChars)
                summary = summary.Substring(0, SummaryListChars - 1).TrimEnd() + "…";
            var parts = new[]
            {
                (c.MayBeOpenElsewhere ? "⚠ " : string.Empty) + AgentLabel(c),
                string.IsNullOrEmpty(e.WorktreeId) ? "project folder" : $"worktree {e.WorktreeId}",
                LaunchSessionChoiceModel.FormatRelativeAge(e.LastActivityUtc, _nowUtc),
                summary ?? "(no summary)",
            };
            return string.Join("  ·  ", parts);
        }

        private static string AgentLabel(LaunchSessionCandidate c)
        {
            var name = string.IsNullOrWhiteSpace(c.Entry.AgentName) ? "(unknown agent)" : c.Entry.AgentName;
            return c.Entry.IsHelper ? $"{name} [Helper]" : name;
        }

        private static string FormatTooltip(LaunchSessionCandidate c)
        {
            var tip = $"{c.Entry.SessionId}\n{c.Entry.Cwd}";
            if (!string.IsNullOrEmpty(c.Entry.Summary))
                tip += "\n" + c.Entry.Summary;
            if (c.MayBeOpenElsewhere)
                tip += "\n\n" + c.MayBeOpenReason;
            return tip;
        }

        private void ApplyTheme(TerminalTheme theme)
        {
            if (theme == null) return;

            BackColor = theme.ToolbarBackground;
            ForeColor = theme.ToolbarForeground;

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
                else if (ctrl is ListBox lb)
                {
                    lb.BackColor = theme.Background;
                    lb.ForeColor = theme.Foreground;
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

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _toolTip?.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _newButton.Focus();
        }

        private sealed class SessionItem
        {
            public SessionItem(LaunchSessionCandidate candidate, string text)
            {
                Candidate = candidate;
                Text = text;
            }

            public LaunchSessionCandidate Candidate { get; }

            private string Text { get; }

            public override string ToString() => Text;
        }
    }
}
