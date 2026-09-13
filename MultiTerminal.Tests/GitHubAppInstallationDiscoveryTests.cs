using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Installation-id resolution (task b42b1883, item 10) — the gap that made items 0–7 inert.
    ///
    /// <para>Everything else in this ticket was proven working while the bot still could not post:
    /// the key was stored, the endpoint answered, the shim ran, and every mint failed because nothing
    /// in MultiTerminal could say WHICH installation to mint for. These facts pin the resolution order
    /// that closes it, and — more importantly — the two places it deliberately refuses to be clever.</para>
    ///
    /// <para>No network and no GitHub App exist here: the clock, the token exchange and the installation
    /// listing are all constructor seams.</para>
    /// </summary>
    public sealed class GitHubAppInstallationDiscoveryTests : IDisposable
    {
        private readonly string _dir;
        private readonly RSA _key;
        private readonly string _pem;

        public GitHubAppInstallationDiscoveryTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"mt_ghdisc_test_{Guid.NewGuid():N}");
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

        /// <summary>An App that is registered but whose installation is unknown — the real starting state.</summary>
        private SettingsService RegisteredButNoInstallation()
        {
            var s = new SettingsService(_dir);
            s.SetGitHubAppId("4922285");
            s.SetGitHubAppSlug("clarionlive-agent");
            s.SetGitHubAppPrivateKeyPem(_pem);
            return s;
        }

        private static GitHubAppTokenService.Installation Install(string id, string account) =>
            new(id, account);

        private static Task<GitHubAppTokenService.InstallationToken> Mint(string token) =>
            Task.FromResult(new GitHubAppTokenService.InstallationToken(token, DateTimeOffset.UtcNow.AddHours(1)));

        // ─── Discovery ───────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task A_single_installation_is_discovered_adopted_and_remembered()
        {
            // The headline fix. Before this, a registered App with one installation still could not mint:
            // the id existed only inside GitHub and nothing ever asked for it.
            SettingsService settings = RegisteredButNoInstallation();
            string mintedFor = null;

            var svc = new GitHubAppTokenService(
                settings,
                () => DateTimeOffset.UtcNow,
                (jwt, installation, ct) =>
                {
                    mintedFor = installation;
                    return Mint("ghs_discovered");
                },
                (jwt, ct) => Task.FromResult<IReadOnlyList<GitHubAppTokenService.Installation>>(
                    new[] { Install("161180702", "ClarionLive") }));

            Assert.Equal("ghs_discovered", await svc.GetInstallationTokenAsync(null));
            Assert.Equal("161180702", mintedFor);

            // Persisted, not merely used: otherwise every gh command would re-ask GitHub the same
            // question, turning a one-off conversation into a per-invocation round trip.
            Assert.Equal("161180702", settings.GetGitHubAppDefaultInstallationId());
        }

        [Fact]
        public async Task Discovery_happens_once_no_matter_how_many_agents_start_at_the_same_moment()
        {
            // Terminals come up together and each runs gh. Without a gate around discovery, each sees an
            // empty default, each asks GitHub, and each writes the same answer — the check-then-act shape
            // the mint gates already exist to prevent, in the one place those gates cannot reach (they
            // are keyed by the installation id, which is what discovery is trying to find out).
            SettingsService settings = RegisteredButNoInstallation();
            int listings = 0;

            var svc = new GitHubAppTokenService(
                settings,
                () => DateTimeOffset.UtcNow,
                (jwt, installation, ct) => Mint("ghs_shared"),
                async (jwt, ct) =>
                {
                    Interlocked.Increment(ref listings);
                    await Task.Delay(40, ct).ConfigureAwait(false);   // hold the window open
                    return (IReadOnlyList<GitHubAppTokenService.Installation>)new[] { Install("161180702", "ClarionLive") };
                });

            string[] tokens = await Task.WhenAll(
                Enumerable.Range(0, 8).Select(_ => svc.GetInstallationTokenAsync(null)));

            Assert.All(tokens, t => Assert.Equal("ghs_shared", t));
            Assert.Equal(1, listings);
        }

        // ─── The two refusals ────────────────────────────────────────────────────────────────

        [Fact]
        public async Task No_installation_anywhere_says_where_to_install_rather_than_failing_opaquely()
        {
            // "No installation id is configured" was the old message, and it described MT's internal
            // state rather than the Owner's next action — the App is registered, so there is nothing
            // left to configure; it simply has not been installed.
            SettingsService settings = RegisteredButNoInstallation();

            var svc = new GitHubAppTokenService(
                settings,
                () => DateTimeOffset.UtcNow,
                (jwt, installation, ct) => Mint("never"),
                (jwt, ct) => Task.FromResult<IReadOnlyList<GitHubAppTokenService.Installation>>(
                    Array.Empty<GitHubAppTokenService.Installation>()));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.GetInstallationTokenAsync(null));

            Assert.Contains("not installed on any account", ex.Message, StringComparison.Ordinal);
            Assert.Contains("clarionlive-agent", ex.Message, StringComparison.Ordinal);
            Assert.Null(settings.GetGitHubAppDefaultInstallationId());
        }

        [Fact]
        public async Task More_than_one_installation_refuses_to_choose_and_names_them_all()
        {
            // Picking the first, the newest, or the one matching some name would bind every future agent
            // comment to an account nobody chose — and would do it silently, which is the failure mode
            // this ticket is about. Refusing costs one Owner decision; guessing costs the attribution.
            SettingsService settings = RegisteredButNoInstallation();

            var svc = new GitHubAppTokenService(
                settings,
                () => DateTimeOffset.UtcNow,
                (jwt, installation, ct) => Mint("never"),
                (jwt, ct) => Task.FromResult<IReadOnlyList<GitHubAppTokenService.Installation>>(
                    new[] { Install("161180702", "ClarionLive"), Install("987654321", "SomeOrg") }));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.GetInstallationTokenAsync(null));

            Assert.Contains("161180702", ex.Message, StringComparison.Ordinal);
            Assert.Contains("987654321", ex.Message, StringComparison.Ordinal);
            Assert.Contains("SomeOrg", ex.Message, StringComparison.Ordinal);
            Assert.Null(settings.GetGitHubAppDefaultInstallationId());
        }

        // ─── The rule that looks like a missing feature ───────────────────────────────────────

        [Fact]
        public async Task A_stored_default_is_never_re_discovered_even_when_minting_fails()
        {
            // ⚠️ THIS IS THE REVOCATION GUARANTEE, IN EXECUTABLE FORM (item 9's "revoking the
            // installation stops access immediately, with no leftover credential that still works").
            //
            // Re-running discovery when a mint fails is the obvious self-healing move and it would
            // quietly defeat that: the Owner revokes the installation agents act through, MT asks GitHub
            // for a list, finds whichever installation survived, and carries on commenting through an
            // identity that was never re-authorised. Nothing would appear broken, which is the problem.
            //
            // If this test ever goes red because someone added a retry, the retry is the bug.
            var settings = new SettingsService(_dir);
            settings.SetGitHubAppId("4922285");
            settings.SetGitHubAppSlug("clarionlive-agent");
            settings.SetGitHubAppPrivateKeyPem(_pem);
            settings.SetGitHubAppDefaultInstallationId("161180702");   // since revoked at GitHub

            int listings = 0;

            var svc = new GitHubAppTokenService(
                settings,
                () => DateTimeOffset.UtcNow,
                (jwt, installation, ct) => throw new InvalidOperationException(
                    "GitHub refused to mint an installation token for 161180702: 404 Not Found."),
                (jwt, ct) =>
                {
                    listings++;
                    return Task.FromResult<IReadOnlyList<GitHubAppTokenService.Installation>>(
                        new[] { Install("222222222", "AnotherAccount") });   // the survivor
                });

            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.GetInstallationTokenAsync(null));

            Assert.Equal(0, listings);
            Assert.Equal("161180702", settings.GetGitHubAppDefaultInstallationId());
        }

        [Fact]
        public async Task An_explicit_id_that_restates_the_configured_default_is_honoured()
        {
            // The helper and the shim both send an explicit id when MULTITERMINAL_GITHUB_INSTALLATION_ID
            // is set, so naming the default must keep working. This is the whole legitimate use of the
            // field, and it is the reason the refusal below is a comparison rather than a flat ban.
            SettingsService settings = RegisteredButNoInstallation();
            settings.SetGitHubAppDefaultInstallationId("161180702");
            string mintedFor = null;
            int listings = 0;

            var svc = new GitHubAppTokenService(
                settings,
                () => DateTimeOffset.UtcNow,
                (jwt, installation, ct) =>
                {
                    mintedFor = installation;
                    return Mint("ghs_explicit");
                },
                (jwt, ct) =>
                {
                    listings++;
                    return Task.FromResult<IReadOnlyList<GitHubAppTokenService.Installation>>(
                        Array.Empty<GitHubAppTokenService.Installation>());
                });

            Assert.Equal("ghs_explicit", await svc.GetInstallationTokenAsync("161180702"));
            Assert.Equal("161180702", mintedFor);
            Assert.Equal(0, listings);
            Assert.Equal("161180702", settings.GetGitHubAppDefaultInstallationId());
        }

        [Fact]
        public async Task An_explicit_id_may_never_select_an_installation_the_owner_did_not_configure()
        {
            // ⚠️ THIS TEST REPLACES An_explicit_id_beats_both_the_stored_default_and_discovery, which
            // asserted the OPPOSITE and was wrong — found by the security and adversary gates in
            // pipeline Run 1 on task b42b1883. The id arrives in the body of POST /api/github/token, so
            // "first hit wins" let any nonce-holding terminal mint a contents:write token for an account
            // the Owner never selected for it (OWASP A01).
            //
            // The original test's own reasoning was half right and is preserved in the sibling test
            // above: an explicit id must never be PERSISTED as the new default, because that would
            // silently repoint every other terminal. It simply never asked whether such an id should be
            // USABLE even once.
            SettingsService settings = RegisteredButNoInstallation();
            settings.SetGitHubAppDefaultInstallationId("161180702");
            int mints = 0;
            int listings = 0;

            var svc = new GitHubAppTokenService(
                settings,
                () => DateTimeOffset.UtcNow,
                (jwt, installation, ct) => { mints++; return Mint("ghs_should_never_be_minted"); },
                (jwt, ct) =>
                {
                    listings++;
                    return Task.FromResult<IReadOnlyList<GitHubAppTokenService.Installation>>(
                        new[] { Install("555555555", "SomeOtherAccount") });
                });

            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.GetInstallationTokenAsync("555555555"));

            // GitHub is never even asked. Refusing AFTER a successful mint would already have created a
            // live credential at GitHub for the wrong account, which is the thing being prevented.
            Assert.Equal(0, mints);
            Assert.Equal(0, listings);

            // Both ids are named, because the caller is usually a script carrying a stale environment
            // variable and cannot act on "no" alone.
            Assert.Contains("555555555", ex.Message, StringComparison.Ordinal);
            Assert.Contains("161180702", ex.Message, StringComparison.Ordinal);

            // The refusal changes nothing: a rejected request must not disturb the configured identity.
            Assert.Equal("161180702", settings.GetGitHubAppDefaultInstallationId());
        }

        [Fact]
        public async Task A_revoked_default_cannot_be_worked_around_by_naming_a_surviving_installation()
        {
            // The revocation half of the same hole, and the reason this is not merely a tidiness fix.
            // Task b42b1883 item 12 proved live that MT does not re-discover a substitute installation
            // after a revoke — but that rule only ever governed callers who let MT choose. A caller
            // naming a survivor explicitly bypassed it completely, so "revoking stops access
            // immediately" held only because exactly one installation happened to exist.
            SettingsService settings = RegisteredButNoInstallation();
            settings.SetGitHubAppDefaultInstallationId("161180702");   // the revoked one
            int mints = 0;

            var svc = new GitHubAppTokenService(
                settings,
                () => DateTimeOffset.UtcNow,
                (jwt, installation, ct) => { mints++; return Mint("ghs_survivor"); },
                (jwt, ct) => Task.FromResult<IReadOnlyList<GitHubAppTokenService.Installation>>(
                    new[] { Install("222222222", "AnotherAccount") }));   // the survivor

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.GetInstallationTokenAsync("222222222"));

            Assert.Equal(0, mints);
            Assert.Equal("161180702", settings.GetGitHubAppDefaultInstallationId());
        }

        [Fact]
        public async Task An_explicit_id_is_refused_when_no_default_is_configured_to_check_it_against()
        {
            // With nothing to compare against there is no way to tell a legitimate restatement from a
            // caller's own choice, so the answer is no. The Owner-managed route
            // (POST /api/github/app/installation) verifies an id against GitHub before storing it, and
            // that is how a default is meant to arrive.
            SettingsService settings = RegisteredButNoInstallation();
            int mints = 0;
            int listings = 0;

            var svc = new GitHubAppTokenService(
                settings,
                () => DateTimeOffset.UtcNow,
                (jwt, installation, ct) => { mints++; return Mint("ghs_should_never_be_minted"); },
                (jwt, ct) =>
                {
                    listings++;
                    return Task.FromResult<IReadOnlyList<GitHubAppTokenService.Installation>>(
                        new[] { Install("555555555", "SomeOtherAccount") });
                });

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.GetInstallationTokenAsync("555555555"));

            Assert.Equal(0, mints);
            Assert.Equal(0, listings);

            // Crucially, a refused request must not become the default by a side effect.
            Assert.True(string.IsNullOrWhiteSpace(settings.GetGitHubAppDefaultInstallationId()));
        }

        // ─── Choosing deliberately ───────────────────────────────────────────────────────────

        [Fact]
        public async Task Setting_a_default_refuses_an_id_github_does_not_list()
        {
            // The write endpoint and the post-install redirect both reach this, and neither carries a
            // secret: MT's REST API is loopback and unauthenticated by design (task c9285d2a). Checking
            // the id against GitHub is what keeps a local caller from repointing the bot at an
            // installation of its own choosing.
            SettingsService settings = RegisteredButNoInstallation();

            var svc = new GitHubAppTokenService(
                settings,
                () => DateTimeOffset.UtcNow,
                (jwt, installation, ct) => Mint("never"),
                (jwt, ct) => Task.FromResult<IReadOnlyList<GitHubAppTokenService.Installation>>(
                    new[] { Install("161180702", "ClarionLive") }));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.SetDefaultInstallationIdAsync("999999999"));

            Assert.Contains("no installation with id 999999999", ex.Message, StringComparison.Ordinal);
            Assert.Null(settings.GetGitHubAppDefaultInstallationId());
        }

        [Fact]
        public async Task Setting_a_default_accepts_a_listed_id_and_drops_tokens_minted_for_the_old_one()
        {
            // Changing which identity agents act through must not leave the previous one usable for the
            // rest of its hour — that is the same "leftover credential that still works" the acceptance
            // rules out, arriving through a different door.
            SettingsService settings = RegisteredButNoInstallation();
            settings.SetGitHubAppDefaultInstallationId("161180702");
            int mints = 0;

            var svc = new GitHubAppTokenService(
                settings,
                () => DateTimeOffset.UtcNow,
                (jwt, installation, ct) =>
                {
                    mints++;
                    return Mint($"ghs_{mints}");
                },
                (jwt, ct) => Task.FromResult<IReadOnlyList<GitHubAppTokenService.Installation>>(
                    new[] { Install("161180702", "ClarionLive"), Install("987654321", "SomeOrg") }));

            Assert.Equal("ghs_1", await svc.GetInstallationTokenAsync("161180702"));
            Assert.Equal("ghs_1", await svc.GetInstallationTokenAsync("161180702"));   // cached
            Assert.Equal(1, mints);

            Assert.Equal("SomeOrg", await svc.SetDefaultInstallationIdAsync("987654321"));
            Assert.Equal("987654321", settings.GetGitHubAppDefaultInstallationId());

            // ⚠️ THE GUARANTEE GOT STRONGER, so this assertion changed shape (pipeline Run 1). It used
            // to re-mint the OLD installation and check the token was fresh — proving the cache had
            // been dropped, but also quietly relying on a caller being able to name any installation it
            // liked. That ability was an escalation and is gone. The previous identity is now not
            // merely re-minted, it is UNREACHABLE: naming the superseded installation is refused
            // outright, which is what "must not leave the previous one usable" should have meant all
            // along.
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.GetInstallationTokenAsync("161180702"));
            Assert.Equal(1, mints);   // and GitHub was never asked

            // The cache-drop itself is still proven, via calls the design permits: switch back, and the
            // token for 161180702 must be minted afresh rather than served from before the change.
            Assert.Equal("ClarionLive", await svc.SetDefaultInstallationIdAsync("161180702"));
            Assert.Equal("ghs_2", await svc.GetInstallationTokenAsync("161180702"));
            Assert.Equal(2, mints);
        }

        [Fact]
        public async Task Listing_installations_without_a_configured_app_fails_loudly()
        {
            var empty = new SettingsService(_dir);   // nothing configured

            var svc = new GitHubAppTokenService(
                empty,
                () => DateTimeOffset.UtcNow,
                (jwt, installation, ct) => Mint("never"),
                (jwt, ct) => Task.FromResult<IReadOnlyList<GitHubAppTokenService.Installation>>(
                    new[] { Install("1", "X") }));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.ListInstallationsAsync());

            Assert.Contains("No GitHub App is configured", ex.Message, StringComparison.Ordinal);
        }
    }
}
