using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MultiTerminal.Models;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// <see cref="GitHubRestClient"/> (task df58b35d, GitHub #53). No network: every fact runs against
    /// <see cref="StubHandler"/>, which records the request and answers with a canned response.
    /// </summary>
    public sealed class GitHubRestClientTests
    {
        private const string Token = "ghp_TESTTOKEN_0123456789abcdef";
        private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

        private const string MixedPayload = """
        [
          {
            "number": 12,
            "title": "Fix ‮gnp.exe‬ the​ crash",
            "user": { "login": "alice" },
            "created_at": "2026-10-22T09:30:00Z",
            "html_url": "https://github.com/ClarionLive/multiterminal/pull/12",
            "draft": true,
            "pull_request": { "url": "https://api.github.com/repos/ClarionLive/multiterminal/pulls/12" },
            "labels": [ { "name": "bug" }, { "name": "ui" } ]
          },
          {
            "number": 11,
            "title": "Feature request",
            "user": { "login": "bob" },
            "created_at": "2026-10-21T08:00:00Z",
            "html_url": "https://github.com/ClarionLive/multiterminal/issues/11",
            "labels": []
          },
          {
            "number": 10,
            "title": "Non-draft PR",
            "user": { "login": "carol" },
            "created_at": "2026-10-20T08:00:00Z",
            "html_url": "https://github.com/ClarionLive/multiterminal/pull/10",
            "draft": false,
            "pull_request": {}
          },
          { "title": "no number, skipped" }
        ]
        """;

        private delegate Task<HttpResponseMessage> Responder(HttpRequestMessage request, CancellationToken ct);

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Responder _respond;

            public StubHandler(Responder respond) => _respond = respond;

            public HttpRequestMessage LastRequest { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                LastRequest = request;
                return _respond(request, cancellationToken);
            }
        }

        /// <summary>A canned answer: <paramref name="status"/>, <paramref name="body"/>, then any extra headers.</summary>
        private static Responder Respond(HttpStatusCode status, string body = "[]", Action<HttpResponseMessage> configure = null) =>
            (_, _) =>
            {
                var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                configure?.Invoke(response);
                return Task.FromResult(response);
            };

        /// <summary>One call through a fresh client; returns the result and the request the stub saw.</summary>
        private static async Task<(RepoIssuesResult Result, HttpRequestMessage Request)> RunAsync(
            Responder respond, string token = Token, string etag = null, TimeSpan? timeout = null,
            CancellationToken ct = default, string owner = "ClarionLive", string repo = "multiterminal")
        {
            using var handler = new StubHandler(respond);
            using var client = new GitHubRestClient(handler, timeout, () => Now);
            var result = await client.GetOpenIssuesAsync(owner, repo, token, etag, ct);
            return (result, handler.LastRequest);
        }

        [Fact]
        public async Task Request_has_the_documented_url_and_headers_with_a_token_and_etag()
        {
            var (_, req) = await RunAsync(Respond(HttpStatusCode.OK), etag: "W/\"abc123\"");

            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Equal(
                "https://api.github.com/repos/ClarionLive/multiterminal/issues?state=open&sort=created&direction=desc&per_page=100",
                req.RequestUri.AbsoluteUri);
            Assert.Equal("application/vnd.github+json", req.Headers.Accept.Single().MediaType);
            Assert.Equal("2022-11-28", req.Headers.GetValues("X-GitHub-Api-Version").Single());
            Assert.Equal("MultiTerminal", req.Headers.UserAgent.ToString());
            Assert.Equal("Bearer", req.Headers.Authorization.Scheme);
            Assert.Equal(Token, req.Headers.Authorization.Parameter);
            Assert.Equal("W/\"abc123\"", req.Headers.GetValues("If-None-Match").Single());
        }

        [Fact]
        public async Task Request_without_token_or_etag_sends_neither_header()
        {
            var (_, req) = await RunAsync(Respond(HttpStatusCode.OK), token: null, etag: null);
            Assert.Null(req.Headers.Authorization);
            Assert.False(req.Headers.Contains("If-None-Match"));

            var (_, emptyReq) = await RunAsync(Respond(HttpStatusCode.OK), token: "", etag: "");
            Assert.Null(emptyReq.Headers.Authorization);
            Assert.False(emptyReq.Headers.Contains("If-None-Match"));
        }

        [Fact]
        public async Task Owner_and_repo_are_escaped_into_the_path()
        {
            var (_, req) = await RunAsync(Respond(HttpStatusCode.OK), owner: "a b", repo: "x/../y");
            Assert.StartsWith("https://api.github.com/repos/a%20b/x%2F..%2Fy/issues?", req.RequestUri.AbsoluteUri);
        }

        [Fact]
        public async Task Ok_parses_a_mixed_issue_and_pr_page_and_strips_hostile_characters()
        {
            var (result, _) = await RunAsync(Respond(HttpStatusCode.OK, MixedPayload, r =>
            {
                r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag-1\"", isWeak: true);
                r.Headers.Add("x-ratelimit-remaining", "4999");
            }));

            Assert.Equal(RepoIssuesStatus.Ok, result.Status);
            Assert.Equal("W/\"etag-1\"", result.ETag);
            Assert.Equal(4999, result.RateLimitRemaining);
            Assert.False(result.IsFullPage);
            Assert.Equal(new long[] { 12, 11, 10 }, result.Items.Select(i => i.Number).ToArray());

            var pr = result.Items[0];
            Assert.Equal("Fix gnp.exe the crash", pr.Title);
            Assert.DoesNotContain('‮', pr.Title);
            Assert.DoesNotContain('​', pr.Title);
            Assert.Equal("alice", pr.Author);
            Assert.Equal(new DateTime(2026, 10, 22, 9, 30, 0, DateTimeKind.Utc), pr.CreatedAtUtc);
            Assert.Equal(DateTimeKind.Utc, pr.CreatedAtUtc.Kind);
            Assert.True(pr.IsPullRequest);
            Assert.True(pr.IsDraft);
            Assert.Equal("https://github.com/ClarionLive/multiterminal/pull/12", pr.HtmlUrl);
            Assert.Equal(new[] { "bug", "ui" }, pr.Labels);

            var issue = result.Items[1];
            Assert.False(issue.IsPullRequest);
            Assert.False(issue.IsDraft);
            Assert.Equal("https://github.com/ClarionLive/multiterminal/issues/11", issue.HtmlUrl);
            Assert.Empty(issue.Labels);

            var plainPr = result.Items[2];
            Assert.True(plainPr.IsPullRequest);
            Assert.False(plainPr.IsDraft);
            Assert.Empty(plainPr.Labels);
        }

        [Fact]
        public async Task Missing_title_and_author_fall_back_and_a_long_title_is_capped()
        {
            var longTitle = new string('x', 500);
            var body = $$"""
            [ { "number": 1, "title": "​‮" },
              { "number": 2, "title": "{{longTitle}}", "user": null } ]
            """;
            var (result, _) = await RunAsync(Respond(HttpStatusCode.OK, body));

            Assert.Equal("(no title)", result.Items[0].Title);
            Assert.Equal("unknown", result.Items[0].Author);
            Assert.Equal(GitHubRestClient.TitleMaxChars, result.Items[1].Title.Length);
            Assert.Equal("unknown", result.Items[1].Author);
        }

        [Fact]
        public async Task A_full_page_is_flagged()
        {
            var body = "[" + string.Join(",", Enumerable.Range(1, 100).Select(n => $"{{\"number\":{n},\"title\":\"t\"}}")) + "]";
            var (result, _) = await RunAsync(Respond(HttpStatusCode.OK, body));
            Assert.Equal(100, result.Items.Count);
            Assert.True(result.IsFullPage);
        }

        [Theory]
        [InlineData("https://github.com/Other/multiterminal/issues/5")]
        [InlineData("https://evil.example/ClarionLive/multiterminal/issues/5")]
        [InlineData("http://github.com/ClarionLive/multiterminal/issues/5")]
        [InlineData("https://github.com/ClarionLive/multiterminal-fork/issues/5")]
        [InlineData("https://github.com/ClarionLive/multiterminal/issues/6")]
        [InlineData("https://github.com/ClarionLive/multiterminal/issues/5?x=1")]
        [InlineData("https://github.com/ClarionLive/multiterminal/issues/5#frag")]
        [InlineData("https://github.com/ClarionLive/multiterminal/wiki/5")]
        [InlineData("javascript:alert(1)")]
        public async Task An_html_url_outside_this_repositorys_item_pages_is_dropped(string url)
        {
            var body = JsonSerializer.Serialize(new[] { new Dictionary<string, object> { ["number"] = 5, ["title"] = "t", ["html_url"] = url } });
            var (result, _) = await RunAsync(Respond(HttpStatusCode.OK, body));
            Assert.Equal(RepoIssuesStatus.Ok, result.Status);
            Assert.Single(result.Items);
            Assert.Null(result.Items[0].HtmlUrl);
        }

        [Fact]
        public async Task An_html_url_matching_with_different_case_is_kept()
        {
            var body = """[ { "number": 5, "title": "t", "html_url": "https://GitHub.com/clarionlive/MultiTerminal/Issues/5" } ]""";
            var (result, _) = await RunAsync(Respond(HttpStatusCode.OK, body));
            Assert.Equal("https://GitHub.com/clarionlive/MultiTerminal/Issues/5", result.Items[0].HtmlUrl);
        }

        [Fact]
        public async Task NotModified_returns_no_items_and_keeps_the_etag()
        {
            var (withHeader, _) = await RunAsync(Respond(HttpStatusCode.NotModified, "", r =>
                r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"new\"")), etag: "\"old\"");
            Assert.Equal(RepoIssuesStatus.NotModified, withHeader.Status);
            Assert.Empty(withHeader.Items);
            Assert.Equal("\"new\"", withHeader.ETag);

            var (withoutHeader, _) = await RunAsync(Respond(HttpStatusCode.NotModified, ""), etag: "\"old\"");
            Assert.Equal("\"old\"", withoutHeader.ETag);
        }

        [Fact]
        public async Task Unauthorized_maps_401()
        {
            var (result, _) = await RunAsync(Respond(HttpStatusCode.Unauthorized, "{\"message\":\"Bad credentials\"}"));
            Assert.Equal(RepoIssuesStatus.Unauthorized, result.Status);
            Assert.Empty(result.Items);
        }

        [Fact]
        public async Task NotFound_maps_404_and_mentions_private_repositories()
        {
            var (result, _) = await RunAsync(Respond(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}"));
            Assert.Equal(RepoIssuesStatus.NotFound, result.Status);
            Assert.Contains("private", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Forbidden_with_zero_remaining_is_rate_limited_until_the_reset()
        {
            var reset = Now.AddMinutes(17).ToUnixTimeSeconds();
            var (result, _) = await RunAsync(Respond(HttpStatusCode.Forbidden, "{}", r =>
            {
                r.Headers.Add("x-ratelimit-remaining", "0");
                r.Headers.Add("x-ratelimit-reset", reset.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }));

            Assert.Equal(RepoIssuesStatus.RateLimited, result.Status);
            Assert.Equal(0, result.RateLimitRemaining);
            Assert.Equal(Now.AddMinutes(17).UtcDateTime, result.RetryAfterUtc);
        }

        [Fact]
        public async Task TooManyRequests_with_retry_after_seconds_is_rate_limited()
        {
            var (result, _) = await RunAsync(Respond(HttpStatusCode.TooManyRequests, "{}", r =>
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90))));

            Assert.Equal(RepoIssuesStatus.RateLimited, result.Status);
            Assert.Equal(Now.AddSeconds(90).UtcDateTime, result.RetryAfterUtc);
        }

        [Fact]
        public async Task Forbidden_with_retry_after_is_a_secondary_rate_limit()
        {
            var (result, _) = await RunAsync(Respond(HttpStatusCode.Forbidden, "{}", r =>
            {
                r.Headers.Add("x-ratelimit-remaining", "12");
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            }));

            Assert.Equal(RepoIssuesStatus.RateLimited, result.Status);
            Assert.Equal(Now.AddSeconds(30).UtcDateTime, result.RetryAfterUtc);
        }

        [Fact]
        public async Task Rate_limit_without_any_timing_header_waits_sixty_seconds()
        {
            var (result, _) = await RunAsync(Respond(HttpStatusCode.Forbidden, "{}", r =>
                r.Headers.Add("x-ratelimit-remaining", "0")));

            Assert.Equal(RepoIssuesStatus.RateLimited, result.Status);
            Assert.Equal(Now.AddSeconds(60).UtcDateTime, result.RetryAfterUtc);
        }

        [Fact]
        public async Task Plain_forbidden_is_failed_not_rate_limited()
        {
            var (result, _) = await RunAsync(Respond(HttpStatusCode.Forbidden, "{}", r =>
                r.Headers.Add("x-ratelimit-remaining", "4000")));

            Assert.Equal(RepoIssuesStatus.Failed, result.Status);
            Assert.Null(result.RetryAfterUtc);
            Assert.Contains("forbidden", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Server_error_is_failed()
        {
            var (result, _) = await RunAsync(Respond(HttpStatusCode.InternalServerError, "oops"));
            Assert.Equal(RepoIssuesStatus.Failed, result.Status);
            Assert.Contains("500", result.Message);
            Assert.Empty(result.Items);
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("{\"message\":\"an object, not an array\"}")]
        [InlineData("[ { \"number\": 1, ")]
        public async Task Malformed_body_is_failed(string body)
        {
            var (result, _) = await RunAsync(Respond(HttpStatusCode.OK, body));
            Assert.Equal(RepoIssuesStatus.Failed, result.Status);
            Assert.Empty(result.Items);
        }

        [Fact]
        public async Task Timeout_is_failed_not_thrown()
        {
            Responder handler = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            };

            var (result, _) = await RunAsync(handler, timeout: TimeSpan.FromMilliseconds(100));
            Assert.Equal(RepoIssuesStatus.Failed, result.Status);
            Assert.Contains("did not answer", result.Message);
        }

        [Fact]
        public async Task Network_error_is_failed_not_thrown()
        {
            var (result, _) = await RunAsync(new Responder((_, _) => throw new HttpRequestException("No such host is known.")));
            Assert.Equal(RepoIssuesStatus.Failed, result.Status);
            Assert.Contains("No such host", result.Message);
        }

        [Fact]
        public async Task Callers_cancellation_propagates()
        {
            using var cts = new CancellationTokenSource();
            Responder handler = async (_, ct) =>
            {
                cts.Cancel();
                await Task.Delay(Timeout.Infinite, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => RunAsync(handler, timeout: TimeSpan.FromSeconds(30), ct: cts.Token));
        }

        /// <summary>
        /// Every non-Ok outcome, each echoing the token in its body. Only the last one can actually put
        /// the token in a message (the client never copies a body into one); it is the case that goes red
        /// if the scrub in <c>GitHubRestClient.Fail</c> is removed.
        /// </summary>
        private static readonly Responder[] FailureResponders =
        {
            Respond(HttpStatusCode.Unauthorized, $"{{\"message\":\"{Token}\"}}"),
            Respond(HttpStatusCode.NotFound, Token),
            Respond(HttpStatusCode.Forbidden, Token),
            Respond(HttpStatusCode.Forbidden, Token, r => r.Headers.Add("x-ratelimit-remaining", "0")),
            Respond(HttpStatusCode.TooManyRequests, Token),
            Respond(HttpStatusCode.InternalServerError, Token),
            Respond(HttpStatusCode.OK, Token),
            Respond(HttpStatusCode.NotModified, Token),
            (_, _) => throw new HttpRequestException($"proxy said Authorization: Bearer {Token}"),
        };

        public static IEnumerable<object[]> FailureCases() =>
            Enumerable.Range(0, FailureResponders.Length).Select(i => new object[] { i });

        [Theory]
        [MemberData(nameof(FailureCases))]
        public async Task The_token_never_appears_in_the_message(int caseIndex)
        {
            var (result, _) = await RunAsync(FailureResponders[caseIndex]);
            Assert.NotEqual(RepoIssuesStatus.Ok, result.Status);
            Assert.False(string.IsNullOrWhiteSpace(result.Message));
            Assert.DoesNotContain(Token, result.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Items_round_trip_through_System_Text_Json()
        {
            var items = new List<RepoActivityItem>
            {
                new()
                {
                    Number = 7,
                    Title = "t",
                    Author = "a",
                    CreatedAtUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                    IsPullRequest = true,
                    IsDraft = true,
                    HtmlUrl = "https://github.com/o/r/pull/7",
                    Labels = new[] { "x", "y" },
                },
            };

            var json = JsonSerializer.Serialize(items);
            var back = JsonSerializer.Deserialize<List<RepoActivityItem>>(json);

            var item = Assert.Single(back);
            Assert.Equal(7, item.Number);
            Assert.Equal("t", item.Title);
            Assert.Equal("a", item.Author);
            Assert.Equal(items[0].CreatedAtUtc, item.CreatedAtUtc);
            Assert.True(item.IsPullRequest);
            Assert.True(item.IsDraft);
            Assert.Equal("https://github.com/o/r/pull/7", item.HtmlUrl);
            Assert.Equal(new[] { "x", "y" }, item.Labels);
        }
    }
}
