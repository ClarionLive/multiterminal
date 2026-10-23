#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MultiTerminal.Services
{
    /// <summary>A git remote that points at github.com: its name in the repo and the repository.</summary>
    public readonly record struct GitRemoteRepo(string RemoteName, GitHubRepoRef Repo);

    /// <summary>
    /// Which GitHub repositories a folder's git remotes point at (task df58b35d, GitHub #53).
    /// </summary>
    /// <remarks>
    /// A fork usually has two: <c>origin</c> (the fork) and <c>upstream</c> (the original),
    /// and issues and PRs are filed against either. So every github.com remote counts, not just
    /// <c>origin</c>. Only fetch URLs are read; a push URL that differs is where the user
    /// pushes, not a repository the project tracks.
    /// </remarks>
    public static class GitRemoteReader
    {
        /// <summary>
        /// The folder's github.com remotes, one per repository, in git's output order (the
        /// first remote naming a repository wins). Empty for a folder that is missing or not a
        /// git repository, and when git fails or times out: "could not look" and "no GitHub
        /// remotes" both mean there is nothing to show, so neither throws.
        /// </summary>
        public static async Task<IReadOnlyList<GitRemoteRepo>> GetGitHubRemotesAsync(string folder, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                return Array.Empty<GitRemoteRepo>();
            }

            // GitExec takes no token; the read is short and bounded by its timeout.
            var result = await GitExec.RunAsync(folder, "remote", "-v").ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // A timeout is not "no remotes" in general, but here both lead to the same answer
            // (nothing to show), and the caller asks again on its next refresh.
            if (result.TimedOut || result.ExitCode != 0)
            {
                return Array.Empty<GitRemoteRepo>();
            }

            return ParseRemoteVerbose(result.Stdout);
        }

        /// <summary>
        /// Parses <c>git remote -v</c> output (<c>name\turl (fetch)</c> / <c>(push)</c> lines).
        /// Keeps fetch lines whose URL is a github.com repository, de-duplicated by repository
        /// (case-insensitive), first occurrence wins.
        /// </summary>
        internal static IReadOnlyList<GitRemoteRepo> ParseRemoteVerbose(string? output)
        {
            var remotes = new List<GitRemoteRepo>();
            if (string.IsNullOrEmpty(output))
            {
                return remotes;
            }

            const string fetchSuffix = " (fetch)";
            var seen = new HashSet<GitHubRepoRef>();
            foreach (string rawLine in output.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                int tab = line.IndexOf('\t');
                if (tab <= 0 || !line.EndsWith(fetchSuffix, StringComparison.Ordinal))
                {
                    continue;
                }

                string name = line.Substring(0, tab);
                string url = line.Substring(tab + 1, line.Length - tab - 1 - fetchSuffix.Length).Trim();
                if (GitHubRepoRef.TryParse(url, out var repo) && seen.Add(repo))
                {
                    remotes.Add(new GitRemoteRepo(name, repo));
                }
            }

            return remotes;
        }
    }
}
