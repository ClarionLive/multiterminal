using System;
using System.Text.RegularExpressions;

namespace MultiTerminal.Services
{
    /// <summary>
    /// A GitHub repository as the REST API names it, parsed from a project's free-text Repository URL
    /// (task 92eeeb47). Pure.
    /// </summary>
    public sealed class GitHubRepoRef
    {
        public string Owner { get; }
        public string Name { get; }

        private GitHubRepoRef(string owner, string name)
        {
            Owner = owner;
            Name = name;
        }

        /// <summary><c>owner/name</c>, the key the watch state is stored under.</summary>
        public string FullName => Owner + "/" + Name;

        // GitHub's own limits: an owner is 1-39 of [A-Za-z0-9-], a repo name 1-100 of [A-Za-z0-9._-].
        // Anything else is refused rather than escaped, because the parts go into an API path.
        private static readonly Regex OwnerPattern = new Regex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$", RegexOptions.CultureInvariant);
        private static readonly Regex NamePattern = new Regex(@"^[A-Za-z0-9._-]{1,100}$", RegexOptions.CultureInvariant);

        /// <summary>
        /// The repository a URL names, or null when it is not a github.com repository. Accepts
        /// <c>https://github.com/o/r</c> (with or without <c>www.</c>, a trailing <c>.git</c> or
        /// slash, or a path such as <c>/issues</c> after it), <c>git@github.com:o/r.git</c>,
        /// <c>ssh://git@github.com/o/r.git</c> and a bare <c>o/r</c>. GitHub Enterprise hosts are
        /// refused: the watcher only calls api.github.com.
        /// </summary>
        public static GitHubRepoRef TryParse(string url)
        {
            string path = ExtractPath(url);
            if (path == null) return null;

            var parts = path.Trim('/').Split('/');
            if (parts.Length < 2) return null;

            string owner = parts[0];
            string name = parts[1];
            if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);

            if (!OwnerPattern.IsMatch(owner) || !NamePattern.IsMatch(name)) return null;
            if (name == "." || name == "..") return null;

            return new GitHubRepoRef(owner, name);
        }

        /// <summary>
        /// True for a github.com URL that names an account and no repository (ticket a923589f), such as
        /// <c>https://github.com/positivesoftware</c>: it does not parse, and the warning says why. A bare
        /// word is not claimed, since it could be anything.
        /// </summary>
        public static bool NamesOnlyAnAccount(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || url.IndexOf("github.com", StringComparison.OrdinalIgnoreCase) < 0) return false;
            string path = ExtractPath(url);
            if (path == null) return false;
            var parts = path.Trim('/').Split('/');
            return parts.Length == 1 && OwnerPattern.IsMatch(parts[0]);
        }

        // The path part of a github.com URL, or null when the text is not one of the accepted forms.
        private static string ExtractPath(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            string s = url.Trim();

            var scp = Regex.Match(s, @"^git@github\.com:(?<p>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (scp.Success)
                return scp.Groups["p"].Value;

            if (Uri.TryCreate(s, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == "ssh" || uri.Scheme == "git"))
            {
                string host = uri.Host.ToLowerInvariant();
                if (host != "github.com" && host != "www.github.com") return null;
                return uri.AbsolutePath;
            }

            if (!s.Contains(":") && !s.StartsWith('/') && !s.Contains("\\"))
                return s; // bare owner/name

            return null;
        }

        /// <summary>
        /// The page a header link opens (task e4924065): <c>"repo"</c>, <c>"issues"</c> or
        /// <c>"pulls"</c> of this repository on github.com, anything else null. The header page names
        /// only the target, never a URL, so a click can open nothing but these three pages.
        /// </summary>
        public string WebUrl(string target)
        {
            string root = "https://github.com/" + Owner + "/" + Name;
            switch (target)
            {
                case "repo": return root;
                case "issues": return root + "/issues";
                case "pulls": return root + "/pulls";
                default: return null;
            }
        }
    }
}
