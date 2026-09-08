using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// App Manifest registration (task b42b1883, item 3).
    /// <para>Two things are being pinned here. First, the manifest content — the permission set is an
    /// explicit Owner decision, and "what did we actually ask GitHub for?" should be answerable by a
    /// test rather than by reading a serializer's output. Second, and more important, the state check:
    /// the callback endpoint is reachable by anything that can talk to localhost, so state validation
    /// is the only thing preventing a local process from planting an App's credentials into MT.</para>
    /// </summary>
    public sealed class GitHubAppManifestServiceTests : IDisposable
    {
        private readonly string _dir;

        public GitHubAppManifestServiceTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"mt_ghmanifest_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
            }
            catch
            {
                // Best effort — a leaked temp dir must never fail a run.
            }
        }

        private const string CallbackUrl = "http://localhost:5050/api/github/app/callback";

        private static JsonElement Manifest() =>
            JsonDocument.Parse(GitHubAppManifestService.BuildManifest(
                "clarionlive-agent", CallbackUrl, "https://github.com/ClarionLive/multiterminal")).RootElement;

        // ─── What we actually ask GitHub for ─────────────────────────────────────────────────

        [Fact]
        public void The_manifest_requests_exactly_the_permissions_the_owner_chose()
        {
            // Owner decision, 2026-09-08. contents:write is the consequential one — it lets agents push
            // commits as the bot, and it means a leaked hour of token buys code write, not just
            // comments. If someone later widens or narrows this set, that should be a deliberate edit
            // that fails a test, not a quiet change nobody notices until a permission is missing in
            // production or an unnecessary one is granted.
            JsonElement perms = Manifest().GetProperty("default_permissions");

            Assert.Equal("write", perms.GetProperty("issues").GetString());
            Assert.Equal("write", perms.GetProperty("pull_requests").GetString());
            Assert.Equal("write", perms.GetProperty("contents").GetString());
            Assert.Equal("read", perms.GetProperty("metadata").GetString());
        }

        [Fact]
        public void The_manifest_asks_for_no_webhook_and_no_events()
        {
            // MT polls and acts; nothing calls back into it. A webhook would be an inbound public
            // listener this design does not need and would then have to defend.
            JsonElement m = Manifest();

            Assert.Empty(m.GetProperty("default_events").EnumerateArray());
            Assert.False(m.GetProperty("hook_attributes").GetProperty("active").GetBoolean());
        }

        [Fact]
        public void The_manifest_is_private_and_points_back_at_this_machine()
        {
            JsonElement m = Manifest();

            Assert.False(m.GetProperty("public").GetBoolean());
            Assert.Equal(CallbackUrl, m.GetProperty("redirect_url").GetString());
            Assert.Equal("clarionlive-agent", m.GetProperty("name").GetString());
        }

        // ─── State: the security boundary ────────────────────────────────────────────────────

        [Fact]
        public void A_freshly_issued_state_is_accepted_once()
        {
            var svc = new GitHubAppManifestService(new SettingsService(_dir), () => DateTimeOffset.UtcNow, convert: null);

            string state = svc.IssueState();
            Assert.True(svc.TryConsumeState(state));
        }

        [Fact]
        public void A_state_cannot_be_used_twice()
        {
            // Single-use matters because the callback URL lands in browser history. Replaying it must
            // not be able to drive a second registration.
            var svc = new GitHubAppManifestService(new SettingsService(_dir), () => DateTimeOffset.UtcNow, convert: null);

            string state = svc.IssueState();
            Assert.True(svc.TryConsumeState(state));
            Assert.False(svc.TryConsumeState(state));
        }

        [Fact]
        public void A_state_this_service_never_issued_is_rejected()
        {
            // THE one that matters. MT's REST API is loopback and unauthenticated by design, so without
            // this check any local process could call the callback with a code from an App IT created,
            // and MT would store that App's credentials — silently repointing every future agent comment
            // at an identity someone else controls.
            var svc = new GitHubAppManifestService(new SettingsService(_dir), () => DateTimeOffset.UtcNow, convert: null);

            Assert.False(svc.TryConsumeState("0123456789ABCDEF0123456789ABCDEF"));
            Assert.False(svc.TryConsumeState(""));
            Assert.False(svc.TryConsumeState(null));
        }

        [Fact]
        public void An_expired_state_is_rejected()
        {
            var clock = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
            var svc = new GitHubAppManifestService(new SettingsService(_dir), () => clock, convert: null);

            string state = svc.IssueState();
            clock += GitHubAppManifestService.StateLifetime + TimeSpan.FromSeconds(1);

            Assert.False(svc.TryConsumeState(state));
        }

        [Fact]
        public void Two_issued_states_are_different()
        {
            // Guards against a predictable or constant state, which would make the check above
            // ceremonial — an attacker who can guess the value defeats it without needing to steal one.
            var svc = new GitHubAppManifestService(new SettingsService(_dir), () => DateTimeOffset.UtcNow, convert: null);

            Assert.NotEqual(svc.IssueState(), svc.IssueState());
        }

        // ─── Completing registration ─────────────────────────────────────────────────────────

        [Fact]
        public async Task A_rejected_state_never_reaches_github()
        {
            // ORDERING, not just outcome. Validating after the exchange would still burn the code and,
            // worse, would let an unsolicited callback make MT talk to GitHub on demand. Asserting the
            // exchange was never invoked is the only way to pin that the check comes first.
            bool exchangeCalled = false;

            var svc = new GitHubAppManifestService(
                new SettingsService(_dir),
                () => DateTimeOffset.UtcNow,
                (code, ct) =>
                {
                    exchangeCalled = true;
                    return Task.FromResult(new GitHubAppManifestService.AppRegistration());
                });

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.CompleteRegistrationAsync("some-code", "a-state-we-never-issued"));

            Assert.False(exchangeCalled, "the state must be checked BEFORE the code is exchanged");
        }

        [Fact]
        public async Task A_successful_registration_stores_everything_and_returns_only_the_slug()
        {
            const string pem = "-----BEGIN RSA PRIVATE KEY-----\nNOTAREALKEYJUSTAMARKER\n-----END RSA PRIVATE KEY-----\n";

            var settings = new SettingsService(_dir);
            var svc = new GitHubAppManifestService(
                settings,
                () => DateTimeOffset.UtcNow,
                (code, ct) => Task.FromResult(new GitHubAppManifestService.AppRegistration
                {
                    Id = "555444",
                    Slug = "clarionlive-agent",
                    Pem = pem,
                    ClientId = "Iv1.deadbeef",
                    ClientSecret = "cs-value",
                    WebhookSecret = "ws-value",
                }));

            string state = svc.IssueState();
            string slug = await svc.CompleteRegistrationAsync("good-code", state);

            // The return value is what a caller may render into a browser page. It must be the slug and
            // nothing else — a key in a page is a key in browser history, cache, and any screenshot.
            Assert.Equal("clarionlive-agent", slug);
            Assert.DoesNotContain("PRIVATE KEY", slug, StringComparison.Ordinal);

            Assert.True(settings.IsGitHubAppConfigured());
            Assert.Equal("555444", settings.GetGitHubAppId());
            Assert.Equal("clarionlive-agent", settings.GetGitHubAppSlug());
            Assert.Equal(pem, settings.GetGitHubAppPrivateKeyPem());
            Assert.Equal("Iv1.deadbeef", settings.GetGitHubAppClientId());
            Assert.Equal("cs-value", settings.GetGitHubAppClientSecret());
            Assert.Equal("ws-value", settings.GetGitHubAppWebhookSecret());

            // And it landed encrypted, not merely stored — the item-0 guarantee still holds through
            // this path, which is the one that actually writes a real key.
            string onDisk = File.ReadAllText(Path.Combine(_dir, "settings.txt"));
            Assert.DoesNotContain("NOTAREALKEYJUSTAMARKER", onDisk, StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_response_without_a_key_is_refused_rather_than_half_stored()
        {
            // A half-registration is worse than none: IsGitHubAppConfigured would report true while
            // every mint fails, and the failure would surface far from here.
            var settings = new SettingsService(_dir);
            var svc = new GitHubAppManifestService(
                settings,
                () => DateTimeOffset.UtcNow,
                (code, ct) => Task.FromResult(new GitHubAppManifestService.AppRegistration
                {
                    Id = "555444",
                    Slug = "clarionlive-agent",
                    Pem = null,          // GitHub returned no key
                }));

            string state = svc.IssueState();

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.CompleteRegistrationAsync("good-code", state));

            Assert.False(settings.IsGitHubAppConfigured());
            Assert.Null(settings.GetGitHubAppId());
        }
    }
}
