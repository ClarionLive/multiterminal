using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Installation-token minting (task b42b1883, item 1).
    /// <para>Every fact here runs with NO GitHub App in existence and NO network: the clock and the
    /// token exchange are constructor seams. That is the point of the seams — a service verifiable only
    /// against real GitHub is a service nobody verifies, and expiry behaviour measured in hours cannot
    /// be asserted any other way.</para>
    /// <para>The RSA key is generated at runtime rather than committed. A fixture PEM in the repository
    /// would be a private key in a public repo — inert, but indistinguishable at a glance from the
    /// real thing, and this is the one ticket where that confusion is least affordable.</para>
    /// </summary>
    public sealed class GitHubAppTokenServiceTests : IDisposable
    {
        private readonly string _dir;
        private readonly RSA _key;
        private readonly string _pem;

        public GitHubAppTokenServiceTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"mt_ghtok_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _key = RSA.Create(2048);
            _pem = _key.ExportRSAPrivateKeyPem();
        }

        public void Dispose()
        {
            _key.Dispose();
            try
            {
                if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
            }
            catch
            {
                // Best effort — a leaked temp dir must never fail a run.
            }
        }

        private SettingsService ConfiguredSettings()
        {
            var s = new SettingsService(_dir);
            s.SetGitHubAppId("123456");
            s.SetGitHubAppPrivateKeyPem(_pem);
            s.SetGitHubAppDefaultInstallationId("99887766");
            return s;
        }

        private static byte[] Base64UrlDecode(string s)
        {
            string padded = s.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - (padded.Length % 4)) % 4);
            return Convert.FromBase64String(padded);
        }

        // ─── The JWT itself ──────────────────────────────────────────────────────────────────

        [Fact]
        public void The_jwt_signature_verifies_against_the_apps_public_key()
        {
            // The fact that matters. Asserting "it has three dot-separated parts" would pass on a
            // signature of random bytes; GitHub would then reject every request and the failure would
            // present as an auth problem with nothing visibly wrong locally.
            string jwt = GitHubAppTokenService.CreateAppJwt(_pem, "123456", DateTimeOffset.UtcNow);

            string[] parts = jwt.Split('.');
            Assert.Equal(3, parts.Length);

            byte[] signed = Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]);
            byte[] signature = Base64UrlDecode(parts[2]);

            using var publicOnly = RSA.Create();
            publicOnly.ImportRSAPublicKey(_key.ExportRSAPublicKey(), out _);

            Assert.True(
                publicOnly.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
                "the JWT must verify under the App's public key — otherwise GitHub rejects every request");
        }

        [Fact]
        public void The_jwt_claims_satisfy_githubs_documented_limits()
        {
            var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
            string jwt = GitHubAppTokenService.CreateAppJwt(_pem, "123456", now);

            using JsonDocument payload = JsonDocument.Parse(Base64UrlDecode(jwt.Split('.')[1]));
            long iat = payload.RootElement.GetProperty("iat").GetInt64();
            long exp = payload.RootElement.GetProperty("exp").GetInt64();
            string iss = payload.RootElement.GetProperty("iss").GetString();

            Assert.Equal("123456", iss);

            // GitHub rejects a lifetime over 10 minutes outright.
            Assert.True(exp - iat <= 600, $"JWT lifetime {exp - iat}s exceeds GitHub's 600s maximum");

            // And iat is backdated: a machine running slightly fast otherwise has every JWT rejected as
            // future-dated, which looks like a credential problem rather than a clock problem.
            Assert.True(iat < now.ToUnixTimeSeconds(), "iat must be backdated to absorb clock drift");
        }

        [Fact]
        public void An_unreadable_key_reports_the_shape_of_the_problem_and_never_the_material()
        {
            // A key in an exception message is a key in a log, and debug_logs is agent-readable.
            const string secretish = "SUPERSECRETKEYMATERIAL";

            var ex = Assert.Throws<InvalidOperationException>(
                () => GitHubAppTokenService.CreateAppJwt("not a pem at all " + secretish, "123456", DateTimeOffset.UtcNow));

            Assert.DoesNotContain(secretish, ex.Message, StringComparison.Ordinal);
            Assert.Contains("PEM", ex.Message, StringComparison.Ordinal);
        }

        // ─── Cache and refresh ───────────────────────────────────────────────────────────────

        [Fact]
        public async Task A_fresh_token_is_reused_rather_than_reminted()
        {
            var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
            int mints = 0;

            var svc = new GitHubAppTokenService(
                ConfiguredSettings(),
                () => now,
                (jwt, installation, ct) =>
                {
                    mints++;
                    return Task.FromResult(new GitHubAppTokenService.InstallationToken("ghs_first", now.AddHours(1)));
                });

            Assert.Equal("ghs_first", await svc.GetInstallationTokenAsync("99887766"));
            Assert.Equal("ghs_first", await svc.GetInstallationTokenAsync("99887766"));

            Assert.Equal(1, mints);
        }

        [Fact]
        public async Task A_token_inside_the_refresh_margin_is_reminted_before_it_can_expire_in_the_callers_hand()
        {
            var clock = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
            DateTimeOffset issuedAt = clock;
            int mints = 0;

            var svc = new GitHubAppTokenService(
                ConfiguredSettings(),
                () => clock,
                (jwt, installation, ct) =>
                {
                    mints++;
                    return Task.FromResult(new GitHubAppTokenService.InstallationToken($"ghs_{mints}", issuedAt.AddHours(1)));
                });

            Assert.Equal("ghs_1", await svc.GetInstallationTokenAsync("99887766"));

            // 54 minutes in: still outside the 5-minute margin, so the SAME token is handed back.
            clock = issuedAt.AddMinutes(54);
            Assert.Equal("ghs_1", await svc.GetInstallationTokenAsync("99887766"));
            Assert.Equal(1, mints);

            // 56 minutes in: inside the margin. A token with four minutes left must not be handed to a
            // caller who is about to start a push.
            clock = issuedAt.AddMinutes(56);
            issuedAt = clock;
            Assert.Equal("ghs_2", await svc.GetInstallationTokenAsync("99887766"));
            Assert.Equal(2, mints);
        }

        [Fact]
        public async Task Invalidating_the_cache_forces_a_remint()
        {
            // The local half of revocation: a cached token would otherwise keep working for up to an
            // hour after the App is cleared, which is the "leftover credential that still works" the
            // ticket's acceptance rules out.
            var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
            int mints = 0;

            var svc = new GitHubAppTokenService(
                ConfiguredSettings(),
                () => now,
                (jwt, installation, ct) =>
                {
                    mints++;
                    return Task.FromResult(new GitHubAppTokenService.InstallationToken($"ghs_{mints}", now.AddHours(1)));
                });

            Assert.Equal("ghs_1", await svc.GetInstallationTokenAsync("99887766"));
            svc.InvalidateCache();
            Assert.Equal("ghs_2", await svc.GetInstallationTokenAsync("99887766"));
        }

        [Fact]
        public async Task Concurrent_callers_share_one_mint_instead_of_racing()
        {
            // MT runs several agents at once, so this is the ordinary case, not an exotic one. A
            // check-then-act on the cache would have every caller find it empty and mint its own token
            // — burning rate limit and, worse, making "how many live tokens exist right now?"
            // unanswerable. This is the exact defect class that cost task c9285d2a several rounds, so
            // it is asserted rather than assumed.
            var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
            int mints = 0;

            var svc = new GitHubAppTokenService(
                ConfiguredSettings(),
                () => now,
                async (jwt, installation, ct) =>
                {
                    Interlocked.Increment(ref mints);
                    await Task.Delay(50, ct);      // widen the window a real HTTP call would open
                    return new GitHubAppTokenService.InstallationToken("ghs_shared", now.AddHours(1));
                });

            string[] results = await Task.WhenAll(
                Enumerable.Range(0, 20).Select(_ => svc.GetInstallationTokenAsync("99887766")));

            Assert.All(results, r => Assert.Equal("ghs_shared", r));
            Assert.Equal(1, Volatile.Read(ref mints));
        }

        [Fact]
        public async Task Different_installations_do_not_block_each_other()
        {
            // The gate is per-installation on purpose: a global lock would make one slow installation
            // stall every other agent's git operation.
            var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
            var seen = new List<string>();

            var svc = new GitHubAppTokenService(
                ConfiguredSettings(),
                () => now,
                (jwt, installation, ct) =>
                {
                    lock (seen) { seen.Add(installation); }
                    return Task.FromResult(new GitHubAppTokenService.InstallationToken($"ghs_{installation}", now.AddHours(1)));
                });

            Assert.Equal("ghs_111", await svc.GetInstallationTokenAsync("111"));
            Assert.Equal("ghs_222", await svc.GetInstallationTokenAsync("222"));

            Assert.Equal(2, seen.Count);
        }

        // ─── Configuration failures ──────────────────────────────────────────────────────────

        [Fact]
        public async Task Minting_without_a_configured_app_fails_loudly_rather_than_returning_nothing()
        {
            // A null or empty token returned quietly would surface far away as an opaque GitHub 401.
            var empty = new SettingsService(_dir);   // nothing configured
            var svc = new GitHubAppTokenService(
                empty,
                () => DateTimeOffset.UtcNow,
                (jwt, installation, ct) => Task.FromResult(new GitHubAppTokenService.InstallationToken("x", DateTimeOffset.UtcNow.AddHours(1))));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.GetInstallationTokenAsync("12345"));

            Assert.Contains("No GitHub App is configured", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task An_absent_installation_id_falls_back_to_the_configured_default()
        {
            var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
            string used = null;

            var svc = new GitHubAppTokenService(
                ConfiguredSettings(),
                () => now,
                (jwt, installation, ct) =>
                {
                    used = installation;
                    return Task.FromResult(new GitHubAppTokenService.InstallationToken("ghs_default", now.AddHours(1)));
                });

            Assert.Equal("ghs_default", await svc.GetInstallationTokenAsync(null));
            Assert.Equal("99887766", used);
        }
    }
}
