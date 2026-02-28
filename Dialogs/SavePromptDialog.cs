using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using MultiTerminal.Terminal;

namespace MultiTerminal.Dialogs
{
    /// <summary>
    /// Dialog for saving a prompt with category, description, and scope selection.
    /// Supports both dark and light themes to match the terminal appearance.
    /// </summary>
    public partial class SavePromptDialog : Form
    {
        private readonly TerminalTheme _theme;
        private readonly string _projectName;
        private bool _isEditMode = false;

        /// <summary>
        /// Gets the selected category for the prompt.
        /// </summary>
        public string Category => categoryComboBox.Text?.Trim() ?? string.Empty;

        /// <summary>
        /// Gets or sets the description for the prompt.
        /// </summary>
        public string Description
        {
            get => descriptionTextBox.Text?.Trim() ?? string.Empty;
            set => descriptionTextBox.Text = value;
        }

        /// <summary>
        /// Gets or sets the prompt text.
        /// </summary>
        public string PromptText
        {
            get => promptTextBox.Text;
            set => promptTextBox.Text = value;
        }

        /// <summary>
        /// Gets or sets whether the prompt text can be edited.
        /// Set to true for creating new prompts, false for saving selected text.
        /// </summary>
        public bool AllowPromptEdit
        {
            get => !promptTextBox.ReadOnly;
            set
            {
                promptTextBox.ReadOnly = !value;
                promptTextBox.TabStop = value;
                if (value)
                {
                    promptTextBox.BackColor = _theme.Background;
                }
            }
        }

        /// <summary>
        /// Gets whether the prompt should be saved globally (true) or locally to the project (false).
        /// </summary>
        public bool IsGlobal => globalRadioButton.Checked;

        /// <summary>
        /// Sets the initially selected category in the dropdown.
        /// </summary>
        public string SelectedCategory
        {
            set
            {
                if (!string.IsNullOrEmpty(value))
                {
                    // Add category to list if not already present
                    if (!categoryComboBox.Items.Contains(value))
                    {
                        categoryComboBox.Items.Add(value);
                    }
                    categoryComboBox.Text = value;
                }
            }
        }

        /// <summary>
        /// Sets the global/local scope radio button selection.
        /// </summary>
        /// <param name="isGlobal">True to select global, false to select local.</param>
        public void SetIsGlobal(bool isGlobal)
        {
            globalRadioButton.Checked = isGlobal;
            localRadioButton.Checked = !isGlobal;
        }

        /// <summary>
        /// Gets or sets whether the dialog is in edit mode.
        /// When true, changes the title to "Edit Prompt" and button text to "Update".
        /// </summary>
        public bool IsEditMode
        {
            get => _isEditMode;
            set
            {
                _isEditMode = value;
                if (_isEditMode)
                {
                    Text = "Edit Prompt";
                    saveButton.Text = "Update";
                }
            }
        }

        /// <summary>
        /// Creates a new SavePromptDialog instance.
        /// </summary>
        /// <param name="existingCategories">Existing categories to populate the dropdown.</param>
        /// <param name="projectName">The current project name for local scope labeling.</param>
        /// <param name="theme">The terminal theme to apply (dark or light).</param>
        public SavePromptDialog(IEnumerable<string> existingCategories, string projectName, TerminalTheme theme)
        {
            _theme = theme ?? TerminalTheme.Dark;
            _projectName = projectName ?? "Current Project";

            InitializeComponent();
            InitializeCategories(existingCategories);
            ApplyTheme();
            UpdateLocalScopeLabel();
        }

        /// <summary>
        /// Initializes the category dropdown with default and existing categories.
        /// </summary>
        private void InitializeCategories(IEnumerable<string> existingCategories)
        {
            // Add default categories
            var defaultCategories = new[] { "Claude", "Git", "Build", "Testing", "General" };
            foreach (var category in defaultCategories)
            {
                categoryComboBox.Items.Add(category);
            }

            // Add existing categories that are not already in defaults
            if (existingCategories != null)
            {
                var defaultSet = new HashSet<string>(defaultCategories, StringComparer.OrdinalIgnoreCase);
                foreach (var category in existingCategories)
                {
                    if (!string.IsNullOrWhiteSpace(category) && !defaultSet.Contains(category))
                    {
                        categoryComboBox.Items.Add(category);
                    }
                }
            }

            // Select first item by default
            if (categoryComboBox.Items.Count > 0)
            {
                categoryComboBox.SelectedIndex = 0;
            }
        }

        /// <summary>
        /// Updates the local scope radio button text with the project name.
        /// </summary>
        private void UpdateLocalScopeLabel()
        {
            localRadioButton.Text = $"Local to {_projectName}";
        }

        /// <summary>
        /// Applies the terminal theme colors to all dialog controls.
        /// </summary>
        private void ApplyTheme()
        {
            // Form colors
            BackColor = _theme.ToolbarBackground;
            ForeColor = _theme.ToolbarForeground;

            // Labels
            ApplyThemeToLabel(categoryLabel);
            ApplyThemeToLabel(descriptionLabel);
            ApplyThemeToLabel(promptLabel);
            ApplyThemeToLabel(scopeLabel);

            // TextBoxes and ComboBox
            ApplyThemeToTextBox(descriptionTextBox);
            ApplyThemeToTextBox(promptTextBox);
            ApplyThemeToComboBox(categoryComboBox);

            // Radio buttons
            ApplyThemeToRadioButton(globalRadioButton);
            ApplyThemeToRadioButton(localRadioButton);

            // Buttons
            ApplyThemeToButton(saveButton);
            ApplyThemeToButton(cancelButton);

            // Group box
            scopeGroupBox.ForeColor = _theme.ToolbarForeground;
            scopeGroupBox.BackColor = _theme.ToolbarBackground;
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

        private void ApplyThemeToComboBox(ComboBox comboBox)
        {
            comboBox.BackColor = _theme.Background;
            comboBox.ForeColor = _theme.Foreground;
            comboBox.FlatStyle = FlatStyle.Flat;
        }

        private void ApplyThemeToRadioButton(RadioButton radioButton)
        {
            radioButton.ForeColor = _theme.ToolbarForeground;
            radioButton.BackColor = Color.Transparent;
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

        /// <summary>
        /// Validates input before closing the dialog with OK result.
        /// </summary>
        private void SaveButton_Click(object sender, EventArgs e)
        {
            // Validate category
            if (string.IsNullOrWhiteSpace(Category))
            {
                ShowValidationError("Please enter or select a category.");
                categoryComboBox.Focus();
                return;
            }

            // Validate description
            if (string.IsNullOrWhiteSpace(Description))
            {
                ShowValidationError("Please enter a description for the prompt.");
                descriptionTextBox.Focus();
                return;
            }

            if (Description.Length > 100)
            {
                ShowValidationError("Description must be 100 characters or less.");
                descriptionTextBox.Focus();
                return;
            }

            DialogResult = DialogResult.OK;
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
    }
}
