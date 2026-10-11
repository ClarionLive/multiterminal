using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using MultiTerminal.Models;
using MultiTerminal.Services;
using MultiTerminal.Terminal;

namespace MultiTerminal.Dialogs
{
    /// <summary>
    /// The "Claude profile" question for "Launch as..." (ticket c8ee0eec item 8). "Launch as..." is a
    /// context-menu click with no dialog of its own, so this is asked on its own, and only when there
    /// is a real choice: more than one profile exists and no project owns the folder (the caller
    /// checks both; a folder a project owns always uses that project's profile and is never asked
    /// about). The pane's current profile is preselected, so Enter keeps it.
    /// <para>The list is built by <see cref="ClaudeProfileChoices.Build"/>, as Edit Project's is. A
    /// pane whose current profile is no longer configured on this machine therefore shows that name
    /// as its own selected "(not configured on this machine)" entry, which cannot be launched: the
    /// Launch button stays disabled until a configured profile is picked. Default is never
    /// preselected in its place, so Enter cannot move the pane to Default (item 34).</para>
    /// </summary>
    public class IdentityPickerProfileDialog : Form
    {
        // Controls — added to Controls collection and auto-disposed by base Form.Dispose().
#pragma warning disable CA2213
        private readonly ComboBox _profileCombo;
        private readonly Label _note;
        private readonly Button _okButton;
#pragma warning restore CA2213
        private readonly Func<string, string> _profileHint;

        /// <summary>The profile name picked ("Default" included). Set when the dialog returns OK.</summary>
        public string SelectedClaudeProfileName { get; private set; }

        /// <param name="identityName">The identity being launched, for the prompt.</param>
        /// <param name="folder">The folder it will run in, for the prompt.</param>
        /// <param name="claudeProfiles">Every configured profile (<see cref="ClaudeProfileResolver.GetAllProfiles()"/>).</param>
        /// <param name="paneProfileName">The pane's current profile; null or blank = Default.</param>
        /// <param name="profileHint">Profile name → a one-line note to show under the choice, or null.</param>
        public IdentityPickerProfileDialog(
            string identityName,
            string folder,
            IReadOnlyList<ClaudeProfile> claudeProfiles,
            string paneProfileName,
            TerminalTheme theme,
            Func<string, string> profileHint = null)
        {
            _profileHint = profileHint;

            Text = "Launch as";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(460, 190);

            var prompt = new Label
            {
                Text = $"Claude profile for \"{identityName}\":",
                Location = new Point(16, 16),
                Size = new Size(428, 20),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };
            Controls.Add(prompt);

            var folderLabel = new Label
            {
                Text = $"This folder belongs to no project: {folder}",
                Location = new Point(16, 40),
                Size = new Size(428, 20),
                AutoEllipsis = true,
                Font = new Font("Segoe UI", 8.5f)
            };
            Controls.Add(folderLabel);

            _profileCombo = new ComboBox
            {
                Location = new Point(16, 68),
                Size = new Size(260, 26),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 9.5f)
            };
            var choices = ClaudeProfileChoices.Build(claudeProfiles, paneProfileName, out int selectedIndex);
            foreach (var choice in choices)
                _profileCombo.Items.Add(choice);
            Controls.Add(_profileCombo);

            _note = new Label
            {
                Location = new Point(16, 100),
                Size = new Size(428, 34),
                AutoSize = false,
                Font = new Font("Segoe UI", 8.5f)
            };
            Controls.Add(_note);

            var okButton = new Button
            {
                Text = "Launch",
                Location = new Point(286, 146),
                Size = new Size(80, 30)
            };
            _okButton = okButton;
            okButton.Click += (s, e) =>
            {
                // Checked again here, not only through Enabled: Enter reaches AcceptButton's click.
                var picked = _profileCombo.SelectedItem as ClaudeProfileChoice;
                if (!ClaudeProfileChoices.CanLaunch(picked))
                    return;
                SelectedClaudeProfileName = picked.Name ?? ClaudeProfile.DefaultName;
                DialogResult = DialogResult.OK;
            };
            Controls.Add(okButton);

            var cancelButton = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(372, 146),
                Size = new Size(72, 30)
            };
            Controls.Add(cancelButton);

            AcceptButton = okButton;
            CancelButton = cancelButton;

            _profileCombo.SelectedIndexChanged += (s, e) => OnProfileSelectionChanged();

            // The pane's own profile, even when it is the "(not configured on this machine)" entry:
            // Build put it in the list so that it, and not Default, is what is selected.
            _profileCombo.SelectedIndex = selectedIndex;
            OnProfileSelectionChanged();

            ApplyTheme(theme);
        }

        private void OnProfileSelectionChanged()
        {
            var picked = _profileCombo.SelectedItem as ClaudeProfileChoice;
            _okButton.Enabled = ClaudeProfileChoices.CanLaunch(picked);
            _note.Text = picked == null
                ? ""
                : ClaudeProfileChoices.DescribeUnlaunchable(picked)
                    ?? _profileHint?.Invoke(picked.Name ?? ClaudeProfile.DefaultName)
                    ?? "";
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
                else if (ctrl is ComboBox cb)
                {
                    cb.BackColor = theme.Background;
                    cb.ForeColor = theme.Foreground;
                    cb.FlatStyle = FlatStyle.Flat;
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
    }
}
