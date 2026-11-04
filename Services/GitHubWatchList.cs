using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// The repositories a project's GitHub watch covers (ticket a923589f): its Repository URL (the
    /// project's own repo) plus the "Also watch" lines. THE one rule for that list: every reader (the
    /// watch service, the header, the form's status line, the reset on a save, a header link's check)
    /// asks <see cref="Of(string, string)"/>, so they cannot disagree about what is watched. Pure.
    /// </summary>
    public sealed class GitHubWatchList
    {
        /// <summary>At most this many repositories per project, so a mistake cannot multiply API calls.</summary>
        public const int MaxRepos = 10;

        /// <summary>One entry that could not be read, for the warning.</summary>
        public sealed class Problem
        {
            public Problem(string line, bool isPrimary, bool namesAccountOnly)
            {
                Line = line;
                IsPrimary = isPrimary;
                NamesAccountOnly = namesAccountOnly;
            }

            /// <summary>What the Owner typed (untrusted text: shown only through <see cref="GitHubWatchList.Shown"/>).</summary>
            public string Line { get; }

            /// <summary>True for the Repository URL, false for an Also watch line.</summary>
            public bool IsPrimary { get; }

            /// <summary>A github.com URL that names an account but no repository.</summary>
            public bool NamesAccountOnly { get; }
        }

        private GitHubWatchList(List<GitHubRepoRef> repos, List<Problem> problems, int overLimit)
        {
            Repos = repos;
            Problems = problems;
            OverLimit = overLimit;
        }

        /// <summary>The readable repositories, de-duplicated by name ignoring case, in order, at most <see cref="MaxRepos"/>.</summary>
        public IReadOnlyList<GitHubRepoRef> Repos { get; }

        /// <summary>Entries that could not be read, in order.</summary>
        public IReadOnlyList<Problem> Problems { get; }

        /// <summary>How many further readable repositories were left out because of <see cref="MaxRepos"/>.</summary>
        public int OverLimit { get; }

        /// <summary>True when something the Owner typed is not being watched.</summary>
        public bool HasProblems => Problems.Count > 0 || OverLimit > 0;

        public bool Contains(string fullName) => Find(fullName) != null;

        public GitHubRepoRef Find(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            return Repos.FirstOrDefault(r => string.Equals(r.FullName, fullName, StringComparison.OrdinalIgnoreCase));
        }

        public static GitHubWatchList Of(Project project) => Of(project?.GitRepoUrl, project?.GitHubExtraRepos);

        /// <param name="primaryUrl">The project's Repository URL: one repository.</param>
        /// <param name="extraRepos">The Also watch text: one repository per line; commas also split.</param>
        public static GitHubWatchList Of(string primaryUrl, string extraRepos)
        {
            var repos = new List<GitHubRepoRef>();
            var problems = new List<Problem>();
            int over = 0;

            void Add(GitHubRepoRef repo)
            {
                if (repos.Any(r => string.Equals(r.FullName, repo.FullName, StringComparison.OrdinalIgnoreCase))) return;
                if (repos.Count >= MaxRepos) { over++; return; }
                repos.Add(repo);
            }

            if (!string.IsNullOrWhiteSpace(primaryUrl))
            {
                var primary = GitHubRepoRef.TryParse(primaryUrl);
                if (primary != null) Add(primary);
                else problems.Add(new Problem(primaryUrl.Trim(), true, GitHubRepoRef.NamesOnlyAnAccount(primaryUrl)));
            }

            foreach (string line in Split(extraRepos))
            {
                var repo = GitHubRepoRef.TryParse(line);
                if (repo != null) Add(repo);
                else problems.Add(new Problem(line, false, GitHubRepoRef.NamesOnlyAnAccount(line)));
            }

            return new GitHubWatchList(repos, problems, over);
        }

        /// <summary>The Also watch text as separate trimmed, non-blank entries: split on new lines and commas.</summary>
        public static List<string> Split(string extraRepos)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(extraRepos)) return list;
            foreach (string part in extraRepos.Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = part.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }

        /// <summary>
        /// The repositories on <paramref name="before"/> that are not on <paramref name="after"/>: the ones
        /// whose stored watch state must go, so adding one back later starts from a fresh baseline.
        /// </summary>
        public static List<string> Removed(GitHubWatchList before, GitHubWatchList after) =>
            before.Repos.Where(r => !after.Contains(r.FullName)).Select(r => r.FullName).ToList();

        /// <summary>
        /// The page a header link opens, or null. The page sends a target and the repository it showed;
        /// the repository must be on THIS project's watch list, and the URL is built here from the
        /// parsed name, never taken from the page.
        /// </summary>
        public static string ResolveLink(Project project, string target, string repoFromPage)
        {
            if (project == null) return null;
            var list = Of(project);
            var repo = list.Find(repoFromPage);
            // An older page sends no repository: only unambiguous with a single one.
            if (repo == null && string.IsNullOrEmpty(repoFromPage) && list.Repos.Count == 1) repo = list.Repos[0];
            return repo?.WebUrl(target);
        }

        /// <summary>
        /// A short label per repository for the header: the bare name, but <c>owner/name</c> when there is
        /// only one repository (as before) or two share a name.
        /// </summary>
        public IReadOnlyList<string> Labels()
        {
            if (Repos.Count <= 1) return Repos.Select(r => r.FullName).ToList();
            return Repos.Select(r =>
                Repos.Count(o => string.Equals(o.Name, r.Name, StringComparison.OrdinalIgnoreCase)) > 1 ? r.FullName : r.Name).ToList();
        }

        // ── The warning (MT's own words) ──────────────────────────────────────────

        /// <summary>
        /// Text the Owner typed, made safe to put in a sentence: control and format characters (bidi
        /// overrides included) become spaces, and a long line is cut.
        /// </summary>
        public static string Shown(string line)
        {
            var sb = new StringBuilder();
            foreach (char ch in line ?? string.Empty)
            {
                var cat = char.GetUnicodeCategory(ch);
                bool bad = char.IsControl(ch) || cat == UnicodeCategory.Format
                    || cat == UnicodeCategory.LineSeparator || cat == UnicodeCategory.ParagraphSeparator;
                sb.Append(bad ? ' ' : ch);
            }
            string s = sb.ToString().Trim();
            return s.Length > 50 ? s.Substring(0, 50) + "…" : s;
        }

        /// <summary>
        /// One sentence about what could not be read or was left out, naming the line, or null when all
        /// of it is watched. No prefix: the header and the form each add their own.
        /// </summary>
        public string WarningBody()
        {
            var parts = new List<string>();
            if (Problems.Count > 0)
            {
                var p = Problems[0];
                string where = p.IsPrimary ? "the Repository URL" : "the Also watch line";
                string why = p.NamesAccountOnly ? "names an account, not a repository" : "is not a github.com repository";
                string text = $"{where} \"{Shown(p.Line)}\" {why}";
                if (Problems.Count > 1) text += $" (and {Problems.Count - 1} more cannot be read)";
                parts.Add(text);
            }
            if (OverLimit > 0)
                parts.Add($"only the first {MaxRepos} repositories are watched; {OverLimit} more {(OverLimit == 1 ? "was" : "were")} left out");
            return parts.Count == 0 ? null : string.Join("; ", parts);
        }
    }
}
