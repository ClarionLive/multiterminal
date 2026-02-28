using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using MultiTerminal.Models;
using MultiTerminal.Terminal;

namespace MultiTerminal.Dialogs
{
    /// <summary>
    /// Dialog for creating a new project or editing existing project details.
    /// </summary>
    public partial class EditProjectDialog : Form
    {
        private readonly TerminalTheme _theme;
        private readonly bool _isEditMode;

        // Controls
        private Label nameLabel;
        private TextBox nameTextBox;
        private Label pathLabel;
        private TextBox pathTextBox;
        private Button browseButton;
        private Label descriptionLabel;
        private TextBox descriptionTextBox;
        private Label changeLogLabel;
        private TextBox changeLogTextBox;
        private Button saveButton;
        private Button cancelButton;

        /// <summary>
        /// Gets or sets the project name.
        /// </summary>
        public string ProjectName
        {
            get => nameTextBox.Text?.Trim() ?? string.Empty;
            set => nameTextBox.Text = value;
        }

        /// <summary>
        /// Gets or sets the project path.
        /// </summary>
        public string ProjectPath
        {
            get => pathTextBox.Text?.Trim() ?? string.Empty;
            set => pathTextBox.Text = value;
        }

        /// <summary>
        /// Gets or sets the project description.
        /// </summary>
        public string Description
        {
            get => descriptionTextBox.Text?.Trim() ?? string.Empty;
            set => descriptionTextBox.Text = value;
        }

        /// <summary>
        /// Gets or sets the project changelog.
        /// </summary>
        public string ChangeLog
        {
            get => changeLogTextBox.Text ?? string.Empty;
            set => changeLogTextBox.Text = value;
        }

        /// <summary>
        /// Gets whether the dialog is in edit mode (true) or create mode (false).
        /// </summary>
        public bool IsEditMode => _isEditMode;

        /// <summary>
        /// Creates a new EditProjectDialog for creating a new project.
        /// </summary>
        /// <param name="theme">The terminal theme to apply.</param>
        public EditProjectDialog(TerminalTheme theme)
        {
            _theme = theme ?? TerminalTheme.Dark;
            _isEditMode = false;

            InitializeComponent();
            ApplyTheme();

            Text = "New Project";
            saveButton.Text = "Create";
        }

        /// <summary>
        /// Creates a new EditProjectDialog for editing an existing project.
        /// </summary>
        /// <param name="project">The project to edit.</param>
        /// <param name="theme">The terminal theme to apply.</param>
        public EditProjectDialog(Project project, TerminalTheme theme)
        {
            if (project == null)
                throw new ArgumentNullException(nameof(project));

            _theme = theme ?? TerminalTheme.Dark;
            _isEditMode = true;

            InitializeComponent();
            ApplyTheme();

            Text = "Edit Project";
            saveButton.Text = "Save";

            // Populate fields
            ProjectName = project.Name;
            ProjectPath = project.Path;
            Description = project.Description ?? string.Empty;
            ChangeLog = project.ChangeLog ?? string.Empty;

            // In edit mode, path is read-only
            pathTextBox.ReadOnly = true;
            browseButton.Enabled = false;
        }

        #region Event Handlers

