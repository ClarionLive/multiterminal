using System.Windows;
using System.Windows.Controls;

namespace MultiTerminal.Dialogs
{
    /// <summary>
    /// Owner profile dialog for configuring the git identity (name + email).
    /// Shown on first run or when the user edits their profile from Settings.
    /// GitHub/Bitbucket accounts are managed separately under Source Control Accounts.
    /// </summary>
    public partial class OwnerProfileDialog : Window
    {
        // Public properties for the caller to read after dialog closes
        public string FullName => FullNameBox.Text.Trim();
        public string Email => EmailBox.Text.Trim();

        public OwnerProfileDialog()
        {
            InitializeComponent();
            FullNameBox.Focus();
        }

        /// <summary>
        /// Pre-populate fields when editing an existing profile.
        /// </summary>
        public void LoadExisting(string fullName, string email)
        {
            FullNameBox.Text = fullName ?? "";
            EmailBox.Text = email ?? "";

            // Change Skip to Cancel when editing existing profile
            SkipButton.Content = "Cancel";

            UpdateSaveEnabled();
        }

        private void OnRequiredFieldChanged(object sender, TextChangedEventArgs e)
        {
            UpdateSaveEnabled();
        }

        private void UpdateSaveEnabled()
        {
            if (SaveButton == null) return;
            SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(FullNameBox.Text)
                                && !string.IsNullOrWhiteSpace(EmailBox.Text);
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void SkipButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
