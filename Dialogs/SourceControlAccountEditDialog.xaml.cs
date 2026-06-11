using System;
using System.Windows;
using System.Windows.Controls;

using MultiTerminal.Models;
using MultiTerminal.Services;

namespace MultiTerminal.Dialogs
{
    /// <summary>
    /// Add / edit dialog for a single source control account (GitHub or Bitbucket).
    /// Provider dropdown, Display Name, Username, and a Personal Access Token field
    /// with a show/hide eye toggle. Test validates the credentials against the provider.
    /// On edit, the token field shows a masked placeholder; leaving it unchanged preserves
    /// the existing stored token rather than overwriting it.
    /// </summary>
    public partial class SourceControlAccountEditDialog : Window
    {
        // Sentinel shown in the token field when editing an account that already has a token.
        private const string TokenPlaceholder = "placeholder-existing";

        // Provider order must match the ComboBox items added in the constructor.
        private static readonly string[] ProviderValues = { "github", "bitbucket" };

        private bool _hadExistingToken;
        // Guards against the two token controls echoing each other while syncing.
        private bool _syncingToken;

        // Public results for the caller to read after the dialog closes.
        public string DisplayName => DisplayNameBox.Text.Trim();
        public string Username => UsernameBox.Text.Trim();

        public string Provider =>
            ProviderCombo.SelectedIndex >= 0 && ProviderCombo.SelectedIndex < ProviderValues.Length
                ? ProviderValues[ProviderCombo.SelectedIndex]
                : "github";

        /// <summary>
        /// True if the user entered or changed the token. When false on an edited account
        /// (the masked placeholder is still in place), the caller should preserve the
        /// existing stored token.
        /// </summary>
        public bool TokenChanged => !(_hadExistingToken && CurrentTokenText() == TokenPlaceholder);

        /// <summary>
        /// The token the user typed. Only meaningful when <see cref="TokenChanged"/> is true.
        /// </summary>
        public string Token => CurrentTokenText();

        public SourceControlAccountEditDialog()
        {
            InitializeComponent();

            ProviderCombo.Items.Add("GitHub");
            ProviderCombo.Items.Add("Bitbucket");
            ProviderCombo.SelectedIndex = 0;

            UpdateTokenHint();
            UpdateSaveEnabled();
            DisplayNameBox.Focus();
        }

        /// <summary>
        /// Pre-populate fields when editing an existing account.
        /// </summary>
        public void LoadExisting(SourceControlAccount account)
        {
            if (account == null) return;

            int providerIndex = Array.IndexOf(ProviderValues,
                (account.Provider ?? "github").Trim().ToLowerInvariant());
            ProviderCombo.SelectedIndex = providerIndex >= 0 ? providerIndex : 0;

            DisplayNameBox.Text = account.DisplayName ?? "";
            UsernameBox.Text = account.Username ?? "";

            _hadExistingToken = account.HasToken;
            if (account.HasToken)
            {
                // Show placeholder so the user knows a token is already set; leaving it
                // untouched preserves the stored token.
                _syncingToken = true;
                TokenPasswordBox.Password = TokenPlaceholder;
                TokenTextBox.Text = TokenPlaceholder;
                _syncingToken = false;
            }

            Title = "Edit Source Control Account";
            UpdateTokenHint();
            UpdateSaveEnabled();
        }

        // -----------------------------------------------------------------
        // Token show/hide eye toggle — keep PasswordBox and TextBox in sync.
        // -----------------------------------------------------------------

        private void ToggleTokenButton_Click(object sender, RoutedEventArgs e)
        {
            // The PasswordChanged/TextChanged handlers keep both controls in sync, so
            // the value is already mirrored — we just flip which one is visible.
            bool showPlain = TokenTextBox.Visibility != Visibility.Visible;
            if (showPlain)
            {
                TokenPasswordBox.Visibility = Visibility.Collapsed;
                TokenTextBox.Visibility = Visibility.Visible;
                TokenTextBox.Focus();
                TokenTextBox.CaretIndex = TokenTextBox.Text.Length;
            }
            else
            {
                TokenTextBox.Visibility = Visibility.Collapsed;
                TokenPasswordBox.Visibility = Visibility.Visible;
                TokenPasswordBox.Focus();
            }
        }

        private void TokenPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_syncingToken) return;
            _syncingToken = true;
            TokenTextBox.Text = TokenPasswordBox.Password;
            _syncingToken = false;
        }

        private void TokenTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_syncingToken) return;
            _syncingToken = true;
            TokenPasswordBox.Password = TokenTextBox.Text;
            _syncingToken = false;
        }

        private string CurrentTokenText()
        {
            return TokenTextBox.Visibility == Visibility.Visible
                ? TokenTextBox.Text
                : TokenPasswordBox.Password;
        }

        // -----------------------------------------------------------------
        // Provider-specific token hint
        // -----------------------------------------------------------------

        private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateTokenHint();
            UpdateSaveEnabled();
        }

        private void UpdateTokenHint()
        {
            if (TokenHintText == null) return;

            TokenHintText.Text = Provider == "bitbucket"
                ? "Bitbucket: create an app password under Settings → App passwords (needs Account + Repository read)."
                : "GitHub: generate a token under Settings → Developer settings → Personal access tokens. Stored securely in Windows Credential Manager.";
        }

        // -----------------------------------------------------------------
        // Save enabling
        // -----------------------------------------------------------------

        private void OnRequiredFieldChanged(object sender, TextChangedEventArgs e)
        {
            UpdateSaveEnabled();
        }

        private void UpdateSaveEnabled()
        {
            if (SaveButton == null) return;
            SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(DisplayNameBox.Text)
                                && !string.IsNullOrWhiteSpace(UsernameBox.Text);
        }

        // -----------------------------------------------------------------
        // Test credentials
        // -----------------------------------------------------------------

        private async void TestButton_Click(object sender, RoutedEventArgs e)
        {
            string token = CurrentTokenText();

            // When editing without changing the token, the placeholder is not a real token —
            // tell the user to re-enter it to test.
            if (!TokenChanged)
            {
                ShowTestResult("Re-enter the token to test it (the stored token is hidden).", false);
                return;
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                ShowTestResult("Enter a token to test.", false);
                return;
            }

            TestButton.IsEnabled = false;
            ShowTestResult("Testing…", null);
            try
            {
                var result = await SourceControlValidator.TestAsync(Provider, Username, token);
                if (result.Success)
                {
                    string login = string.IsNullOrWhiteSpace(result.Login) ? Username : result.Login;
                    ShowTestResult($"Authenticated as {login}.", true);
                }
                else
                {
                    ShowTestResult(result.Error ?? "Validation failed.", false);
                }
            }
            catch (Exception ex)
            {
                ShowTestResult("Test failed: " + ex.Message, false);
            }
            finally
            {
                TestButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Shows the inline test result. success == true -> green, false -> red, null -> neutral.
        /// </summary>
        private void ShowTestResult(string message, bool? success)
        {
            TestResultText.Text = message;
            TestResultText.Foreground = success switch
            {
                true => new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x4E, 0xC9, 0x4E)),
                false => new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xE0, 0x6C, 0x6C)),
                null => new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xA0, 0xA0, 0xA0))
            };
            TestResultText.Visibility = Visibility.Visible;
        }

        // -----------------------------------------------------------------
        // Bottom bar
        // -----------------------------------------------------------------

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
