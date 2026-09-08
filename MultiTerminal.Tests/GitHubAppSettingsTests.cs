using System;
using System.IO;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// GitHub App credential storage (task b42b1883, item 0).
    /// <para>The whole point of the ticket is that the App's private key is never somewhere an agent
    /// can read it. "It is encrypted" is a claim until something opens the file and looks, so the
    /// central fact here greps the raw settings file rather than asserting intent — the ticket's own
    /// acceptance says "checked by grep, not by intention".</para>
    /// <para>Like <see cref="HudTabZoomSettingsTests"/>, these run against an isolated temp folder via
    /// the internal settings-directory seam. Without it a persistence test would read and overwrite the
    /// developer's REAL settings.txt — here that would mean a test writing a fake private key into the
    /// running app's configuration.</para>
    /// </summary>
    public sealed class GitHubAppSettingsTests : IDisposable
    {
        private readonly string _dir;

        /// <summary>
        /// A realistic multi-line PEM. The shape matters: a PEM contains newlines, and settings.txt is
        /// a line-oriented key=value file, so storing one naively would truncate at the first newline.
        /// The base64 body also ends in '=' padding, which is why Load() splitting on the FIRST '=' is
        /// load-bearing rather than incidental.
        /// </summary>
        private const string SamplePem =
            "-----BEGIN RSA PRIVATE KEY-----\n" +
            "MIIEpAIBAAKCAQEAx4fT0Zq9nQ6NpVvVQnrGdVvUniqueMarkerForGrepping1234\n" +
            "5678abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789\n" +
            "aGVsbG8gdGhpcyBpcyBub3QgYSByZWFsIGtleSBidXQgaXQgaGFzIHBhZGRpbmc=\n" +
            "-----END RSA PRIVATE KEY-----\n";

        /// <summary>A string that appears ONLY inside the PEM, so a file scan cannot false-negative.</summary>
        private const string PemNeedle = "UniqueMarkerForGrepping1234";

        public GitHubAppSettingsTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"mt_ghapp_test_{Guid.NewGuid():N}");
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

        private string SettingsFileText() => File.ReadAllText(Path.Combine(_dir, "settings.txt"));

        [Fact]
        public void A_multi_line_pem_survives_being_stored_and_read_back()
        {
            // Modelled as a restart, deliberately: write with one service, read with a SECOND one over
            // the same folder. Asserting against the same in-memory instance would pass even if nothing
            // reached disk, which is the failure this needs to exclude.
            var writer = new SettingsService(_dir);
            writer.SetGitHubAppPrivateKeyPem(SamplePem);

            var reader = new SettingsService(_dir);
            Assert.Equal(SamplePem, reader.GetGitHubAppPrivateKeyPem());
        }

        [Fact]
        public void The_private_key_is_not_present_in_the_settings_file_in_readable_form()
        {
            // THE fact this item exists for. Everything else is plumbing.
            var svc = new SettingsService(_dir);
            svc.SetGitHubAppPrivateKeyPem(SamplePem);

            string onDisk = SettingsFileText();

            Assert.DoesNotContain(PemNeedle, onDisk, StringComparison.Ordinal);
            Assert.DoesNotContain("BEGIN RSA PRIVATE KEY", onDisk, StringComparison.Ordinal);

            // And the key IS stored — otherwise the two assertions above pass trivially on an empty
            // file, which would be the same class of vacuous test this codebase keeps catching.
            Assert.Contains("GitHub.App.PrivateKeyPem", onDisk, StringComparison.Ordinal);
            Assert.Equal(SamplePem, new SettingsService(_dir).GetGitHubAppPrivateKeyPem());
        }

        [Fact]
        public void Routing_values_are_stored_in_the_clear_because_they_are_not_secrets()
        {
            // The secret/non-secret split is a design decision, not an accident: the id, slug and
            // installation id say WHICH installation to act as, never how to authenticate. Pinning that
            // they are readable stops a later "encrypt everything" tidy-up from quietly making routing
            // undebuggable for no security gain.
            var svc = new SettingsService(_dir);
            svc.SetGitHubAppId("123456");
            svc.SetGitHubAppSlug("clarionlive-agent");
            svc.SetGitHubAppDefaultInstallationId("87654321");

            string onDisk = SettingsFileText();

            Assert.Contains("123456", onDisk, StringComparison.Ordinal);
            Assert.Contains("clarionlive-agent", onDisk, StringComparison.Ordinal);
            Assert.Contains("87654321", onDisk, StringComparison.Ordinal);
        }

        [Fact]
        public void Clearing_the_app_leaves_no_leftover_credential()
        {
            // The ticket's acceptance includes "no leftover credential that still works". Revocation on
            // GitHub's side is what withdraws access; this is the local half — a re-registration must
            // start from nothing rather than inherit half of a previous App's identity.
            var svc = new SettingsService(_dir);
            svc.SetGitHubAppId("123456");
            svc.SetGitHubAppSlug("clarionlive-agent");
            svc.SetGitHubAppClientId("Iv1.abc123");
            svc.SetGitHubAppDefaultInstallationId("87654321");
            svc.SetGitHubAppPrivateKeyPem(SamplePem);
            svc.SetGitHubAppClientSecret("client-secret-value");
            svc.SetGitHubAppWebhookSecret("webhook-secret-value");

            Assert.True(svc.IsGitHubAppConfigured());

            svc.ClearGitHubApp();

            Assert.False(svc.IsGitHubAppConfigured());
            Assert.False(svc.HasGitHubAppPrivateKey());
            Assert.Null(svc.GetGitHubAppId());
            Assert.Null(svc.GetGitHubAppSlug());
            Assert.Null(svc.GetGitHubAppClientId());
            Assert.Null(svc.GetGitHubAppDefaultInstallationId());
            Assert.Null(svc.GetGitHubAppPrivateKeyPem());
            Assert.Null(svc.GetGitHubAppClientSecret());
            Assert.Null(svc.GetGitHubAppWebhookSecret());

            // Survives a restart, and leaves nothing recoverable on disk.
            var afterRestart = new SettingsService(_dir);
            Assert.False(afterRestart.IsGitHubAppConfigured());

            string onDisk = SettingsFileText();
            Assert.DoesNotContain("GitHub.App.", onDisk, StringComparison.Ordinal);
        }

        [Fact]
        public void A_configured_check_reports_true_without_decrypting_the_key()
        {
            // "Is the bot set up?" is asked by status reads that have no business touching key material.
            // Proven behaviourally rather than by inspection: store a blob that CANNOT be decrypted, then
            // observe the two answers diverge. Has* reads the raw value and still says configured; the
            // accessor attempts DPAPI, fails, and degrades to null rather than throwing.
            var svc = new SettingsService(_dir);
            svc.SetGitHubAppId("123456");
            svc.Set("GitHub.App.PrivateKeyPem", "bm90LWEtdmFsaWQtZHBhcGktYmxvYg==");   // valid base64, not a DPAPI blob

            Assert.True(svc.HasGitHubAppPrivateKey());
            Assert.True(svc.IsGitHubAppConfigured());
            Assert.Null(svc.GetGitHubAppPrivateKeyPem());
        }
    }
}
