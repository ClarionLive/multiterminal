#nullable enable

using System;
using System.Collections.Generic;

namespace MultiTerminal.Models
{
    /// <summary>
    /// One open GitHub issue or pull request of a project's repository, as listed by
    /// <see cref="Services.GitHubRestClient.GetOpenIssuesAsync"/> (task df58b35d, GitHub #53).
    ///
    /// <para>Plain settable properties so a list of these round-trips through
    /// <see cref="System.Text.Json.JsonSerializer"/>: a later step stores the last list seen as a
    /// snapshot to tell which items are new.</para>
    ///
    /// <para><b>Text fields are already display-safe.</b> <see cref="Title"/>, <see cref="Author"/> and
    /// <see cref="Labels"/> come from anyone who can open an issue on the repository, so the client has
    /// run them through <see cref="MCPServer.Services.SessionCatalog.ToDisplayText"/> (no control or
    /// bidi/zero-width characters, one line). <see cref="HtmlUrl"/> is null unless it pointed at this
    /// repository's own issue or pull page.</para>
    /// </summary>
    public sealed class RepoActivityItem
    {
        /// <summary>The issue or pull request number (GitHub numbers both from one sequence).</summary>
        public long Number { get; set; }

        /// <summary>Display-safe title, capped in length; "(no title)" when GitHub sent nothing visible.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Display-safe login of the author; "unknown" when absent.</summary>
        public string Author { get; set; } = string.Empty;

        /// <summary>When the item was opened, in UTC.</summary>
        public DateTime CreatedAtUtc { get; set; }

        /// <summary>True for a pull request (GitHub's issues endpoint lists both).</summary>
        public bool IsPullRequest { get; set; }

        /// <summary>True for a draft pull request; always false for an issue.</summary>
        public bool IsDraft { get; set; }

        /// <summary>
        /// <c>https://github.com/{owner}/{repo}/issues/{n}</c> or <c>.../pull/{n}</c>, or null when
        /// GitHub's value was anything else. Safe to open in a browser when non-null.
        /// </summary>
        public string? HtmlUrl { get; set; }

        /// <summary>Display-safe label names, in GitHub's order.</summary>
        public IReadOnlyList<string> Labels { get; set; } = Array.Empty<string>();
    }
}
