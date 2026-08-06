using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// GH#6 (ticket 2b202b9a) — the dashboard inbox badge showed a count belonging to a
    /// DIFFERENT user than the inbox list rendered.
    ///
    /// Two defects, both in DashboardHeaderControl:
    ///   A) OnInboxUpdated applied <c>e.UnreadCount</c> while discarding <c>e.UserId</c>, so a
    ///      notification raised for ANY agent overwrote the badge with that agent's total.
    ///      This is the one the reporter hit: badge 2, list 1.
    ///   B) RefreshInbox read the literal "Owner" while the list reads
    ///      <c>Broker.DefaultInboxRecipient</c> — which MainForm overwrites with the owner
    ///      profile's first name. On a machine with a profile set, the badge's baseline was the
    ///      unread count of a user with no inbox rows at all (observed: badge 0 while the real
    ///      owner had 209 unread).
    ///
    /// WHY THESE ARE SOURCE-LEVEL ASSERTIONS: DashboardHeaderControl is a WinForms UserControl
    /// hosting WebView2. It is not unit-instantiable without a message loop and a browser
    /// environment, so there is no honest way to exercise the handler in-process. The repo
    /// already uses this technique for exactly that reason — see
    /// <see cref="ProjectPanelCopyCompletenessTests"/>, whose failure mode was likewise "silence
    /// in the UI layer". These tests pin the two properties that make the badge correct, so the
    /// regression cannot return unnoticed even though the control itself cannot be driven here.
    ///
    /// They are deliberately narrow: they assert the SHAPE of the fix, not its formatting, and
    /// each carries a message telling a future editor what to do if the code was restructured
    /// on purpose.
    /// </summary>
    public class InboxBadgeRecipientTests
    {
        /// <summary>
        /// Repo root from THIS FILE's compile-time path — unaffected by the runner's working
        /// directory and correct inside a git worktree. (Same approach as
        /// ProjectPanelCopyCompletenessTests.)
        /// </summary>
        private static string RepoRoot([CallerFilePath] string thisFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", ".."));

        private static string ReadDashboardHeaderSource()
        {
            string path = Path.Combine(RepoRoot(), "DashboardHeader", "DashboardHeaderControl.cs");
            Assert.True(File.Exists(path), $"Could not locate DashboardHeaderControl.cs at '{path}'.");
            return File.ReadAllText(path);
        }

        [Fact]
        public void Badge_baseline_does_not_read_a_hardcoded_user()
        {
            string src = ReadDashboardHeaderSource();

            // Defect B: GetInbox("Owner") — or any string literal — pins the badge to a user
            // that is not necessarily the one whose inbox the list shows.
            var hardcoded = Regex.Match(src, @"GetInbox\(\s*""([^""]*)""\s*\)");

            Assert.False(
                hardcoded.Success,
                $"DashboardHeaderControl calls GetInbox with the hard-coded user \"{(hardcoded.Success ? hardcoded.Groups[1].Value : "")}\". " +
                "The badge must read Broker.DefaultInboxRecipient so it agrees with the inbox list " +
                "(GH#6 defect B). If the badge is deliberately meant to track a fixed user, update this " +
                "test deliberately rather than deleting it.");
        }

        [Fact]
        public void Badge_baseline_resolves_the_recipient_from_the_broker()
        {
            string src = ReadDashboardHeaderSource();

            Assert.Contains("DefaultInboxRecipient", src, StringComparison.Ordinal);
        }

        [Fact]
        public void Inbox_event_handler_filters_on_the_events_user()
        {
            string src = ReadDashboardHeaderSource();

            // Defect A: the handler must not blindly consume e.UnreadCount. Locate the handler
            // and require that it consults the event's UserId (directly or via the helper).
            int start = src.IndexOf("private void OnInboxUpdated", StringComparison.Ordinal);
            Assert.True(
                start >= 0,
                "Could not find OnInboxUpdated in DashboardHeaderControl.cs. If it was renamed, " +
                "update this test deliberately — do not let it silently stop checking anything.");

            // Take a generous window: the handler is a short lambda body.
            int length = Math.Min(600, src.Length - start);
            string handler = src.Substring(start, length);

            Assert.True(
                handler.Contains("IsBadgeRecipient", StringComparison.Ordinal)
                    || handler.Contains("e.UserId", StringComparison.Ordinal),
                "OnInboxUpdated applies the inbox unread count without consulting e.UserId. " +
                "InboxUpdatedEventArgs carries the count for the user whose inbox changed, so an " +
                "unfiltered handler shows another agent's total on the owner's badge (GH#6 defect A).");
        }

        [Fact]
        public void Recipient_is_resolved_live_and_not_cached_in_a_field()
        {
            string src = ReadDashboardHeaderSource();

            // MainForm assigns Broker.DefaultInboxRecipient from the owner profile AFTER this
            // control is constructed. A field initialised at construction would capture the
            // stale "Owner" default and silently reintroduce defect B — so the value must be
            // read from the broker at use time, never stored.
            var cached = Regex.Match(
                src,
                @"(private|protected|internal|public)\s+(readonly\s+)?string\s+_?\w*[Rr]ecipient\w*\s*(=|;)");

            Assert.False(
                cached.Success,
                "DashboardHeaderControl appears to cache the inbox recipient in a field " +
                $"('{(cached.Success ? cached.Value.Trim() : "")}'). Broker.DefaultInboxRecipient is " +
                "assigned from the owner profile AFTER this control is built, so a cached copy pins " +
                "the stale \"Owner\" default and GH#6 defect B returns. Resolve it from the broker at use time.");
        }
    }
}
