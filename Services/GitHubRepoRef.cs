#nullable enable
using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MultiTerminal.Services
{
    /// <summary>
    /// One repository on github.com, as <c>owner/name</c> (task df58b35d, GitHub #53).
    /// </summary>
    /// <remarks>
    /// <para><b>Only github.com.</b> <see cref="TryParse"/> accepts exactly the remote forms git
    /// writes for github.com (https, scp-style ssh, ssh URL) and rejects everything else,
    /// including a GitHub Enterprise host and a look-alike such as <c>github.com.evil.com</c>.
    /// A remote that is not provably github.com must not be turned into an api.github.com
    /// query.</para>
    /// <para><b>Credentials are never kept.</b> An https remote may carry <c>user:token@</c>;
    /// the value holds only the owner and name, so nothing parsed from a URL can leak a token
    /// through <see cref="FullName"/>, <see cref="ToString"/> or a log line.</para>
    /// <para><b>Equality ignores case</b>, as GitHub does for owner and repository names, so
    /// two remotes spelled <c>Owner/Repo</c> and <c>owner/repo</c> de-duplicate.</para>
    /// </remarks>
    public readonly struct GitHubRepoRef : IEquatable<GitHubRepoRef>
    {
        private const string Host = "github.com";
        private const int MaxOwnerLength = 39;
        private const int MaxNameLength = 100;

        private GitHubRepoRef(string owner, string name)
        {
            Owner = owner;
            Name = name;
        }

        /// <summary>The account or organisation, as spelled in the remote.</summary>
        public string Owner { get; }

        /// <summary>The repository name, without <c>.git</c>.</summary>
        public string Name { get; }

        /// <summary><c>owner/name</c>.</summary>
        public string FullName => $"{Owner}/{Name}";

        public static bool operator ==(GitHubRepoRef left, GitHubRepoRef right) => left.Equals(right);

        public static bool operator !=(GitHubRepoRef left, GitHubRepoRef right) => !left.Equals(right);

        /// <summary>
        /// Parses a git remote URL that points at github.com. Accepted:
        /// <c>https://[user[:token]@]github.com/owner/repo[.git][/]</c>,
        /// <c>git@github.com:owner/repo[.git]</c>,
        /// <c>ssh://git@github.com[:22]/owner/repo[.git]</c>.
        /// Anything else (http, another host, a port other than 22, extra path segments,
        /// a query, names GitHub would not allow) returns false.
        /// </summary>
        public static bool TryParse(string? remoteUrl, out GitHubRepoRef repo)
        {
            repo = default;
            if (string.IsNullOrWhiteSpace(remoteUrl))
            {
                return false;
            }

            string url = remoteUrl.Trim();
            string path;

            const string https = "https://";
            const string ssh = "ssh://";
            if (url.StartsWith(https, StringComparison.OrdinalIgnoreCase))
            {
                // Any user (or user:token) is allowed and dropped here, unread; no port.
                if (!TrySplitAuthority(url.Substring(https.Length), out _, out string hostPort, out path))
                {
                    return false;
                }

                if (!string.Equals(hostPort, Host, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            else if (url.StartsWith(ssh, StringComparison.OrdinalIgnoreCase))
            {
                if (!TrySplitAuthority(url.Substring(ssh.Length), out string? user, out string hostPort, out path)
                    || !string.Equals(user, "git", StringComparison.Ordinal))
                {
                    return false;
                }

                if (!string.Equals(hostPort, Host, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(hostPort, Host + ":22", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            else if (url.Contains("://", StringComparison.Ordinal))
            {
                // http://, git://, file:// and anything else with a scheme.
                return false;
            }
            else
            {
                // scp-style: git@github.com:owner/repo
                const string scpPrefix = "git@";
                if (!url.StartsWith(scpPrefix, StringComparison.Ordinal))
                {
                    return false;
                }

                int colon = url.IndexOf(':', scpPrefix.Length);
                if (colon < 0
                    || !string.Equals(url.Substring(scpPrefix.Length, colon - scpPrefix.Length), Host, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                path = url.Substring(colon + 1);
            }

            return TryParsePath(path, out repo);
        }

        /// <summary>
        /// Parses exactly <c>owner/repo</c> as a person types it (not a URL; no <c>.git</c> or trailing
        /// <c>/</c> is removed). The one owner/name rule, shared with <see cref="RepoWatchSettings"/>.
        /// </summary>
        public static bool TryParseFullName(string? fullName, out GitHubRepoRef repo)
        {
            repo = default;
            if (string.IsNullOrEmpty(fullName))
            {
                return false;
            }

            string[] parts = fullName.Split('/');
            if (parts.Length != 2 || !IsValidOwner(parts[0]) || !IsValidName(parts[1]))
            {
                return false;
            }

            repo = new GitHubRepoRef(parts[0], parts[1]);
            return true;
        }

        public bool Equals(GitHubRepoRef other)
            => StringComparer.OrdinalIgnoreCase.Equals(Owner, other.Owner)
               && StringComparer.OrdinalIgnoreCase.Equals(Name, other.Name);

        public override bool Equals(object? obj) => obj is GitHubRepoRef other && Equals(other);

        public override int GetHashCode()
            => HashCode.Combine(
                Owner is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Owner),
                Name is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Name));

        public override string ToString() => FullName;

        // ---- github.com pages of this repository: the one URL rule, used to build links, to keep
        // ---- an html_url from the API, and as the menu's last gate before the shell opens a link.

        private static readonly Regex PagePathPattern = new(
            @"^/(?:(?:issues|pull)/([0-9]{1,18})|issues|pulls)$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary><c>https://github.com/{owner}/{name}</c>.</summary>
        public string WebUrl => $"https://github.com/{FullName}";

        /// <summary>The repository's issue list.</summary>
        public string IssuesUrl => WebUrl + "/issues";

        /// <summary>The repository's pull request list.</summary>
        public string PullsUrl => WebUrl + "/pulls";

        /// <summary>One issue (<c>/issues/{n}</c>) or pull request (<c>/pull/{n}</c>).</summary>
        public string ItemUrl(long number, bool isPullRequest)
            => $"{WebUrl}/{(isPullRequest ? "pull" : "issues")}/{number.ToString(CultureInfo.InvariantCulture)}";

        /// <summary>
        /// True only for <see cref="WebUrl"/> followed by exactly <c>/issues/{n}</c>, <c>/pull/{n}</c>,
        /// <c>/issues</c> or <c>/pulls</c> (case-insensitive). The prefix comparison is on the raw string,
        /// so a user-info part, a port, another host (<c>github.com.evil.com</c>) or scheme cannot pass; the
        /// anchored remainder rules out a query, a fragment, an escape or a dot segment.
        /// </summary>
        public bool IsPageUrl(string? url) => TryMatchPage(url, out _);

        /// <summary>
        /// True only for this repository's issue or pull request page for exactly <paramref name="number"/>.
        /// An html_url from the API is kept only when this holds: it is opened in a browser, and it came
        /// from the network.
        /// </summary>
        public bool IsItemUrl(string? url, long number) => TryMatchPage(url, out long? n) && n == number;

        private bool TryMatchPage(string? url, out long? itemNumber)
        {
            itemNumber = null;
            if (string.IsNullOrEmpty(url) || Owner is null || Name is null)
            {
                return false;
            }

            string prefix = WebUrl;
            if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var match = PagePathPattern.Match(url.Substring(prefix.Length));
            if (!match.Success)
            {
                return false;
            }

            if (match.Groups[1].Success)
            {
                if (!long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long n))
                {
                    return false;
                }

                itemNumber = n;
            }

            return true;
        }

        /// <summary>
        /// Splits <c>[user@]host[:port]/path</c>. The user part is everything before the LAST
        /// <c>@</c> in the authority, so a token containing <c>@</c> cannot shift the host.
        /// </summary>
        private static bool TrySplitAuthority(string rest, out string? user, out string hostPort, out string path)
        {
            user = null;
            hostPort = string.Empty;
            path = string.Empty;

            int slash = rest.IndexOf('/');
            if (slash <= 0)
            {
                return false;
            }

            string authority = rest.Substring(0, slash);
            path = rest.Substring(slash + 1);

            int at = authority.LastIndexOf('@');
            if (at >= 0)
            {
                user = authority.Substring(0, at);
                authority = authority.Substring(at + 1);
            }

            hostPort = authority;
            return hostPort.Length > 0;
        }

        /// <summary><c>owner/repo[.git][/]</c>: exactly two segments, each valid on GitHub.</summary>
        private static bool TryParsePath(string path, out GitHubRepoRef repo)
        {
            repo = default;

            if (path.EndsWith('/'))
            {
                path = path.Substring(0, path.Length - 1);
            }

            if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(0, path.Length - 4);
            }

            string[] parts = path.Split('/');
            if (parts.Length != 2 || !IsValidOwner(parts[0]) || !IsValidName(parts[1]))
            {
                return false;
            }

            repo = new GitHubRepoRef(parts[0], parts[1]);
            return true;
        }

        /// <summary>Letters, digits and single hyphens; at most 39; no leading or trailing hyphen.</summary>
        private static bool IsValidOwner(string owner)
        {
            if (owner.Length == 0 || owner.Length > MaxOwnerLength || owner[0] == '-' || owner[^1] == '-')
            {
                return false;
            }

            for (int i = 0; i < owner.Length; i++)
            {
                char c = owner[i];
                if (c == '-')
                {
                    if (owner[i - 1] == '-')
                    {
                        return false;
                    }
                }
                else if (!char.IsAsciiLetterOrDigit(c))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Letters, digits, <c>-</c>, <c>_</c>, <c>.</c>; at most 100; not <c>.</c> or <c>..</c>.</summary>
        private static bool IsValidName(string name)
        {
            if (name.Length == 0 || name.Length > MaxNameLength || name == "." || name == "..")
            {
                return false;
            }

            foreach (char c in name)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_' && c != '.')
                {
                    return false;
                }
            }

            return true;
        }
    }
}
