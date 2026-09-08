using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Registers the GitHub App through GitHub's App Manifest flow (task b42b1883, item 3).
    ///
    /// <para><b>Why a manifest rather than written instructions.</b> Creating a GitHub App is a browser
    /// flow by design — there is no "create app" REST endpoint any PAT can call, and the private key is
    /// generated inside that flow. So this cannot be automated end to end. The manifest reduces it to
    /// one click: MT states the App's name, permissions and redirect up front, the Owner presses
    /// "Create GitHub App" once, and GitHub hands back a code that MT exchanges for the id, key and
    /// secrets. The alternative — a page of manual steps ending in "paste the private key here" — is
    /// how a key ends up in a clipboard, a chat log, or a text file.</para>
    ///
    /// <para><b>The click is deliberately the Owner's.</b> This mints repository write access under
    /// their account. MT prepares everything and stops; it does not drive the browser through the
    /// confirmation. A credential that grants write access should be created by a human action, not by
    /// something an agent triggered.</para>
    ///
    /// <para><b>⚠️ State validation is the security boundary of this file.</b> The callback endpoint is
    /// reachable by anything that can talk to localhost, and MT's REST API is unauthenticated by design
    /// (established by task c9285d2a's security audit). Without a state check, any local process could
    /// call the callback with a code from an App IT created and have MT store that App's credentials —
    /// silently repointing every future agent comment at an identity someone else controls. States are
    /// therefore random, single-use, and short-lived.</para>
    /// </summary>
    public sealed class GitHubAppManifestService
    {
        /// <summary>
        /// How long an issued state stays valid. Long enough for a human to read the GitHub
        /// confirmation page and decide; short enough that an abandoned attempt does not leave a
        /// usable slot open for the rest of the session.
        /// </summary>
        internal static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(15);

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

        private readonly SettingsService _settings;
        private readonly Func<DateTimeOffset> _now;
        private readonly Func<string, CancellationToken, Task<AppRegistration>> _convert;

        /// <summary>Issued states and when they were issued. Entries are REMOVED on use, never reused.</summary>
        private readonly ConcurrentDictionary<string, DateTimeOffset> _pendingStates = new(StringComparer.Ordinal);

        /// <summary>Production constructor: real clock, real GitHub conversion.</summary>
        public GitHubAppManifestService(SettingsService settings)
            : this(settings, () => DateTimeOffset.UtcNow, convert: null)
        {
        }

        /// <summary>Test seam (InternalsVisibleTo → MultiTerminal.Tests).</summary>
        internal GitHubAppManifestService(
            SettingsService settings,
            Func<DateTimeOffset> now,
            Func<string, CancellationToken, Task<AppRegistration>> convert)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _now = now ?? throw new ArgumentNullException(nameof(now));
            _convert = convert ?? ConvertWithGitHubAsync;
        }

        /// <summary>
        /// What GitHub returns from a manifest conversion. <see cref="Pem"/> is live key material and is
        /// handed straight to DPAPI storage — it must never be logged, returned over REST, or rendered
        /// into the browser page.
        /// </summary>
        internal sealed class AppRegistration
        {
            public string Id { get; set; }

            public string Slug { get; set; }

            public string Pem { get; set; }

            public string ClientId { get; set; }

            public string ClientSecret { get; set; }

            public string WebhookSecret { get; set; }
        }

        /// <summary>
        /// Builds the App manifest. Pure and static so the permission set — an explicit Owner decision —
        /// is pinned by a test rather than by whatever the code happened to send.
        /// </summary>
        /// <param name="appName">The App's name; its slug becomes the <c>name[bot]</c> on every comment.</param>
        /// <param name="redirectUrl">Where GitHub returns the temporary code.</param>
        /// <param name="homepageUrl">Shown on the App's public page.</param>
        internal static string BuildManifest(string appName, string redirectUrl, string homepageUrl)
        {
            if (string.IsNullOrWhiteSpace(appName)) throw new ArgumentException("App name is required.", nameof(appName));
            if (string.IsNullOrWhiteSpace(redirectUrl)) throw new ArgumentException("Redirect URL is required.", nameof(redirectUrl));

            var manifest = new
            {
                name = appName,
                url = homepageUrl,
                redirect_url = redirectUrl,

                // Private: installable only by the account that owns it. This App exists to act on two
                // repositories under one account, so there is no reason for anyone else to install it.
                @public = false,

                // Owner decision, 2026-09-08. contents:write is the consequential one — it lets agents
                // push commits as the bot rather than as a person who did not write them, and it also
                // means a leaked hour of token buys code write, not just comments. Chosen deliberately.
                // metadata:read is required by GitHub for essentially every App and is included
                // explicitly rather than left to be inferred.
                default_permissions = new
                {
                    issues = "write",
                    pull_requests = "write",
                    contents = "write",
                    metadata = "read",
                },

                // No webhook and no events: MT polls and acts, nothing calls back into it. An inbound
                // webhook would be a public listener this design does not need and would have to defend.
                default_events = Array.Empty<string>(),
                hook_attributes = new { active = false },
            };

            return JsonSerializer.Serialize(manifest);
        }

        /// <summary>
        /// Issues a fresh single-use state token for a registration attempt.
        /// <para>Random rather than sequential, because this is the only thing standing between the
        /// callback and a foreign process planting an App's credentials.</para>
        /// </summary>
        internal string IssueState()
        {
            Span<byte> raw = stackalloc byte[32];
            RandomNumberGenerator.Fill(raw);
            string state = Convert.ToHexString(raw);
            _pendingStates[state] = _now();
            PruneExpiredStates();
            return state;
        }

        /// <summary>
        /// Consumes a state: true only when it was issued by THIS service, has not expired, and has not
        /// already been used. Removal happens on the first successful check, so a replay of the same
        /// callback URL cannot register a second time.
        /// </summary>
        internal bool TryConsumeState(string state)
        {
            if (string.IsNullOrWhiteSpace(state)) return false;
            if (!_pendingStates.TryRemove(state, out DateTimeOffset issued)) return false;
            return _now() - issued <= StateLifetime;
        }

        private void PruneExpiredStates()
        {
            DateTimeOffset cutoff = _now() - StateLifetime;
            foreach (var kvp in _pendingStates)
            {
                if (kvp.Value < cutoff)
                    _pendingStates.TryRemove(kvp.Key, out _);
            }
        }

        /// <summary>
        /// Completes registration: validates the state, exchanges the code, and stores the result.
        /// </summary>
        /// <returns>The App slug, for display. Never the key material.</returns>
        /// <exception cref="InvalidOperationException">State rejected, or GitHub returned nothing usable.</exception>
        public async Task<string> CompleteRegistrationAsync(string code, string state, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(code))
                throw new InvalidOperationException("GitHub returned no code — the registration was not completed.");

            // Checked BEFORE the exchange, deliberately. Converting first and validating after would
            // still burn the code, and would mean an unsolicited callback could make MT talk to GitHub.
            if (!TryConsumeState(state))
            {
                throw new InvalidOperationException(
                    "This registration callback did not come from a request MultiTerminal issued, or it "
                    + "has expired or already been used. Start the registration again from Settings.");
            }

            AppRegistration reg = await _convert(code, ct).ConfigureAwait(false);

            if (reg == null || string.IsNullOrWhiteSpace(reg.Id) || string.IsNullOrWhiteSpace(reg.Pem))
                throw new InvalidOperationException("GitHub's response did not include an app id and private key.");

            // Straight into DPAPI storage. The PEM does not pass through a log line or a response body
            // on the way, and the caller receives only the slug.
            _settings.SetGitHubAppId(reg.Id);
            _settings.SetGitHubAppSlug(reg.Slug);
            _settings.SetGitHubAppPrivateKeyPem(reg.Pem);
            _settings.SetGitHubAppClientId(reg.ClientId);
            _settings.SetGitHubAppClientSecret(reg.ClientSecret);
            _settings.SetGitHubAppWebhookSecret(reg.WebhookSecret);

            return reg.Slug;
        }

        /// <summary>
        /// The real conversion. Replaced wholesale in tests — the only part of this service needing network.
        /// </summary>
        private static async Task<AppRegistration> ConvertWithGitHubAsync(string code, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://api.github.com/app-manifests/{Uri.EscapeDataString(code)}/conversions");

            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("MultiTerminal", "1.0"));

            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // Status only. The success body contains the private key, so no code path here may fold
                // a response body into an exception without knowing which body it has.
                throw new InvalidOperationException(
                    $"GitHub refused to convert the app manifest: {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;

            return new AppRegistration
            {
                Id = root.TryGetProperty("id", out JsonElement id) ? id.ToString() : null,
                Slug = root.TryGetProperty("slug", out JsonElement slug) ? slug.GetString() : null,
                Pem = root.TryGetProperty("pem", out JsonElement pem) ? pem.GetString() : null,
                ClientId = root.TryGetProperty("client_id", out JsonElement cid) ? cid.GetString() : null,
                ClientSecret = root.TryGetProperty("client_secret", out JsonElement cs) ? cs.GetString() : null,
                WebhookSecret = root.TryGetProperty("webhook_secret", out JsonElement ws) ? ws.GetString() : null,
            };
        }
    }
}
