using System;
using System.Collections.Concurrent;
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

        private readonly ConcurrentDictionary<string, CachedToken> _cache = new(StringComparer.Ordinal);

        /// <summary>
        /// One gate per installation. Without it, N agents asking simultaneously each see an empty cache
        /// and each mint a token — a check-then-act race on shared state, which is exactly the defect
        /// class that cost task c9285d2a several rounds. Per-installation rather than global so one
        /// slow installation cannot block another.
        /// </summary>
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _mintGates = new(StringComparer.Ordinal);

        /// <summary>Production constructor: real clock, real GitHub exchange.</summary>
        public GitHubAppTokenService(SettingsService settings)
            : this(settings, () => DateTimeOffset.UtcNow, exchange: null)
        {
        }

        /// <summary>
        /// Test seam (InternalsVisibleTo → MultiTerminal.Tests). Injecting the clock and the exchange is
        /// what lets expiry, refresh and concurrency be asserted in milliseconds instead of hours.
        /// </summary>
        internal GitHubAppTokenService(
            SettingsService settings,
            Func<DateTimeOffset> now,
            Func<string, string, CancellationToken, Task<InstallationToken>> exchange)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _now = now ?? throw new ArgumentNullException(nameof(now));
            _exchange = exchange ?? ExchangeWithGitHubAsync;
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
            string installation = string.IsNullOrWhiteSpace(installationId)
                ? _settings.GetGitHubAppDefaultInstallationId()
                : installationId;

            if (string.IsNullOrWhiteSpace(installation))
            {
                throw new InvalidOperationException(
                    "No GitHub App installation id is configured. Register the App (task b42b1883 item 3), "
                    + "or set a default installation id.");
            }

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

                string appId = _settings.GetGitHubAppId();
                string pem = _settings.GetGitHubAppPrivateKeyPem();

                if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(pem))
                {
                    throw new InvalidOperationException(
                        "No GitHub App is configured (missing app id or private key). Register the App first.");
                }

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