        private void BrowseButton_Click(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select project folder";
                dialog.ShowNewFolderButton = true;

                if (!string.IsNullOrEmpty(pathTextBox.Text) && Directory.Exists(pathTextBox.Text))
                {
                    dialog.SelectedPath = pathTextBox.Text;
                }

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    pathTextBox.Text = dialog.SelectedPath;

                    // Auto-fill name from folder name if empty
                    if (string.IsNullOrWhiteSpace(nameTextBox.Text))
                    {
                        nameTextBox.Text = Path.GetFileName(dialog.SelectedPath);
                    }
                }
            }
        }

        private void SaveButton_Click(object sender, EventArgs e)
        {
            // Validate name
            if (string.IsNullOrWhiteSpace(ProjectName))
            {
                ShowValidationError("Please enter a project name.");
                nameTextBox.Focus();
                return;
            }

            // Validate path
            if (string.IsNullOrWhiteSpace(ProjectPath))
            {
                ShowValidationError("Please select a project path.");
                pathTextBox.Focus();
                return;
            }

            if (!Directory.Exists(ProjectPath))
            {
                ShowValidationError("The specified path does not exist.");
                pathTextBox.Focus();
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }

        private void CancelButton_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }

        private void ShowValidationError(string message)
        {
            MessageBox.Show(
                message,
                "Validation Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
        }

        #endregion

        #region Theme Application

        private void ApplyTheme()
        {
            // Form colors
            BackColor = _theme.ToolbarBackground;
            ForeColor = _theme.ToolbarForeground;

            // Labels
            ApplyThemeToLabel(nameLabel);
            ApplyThemeToLabel(pathLabel);
            ApplyThemeToLabel(descriptionLabel);
            ApplyThemeToLabel(changeLogLabel);

            // TextBoxes
            ApplyThemeToTextBox(nameTextBox);
            ApplyThemeToTextBox(pathTextBox);
            ApplyThemeToTextBox(descriptionTextBox);
            ApplyThemeToTextBox(changeLogTextBox);

            // Buttons
            ApplyThemeToButton(browseButton);
            ApplyThemeToButton(saveButton);
            ApplyThemeToButton(cancelButton);
        }

        private void ApplyThemeToLabel(Label label)
        {
            label.ForeColor = _theme.ToolbarForeground;
            label.BackColor = Color.Transparent;
        }

        private void ApplyThemeToTextBox(TextBox textBox)
        {
            textBox.BackColor = _theme.Background;
            textBox.ForeColor = _theme.Foreground;
            textBox.BorderStyle = BorderStyle.FixedSingle;
        }

        private void ApplyThemeToButton(Button button)
        {
            if (_theme.IsDark)
            {
                button.BackColor = Color.FromArgb(62, 62, 66);
                button.ForeColor = Color.White;
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = Color.FromArgb(86, 86, 86);
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(80, 80, 84);
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(90, 90, 94);
            }
            else
            {
                button.BackColor = Color.FromArgb(225, 225, 225);
                button.ForeColor = Color.FromArgb(30, 30, 30);
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = Color.FromArgb(173, 173, 173);
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(200, 220, 240);
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(180, 200, 220);
            }
        }

        #endregion

        #region Designer Code

        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.nameLabel = new Label();
            this.nameTextBox = new TextBox();
            this.pathLabel = new Label();
            this.pathTextBox = new TextBox();
            this.browseButton = new Button();
            this.descriptionLabel = new Label();
            this.descriptionTextBox = new TextBox();
            this.changeLogLabel = new Label();
            this.changeLogTextBox = new TextBox();
            this.saveButton = new Button();
            this.cancelButton = new Button();
            this.SuspendLayout();

            //
            // nameLabel
            //
            this.nameLabel.AutoSize = true;
            this.nameLabel.Location = new Point(12, 18);
            this.nameLabel.Name = "nameLabel";
            this.nameLabel.Size = new Size(42, 15);
            this.nameLabel.TabIndex = 0;
            this.nameLabel.Text = "Name:";

            //
            // nameTextBox
            //
            this.nameTextBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            this.nameTextBox.Location = new Point(100, 15);
            this.nameTextBox.Name = "nameTextBox";
            this.nameTextBox.Size = new Size(320, 23);
            this.nameTextBox.TabIndex = 1;

            //
            // pathLabel
            //
            this.pathLabel.AutoSize = true;
            this.pathLabel.Location = new Point(12, 53);
            this.pathLabel.Name = "pathLabel";
            this.pathLabel.Size = new Size(34, 15);
            this.pathLabel.TabIndex = 2;
            this.pathLabel.Text = "Path:";

            //
            // pathTextBox
            //
            this.pathTextBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            this.pathTextBox.Location = new Point(100, 50);
            this.pathTextBox.Name = "pathTextBox";
            this.pathTextBox.Size = new Size(270, 23);
            this.pathTextBox.TabIndex = 3;

            //
            // browseButton
            //
            this.browseButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            this.browseButton.Location = new Point(376, 49);
            this.browseButton.Name = "browseButton";
            this.browseButton.Size = new Size(44, 25);
            this.browseButton.TabIndex = 4;
            this.browseButton.Text = "...";
            this.browseButton.Click += new EventHandler(this.BrowseButton_Click);

            //
            // descriptionLabel
            //
            this.descriptionLabel.AutoSize = true;
            this.descriptionLabel.Location = new Point(12, 88);
            this.descriptionLabel.Name = "descriptionLabel";
            this.descriptionLabel.Size = new Size(70, 15);
            this.descriptionLabel.TabIndex = 5;
            this.descriptionLabel.Text = "Description:";

            //
            // descriptionTextBox
            //
            this.descriptionTextBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            this.descriptionTextBox.Location = new Point(100, 85);
            this.descriptionTextBox.Multiline = true;
            this.descriptionTextBox.Name = "descriptionTextBox";
            this.descriptionTextBox.ScrollBars = ScrollBars.Vertical;
            this.descriptionTextBox.Size = new Size(320, 60);
            this.descriptionTextBox.TabIndex = 6;

            //
            // changeLogLabel
            //
            this.changeLogLabel.AutoSize = true;
            this.changeLogLabel.Location = new Point(12, 163);
            this.changeLogLabel.Name = "changeLogLabel";
            this.changeLogLabel.Size = new Size(68, 15);
            this.changeLogLabel.TabIndex = 7;
            this.changeLogLabel.Text = "ChangeLog:";

            //
            // changeLogTextBox
            //
            this.changeLogTextBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            this.changeLogTextBox.Location = new Point(100, 160);
            this.changeLogTextBox.Multiline = true;
            this.changeLogTextBox.Name = "changeLogTextBox";
            this.changeLogTextBox.ScrollBars = ScrollBars.Vertical;
            this.changeLogTextBox.Size = new Size(320, 130);
            this.changeLogTextBox.TabIndex = 8;

            //
            // saveButton
            //
            this.saveButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            this.saveButton.Location = new Point(254, 305);
            this.saveButton.Name = "saveButton";
            this.saveButton.Size = new Size(80, 30);
            this.saveButton.TabIndex = 9;
            this.saveButton.Text = "Save";
            this.saveButton.Click += new EventHandler(this.SaveButton_Click);

            //
            // cancelButton
            //
            this.cancelButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            this.cancelButton.DialogResult = DialogResult.Cancel;
            this.cancelButton.Location = new Point(340, 305);
            this.cancelButton.Name = "cancelButton";
            this.cancelButton.Size = new Size(80, 30);
            this.cancelButton.TabIndex = 10;
            this.cancelButton.Text = "Cancel";
            this.cancelButton.Click += new EventHandler(this.CancelButton_Click);

            //
            // EditProjectDialog
            //
            this.AcceptButton = this.saveButton;
            this.AutoScaleDimensions = new SizeF(7F, 15F);
            this.AutoScaleMode = AutoScaleMode.Font;
            this.CancelButton = this.cancelButton;
            this.ClientSize = new Size(432, 347);
            this.Controls.Add(this.nameLabel);
            this.Controls.Add(this.nameTextBox);
            this.Controls.Add(this.pathLabel);
            this.Controls.Add(this.pathTextBox);
            this.Controls.Add(this.browseButton);
            this.Controls.Add(this.descriptionLabel);
            this.Controls.Add(this.descriptionTextBox);
            this.Controls.Add(this.changeLogLabel);
            this.Controls.Add(this.changeLogTextBox);
            this.Controls.Add(this.saveButton);
            this.Controls.Add(this.cancelButton);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "EditProjectDialog";
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.Text = "New Project";
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        #endregion
    }
}
