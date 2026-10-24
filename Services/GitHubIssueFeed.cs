using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultiTerminal.Services
{
    /// <summary>One issue or pull request as the watcher needs it (task 92eeeb47).</summary>
    public sealed class GitHubItem
    {
        public int Number { get; set; }
        public bool IsPullRequest { get; set; }

        /// <summary>Untrusted: control and format characters stripped, length capped.</summary>
        public string Title { get; set; }

        /// <summary>Always an https://github.com/ URL, or null.</summary>
        public string Url { get; set; }

        /// <summary>Untrusted, sanitized like <see cref="Title"/>.</summary>
        public string Author { get; set; }

        public DateTime? CreatedAtUtc { get; set; }
    }

    /// <summary>The result of one fetch: the items, or why there are none.</summary>
    public sealed class GitHubFeedResult
    {
        public bool Ok { get; private set; }
        public string Error { get; private set; }
        public IReadOnlyList<GitHubItem> Items { get; private set; } = Array.Empty<GitHubItem>();

        public static GitHubFeedResult Success(IReadOnlyList<GitHubItem> items) => new GitHubFeedResult { Ok = true, Items = items };
        public static GitHubFeedResult Fail(string error) => new GitHubFeedResult { Ok = false, Error = error };
    }

    /// <summary>
    /// Reads the newest issues and pull requests of a repository from the GitHub REST API (task
    /// 92eeeb47). One request per check: <c>GET /repos/{o}/{r}/issues?state=all&amp;sort=created</c>,
    /// newest first, which lists pull requests too (they carry a <c>pull_request</c> member). Everything
    /// in the response is untrusted and is validated or sanitized before anything else sees it.
    /// </summary>
    public sealed class GitHubIssueFeed
    {
        /// <summary>How many of the newest items one check reads.</summary>
        public const int PageSize = 50;

        /// <summary>A response larger than this is refused, not read.</summary>
        public const int MaxResponseBytes = 4 * 1024 * 1024;

        public const int MaxTitleLength = 200;
        private const int MaxAuthorLength = 39;

        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

        private readonly HttpClient _http;

        /// <param name="handler">Null for the real network; tests pass a stub.</param>
        public GitHubIssueFeed(HttpMessageHandler handler = null)
        {
            _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            _http.Timeout = Timeout.InfiniteTimeSpan; // the per-request token below is the one timeout
        }

        public async Task<GitHubFeedResult> FetchNewestAsync(GitHubRepoRef repo, string token, CancellationToken cancellationToken = default)
        {
            if (repo == null) return GitHubFeedResult.Fail("No GitHub repository.");
            if (string.IsNullOrWhiteSpace(token)) return GitHubFeedResult.Fail("No GitHub token.");

            // Owner and name are validated by GitHubRepoRef to GitHub's own character sets, so they
            // are safe in the path; escaped anyway.
            string url = "https://api.github.com/repos/" + Uri.EscapeDataString(repo.Owner) + "/" + Uri.EscapeDataString(repo.Name)
                         + "/issues?state=all&sort=created&direction=desc&per_page=" + PageSize;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.UserAgent.ParseAdd("MultiTerminal");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    return GitHubFeedResult.Fail("GitHub refused the account's token (401). Check the source control account.");
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return GitHubFeedResult.Fail($"GitHub has no repository {repo.FullName} that this account can see (404).");
                if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429)
                    return GitHubFeedResult.Fail($"GitHub refused the request ({(int)response.StatusCode}); rate limited or not allowed.");
                if (!response.IsSuccessStatusCode)
                    return GitHubFeedResult.Fail($"GitHub returned {(int)response.StatusCode}.");

                if (response.Content.Headers.ContentLength > MaxResponseBytes)
                    return GitHubFeedResult.Fail("GitHub's response was too large to read.");

                byte[] body = await ReadCappedAsync(response.Content, timeout.Token).ConfigureAwait(false);
                if (body == null)
                    return GitHubFeedResult.Fail("GitHub's response was too large to read.");

                return Parse(body);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return GitHubFeedResult.Fail("GitHub did not answer in time.");
            }
            catch (HttpRequestException ex)
            {
                return GitHubFeedResult.Fail("Network error: " + Sanitize(ex.Message, 200));
            }
        }

        private static async Task<byte[]> ReadCappedAsync(HttpContent content, CancellationToken ct)
        {
            using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes) return null;
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }

        /// <summary>Pure: the items in a response body. Malformed entries are skipped, not guessed.</summary>
        internal static GitHubFeedResult Parse(byte[] body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    return GitHubFeedResult.Fail("GitHub's response was not a list.");

                var items = new List<GitHubItem>();
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    if (!e.TryGetProperty("number", out var n) || n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out int number) || number <= 0)
                        continue;

                    string title = e.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                    string htmlUrl = e.TryGetProperty("html_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                    string author = e.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
                                    && user.TryGetProperty("login", out var l) && l.ValueKind == JsonValueKind.String
                        ? l.GetString() : null;
                    DateTime? created = e.TryGetProperty("created_at", out var c) && c.ValueKind == JsonValueKind.String
                                        && c.TryGetDateTime(out var dt) ? dt.ToUniversalTime() : (DateTime?)null;

                    items.Add(new GitHubItem
                    {
                        Number = number,
                        IsPullRequest = e.TryGetProperty("pull_request", out var pr) && pr.ValueKind == JsonValueKind.Object,
                        Title = Sanitize(title, MaxTitleLength) ?? "(no title)",
                        Url = SafeGitHubUrl(htmlUrl),
                        Author = Sanitize(author, MaxAuthorLength),
                        CreatedAtUtc = created,
                    });
                }

                return GitHubFeedResult.Success(items);
            }
            catch (JsonException)
            {
                return GitHubFeedResult.Fail("GitHub's response was not valid JSON.");
            }
        }

        /// <summary>
        /// Removes control and format characters (bidi overrides, zero-width), collapses whitespace
        /// and caps the length. Untrusted text ends up on a board card and in a phone notification.
        /// </summary>
        internal static string Sanitize(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var sb = new StringBuilder(Math.Min(value.Length, maxLength));
            bool lastSpace = false;
            foreach (char ch in value)
            {
                var cat = char.GetUnicodeCategory(ch);
                if (cat == System.Globalization.UnicodeCategory.Control || cat == System.Globalization.UnicodeCategory.Format)
                {
                    if (!lastSpace && sb.Length > 0) { sb.Append(' '); lastSpace = true; }
                    continue;
                }
                if (char.IsWhiteSpace(ch))
                {
                    if (!lastSpace && sb.Length > 0) { sb.Append(' '); lastSpace = true; }
                    continue;
                }
                sb.Append(ch);
                lastSpace = false;
                if (sb.Length >= maxLength) break;
            }
            string s = sb.ToString().Trim();
            return s.Length == 0 ? null : s;
        }

        /// <summary>The URL only when it is an https://github.com/ page; anything else is dropped.</summary>
        internal static string SafeGitHubUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 500) return null;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
            if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return null;
            return uri.AbsoluteUri;
        }
    }
}
