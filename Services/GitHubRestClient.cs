#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>How a <see cref="GitHubRestClient.GetOpenIssuesAsync"/> call ended.</summary>
    public enum RepoIssuesStatus
    {
        /// <summary>200: <see cref="RepoIssuesResult.Items"/> holds the open issues and PRs.</summary>
        Ok = 0,

        /// <summary>304: nothing changed since the ETag that was sent. Does not count against the rate limit.</summary>
        NotModified,

        /// <summary>403/429 rate limit (primary or secondary). Wait until <see cref="RepoIssuesResult.RetryAfterUtc"/>.</summary>
        RateLimited,

        /// <summary>401: the token is invalid, expired or revoked.</summary>
        Unauthorized,

        /// <summary>404: no such repository, or a private one the token cannot see.</summary>
        NotFound,

        /// <summary>Anything else: another status, a timeout, a network error, a body that would not parse.</summary>
        Failed,
    }

    /// <summary>
    /// Outcome of one <see cref="GitHubRestClient.GetOpenIssuesAsync"/> call.
    /// <para><see cref="Message"/> is for a human and never carries the token or the Authorization
    /// header: it may be shown in the UI or written to a log, and <c>debug_logs</c> puts a log line in
    /// every agent's reach.</para>
    /// </summary>
    public sealed class RepoIssuesResult
    {
        public RepoIssuesStatus Status { get; init; }

        /// <summary>The open issues and PRs, newest first. Empty unless <see cref="Status"/> is Ok.</summary>
        public IReadOnlyList<RepoActivityItem> Items { get; init; } = Array.Empty<RepoActivityItem>();

        /// <summary>
        /// The response's ETag, to send back as the next call's <c>etag</c>. On a 304 without one, the
        /// ETag that was sent (it is still the current one).
        /// </summary>
        public string? ETag { get; init; }

        /// <summary>
        /// True when GitHub returned a full page (<see cref="GitHubRestClient.PageSize"/> items): there
        /// may be more open items than were listed, so "not in this list" does not mean "closed".
        /// </summary>
        public bool IsFullPage => Items.Count == GitHubRestClient.PageSize;

        /// <summary>When a rate-limited caller may try again (UTC). Set only for RateLimited.</summary>
        public DateTime? RetryAfterUtc { get; init; }

        /// <summary>GitHub's <c>x-ratelimit-remaining</c>, when the response carried it.</summary>
        public int? RateLimitRemaining { get; init; }

        /// <summary>Human-readable summary of the outcome. Never contains the token.</summary>
        public string Message { get; init; } = string.Empty;
    }

    /// <summary>
    /// Minimal GitHub REST client for the "new issues and PRs" feature (task df58b35d, GitHub #53):
    /// one call, the repository's open issues and pull requests, newest first.
    ///
    /// <para><b>Conditional requests.</b> The caller hands back the previous ETag; a 304 costs nothing
    /// against the rate limit, which is what makes polling affordable without a token (60/hour).</para>
    ///
    /// <para><b>⚠️ The token never escapes.</b> It goes into the Authorization header and nowhere else:
    /// not into <see cref="RepoIssuesResult.Message"/>, not into an exception message, not into a log
    /// (same rule and reason as <see cref="GitHubAppTokenService"/>). As a backstop every message is
    /// scrubbed of the token before it is returned.</para>
    ///
    /// <para><b>Everything in the response is untrusted.</b> Anyone can open an issue, so titles,
    /// logins and label names go through <see cref="SessionCatalog.ToDisplayText"/>, and an
    /// <c>html_url</c> is kept only when it is this repository's own issue or pull page on
    /// github.com (a renamed repository's new URL is dropped too; the item is still listed).</para>
    ///
    /// <para><b>No network in tests:</b> the internal constructor takes the message handler, the
    /// timeout and the clock.</para>
    /// </summary>
    public sealed class GitHubRestClient : IDisposable, IRepoIssuesClient
    {
        /// <summary>Items per page requested, GitHub's maximum.</summary>
        public const int PageSize = 100;

        internal const string ApiBase = "https://api.github.com";
        internal const int TitleMaxChars = 200;
        private const int LabelMaxChars = 50;
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

        /// <summary>When a rate-limited response says nothing about when to retry.</summary>
        private static readonly TimeSpan FallbackRetryDelay = TimeSpan.FromSeconds(60);

        private static readonly HttpClient SharedHttp = new() { Timeout = DefaultTimeout };

        private readonly HttpClient _http;
        private readonly bool _ownsHttp;
        private readonly Func<DateTimeOffset> _utcNow;

        /// <summary>Production: the shared process-wide <see cref="HttpClient"/>.</summary>
        public GitHubRestClient()
        {
            _http = SharedHttp;
            _ownsHttp = false;
            _utcNow = () => DateTimeOffset.UtcNow;
        }

        /// <summary>Test seam: a stub handler, a short timeout, a fixed clock.</summary>
        internal GitHubRestClient(HttpMessageHandler handler, TimeSpan? timeout = null, Func<DateTimeOffset>? utcNow = null)
        {
            _http = new HttpClient(handler, disposeHandler: false) { Timeout = timeout ?? DefaultTimeout };
            _ownsHttp = true;
            _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        }

        public void Dispose()
        {
            if (_ownsHttp)
                _http.Dispose();
        }

        /// <summary>
        /// Lists <paramref name="owner"/>/<paramref name="repo"/>'s open issues and pull requests,
        /// newest first, one page of <see cref="PageSize"/>. Pass the previous result's ETag as
        /// <paramref name="etag"/> to get NotModified when nothing changed. <paramref name="token"/> is
        /// optional (public repositories work without one, at a lower rate limit).
        /// <para>Never throws for a GitHub or network failure (see <see cref="RepoIssuesStatus"/>); the
        /// caller's own cancellation of <paramref name="ct"/> propagates as
        /// <see cref="OperationCanceledException"/>.</para>
        /// </summary>
        public async Task<RepoIssuesResult> GetOpenIssuesAsync(
            string owner, string repo, string? token, string? etag, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
                return Fail(RepoIssuesStatus.Failed, "Repository owner and name are required.", token);

            var url = $"{ApiBase}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/issues"
                      + $"?state=open&sort=created&direction=desc&per_page={PageSize}";

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("MultiTerminal", null));
                if (!string.IsNullOrEmpty(token))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (!string.IsNullOrEmpty(etag))
                    request.Headers.TryAddWithoutValidation("If-None-Match", etag);

                using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                return await MapResponseAsync(response, owner, repo, token, etag, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // HttpClient.Timeout surfaces as a TaskCanceledException the caller did not ask for.
                return Fail(RepoIssuesStatus.Failed, $"GitHub did not answer within {_http.Timeout.TotalSeconds:0}s.", token);
            }
            catch (HttpRequestException ex)
            {
                return Fail(RepoIssuesStatus.Failed, $"Network error contacting GitHub: {ex.Message}", token);
            }
        }

        private async Task<RepoIssuesResult> MapResponseAsync(
            HttpResponseMessage response, string owner, string repo, string? token, string? sentEtag, CancellationToken ct)
        {
            var remaining = ReadIntHeader(response, "x-ratelimit-remaining");
            var responseEtag = response.Headers.ETag?.ToString();
            var where = $"{owner}/{repo}";

            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    break;

                case HttpStatusCode.NotModified:
                    return new RepoIssuesResult
                    {
                        Status = RepoIssuesStatus.NotModified,
                        ETag = responseEtag ?? sentEtag,
                        RateLimitRemaining = remaining,
                        Message = $"No change in {where}.",
                    };

                case HttpStatusCode.Unauthorized:
                    return Fail(RepoIssuesStatus.Unauthorized,
                        $"GitHub rejected the token for {where} (401): it is invalid, expired or revoked.", token, remaining);

                case HttpStatusCode.NotFound:
                    return Fail(RepoIssuesStatus.NotFound,
                        $"GitHub found no repository {where} (404). A private repository also answers 404 "
                        + "when the token is missing or cannot see it.", token, remaining);

                case HttpStatusCode.Forbidden:
                case HttpStatusCode.TooManyRequests:
                    if (response.StatusCode == HttpStatusCode.TooManyRequests
                        || remaining == 0
                        || response.Headers.RetryAfter != null)
                    {
                        var retryAt = RetryAfter(response);
                        return new RepoIssuesResult
                        {
                            Status = RepoIssuesStatus.RateLimited,
                            RetryAfterUtc = retryAt,
                            RateLimitRemaining = remaining,
                            Message = Scrub(
                                $"GitHub rate limit reached for {where}; retry after "
                                + $"{retryAt.ToString("u", CultureInfo.InvariantCulture)}.", token),
                        };
                    }

                    return Fail(RepoIssuesStatus.Failed,
                        $"GitHub refused access to {where} (403 forbidden).", token, remaining);

                default:
                    return Fail(RepoIssuesStatus.Failed,
                        $"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase} for {where}.", token, remaining);
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            List<RepoActivityItem>? items = ParseItems(body, owner, repo);
            if (items == null)
                return Fail(RepoIssuesStatus.Failed, $"GitHub's answer for {where} could not be read.", token, remaining);

            return new RepoIssuesResult
            {
                Status = RepoIssuesStatus.Ok,
                Items = items,
                ETag = responseEtag,
                RateLimitRemaining = remaining,
                Message = $"{items.Count} open issue(s) and pull request(s) in {where}.",
            };
        }

        /// <summary>
        /// The issues array, or null when the body is not one (malformed JSON or another shape).
        /// Elements without a positive number are skipped rather than failing the whole page.
        /// </summary>
        internal static List<RepoActivityItem>? ParseItems(string body, string owner, string repo)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    return null;

                var items = new List<RepoActivityItem>();
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object
                        || !el.TryGetProperty("number", out var numberEl)
                        || numberEl.ValueKind != JsonValueKind.Number
                        || !numberEl.TryGetInt64(out var number)
                        || number <= 0)
                    {
                        continue;
                    }

                    var isPr = el.TryGetProperty("pull_request", out var prEl) && prEl.ValueKind != JsonValueKind.Null;
                    items.Add(new RepoActivityItem
                    {
                        Number = number,
                        Title = SessionCatalog.ToDisplayText(GetString(el, "title"), TitleMaxChars) ?? "(no title)",
                        Author = SessionCatalog.ToDisplayText(
                            el.TryGetProperty("user", out var userEl) && userEl.ValueKind == JsonValueKind.Object
                                ? GetString(userEl, "login")
                                : null) ?? "unknown",
                        CreatedAtUtc = ParseUtc(GetString(el, "created_at")),
                        IsPullRequest = isPr,
                        IsDraft = isPr && el.TryGetProperty("draft", out var draftEl) && draftEl.ValueKind == JsonValueKind.True,
                        HtmlUrl = SafeHtmlUrl(GetString(el, "html_url"), owner, repo, number),
                        Labels = ParseLabels(el),
                    });
                }

                return items;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Keeps <paramref name="url"/> only when it is exactly
        /// <c>https://github.com/{owner}/{repo}/(issues|pull)/{number}</c> (case-insensitive) for this
        /// item's own number. Anything else (another host, another repository, a query, a fragment, a
        /// different number) is null: the value is opened in a browser, and it came from the network.
        /// The rule itself is <see cref="GitHubRepoRef.IsItemUrl"/>.
        /// </summary>
        internal static string? SafeHtmlUrl(string? url, string owner, string repo, long number)
            => GitHubRepoRef.TryParseFullName($"{owner}/{repo}", out var repoRef) && repoRef.IsItemUrl(url, number)
                ? url
                : null;

        /// <summary>
        /// An item read back from the stored snapshot, cleaned exactly as a fresh one from the API is:
        /// title, author and labels through <see cref="SessionCatalog.ToDisplayText"/> with the same
        /// caps, and the link kept only if it is this item's page. The snapshot is a database column any
        /// local process can write, so what it holds is not trusted just because MT wrote it first.
        /// </summary>
        internal static RepoActivityItem CleanStored(RepoActivityItem item, GitHubRepoRef repo) => new()
        {
            Number = item.Number,
            Title = SessionCatalog.ToDisplayText(item.Title, TitleMaxChars) ?? "(no title)",
            Author = SessionCatalog.ToDisplayText(item.Author) ?? "unknown",
            CreatedAtUtc = item.CreatedAtUtc,
            IsPullRequest = item.IsPullRequest,
            IsDraft = item.IsDraft,
            HtmlUrl = repo.IsItemUrl(item.HtmlUrl, item.Number) ? item.HtmlUrl : null,
            Labels = (item.Labels ?? Array.Empty<string>())
                .Select(l => SessionCatalog.ToDisplayText(l, LabelMaxChars))
                .Where(l => l != null)
                .Select(l => l!)
                .ToList(),
        };

        private static IReadOnlyList<string> ParseLabels(JsonElement el)
        {
            if (!el.TryGetProperty("labels", out var labelsEl) || labelsEl.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            var labels = new List<string>();
            foreach (var label in labelsEl.EnumerateArray())
            {
                // GitHub sends label objects; the API also documents a bare-string form.
                var raw = label.ValueKind switch
                {
                    JsonValueKind.Object => GetString(label, "name"),
                    JsonValueKind.String => label.GetString(),
                    _ => null,
                };
                var name = SessionCatalog.ToDisplayText(raw, LabelMaxChars);
                if (name != null)
                    labels.Add(name);
            }

            return labels;
        }

        private static string? GetString(JsonElement el, string property) =>
            el.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        private static DateTime ParseUtc(string? value) =>
            value != null && DateTimeOffset.TryParse(
                value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed.UtcDateTime
                : default;

        /// <summary>
        /// <c>retry-after</c> (seconds or an HTTP date), else <c>x-ratelimit-reset</c> (unix seconds),
        /// else now + 60s, as GitHub's rate-limit guidance orders them.
        /// </summary>
        private DateTime RetryAfter(HttpResponseMessage response)
        {
            var now = _utcNow();
            var retryAfter = response.Headers.RetryAfter;
            if (retryAfter?.Delta is TimeSpan delta)
                return (now + delta).UtcDateTime;
            if (retryAfter?.Date is DateTimeOffset date)
                return date.UtcDateTime;

            if (response.Headers.TryGetValues("x-ratelimit-reset", out var values)
                && long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var unix))
            {
                try
                {
                    return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
                }
                catch (ArgumentOutOfRangeException)
                {
                    // An absurd reset value falls through to the default delay.
                }
            }

            return (now + FallbackRetryDelay).UtcDateTime;
        }

        private static int? ReadIntHeader(HttpResponseMessage response, string name) =>
            response.Headers.TryGetValues(name, out var values)
            && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n
                : null;

        private static RepoIssuesResult Fail(RepoIssuesStatus status, string message, string? token, int? remaining = null) =>
            new() { Status = status, Message = Scrub(message, token), RateLimitRemaining = remaining };

        /// <summary>
        /// Backstop for the rule in the class summary: no message built here should contain the token,
        /// but an exception message is not ours to vouch for.
        /// </summary>
        private static string Scrub(string message, string? token) =>
            string.IsNullOrEmpty(token) ? message : message.Replace(token, "***", StringComparison.Ordinal);
    }
}
