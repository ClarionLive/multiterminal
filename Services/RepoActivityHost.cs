#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// The live <see cref="IRepoActivityHost"/> (task df58b35d, GitHub #53). Every member is called by
    /// <see cref="RepoActivityService"/> on thread-pool threads, so none of them touches WinForms:
    /// <list type="bullet">
    /// <item>projects come from <see cref="ProjectDatabase"/>, whose every connection-touching method takes
    /// its own lock;</item>
    /// <item>"is a pane open on this project" reads an immutable snapshot that MainForm replaces on the UI
    /// thread (<see cref="SetOpenPaneProjects"/>) whenever a pane's project changes or a pane leaves the
    /// dock, so this side never enumerates controls;</item>
    /// <item>the token is read per call through a short-lived <see cref="SourceControlAccountService"/>
    /// (it opens its own connection), and only for an account whose provider is <c>github</c>.</item>
    /// </list>
    /// The token is never logged.
    /// </summary>
    public sealed class RepoActivityHost : IRepoActivityHost
    {
        private readonly Func<IReadOnlyList<Project>> _allProjects;
        private readonly Func<string, Project?> _getProject;
        private readonly Func<string, (SourceControlAccount? Account, Func<string?> ReadToken)> _getAccount;
        private readonly Action<string>? _log;

        // Replaced whole, never mutated: a reader on any thread sees one complete set.
        private volatile HashSet<string> _openPaneProjects = new(StringComparer.Ordinal);

        internal RepoActivityHost(
            Func<IReadOnlyList<Project>> allProjects,
            Func<string, Project?> getProject,
            Func<string, (SourceControlAccount? Account, Func<string?> ReadToken)> getAccount,
            Action<string>? log = null)
        {
            _allProjects = allProjects ?? throw new ArgumentNullException(nameof(allProjects));
            _getProject = getProject ?? throw new ArgumentNullException(nameof(getProject));
            _getAccount = getAccount ?? throw new ArgumentNullException(nameof(getAccount));
            _log = log;
        }

        /// <summary>The production host over the shared project database and the stored source control accounts.</summary>
        public static RepoActivityHost Create(ProjectDatabase projectDb, Action<string>? log = null)
        {
            if (projectDb == null) throw new ArgumentNullException(nameof(projectDb));
            return new RepoActivityHost(
                () => projectDb.GetAllRichProjects(),
                projectDb.GetRichProject,
                ReadAccount,
                log);
        }

        /// <summary>
        /// Replaces the set of projects that have a terminal pane open. UI thread (MainForm); takes a copy.
        /// </summary>
        public void SetOpenPaneProjects(IEnumerable<string?> projectIds)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (projectIds != null)
            {
                foreach (var id in projectIds)
                {
                    if (!string.IsNullOrEmpty(id)) set.Add(id);
                }
            }

            _openPaneProjects = set;
        }

        /// <inheritdoc />
        public IReadOnlyList<Project> GetWatchedProjects()
            => (_allProjects() ?? (IReadOnlyList<Project>)Array.Empty<Project>())
                .Where(p => p != null && p.IsRepoWatchEnabled)
                .ToList();

        /// <inheritdoc />
        public Project? GetProject(string projectId)
            => string.IsNullOrWhiteSpace(projectId) ? null : _getProject(projectId);

        /// <inheritdoc />
        public bool HasOpenPane(string projectId)
            => !string.IsNullOrEmpty(projectId) && _openPaneProjects.Contains(projectId);

        /// <inheritdoc />
        public string? GetGitHubToken(Project project)
        {
            string? accountId = project?.SourceControlAccountId;
            if (string.IsNullOrWhiteSpace(accountId)) return null;

            try
            {
                var (account, readToken) = _getAccount(accountId);
                return TokenFor(account, readToken);
            }
            catch (Exception ex)
            {
                // The message is the store's or the database's, never the token.
                _log?.Invoke($"RepoActivityHost could not read source control account {accountId} of project {project!.Name}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// The account's token when the account exists and is a GitHub one, else null. A Bitbucket token is
        /// never sent to GitHub. <paramref name="readToken"/> is called only for a GitHub account.
        /// </summary>
        internal static string? TokenFor(SourceControlAccount? account, Func<string?> readToken)
        {
            if (account == null || !string.Equals(account.Provider?.Trim(), "github", StringComparison.OrdinalIgnoreCase))
                return null;

            string? token = readToken();
            return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        }

        private static (SourceControlAccount? Account, Func<string?> ReadToken) ReadAccount(string accountId)
        {
            using var accounts = new SourceControlAccountService();
            var account = accounts.Get(accountId);
            if (account == null) return (null, () => null);

            // The token lives in Windows Credential Manager, not in the connection being closed here.
            string? token = string.Equals(account.Provider?.Trim(), "github", StringComparison.OrdinalIgnoreCase)
                ? accounts.GetToken(accountId)
                : null;
            return (account, () => token);
        }
    }
}
