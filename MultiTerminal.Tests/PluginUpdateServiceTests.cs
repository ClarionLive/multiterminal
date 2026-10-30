using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 4dc459e4: MT updates its own Claude Code plugin from the marketplace's GitHub main. The pure
    /// decisions (stamp, verdict, zip-slip, top-folder strip, the git-checkout rule) are tested directly; the
    /// swap and the whole update run against temp folders, with a stub HTTP handler and an injected fault.
    /// No test touches the real plugin folder or the network.
    /// </summary>
    public sealed class PluginUpdateServiceTests : IDisposable
    {
        private const string ShaA = "4015135b7d1cfa16b60e10ff2e959bc311ff4d43";
        private const string ShaB = "aaaaaaaaaabbbbbbbbbbccccccccccdddddddddd";
        private const string Top = "multiterminal-marketplace-" + ShaB;

        private readonly string _temp = Path.Combine(Path.GetTempPath(), "mt-plugin-update-tests-" + Guid.NewGuid().ToString("N"));

        public PluginUpdateServiceTests() => Directory.CreateDirectory(_temp);

        public void Dispose()
        {
            try { Directory.Delete(_temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        // ------------------------------------------------------------------ stamp

        [Fact]
        public void A_valid_stamp_parses_and_its_commit_is_lowercased()
        {
            var stamp = PluginUpdateService.ParseStamp(
                "{\"repo\":\"ClarionLive/multiterminal-marketplace\",\"ref\":\"main\",\"commit\":\"" + ShaA.ToUpperInvariant()
                + "\",\"stampedAt\":\"2026-10-30T12:00:00Z\",\"stampedBy\":\"installer\"}");
            Assert.NotNull(stamp);
            Assert.Equal(ShaA, stamp.Commit);
            Assert.Equal("main", stamp.Ref);
            Assert.Equal("installer", stamp.StampedBy);
            Assert.Equal(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero), stamp.StampedAt);
        }

        [Theory]
        [InlineData("{\"commit\":\"4015135\"}")]                                          // short sha
        [InlineData("{\"commit\":\"zz15135b7d1cfa16b60e10ff2e959bc311ff4d43\"}")]         // not hex
        [InlineData("{\"commit\":\" 4015135b7d1cfa16b60e10ff2e959bc311ff4d4\"}")]        // padded
        [InlineData("{\"commit\":40}")]                                                   // not a string
        [InlineData("[\"4015135b7d1cfa16b60e10ff2e959bc311ff4d43\"]")]                    // not an object
        [InlineData("not json")]
        [InlineData("")]
        public void An_unusable_stamp_is_unknown(string json)
        {
            Assert.Null(PluginUpdateService.ParseStamp(json));
        }

        [Fact]
        public void A_bad_date_drops_the_date_not_the_stamp()
        {
            var stamp = PluginUpdateService.ParseStamp("{\"commit\":\"" + ShaA + "\",\"stampedAt\":\"yesterday\"}");
            Assert.NotNull(stamp);
            Assert.Null(stamp.StampedAt);
        }

        [Fact]
        public void Stamp_text_from_a_stamp_field_has_format_characters_removed()
        {
            // U+202E (right-to-left override) would reorder the Settings line.
            var stamp = PluginUpdateService.ParseStamp("{\"commit\":\"" + ShaA + "\",\"stampedBy\":\"inst\\u202Ealler\"}");
            Assert.DoesNotContain('\u202E', stamp.StampedBy);
        }

        [Fact]
        public void The_written_stamp_reads_back()
        {
            var at = new DateTimeOffset(2026, 10, 30, 9, 15, 0, TimeSpan.Zero);
            var stamp = PluginUpdateService.ParseStamp(PluginUpdateService.BuildStampJson(ShaA, at, "mt-update"));
            Assert.Equal(ShaA, stamp.Commit);
            Assert.Equal(PluginUpdateService.Repo, stamp.Repo);
            Assert.Equal("main", stamp.Ref);
            Assert.Equal("mt-update", stamp.StampedBy);
            Assert.Equal(at, stamp.StampedAt);
        }

        [Fact]
        public void The_marketplace_root_is_two_levels_above_the_plugin_folder_and_only_for_that_shape()
        {
            Assert.Equal(@"D:\Shared\multiterminal-marketplace",
                PluginUpdateService.MarketplaceRootFromPluginDir(@"D:\Shared\multiterminal-marketplace\plugins\multiterminal"));
            Assert.Null(PluginUpdateService.MarketplaceRootFromPluginDir(@"D:\Shared\multiterminal-marketplace\other\multiterminal"));
            Assert.Null(PluginUpdateService.MarketplaceRootFromPluginDir(@"D:\plugins\multiterminal")); // a drive root cannot be swapped
            Assert.Null(PluginUpdateService.MarketplaceRootFromPluginDir(null));
        }

        // ------------------------------------------------------------------ verdict

        private static PluginSourceStamp Stamp(string commit, string repo = PluginUpdateService.Repo, string @ref = "main") =>
            new PluginSourceStamp { Repo = repo, Ref = @ref, Commit = commit };

        private static PluginLatestCommit Latest(string sha) => new PluginLatestCommit { Sha = sha };

        [Fact]
        public void Equal_commits_are_up_to_date_whatever_their_case()
        {
            var r = PluginUpdateService.Decide(@"C:\m", Stamp(ShaA), Latest(ShaA.ToUpperInvariant()), null);
            Assert.Equal(PluginCheckOutcome.UpToDate, r.Outcome);
            Assert.False(r.CanUpdate);
        }

        [Fact]
        public void A_different_commit_is_an_update()
        {
            var r = PluginUpdateService.Decide(@"C:\m", Stamp(ShaA), Latest(ShaB), null);
            Assert.Equal(PluginCheckOutcome.UpdateAvailable, r.Outcome);
            Assert.True(r.CanUpdate);
        }

        [Fact]
        public void No_stamp_or_a_stamp_for_another_repo_or_ref_is_unknown_origin_and_can_update()
        {
            Assert.Equal(PluginCheckOutcome.UnknownOrigin, PluginUpdateService.Decide(@"C:\m", null, Latest(ShaB), null).Outcome);
            // Equal commit but another repo: not comparable, so NOT "up to date".
            Assert.Equal(PluginCheckOutcome.UnknownOrigin, PluginUpdateService.Decide(@"C:\m", Stamp(ShaB, repo: "Someone/fork"), Latest(ShaB), null).Outcome);
            Assert.Equal(PluginCheckOutcome.UnknownOrigin, PluginUpdateService.Decide(@"C:\m", Stamp(ShaB, @ref: "dev"), Latest(ShaB), null).Outcome);
            Assert.True(PluginUpdateService.Decide(@"C:\m", null, Latest(ShaB), null).CanUpdate);
        }

        [Fact]
        public void A_failure_wins_and_cannot_update()
        {
            var r = PluginUpdateService.Decide(@"C:\m", Stamp(ShaA), Latest(ShaB), "GitHub returned 500");
            Assert.Equal(PluginCheckOutcome.Failed, r.Outcome);
            Assert.Equal("GitHub returned 500", r.Reason);
            Assert.False(r.CanUpdate);
            Assert.Equal(PluginCheckOutcome.Failed, PluginUpdateService.Decide(null, null, Latest(ShaB), null).Outcome);
        }

        [Fact]
        public void The_latest_commit_parses_with_a_sanitized_first_line()
        {
            string json = "{\"sha\":\"" + ShaB + "\",\"commit\":{\"committer\":{\"date\":\"2026-10-30T17:25:28Z\"},"
                          + "\"message\":\"Fix\\u202E the hook\\n\\nLong body\"}}";
            var latest = PluginUpdateService.ParseLatestCommit(Encoding.UTF8.GetBytes(json));
            Assert.Equal(ShaB, latest.Sha);
            Assert.Equal("Fix the hook", latest.Message);
            Assert.Equal(new DateTimeOffset(2026, 10, 30, 17, 25, 28, TimeSpan.Zero), latest.Date);
            Assert.Null(PluginUpdateService.ParseLatestCommit(Encoding.UTF8.GetBytes("{\"sha\":\"main\"}")));
        }

        // ------------------------------------------------------------------ archive

        [Fact]
        public void The_top_folder_is_stripped_and_the_top_folder_itself_is_skipped()
        {
            Assert.Null(PluginUpdateService.StripTopFolder(Top + "/", Top));
            Assert.Equal(Path.Combine("plugins", "multiterminal", "x.js"), PluginUpdateService.StripTopFolder(Top + "/plugins/multiterminal/x.js", Top));
            Assert.Throws<InvalidDataException>(() => PluginUpdateService.StripTopFolder("other-top/x.js", Top));
        }

        [Theory]
        [InlineData(@"..\evil.txt")]
        [InlineData(@"plugins\..\..\evil.txt")]
        [InlineData(@"C:\Windows\evil.txt")]
        [InlineData(@"\evil.txt")]
        [InlineData(@"C:evil.txt")]
        public void An_entry_outside_the_target_is_refused(string relative)
        {
            Assert.Throws<InvalidDataException>(() => PluginUpdateService.SafeEntryPath(Path.Combine(_temp, "target"), relative));
        }

        [Fact]
        public void A_target_prefix_sibling_is_not_inside_the_target()
        {
            // "target-evil" starts with "target"; the guard compares with a trailing separator.
            Assert.Throws<InvalidDataException>(() => PluginUpdateService.SafeEntryPath(Path.Combine(_temp, "target"), @"..\target-evil\x.txt"));
        }

        private static byte[] Zip(params (string Name, string Content)[] entries)
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, content) in entries)
                {
                    var entry = zip.CreateEntry(name);
                    if (content == null) continue;
                    using var w = new StreamWriter(entry.Open());
                    w.Write(content);
                }
            }
            return ms.ToArray();
        }

        private static (string, string)[] ValidMarketplace(string extraName = null, string extraContent = null)
        {
            var list = new List<(string, string)>
            {
                (Top + "/", null),
                (Top + "/.claude-plugin/marketplace.json", "{\"name\":\"multiterminal-marketplace\"}"),
                (Top + "/plugins/multiterminal/.claude-plugin/plugin.json", "{\"name\":\"multiterminal\"}"),
                (Top + "/plugins/multiterminal/hooks/new.js", "// new"),
            };
            if (extraName != null) list.Add((extraName, extraContent));
            return list.ToArray();
        }

        [Fact]
        public void A_zipball_extracts_without_its_top_folder_and_validates()
        {
            string target = Path.Combine(_temp, "out");
            using (var s = new MemoryStream(Zip(ValidMarketplace())))
                PluginUpdateService.ExtractZipball(s, target, 1024 * 1024, 100);
            Assert.True(File.Exists(Path.Combine(target, "plugins", "multiterminal", "hooks", "new.js")));
            Assert.False(Directory.Exists(Path.Combine(target, Top)));
            Assert.Null(PluginUpdateService.ValidateTree(target));
        }

        [Fact]
        public void A_zip_slip_entry_is_refused_and_nothing_lands_outside()
        {
            // Two levels deep, so ../../ lands inside this test's own folder: a broken guard must not write
            // into the shared %TEMP% (it did, during falsification, and poisoned every later run).
            string target = Path.Combine(_temp, "a", "b", "out");
            var zip = Zip(ValidMarketplace(Top + "/../../evil.txt", "pwned"));
            using (var s = new MemoryStream(zip))
                Assert.Throws<InvalidDataException>(() => PluginUpdateService.ExtractZipball(s, target, 1024 * 1024, 100));
            Assert.False(File.Exists(Path.Combine(_temp, "a", "evil.txt")));
        }

        [Fact]
        public void An_archive_over_the_size_or_entry_cap_is_refused()
        {
            var big = Zip(ValidMarketplace(Top + "/big.bin", new string('x', 5000)));
            using (var s = new MemoryStream(big))
                Assert.Throws<InvalidDataException>(() => PluginUpdateService.ExtractZipball(s, Path.Combine(_temp, "a"), 4000, 100));
            using (var s = new MemoryStream(big))
                Assert.Throws<InvalidDataException>(() => PluginUpdateService.ExtractZipball(s, Path.Combine(_temp, "b"), 1024 * 1024, 3));
        }

        [Fact]
        public void A_tree_without_the_plugin_manifest_is_not_valid()
        {
            string target = Path.Combine(_temp, "out");
            using (var s = new MemoryStream(Zip((Top + "/.claude-plugin/marketplace.json", "{\"name\":\"multiterminal-marketplace\"}"))))
                PluginUpdateService.ExtractZipball(s, target, 1024 * 1024, 100);
            Assert.Contains("plugin.json", PluginUpdateService.ValidateTree(target));
        }

        // ------------------------------------------------------------------ refuse rules

        private static PluginUpdateService.GitCommandResult Ok(string output = "") => new PluginUpdateService.GitCommandResult(0, output);
        private static PluginUpdateService.GitCommandResult Exit(int code) => new PluginUpdateService.GitCommandResult(code, null);
        private static PluginUpdateService.GitCommandResult TimedOut() => new PluginUpdateService.GitCommandResult(-1, null, TimedOut: true);

        /// <summary>A checkout git fully vouches for: clean, no stash, fetched, level with main.</summary>
        private static PluginUpdateService.GitProbe CleanProbe() => new PluginUpdateService.GitProbe(true, Ok(), Ok(), Ok(), Ok("0\n"));

        [Fact]
        public void A_clean_checkout_level_with_main_is_replaced_and_no_git_on_path_is_a_leftover()
        {
            Assert.Null(PluginUpdateService.GitCheckoutRefuseReason(@"C:\m", CleanProbe()));
            Assert.Null(PluginUpdateService.GitCheckoutRefuseReason(@"C:\m", new PluginUpdateService.GitProbe(false)));
        }

        private static IEnumerable<(string Label, PluginUpdateService.GitProbe Probe, string Expected)> FailingGitProbes()
        {
            var clean = CleanProbe();
            yield return ("status exit", clean with { Status = Exit(128) }, "could not check");
            yield return ("status timeout", clean with { Status = TimedOut() }, "timed out");
            yield return ("status not run", clean with { Status = null }, "could not check");
            yield return ("stash exit", clean with { Stash = Exit(1) }, "could not check");
            yield return ("fetch exit", clean with { Fetch = Exit(128) }, "could not check");
            yield return ("fetch timeout", clean with { Fetch = TimedOut() }, "timed out");
            yield return ("fetch not run", clean with { Fetch = null }, "could not check");
            yield return ("rev-list exit", clean with { Ahead = Exit(128) }, "could not check");
            yield return ("rev-list garbage", clean with { Ahead = Ok("not a number") }, "could not check");
            yield return ("uncommitted", clean with { Status = Ok(" M hooks/x.js\n") }, "uncommitted");
            yield return ("ignored only", clean with { Status = Ok("!! node_modules/\n") }, "ignored files");
            yield return ("stash", clean with { Stash = Ok("stash@{0}: WIP on main\n") }, "stashed");
            yield return ("ahead", clean with { Ahead = Ok("3\n") }, "not on GitHub main");
        }

        [Fact]
        public void A_git_checkout_fails_closed_on_any_failed_command_or_local_work()
        {
            foreach (var (label, probe, expected) in FailingGitProbes())
            {
                string reason = PluginUpdateService.GitCheckoutRefuseReason(@"C:\m", probe);
                Assert.True(reason != null, label + " was not refused");
                Assert.True(reason.Contains(expected, StringComparison.Ordinal), label + ": " + reason);
            }
        }

        [Fact]
        public void Git_is_asked_only_when_the_root_has_a_git_folder_or_file()
        {
            int probes = 0;
            PluginUpdateService.GitProbe Dirty(string root) { probes++; return CleanProbe() with { Status = Ok("?? x\n") }; }

            // No .git: not asked, no refusal.
            Assert.Null(PluginUpdateService.RefuseReason(@"C:\m", p => p == @"C:\m", _ => false, _ => true, Dirty));
            Assert.Equal(0, probes);

            // A worktree's .git FILE counts.
            Assert.NotNull(PluginUpdateService.RefuseReason(@"C:\m", p => p == @"C:\m", p => p == @"C:\m\.git", _ => true, Dirty));
            Assert.Equal(1, probes);
        }

        [Fact]
        public void A_root_with_git_goes_to_a_timestamped_previous_that_is_never_deleted()
        {
            var now = new DateTimeOffset(2026, 10, 30, 18, 5, 9, TimeSpan.Zero);
            Assert.Equal((@"C:\m.previous-20261030180509", false), PluginUpdateService.PreviousTarget(@"C:\m", true, true, false, now));
            // A plain .previous is replaced only when it holds no .git.
            Assert.Equal((@"C:\m.previous", true), PluginUpdateService.PreviousTarget(@"C:\m", false, true, false, now));
            Assert.Equal((@"C:\m.previous", false), PluginUpdateService.PreviousTarget(@"C:\m", false, false, false, now));
            Assert.Equal((@"C:\m.previous-20261030180509", false), PluginUpdateService.PreviousTarget(@"C:\m", false, true, true, now));
        }

        [Fact]
        public void A_folder_that_cannot_be_written_or_does_not_exist_is_refused()
        {
            PluginUpdateService.GitProbe Never(string root) => throw new InvalidOperationException("no .git here");
            Assert.Contains("cannot write", PluginUpdateService.RefuseReason(@"C:\m", p => p == @"C:\m", _ => false, _ => false, Never),
                StringComparison.Ordinal);
            Assert.Contains("not found", PluginUpdateService.RefuseReason(@"C:\m", _ => false, _ => false, _ => true, Never), StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ swap

        /// <summary>
        /// The production file system (with its retries), failing the Nth call to MoveDirectory (1-based).
        /// Delegating to plain Directory.Move instead made the rollback move fail with "Access denied" on a
        /// folder renamed milliseconds earlier, which is why the production moves retry.
        /// </summary>
        private sealed class FaultyFs : IPluginSwapFileSystem
        {
            private readonly HashSet<int> _failMoves;
            private int _moves;
            public FaultyFs(params int[] failMoves) => _failMoves = new HashSet<int>(failMoves);
            public bool DirectoryExists(string path) => PluginUpdateService.RealSwapFileSystem.Instance.DirectoryExists(path);
            public void DeleteDirectory(string path) => PluginUpdateService.RealSwapFileSystem.Instance.DeleteDirectory(path);
            public void MoveDirectory(string from, string to)
            {
                if (_failMoves.Contains(++_moves)) throw new IOException($"injected fault on move {_moves}");
                PluginUpdateService.RealSwapFileSystem.Instance.MoveDirectory(from, to);
            }
            public void WriteAllText(string path, string text) => PluginUpdateService.RealSwapFileSystem.Instance.WriteAllText(path, text);
            public void DeleteFile(string path) => PluginUpdateService.RealSwapFileSystem.Instance.DeleteFile(path);
        }

        /// <summary>A swap of a root with no .git: to plain .previous, replacing the one there.</summary>
        private static PluginUpdateResult Swap(string root, string incoming, IPluginSwapFileSystem fs) =>
            PluginUpdateService.Swap(root, incoming, fs, root + ".previous", deleteExistingPrevious: true);

        private string MakeTree(string path, string marker, bool valid = true)
        {
            Directory.CreateDirectory(Path.Combine(path, ".claude-plugin"));
            Directory.CreateDirectory(Path.Combine(path, "plugins", "multiterminal", ".claude-plugin"));
            File.WriteAllText(Path.Combine(path, ".claude-plugin", "marketplace.json"), "{\"name\":\"multiterminal-marketplace\"}");
            if (valid)
                File.WriteAllText(Path.Combine(path, "plugins", "multiterminal", ".claude-plugin", "plugin.json"), "{\"name\":\"multiterminal\"}");
            File.WriteAllText(Path.Combine(path, "marker.txt"), marker);
            return path;
        }

        private static string Marker(string root) => File.ReadAllText(Path.Combine(root, "marker.txt"));

        [Fact]
        public void A_swap_puts_the_new_tree_in_and_keeps_one_previous()
        {
            string root = MakeTree(Path.Combine(_temp, "mp"), "old");
            MakeTree(root + ".previous", "older");
            string incoming = MakeTree(Path.Combine(_temp, "mp.incoming"), "new");

            var result = Swap(root, incoming, new FaultyFs());

            Assert.True(result.Success, result.Message);
            Assert.Equal("new", Marker(root));
            Assert.Equal("old", Marker(root + ".previous"));
            Assert.False(Directory.Exists(incoming));
        }

        [Fact]
        public void A_fault_moving_the_new_tree_in_puts_the_old_one_back()
        {
            string root = MakeTree(Path.Combine(_temp, "mp"), "old");
            string incoming = MakeTree(Path.Combine(_temp, "mp.incoming"), "new");

            var result = Swap(root, incoming, new FaultyFs(2));

            Assert.False(result.Success);
            // The exact success wording: "could not be put back" also contains "put back".
            Assert.True(result.Message.Contains("The previous plugin was put back", StringComparison.Ordinal), result.Message);
            Assert.Equal("old", Marker(root));
            Assert.False(Directory.Exists(root + ".previous"));
        }

        [Fact]
        public void A_new_tree_that_fails_validation_after_the_move_is_rolled_back()
        {
            string root = MakeTree(Path.Combine(_temp, "mp"), "old");
            string incoming = MakeTree(Path.Combine(_temp, "mp.incoming"), "new", valid: false);

            var result = Swap(root, incoming, new FaultyFs());

            Assert.False(result.Success);
            Assert.Equal("old", Marker(root));
            // Backed out by a rename, not a delete, so a crash mid-rollback leaves a state the reconcile handles.
            Assert.Equal("new", Marker(incoming));
            Assert.False(File.Exists(root + PluginUpdateService.JournalSuffix));
        }

        [Fact]
        public void A_fault_moving_the_live_root_aside_changes_nothing()
        {
            string root = MakeTree(Path.Combine(_temp, "mp"), "old");
            string incoming = MakeTree(Path.Combine(_temp, "mp.incoming"), "new");

            var result = Swap(root, incoming, new FaultyFs(1));

            Assert.False(result.Success);
            Assert.Contains("Nothing was changed", result.Message);
            Assert.Equal("old", Marker(root));
        }

        [Fact]
        public void A_failed_rollback_says_where_the_old_plugin_is()
        {
            string root = MakeTree(Path.Combine(_temp, "mp"), "old");
            string incoming = MakeTree(Path.Combine(_temp, "mp.incoming"), "new");

            var result = Swap(root, incoming, new FaultyFs(2, 3));

            Assert.False(result.Success);
            Assert.Contains(root + ".previous", result.Message);
            Assert.Equal("old", Marker(root + ".previous"));

            // The next startup finishes the swap from the still-valid .incoming.
            var decision = PluginUpdateService.ReconcileRoot(root, PluginUpdateService.RealSwapFileSystem.Instance, null);
            Assert.Equal(PluginFolderRepair.FinishSwap, decision.Action);
            Assert.Equal("new", Marker(root));
        }

        [Fact]
        public void A_swap_of_a_git_root_leaves_an_older_timestamped_previous_alone()
        {
            string root = MakeTree(Path.Combine(_temp, "mp"), "old");
            string kept = MakeTree(root + ".previous-20261029000000", "developer clone");
            string incoming = MakeTree(Path.Combine(_temp, "mp.incoming"), "new");

            var result = PluginUpdateService.Swap(root, incoming, new FaultyFs(), root + ".previous-20261030000000", deleteExistingPrevious: false);

            Assert.True(result.Success, result.Message);
            Assert.Equal("developer clone", Marker(kept));
            Assert.Equal("old", Marker(root + ".previous-20261030000000"));
        }

        // ------------------------------------------------------------------ reconcile after a crash mid-swap

        private static PluginFolderSnapshot Snap(bool root, bool incoming = false, bool incomingValid = false, bool journal = false,
            params PluginPreviousFolder[] previous) =>
            new PluginFolderSnapshot { RootExists = root, IncomingExists = incoming, IncomingValid = incomingValid, JournalExists = journal, Previous = previous };

        [Fact]
        public void Reconcile_decides_each_folder_state()
        {
            var validPrev = new PluginPreviousFolder(@"C:\m.previous", true);
            var invalidPrev = new PluginPreviousFolder(@"C:\m.previous", false);

            // Root present: nothing, or delete a stale .incoming (valid or not); the journal goes too.
            Assert.Equal(new PluginFolderDecision(PluginFolderRepair.None, null, false, false), PluginUpdateService.DecideRepair(Snap(true)));
            Assert.Equal(new PluginFolderDecision(PluginFolderRepair.DeleteStaleIncoming, null, false, true),
                PluginUpdateService.DecideRepair(Snap(true, incoming: true, incomingValid: true, journal: true, validPrev)));

            // Root missing, valid .incoming: finish, even with a valid previous.
            Assert.Equal(PluginFolderRepair.FinishSwap,
                PluginUpdateService.DecideRepair(Snap(false, incoming: true, incomingValid: true, journal: true, validPrev)).Action);

            // Root missing, invalid or no .incoming, valid previous: restore it; an invalid .incoming is deleted after.
            Assert.Equal(new PluginFolderDecision(PluginFolderRepair.RestorePrevious, @"C:\m.previous", true, true),
                PluginUpdateService.DecideRepair(Snap(false, incoming: true, incomingValid: false, journal: true, validPrev)));
            Assert.Equal(new PluginFolderDecision(PluginFolderRepair.RestorePrevious, @"C:\m.previous", false, false),
                PluginUpdateService.DecideRepair(Snap(false, false, false, false, validPrev)));

            // The first VALID previous wins, in the given order.
            Assert.Equal(@"C:\m.previous-20261030000000", PluginUpdateService.DecideRepair(Snap(false, false, false, false,
                invalidPrev, new PluginPreviousFolder(@"C:\m.previous-20261030000000", true))).RestoreFrom);

            // Root missing, nothing valid: delete nothing, and say so only when something is there.
            Assert.Equal(new PluginFolderDecision(PluginFolderRepair.NothingValid, null, false, false),
                PluginUpdateService.DecideRepair(Snap(false, incoming: true, incomingValid: false, journal: true, invalidPrev)));
            Assert.Equal(new PluginFolderDecision(PluginFolderRepair.None, null, false, false), PluginUpdateService.DecideRepair(Snap(false)));
        }

        [Fact]
        public void Previous_folders_are_ordered_journal_first_then_plain_then_newest_and_strangers_ignored()
        {
            string[] siblings =
            {
                @"C:\x\m.previous-20261001000000", @"C:\x\m.previous", @"C:\x\m.previous-20261030000000",
                @"C:\x\m.previous-junk", @"C:\x\m.previous-2026103000000", // not 14 digits
            };
            Assert.Equal(new[] { @"C:\x\m.previous", @"C:\x\m.previous-20261030000000", @"C:\x\m.previous-20261001000000" },
                PluginUpdateService.OrderPreviousFolders(@"C:\x\m", siblings, null));
            Assert.Equal(new[] { @"C:\x\m.previous-20261001000000", @"C:\x\m.previous", @"C:\x\m.previous-20261030000000" },
                PluginUpdateService.OrderPreviousFolders(@"C:\x\m", siblings, "m.previous-20261001000000"));
            // A journal naming something else (a path, a stranger) changes nothing.
            Assert.Equal(@"C:\x\m.previous", PluginUpdateService.OrderPreviousFolders(@"C:\x\m", siblings, @"..\evil")[0]);
        }

        [Fact]
        public void Terminated_after_rename_1_finishes_the_swap()
        {
            // Rename 1 done (root -> .previous-<time>, journal names it), rename 2 not: root missing.
            string root = Path.Combine(_temp, "mp");
            MakeTree(root + ".previous", "older");
            MakeTree(root + ".previous-20261030000000", "old");
            File.WriteAllText(root + PluginUpdateService.JournalSuffix, "mp.previous-20261030000000");
            MakeTree(root + ".incoming", "new");

            var decision = PluginUpdateService.ReconcileRoot(root, PluginUpdateService.RealSwapFileSystem.Instance, null);

            Assert.Equal(PluginFolderRepair.FinishSwap, decision.Action);
            Assert.Equal("new", Marker(root));
            Assert.False(Directory.Exists(root + ".incoming"));
            Assert.Equal("old", Marker(root + ".previous-20261030000000"));
            Assert.Equal("older", Marker(root + ".previous"));
            Assert.False(File.Exists(root + PluginUpdateService.JournalSuffix));
        }

        [Fact]
        public void Terminated_after_rename_2_keeps_the_new_root_and_clears_the_journal()
        {
            string root = MakeTree(Path.Combine(_temp, "mp"), "new");
            MakeTree(root + ".previous", "old");
            File.WriteAllText(root + PluginUpdateService.JournalSuffix, "mp.previous");

            var decision = PluginUpdateService.ReconcileRoot(root, PluginUpdateService.RealSwapFileSystem.Instance, null);

            Assert.Equal(PluginFolderRepair.None, decision.Action);
            Assert.Equal("new", Marker(root));
            Assert.Equal("old", Marker(root + ".previous"));
            Assert.False(File.Exists(root + PluginUpdateService.JournalSuffix));
        }

        [Fact]
        public void Terminated_mid_rollback_restores_the_journal_named_previous_and_drops_the_invalid_incoming()
        {
            // The new tree failed validation and was renamed back; the crash came before the old one moved back.
            string root = Path.Combine(_temp, "mp");
            MakeTree(root + ".previous", "older");
            MakeTree(root + ".previous-20261030000000", "old");
            File.WriteAllText(root + PluginUpdateService.JournalSuffix, "mp.previous-20261030000000");
            MakeTree(root + ".incoming", "new", valid: false);

            var decision = PluginUpdateService.ReconcileRoot(root, PluginUpdateService.RealSwapFileSystem.Instance, null);

            Assert.Equal(PluginFolderRepair.RestorePrevious, decision.Action);
            Assert.Equal("old", Marker(root));
            Assert.False(Directory.Exists(root + ".incoming"));
            Assert.Equal("older", Marker(root + ".previous"));
        }

        [Fact]
        public void A_stale_incoming_beside_a_live_root_is_deleted()
        {
            string root = MakeTree(Path.Combine(_temp, "mp"), "live");
            MakeTree(root + ".incoming", "never went live");

            Assert.Equal(PluginFolderRepair.DeleteStaleIncoming,
                PluginUpdateService.ReconcileRoot(root, PluginUpdateService.RealSwapFileSystem.Instance, null).Action);
            Assert.Equal("live", Marker(root));
            Assert.False(Directory.Exists(root + ".incoming"));
        }

        [Fact]
        public void A_missing_root_with_nothing_valid_deletes_nothing_and_says_so()
        {
            string root = Path.Combine(_temp, "mp");
            MakeTree(root + ".incoming", "broken new", valid: false);
            MakeTree(root + ".previous", "broken old", valid: false);
            var log = new List<string>();

            var decision = PluginUpdateService.ReconcileRoot(root, PluginUpdateService.RealSwapFileSystem.Instance, log.Add);

            Assert.Equal(PluginFolderRepair.NothingValid, decision.Action);
            Assert.False(Directory.Exists(root));
            Assert.Equal("broken new", Marker(root + ".incoming"));
            Assert.Equal("broken old", Marker(root + ".previous"));
            Assert.Contains(log, m => m.StartsWith("PROBLEM:", StringComparison.Ordinal));
        }

        [Fact]
        public async Task A_check_repairs_an_interrupted_update_before_it_reads_the_stamp()
        {
            string root = Path.Combine(_temp, "multiterminal-marketplace");
            MakeTree(root + ".previous", "old");
            MakeTree(root + ".incoming", "new");
            File.WriteAllText(Path.Combine(root + ".incoming", PluginUpdateService.StampFileName), PluginUpdateService.BuildStampJson(ShaB, DateTimeOffset.UtcNow, "mt-update"));
            string pluginDir = Path.Combine(root, "plugins", "multiterminal");

            using var handler = new StubHandler(_ => CommitAnswer(ShaB));
            using var service = new PluginUpdateService(handler, () => pluginDir, Path.Combine(_temp, "dl"));

            Assert.Equal(PluginCheckOutcome.UpToDate, (await service.CheckAsync()).Outcome);
            Assert.Equal("new", Marker(root));
        }

        // ------------------------------------------------------------------ daily check

        [Fact]
        public void The_daily_check_is_due_after_a_day_or_with_no_or_a_future_record()
        {
            var now = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
            Assert.True(PluginUpdateService.IsDailyCheckDue(null, now));
            Assert.True(PluginUpdateService.IsDailyCheckDue("garbage", now));
            Assert.False(PluginUpdateService.IsDailyCheckDue(now.AddHours(-23).ToString("o"), now));
            Assert.True(PluginUpdateService.IsDailyCheckDue(now.AddHours(-24).ToString("o"), now));
            Assert.True(PluginUpdateService.IsDailyCheckDue(now.AddDays(3).ToString("o"), now));
        }

        [Theory]
        [InlineData(null, true, false)]
        [InlineData("0", false, false)]
        [InlineData("OFF", false, false)]
        [InlineData("disabled", false, false)]
        [InlineData("on", true, false)]
        [InlineData("nope", true, true)]
        public void The_env_switch_follows_the_on_off_convention(string raw, bool enabled, bool warns)
        {
            Assert.Equal(enabled, PluginUpdateService.IsStartupCheckEnabled(raw, out string warning));
            Assert.Equal(warns, warning != null);
        }

        [Fact]
        public void A_notice_is_posted_once_per_commit_and_only_for_a_real_update()
        {
            var update = PluginUpdateService.Decide(@"C:\m", Stamp(ShaA), Latest(ShaB), null);
            Assert.True(PluginUpdateService.ShouldNotify(update, null));
            Assert.False(PluginUpdateService.ShouldNotify(update, ShaB.ToUpperInvariant()));
            Assert.False(PluginUpdateService.ShouldNotify(PluginUpdateService.Decide(@"C:\m", null, Latest(ShaB), null), null));
        }

        // ------------------------------------------------------------------ end to end (stub network, temp folders)

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;
            public readonly List<string> Urls = new List<string>();
            public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) => _answer = answer;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Urls.Add(request.RequestUri.ToString());
                return Task.FromResult(_answer(request));
            }
        }

        private static HttpResponseMessage CommitAnswer(string sha) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"sha\":\"" + sha + "\",\"commit\":{\"committer\":{\"date\":\"2026-10-30T17:25:28Z\"},\"message\":\"m\"}}"),
        };

        [Fact]
        public async Task Check_then_update_installs_exactly_the_checked_commit_and_stamps_it()
        {
            string root = MakeTree(Path.Combine(_temp, "multiterminal-marketplace"), "old");
            File.WriteAllText(Path.Combine(root, PluginUpdateService.StampFileName), PluginUpdateService.BuildStampJson(ShaA, DateTimeOffset.UtcNow, "installer"));
            string pluginDir = Path.Combine(root, "plugins", "multiterminal");
            byte[] zip = Zip(ValidMarketplace());

            using var handler = new StubHandler(req => req.RequestUri.Host == "api.github.com"
                ? CommitAnswer(ShaB)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });
            using var service = new PluginUpdateService(handler, () => pluginDir, Path.Combine(_temp, "dl"));

            var check = await service.CheckAsync();
            Assert.Equal(PluginCheckOutcome.UpdateAvailable, check.Outcome);

            var result = await service.UpdateAsync(check);

            Assert.True(result.Success, result.Message);
            Assert.Equal(PluginUpdateService.ZipUrlPrefix + ShaB, handler.Urls[^1]);
            Assert.Equal(ShaB, PluginUpdateService.ReadStamp(root).Commit);
            Assert.Equal("mt-update", PluginUpdateService.ReadStamp(root).StampedBy);
            Assert.True(File.Exists(Path.Combine(root, "plugins", "multiterminal", "hooks", "new.js")));
            Assert.False(File.Exists(Path.Combine(root, "marker.txt"))); // the old tree is replaced whole
            Assert.Equal("old", Marker(root + ".previous"));
            Assert.False(Directory.Exists(root + ".incoming"));
            Assert.Equal(PluginCheckOutcome.UpToDate, (await service.CheckAsync()).Outcome);
        }

        [Fact]
        public async Task A_zip_slip_download_leaves_the_live_plugin_untouched()
        {
            // Nested so the extraction folder (<root>.incoming) has ../../ inside this test's folder.
            string root = MakeTree(Path.Combine(_temp, "a", "b", "multiterminal-marketplace"), "old");
            string pluginDir = Path.Combine(root, "plugins", "multiterminal");
            byte[] zip = Zip(ValidMarketplace(Top + "/../../evil.txt", "pwned"));

            using var handler = new StubHandler(req => req.RequestUri.Host == "api.github.com"
                ? CommitAnswer(ShaB)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });
            using var service = new PluginUpdateService(handler, () => pluginDir, Path.Combine(_temp, "dl"));

            var result = await service.UpdateAsync(await service.CheckAsync());

            Assert.False(result.Success);
            Assert.Contains("archive was refused", result.Message);
            Assert.Equal("old", Marker(root));
            Assert.False(Directory.Exists(root + ".previous"));
            Assert.False(File.Exists(Path.Combine(_temp, "a", "evil.txt")));
        }

        [Fact]
        public async Task A_rate_limited_check_fails_and_offers_no_update()
        {
            string root = MakeTree(Path.Combine(_temp, "multiterminal-marketplace"), "old");
            using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
            using var service = new PluginUpdateService(handler, () => Path.Combine(root, "plugins", "multiterminal"), Path.Combine(_temp, "dl"));

            var check = await service.CheckAsync();

            Assert.Equal(PluginCheckOutcome.Failed, check.Outcome);
            Assert.Contains("403", check.Reason);
            Assert.False((await service.UpdateAsync(check)).Success);
        }
        // ------------------------------------------------------------------ cross-process lock

        /// <summary>Opened the way another MultiTerminal process would hold it: no sharing, independent of the service's own open.</summary>
        private static FileStream HoldLockAsAnotherProcess(string root) =>
            new FileStream(root + PluginUpdateService.LockSuffix, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        [Fact]
        public void A_second_holder_of_a_root_lock_is_refused_until_the_first_lets_go()
        {
            string root = MakeTree(Path.Combine(_temp, "mp"), "old");

            using (var first = PluginUpdateService.TryOpenRootLock(root, out var firstOutcome, out _))
            {
                Assert.Equal(PluginUpdateService.RootLockOutcome.Acquired, firstOutcome);
                Assert.NotNull(first);

                // The same root spelled differently is the same lock: the path comes from the canonical root.
                var second = PluginUpdateService.TryOpenRootLock(root + @"\..\mp\", out var secondOutcome, out _);
                Assert.Null(second);
                Assert.Equal(PluginUpdateService.RootLockOutcome.HeldElsewhere, secondOutcome);
            }

            using var third = PluginUpdateService.TryOpenRootLock(root, out var thirdOutcome, out _);
            Assert.Equal(PluginUpdateService.RootLockOutcome.Acquired, thirdOutcome);
        }

        [Fact]
        public void Reconcile_skips_a_root_another_process_holds_and_repairs_it_once_released()
        {
            string root = Path.Combine(_temp, "multiterminal-marketplace");
            MakeTree(root + ".previous", "old");
            MakeTree(root + ".incoming", "new");
            string[] pluginDirs = { Path.Combine(root, "plugins", "multiterminal") };
            var log = new List<string>();

            using (HoldLockAsAnotherProcess(root))
            {
                var started = DateTime.UtcNow;
                PluginUpdateService.ReconcileRoots(pluginDirs, PluginUpdateService.RealSwapFileSystem.Instance, log.Add, TimeSpan.FromMilliseconds(300));

                Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3), "the reconcile waited past its budget");
                Assert.False(Directory.Exists(root)); // the other process's in-progress state is untouched
                Assert.Equal("new", Marker(root + ".incoming"));
                Assert.Contains(log, m => m.StartsWith("Skipped", StringComparison.Ordinal));
            }

            PluginUpdateService.ReconcileRoots(pluginDirs, PluginUpdateService.RealSwapFileSystem.Instance, log.Add, TimeSpan.Zero);
            Assert.Equal("new", Marker(root));
        }

        [Fact]
        public void A_lock_file_beside_a_missing_root_is_not_a_repair_candidate()
        {
            string root = Path.Combine(_temp, "mp");
            Directory.CreateDirectory(_temp);
            File.WriteAllText(root + PluginUpdateService.LockSuffix, "");

            var decision = PluginUpdateService.ReconcileRoot(root, PluginUpdateService.RealSwapFileSystem.Instance, null);

            Assert.Equal(PluginFolderRepair.None, decision.Action);
            Assert.True(File.Exists(root + PluginUpdateService.LockSuffix));
        }

        [Fact]
        public async Task An_update_holds_the_lock_throughout_and_is_refused_while_another_process_holds_it()
        {
            string root = MakeTree(Path.Combine(_temp, "multiterminal-marketplace"), "old");
            File.WriteAllText(Path.Combine(root, PluginUpdateService.StampFileName), PluginUpdateService.BuildStampJson(ShaA, DateTimeOffset.UtcNow, "installer"));
            string pluginDir = Path.Combine(root, "plugins", "multiterminal");
            byte[] zip = Zip(ValidMarketplace());
            PluginUpdateService.RootLockOutcome? duringDownload = null;

            using var handler = new StubHandler(req =>
            {
                if (req.RequestUri.Host == "api.github.com") return CommitAnswer(ShaB);
                PluginUpdateService.TryOpenRootLock(root, out var outcome, out _)?.Dispose();
                duringDownload = outcome;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
            });
            using var service = new PluginUpdateService(handler, () => pluginDir, Path.Combine(_temp, "dl"))
            {
                UpdateLockWait = TimeSpan.FromMilliseconds(300),
            };
            var check = await service.CheckAsync();

            using (HoldLockAsAnotherProcess(root))
            {
                var refused = await service.UpdateAsync(check);
                Assert.False(refused.Success);
                Assert.Contains("Another MultiTerminal is updating", refused.Message);
                Assert.Equal("old", Marker(root));
                Assert.Null(duringDownload); // refused before anything was downloaded
            }

            // The in-process gate was taken first and released with the failed lock, so this one runs.
            var result = await service.UpdateAsync(check);
            Assert.True(result.Success, result.Message);
            Assert.Equal(PluginUpdateService.RootLockOutcome.HeldElsewhere, duringDownload);
            Assert.Equal(ShaB, PluginUpdateService.ReadStamp(root).Commit);
        }
    }
}
