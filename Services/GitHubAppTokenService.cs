using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Mints short-lived GitHub App installation tokens from the private key held by
    /// <see cref="SettingsService"/> (task b42b1883, item 1).
    ///
    /// <para><b>What this exists to make true:</b> an agent holds a credential that expires within the
    /// hour and is scoped to one installation, while the thing that can mint credentials forever — the
    /// App private key — never leaves this process. The key signs a JWT here and nothing else.</para>
    ///
    /// <para><b>⚠️ The PEM and the JWT must never escape.</b> Not into a log, not into a REST response,
    /// not into an exception message, not into a process environment. <see cref="DebugLogService"/>
    /// output is readable by any agent through the <c>debug_logs</c> MCP tool, so a key in a log line is
    /// a key in every agent's reach — the launch-nonce leak (task fd3437e6) is the precedent. Failures
    /// here therefore report the SHAPE of the problem, never the material.</para>
    ///
    /// <para><b>Testability is deliberate.</b> The JWT builder is pure and static, and both the clock
    /// and the token exchange are constructor seams, so cache and refresh behaviour can be driven
    /// deterministically with no GitHub App in existence and no network. A service that could only be
    /// exercised against real GitHub would be a service nobody verifies.</para>
    /// </summary>
    public sealed class GitHubAppTokenService
    {
        /// <summary>
        /// GitHub rejects a JWT whose lifetime exceeds 10 minutes. Nine is used rather than ten so a
        /// slow request cannot arrive already expired at the far end.
        /// </summary>
        private const int JwtLifetimeSeconds = 9 * 60;

        /// <summary>
        /// GitHub's own guidance: backdate <c>iat</c> to absorb clock drift between this machine and
        /// GitHub. Without it a machine running slightly fast has every JWT rejected as future-dated,
        /// which presents as an authentication failure with nothing obviously wrong.
        /// </summary>
        private const int JwtBackdateSeconds = 60;

        /// <summary>
        /// Refresh this far ahead of expiry. An installation token lasts ~1h; renewing at 55 minutes
        /// means a token handed out is never about to die in the caller's hand.
        /// </summary>
        internal static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

        private readonly SettingsService _settings;
        private readonly Func<DateTimeOffset> _now;
        private readonly Func<string, string, CancellationToken, Task<InstallationToken>> _exchange;
        private readonly Func<string, CancellationToken, Task<IReadOnlyList<Installation>>> _listInstallations;

        private readonly ConcurrentDictionary<string, CachedToken> _cache = new(StringComparer.Ordinal);

        /// <summary>
        /// One gate per installation. Without it, N agents asking simultaneously each see an empty cache
        /// and each mint a token — a check-then-act race on shared state, which is exactly the defect
        /// class that cost task c9285d2a several rounds. Per-installation rather than global so one
        /// slow installation cannot block another.
        /// </summary>
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _mintGates = new(StringComparer.Ordinal);

        /// <summary>
        /// The gate key for installation DISCOVERY, which has no installation id of its own to be keyed
        /// by — discovery is precisely what runs when no id is known yet. It reuses
        /// <see cref="_mintGates"/> rather than adding a second <see cref="SemaphoreSlim"/> field,
        /// because a lone disposable field would make this type owe a Dispose it has no lifecycle for
        /// (CA1001), and a gate is a gate. A space is not legal in a GitHub installation id, so this key
        /// can never collide with a real one.
        /// </summary>
        private const string DiscoveryGateKey = " discovery ";

        /// <summary>Production constructor: real clock, real GitHub exchange.</summary>
        public GitHubAppTokenService(SettingsService settings)
            : this(settings, () => DateTimeOffset.UtcNow, exchange: null, listInstallations: null)
        {
        }

        /// <summary>
        /// Test seam (InternalsVisibleTo → MultiTerminal.Tests). Injecting the clock and the exchange is
        /// what lets expiry, refresh and concurrency be asserted in milliseconds instead of hours.
        /// </summary>
        internal GitHubAppTokenService(
            SettingsService settings,
            Func<DateTimeOffset> now,
            Func<string, string, CancellationToken, Task<InstallationToken>> exchange,
            Func<string, CancellationToken, Task<IReadOnlyList<Installation>>> listInstallations = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _now = now ?? throw new ArgumentNullException(nameof(now));
            _exchange = exchange ?? ExchangeWithGitHubAsync;
            _listInstallations = listInstallations ?? ListWithGitHubAsync;
        }

        /// <summary>An installation token and the moment it stops being valid.</summary>
        internal sealed class InstallationToken
        {
            public InstallationToken(string token, DateTimeOffset expiresAt)
            {
                Token = token;
                ExpiresAt = expiresAt;
            }

            public string Token { get; }

            public DateTimeOffset ExpiresAt { get; }
        }

        /// <summary>
        /// One installation of this App: the id a token is minted against, plus the account it sits on
        /// for display. <see cref="Account"/> is never used to CHOOSE an installation — a name is not an
        /// identity, and picking by it would silently follow a rename.
        /// </summary>
        internal sealed class Installation
        {
            public Installation(string id, string account)
            {
                Id = id;
                Account = account;
            }

            public string Id { get; }

            public string Account { get; }
        }

        private sealed class CachedToken
        {
            public CachedToken(string token, DateTimeOffset expiresAt)
            {
                Token = token;
                ExpiresAt = expiresAt;
            }

            public string Token { get; }

            public DateTimeOffset ExpiresAt { get; }
        }

        /// <summary>
        /// Returns a usable installation token, minting one only when the cached token is missing or
        /// within <see cref="RefreshMargin"/> of expiry. Concurrent callers for the same installation
        /// share a single mint rather than racing.
        /// </summary>
        /// <exception cref="InvalidOperationException">No App is configured, or no installation id.</exception>
        public async Task<string> GetInstallationTokenAsync(string installationId, CancellationToken ct = default)
        {
            string appId = _settings.GetGitHubAppId();
            string pem = _settings.GetGitHubAppPrivateKeyPem();

            // ⚠️ CHECKED FIRST, AHEAD OF RESOLUTION, AND THE ORDER IS THE POINT. "No App is configured"
            // is the more fundamental failure: without one, no installation id could be usable anyway,
            // and resolution's own errors would send the caller somewhere useless — telling them to set
            // a default installation, or to ask GitHub, when what they actually need is to register the
            // App. Resolving first also meant a caller could create a mint-gate entry on a machine that
            // has no App at all. Pinned by
            // GitHubAppTokenServiceTests.Minting_without_a_configured_app_fails_loudly_rather_than_returning_nothing,
            // which caught this the moment resolution grew a refusal of its own.
            if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(pem))
            {
                throw new InvalidOperationException(
                    "No GitHub App is configured (missing app id or private key). Register the App first.");
            }

            string installation = await ResolveInstallationIdAsync(installationId, ct).ConfigureAwait(false);

            if (TryGetFresh(installation, out string cached))
                return cached;

            SemaphoreSlim gate = _mintGates.GetOrAdd(installation, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Re-check inside the gate. The caller that lost the race must NOT mint a second token
                // just because the cache was empty when it queued — that is the whole point of the gate.
                if (TryGetFresh(installation, out cached))
                    return cached;

                string jwt = CreateAppJwt(pem, appId, _now());
                InstallationToken minted = await _exchange(jwt, installation, ct).ConfigureAwait(false);

                if (minted == null || string.IsNullOrWhiteSpace(minted.Token))
                    throw new InvalidOperationException("GitHub returned no installation token.");

                _cache[installation] = new CachedToken(minted.Token, minted.ExpiresAt);
                return minted.Token;
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// Answers "which installation should this token be minted against?".
        ///
        /// <para><b>This method is the fix for the gap that made items 0-7 inert</b> (task b42b1883,
        /// item 10). Registration was proven end to end, and every mint still failed, because nothing in
        /// MultiTerminal could supply an installation id: the setter had no caller outside tests, there
        /// was no settings UI and no write endpoint, and the manifest callback CANNOT know the id by
        /// construction — conversion happens BEFORE the App is installed anywhere. The result was a 503
        /// on every mint and a fall back to the Owner's own account on every <c>gh</c> command, which is
        /// the single outcome this ticket exists to prevent.</para>
        ///
        /// <para>Order: a requested id is CHECKED AGAINST the stored default (see below), then the stored
        /// default is used, then GitHub is asked. Discovery is what repairs an ALREADY-REGISTERED App —
        /// it needs no Owner action, no re-registration and no GitHub-side configuration change, which is
        /// why it is the primary fix and the manifest's <c>setup_url</c> is only the thing that stops a
        /// FUTURE registration from ever landing here.</para>
        ///
        /// <para><b>⚠️ A requested id may only RESTATE the configured default; it can never select a
        /// different one.</b> This used to be "first hit wins", and an explicitly requested id was
        /// returned trimmed and unverified, ahead of everything else. That was an escalation
        /// (OWASP A01), found by pipeline Run 1 on task b42b1883: the id arrives in the body of
        /// <c>POST /api/github/token</c>, so any terminal holding a launch nonce could list installations
        /// through <c>GET /app/installations</c> and mint a <c>contents:write</c> token for an account
        /// the Owner had never selected for it. <see cref="SetDefaultInstallationIdAsync"/> verifies an id
        /// against GitHub before storing it; the mint path verified nothing, so the endpoint that took
        /// untrusted input was the one that checked least.</para>
        ///
        /// <para><b>It also punched a hole in revocation, which is the subtler half.</b> The
        /// never-re-discover rule below stops MT hopping to a surviving installation after a revoke — but
        /// only for callers that let MT choose. A caller naming a survivor explicitly bypassed the rule
        /// entirely, so "revoking stops access" held only while exactly one installation existed. The
        /// two defences have to agree, or the weaker one is the real policy.</para>
        ///
        /// <para>The previous behaviour was deliberate and tested
        /// (<c>An_explicit_id_beats_both_the_stored_default_and_discovery</c>), and that test was
        /// rewritten rather than deleted — it now asserts the refusal. Its original guarded the adjacent
        /// risk, that an explicit id must not be PERSISTED as the new default and silently repoint every
        /// other terminal; that reasoning was right and still holds. It simply stopped one step short of
        /// asking whether a caller should be able to USE such an id even once.</para>
        ///
        /// <para><b>⚠️ A stored default is never re-discovered, and that is deliberate.</b> Re-running
        /// discovery when a mint fails is the obvious "self-healing" behaviour and it would quietly
        /// defeat this ticket's own acceptance: revoke the installation agents act through, and MT would
        /// hop to whichever installation survived and carry on commenting. Access must stop when the
        /// Owner stops it, so a stale default stays stale and surfaces as an error.</para>
        ///
        /// <para>Zero and many installations are distinct failures with distinct instructions, never a
        /// guess. Silently picking one of several would bind the bot to an account nobody chose.</para>
        /// </summary>
        /// <exception cref="InvalidOperationException">No App configured, or no single installation to adopt.</exception>
        internal async Task<string> ResolveInstallationIdAsync(string requested, CancellationToken ct = default)
        {
            string stored = _settings.GetGitHubAppDefaultInstallationId();

            // ⚠️ A REQUESTED ID IS ONLY EVER A RESTATEMENT OF THE CONFIGURED DEFAULT — never a choice.
            // See the remarks above for why letting the caller pick is an escalation and a hole in
            // revocation. Compared BEFORE the "use the stored default" path so that naming the default
            // explicitly (which the helper and the shim both do when
            // MULTITERMINAL_GITHUB_INSTALLATION_ID is set) keeps working unchanged.
            if (!string.IsNullOrWhiteSpace(requested))
            {
                string wanted = requested.Trim();

                if (string.IsNullOrWhiteSpace(stored))
                {
                    throw new InvalidOperationException(
                        "This request named installation " + wanted + ", but MultiTerminal has no default "
                        + "installation configured to check it against, and it will not mint for an "
                        + "installation chosen by the caller. Set the default with "
                        + "POST /api/github/app/installation, which verifies the id against GitHub first.");
                }

                if (!string.Equals(wanted, stored.Trim(), StringComparison.Ordinal))
                {
                    // Names both ids: the caller is normally a script passing a stale environment
                    // variable, and "yours is not ours" is only actionable if it says what ours is.
                    // Neither value is a secret — an installation id says WHICH installation, not how
                    // to authenticate as it.
                    throw new InvalidOperationException(
                        "This request named installation " + wanted + ", but MultiTerminal is configured "
                        + "to act through " + stored.Trim() + ". A caller does not get to choose the "
                        + "installation: that would let any terminal mint a contents:write token for an "
                        + "account the Owner never selected, and would let a revoked installation be "
                        + "worked around by naming a surviving one. Change the default deliberately with "
                        + "POST /api/github/app/installation, or omit the id to use the configured one.");
                }

                return wanted;
            }

            if (!string.IsNullOrWhiteSpace(stored))
                return stored.Trim();

            SemaphoreSlim discoveryGate = _mintGates.GetOrAdd(DiscoveryGateKey, _ => new SemaphoreSlim(1, 1));
            await discoveryGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Re-read inside the gate: a caller that queued behind a discovery must use ITS result
                // rather than starting a second identical conversation with GitHub.
                stored = _settings.GetGitHubAppDefaultInstallationId();
                if (!string.IsNullOrWhiteSpace(stored))
                    return stored.Trim();

                IReadOnlyList<Installation> found = await ListInstallationsAsync(ct).ConfigureAwait(false);

                if (found.Count == 0)
                {
                    throw new InvalidOperationException(
                        "The GitHub App is registered but is not installed on any account, so there is no "
                        + "installation to mint a token for. Install it at " + InstallUrl() + ".");
                }

                if (found.Count > 1)
                {
                    throw new InvalidOperationException(
                        "This GitHub App is installed on more than one account and MultiTerminal will not "
                        + "guess which one agents should act through. Installations: "
                        + string.Join(", ", found.Select(i => $"{i.Id} ({i.Account})"))
                        + ". Choose one with POST /api/github/app/installation.");
                }

                // Persisted rather than merely returned, so this conversation with GitHub happens once per
                // machine instead of once per command an agent runs.
                _settings.SetGitHubAppDefaultInstallationId(found[0].Id);
                return found[0].Id;
            }
            finally
            {
                discoveryGate.Release();
            }
        }

        /// <summary>
        /// Records which installation agents act through, after checking that it is real.
        ///
        /// <para><b>The verification is the point, not politeness.</b> This is reachable from MT's
        /// loopback REST API, which is unauthenticated by design (task c9285d2a), and from a browser
        /// redirect that carries no secret of its own. Accepting an arbitrary number would let any local
        /// process repoint the bot. Accepting only an id GitHub itself lists for THIS App means the worst
        /// a caller can do is select among installations the Owner already created.</para>
        /// </summary>
        /// <returns>The account the chosen installation sits on, for display.</returns>
        /// <exception cref="InvalidOperationException">No App configured, or no such installation.</exception>
        public async Task<string> SetDefaultInstallationIdAsync(string installationId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(installationId))
                throw new InvalidOperationException("An installation id is required.");

            string wanted = installationId.Trim();
            IReadOnlyList<Installation> found = await ListInstallationsAsync(ct).ConfigureAwait(false);
            Installation match = found.FirstOrDefault(i => string.Equals(i.Id, wanted, StringComparison.Ordinal));

            if (match == null)
            {
                throw new InvalidOperationException(
                    $"This GitHub App has no installation with id {wanted}. Known installations: "
                    + (found.Count == 0 ? "none" : string.Join(", ", found.Select(i => i.Id))) + ".");
            }

            _settings.SetGitHubAppDefaultInstallationId(match.Id);

            // Tokens already minted against the PREVIOUS default must not keep being handed out after a
            // deliberate change of identity — that is the same "leftover credential that still works"
            // the acceptance rules out, arriving through a different door.
            InvalidateCache();

            return match.Account;
        }

        /// <summary>
        /// Every installation of this App, straight from GitHub, authenticated as the App itself.
        /// <para>The JWT built here is a live credential for up to nine minutes and goes only to GitHub.</para>
        /// <para>One page of up to 100 is read. An App installed more times than that is already the
        /// "many" failure below, which refuses to choose rather than paging to find more candidates.</para>
        /// </summary>
        /// <exception cref="InvalidOperationException">No App configured, or GitHub refused.</exception>
        internal async Task<IReadOnlyList<Installation>> ListInstallationsAsync(CancellationToken ct = default)
        {
            string appId = _settings.GetGitHubAppId();
            string pem = _settings.GetGitHubAppPrivateKeyPem();

            if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(pem))
            {
                throw new InvalidOperationException(
                    "No GitHub App is configured (missing app id or private key). Register the App first.");
            }

            string jwt = CreateAppJwt(pem, appId, _now());
            return await _listInstallations(jwt, ct).ConfigureAwait(false) ?? Array.Empty<Installation>();
        }

        /// <summary>Where the Owner installs this App. Slug-specific when known, the App list otherwise.</summary>
        private string InstallUrl()
        {
            string slug = _settings.GetGitHubAppSlug();
            return string.IsNullOrWhiteSpace(slug)
                ? "https://github.com/settings/apps"
                : $"https://github.com/apps/{Uri.EscapeDataString(slug)}/installations/new";
        }

        /// <summary>
        /// Drops cached tokens — all of them, or one installation's.
        /// <para>Part of revocation: after the App is cleared or an installation is revoked, a cached
        /// token would otherwise keep working until it expired, which is precisely the "leftover
        /// credential that still works" the ticket's acceptance rules out.</para>
        /// </summary>
        public void InvalidateCache(string installationId = null)
        {
            if (string.IsNullOrWhiteSpace(installationId))
                _cache.Clear();
            else
                _cache.TryRemove(installationId, out _);
        }

        private bool TryGetFresh(string installation, out string token)
        {
            token = null;
            if (!_cache.TryGetValue(installation, out CachedToken hit))
                return false;

            if (_now() >= hit.ExpiresAt - RefreshMargin)
                return false;

            token = hit.Token;
            return true;
        }

        /// <summary>
        /// Builds the RS256 JWT that authenticates as the App itself. Pure and static so it can be
        /// verified against a public key in a unit test rather than trusted.
        /// <para>The returned string is a live credential for up to nine minutes. Callers pass it
        /// straight to GitHub and must not log it.</para>
        /// </summary>
        internal static string CreateAppJwt(string pem, string appId, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(pem)) throw new ArgumentException("PEM is required.", nameof(pem));
            if (string.IsNullOrWhiteSpace(appId)) throw new ArgumentException("App id is required.", nameof(appId));

            long iat = now.ToUnixTimeSeconds() - JwtBackdateSeconds;
            long exp = now.ToUnixTimeSeconds() + JwtLifetimeSeconds;

            string header = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
            string payload = Base64Url(Encoding.UTF8.GetBytes(
                $"{{\"iat\":{iat},\"exp\":{exp},\"iss\":\"{appId}\"}}"));

            string signingInput = header + "." + payload;

            using var rsa = RSA.Create();
            try
            {
                rsa.ImportFromPem(pem);
            }
            catch (ArgumentException ex)
            {
                // Deliberately does NOT include the PEM or the exception's own message, which can echo
                // key material back. Say what is wrong with the shape, never what the value was.
                throw new InvalidOperationException(
                    "The stored GitHub App private key is not a readable PEM. Re-register the App.", ex);
            }

            byte[] signature = rsa.SignData(
                Encoding.UTF8.GetBytes(signingInput),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            return signingInput + "." + Base64Url(signature);
        }

        /// <summary>base64url per RFC 7515: '+/' → '-_', padding stripped.</summary>
        private static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        /// <summary>
        /// The real exchange: authenticate as the App with the JWT, receive an installation token.
        /// Replaced wholesale in tests — this method is the only part of the service that needs network.
        /// </summary>
        private static async Task<InstallationToken> ExchangeWithGitHubAsync(
            string jwt, string installationId, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://api.github.com/app/installations/{Uri.EscapeDataString(installationId)}/access_tokens");

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("MultiTerminal", "1.0"));

            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // Status and GitHub's message only. The request carried the JWT; echoing the request or
                // its headers into an exception is how a credential reaches a log.
                throw new InvalidOperationException(
                    $"GitHub refused to mint an installation token for {installationId}: "
                    + $"{(int)response.StatusCode} {response.ReasonPhrase}. {Summarize(body)}");
            }

            using JsonDocument doc = JsonDocument.Parse(body);
            string token = doc.RootElement.TryGetProperty("token", out JsonElement t) ? t.GetString() : null;
            DateTimeOffset expires = doc.RootElement.TryGetProperty("expires_at", out JsonElement e)
                                     && e.ValueKind == JsonValueKind.String
                                     && DateTimeOffset.TryParse(e.GetString(), out DateTimeOffset parsed)
                ? parsed
                // GitHub always sends expires_at; if that ever changes, assume the documented 1h rather
                // than treating the token as immortal. Erring short costs one extra mint; erring long
                // hands out a dead credential.
                : DateTimeOffset.UtcNow.AddHours(1);

            return new InstallationToken(token, expires);
        }

        /// <summary>
        /// The real listing. Replaced wholesale in tests — the only part of discovery needing network.
        /// <para>Reads ids as strings via <c>ToString()</c> because GitHub sends them as JSON numbers
        /// while every other id in this file is a string; parsing to a long and back would be one more
        /// place for a 64-bit id to lose its tail.</para>
        /// </summary>
        private static async Task<IReadOnlyList<Installation>> ListWithGitHubAsync(string jwt, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "https://api.github.com/app/installations?per_page=100");

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("MultiTerminal", "1.0"));

            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // Status and a capped body only. This request carried the App JWT; echoing the request or
                // its headers into an exception is how a credential reaches a log.
                throw new InvalidOperationException(
                    "GitHub refused to list this App's installations: "
                    + $"{(int)response.StatusCode} {response.ReasonPhrase}. {Summarize(body)}");
            }

            using JsonDocument doc = JsonDocument.Parse(body);
            var found = new List<Installation>();

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return found;

            foreach (JsonElement element in doc.RootElement.EnumerateArray())
            {
                string id = element.TryGetProperty("id", out JsonElement idElement) ? idElement.ToString() : null;

                // An entry with no id cannot be minted against, so it is dropped rather than carried as a
                // half-installation that would later fail somewhere less obvious.
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                string account = element.TryGetProperty("account", out JsonElement accountElement)
                                 && accountElement.ValueKind == JsonValueKind.Object
                                 && accountElement.TryGetProperty("login", out JsonElement login)
                    ? login.GetString()
                    : null;

                found.Add(new Installation(id, string.IsNullOrWhiteSpace(account) ? "unknown account" : account));
            }

            return found;
        }

        /// <summary>
        /// Trims an error body to something safe and short for an exception message. GitHub's error
        /// bodies are small JSON, but this is a response from the network being folded into a string
        /// that may be logged, so it is capped rather than trusted.
        /// </summary>
        private static string Summarize(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return string.Empty;
            string flat = body.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return flat.Length > 200 ? flat.Substring(0, 200) + "…" : flat;
        }
    }
}
