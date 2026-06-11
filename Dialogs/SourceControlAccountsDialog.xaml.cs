using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using MultiTerminal.Models;
using MultiTerminal.Services;

namespace MultiTerminal.Dialogs
{
    /// <summary>
    /// Accounts manager for source control accounts (GitHub / Bitbucket).
    /// Lists accounts in a grid and supports Add / Edit / Delete via the
    /// <see cref="SourceControlAccountEditDialog"/>. All persistence flows through
    /// <see cref="SourceControlAccountService"/>; tokens are saved/removed via its
    /// Windows Credential Manager helpers.
    /// </summary>
    public partial class SourceControlAccountsDialog : Window
    {
        private readonly SourceControlAccountService _service;

        public SourceControlAccountsDialog(SourceControlAccountService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            InitializeComponent();
            RefreshGrid();
        }

        /// <summary>
        /// Lightweight row view-model so the grid can show a friendly provider label
        /// while keeping the underlying account for edit/delete.
        /// </summary>
        private sealed class AccountRow
        {
            public AccountRow(SourceControlAccount account)
            {
                Account = account;
            }

            public SourceControlAccount Account { get; }
            public string DisplayName => Account.DisplayName;
            public string Username => Account.Username;
            public string ProviderLabel =>
                string.Equals(Account.Provider, "bitbucket", StringComparison.OrdinalIgnoreCase)
                    ? "Bitbucket"
                    : "GitHub";
        }

        private void RefreshGrid()
        {
            string selectedId = SelectedAccount()?.Id;

            var rows = new List<AccountRow>();
            foreach (var account in _service.GetAll())
                rows.Add(new AccountRow(account));

            AccountsGrid.ItemsSource = null;
            AccountsGrid.ItemsSource = rows;

            // Restore selection if the previously selected account still exists.
            if (selectedId != null)
            {
                AccountsGrid.SelectedItem = rows.Find(r => r.Account.Id == selectedId);
            }

            UpdateButtonsEnabled();
        }

        private SourceControlAccount SelectedAccount()
        {
            return (AccountsGrid.SelectedItem as AccountRow)?.Account;
        }

        private void UpdateButtonsEnabled()
        {
            bool hasSelection = AccountsGrid.SelectedItem is AccountRow;
            if (EditButton != null) EditButton.IsEnabled = hasSelection;
            if (DeleteButton != null) DeleteButton.IsEnabled = hasSelection;
        }

        // -----------------------------------------------------------------
        // Add
        // -----------------------------------------------------------------

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SourceControlAccountEditDialog { Owner = this };
            if (dialog.ShowDialog() != true) return;

            var account = new SourceControlAccount
            {
                DisplayName = dialog.DisplayName,
                Provider = dialog.Provider,
                Username = dialog.Username
            };

            var saved = _service.Add(account);

            if (dialog.TokenChanged && !string.IsNullOrEmpty(dialog.Token))
            {
                _service.SaveToken(saved.Id, dialog.Token);
            }

            RefreshGrid();
        }

        // -----------------------------------------------------------------
        // Edit
        // -----------------------------------------------------------------

        private void EditButton_Click(object sender, RoutedEventArgs e)
        {
            EditSelected();
        }

        private void AccountsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateButtonsEnabled();
        }

        private void AccountsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (AccountsGrid.SelectedItem is AccountRow)
                EditSelected();
        }

        private void EditSelected()
        {
            var account = SelectedAccount();
            if (account == null) return;

            var dialog = new SourceControlAccountEditDialog { Owner = this };
            dialog.LoadExisting(account);
            if (dialog.ShowDialog() != true) return;

            account.DisplayName = dialog.DisplayName;
            account.Provider = dialog.Provider;
            account.Username = dialog.Username;
            _service.Update(account);

            // Only touch the token when the user actually changed it; otherwise the
            // existing stored token is preserved.
            if (dialog.TokenChanged)
            {
                if (string.IsNullOrEmpty(dialog.Token))
                    _service.RemoveToken(account.Id);
                else
                    _service.SaveToken(account.Id, dialog.Token);
            }

            RefreshGrid();
        }

        // -----------------------------------------------------------------
        // Delete
        // -----------------------------------------------------------------

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var account = SelectedAccount();
            if (account == null) return;

            var result = MessageBox.Show(
                this,
                $"Delete account \"{account.DisplayName}\"? This also removes its stored token.",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            // Delete() already removes the token from Windows Credential Manager.
            _service.Delete(account.Id);
            RefreshGrid();
        }

        // -----------------------------------------------------------------
        // Close
        // -----------------------------------------------------------------

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
