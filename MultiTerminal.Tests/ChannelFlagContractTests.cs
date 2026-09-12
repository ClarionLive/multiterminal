using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Pins the channel-authorization flag string across every launch path (task c9285d2a, item 4).
    ///
    /// <para>THE REGRESSION THIS EXISTS TO CATCH. Every MT terminal opens with a warning that reads
    /// like an error — <c>plugin:multiterminal@inline · plugin not installed</c>. It is a false
    /// positive: Claude Code validates the channel entry against the *installed* plugin registry,
    /// and a <c>--plugin-dir</c> plugin is session-loaded rather than installed, so it is
    /// legitimately absent from that registry. The obvious "fix" is to change <c>@inline</c> to
    /// <c>@multiterminal-marketplace</c> so it matches the installed key.</para>
    ///
    /// <para><b>That change silently kills channels.</b> Registration runs a *different* check, and
    /// the marketplace mismatch is a skip evaluated BEFORE the dev-flag check — so
    /// <c>--dangerously-load-development-channels</c> does not rescue it. Nothing throws, nothing
    /// logs an error, and <c>get_messages</c> polling keeps working perfectly; only the unprompted
    /// push dies, which looks like "nobody messaged me" rather than like a fault. The string dates
    /// to c790be1 (2026-04-01) and until this file NO TEST PINNED IT.</para>
    ///
    /// <para>WHY A SOURCE-LEVEL CENSUS RATHER THAN A BEHAVIOURAL TEST. The flag is built by
    /// <c>LaunchCommandBuilder.BuildFlags</c> (private) and two hand-rolled copies, each of which
    /// calls <c>GetMtPluginPath()</c> and probes the real filesystem — so a behavioural assertion
    /// would pass or fail on whether the machine happens to have a plugin checkout, which is exactly
    /// the kind of environment-dependence CI cannot rely on. The actual risk is a string repeated
    /// across files that no compiler checks, so it is pinned where it lives. Same discipline as
    /// <c>PanelPersistRosterTests</c> and the cross-file checks in <c>BoardHudDoorwayTests</c>.</para>
    /// </summary>
    public class ChannelFlagContractTests
    {
        /// <summary>The CLI flag that authorizes a development (inline) channel server.</summary>
        private const string ChannelFlag = "--dangerously-load-development-channels";

        /// <summary>
        /// The sentinel marketplace Claude Code assigns to a plugin loaded via <c>--plugin-dir</c>.
        /// Load-bearing: channel registration matches on it.
        /// </summary>
        private const string RequiredMarketplaceSuffix = "@inline";

        /// <summary>
        /// The launch paths that build the channel flag today. Kept as an explicit census so a new
        /// site is a deliberate decision rather than a copy-paste that drifts.
        /// </summary>
        /// <remarks>
        /// <c>MainForm.cs</c> is deliberately ABSENT: <c>OnLaunchAsIdentityRequested</c> passes
        /// <c>--plugin-dir</c> but no channel flag, so terminals restarted via "Launch as identity…"
        /// have dead channels. That is a real pre-existing bug tracked as task 5999a182 — when it is
        /// fixed, MainForm.cs joins this list and this test is updated in the same commit.
        /// </remarks>
        private static readonly string[] ExpectedChannelFlagFiles =
        {
            Path.Combine("Services", "LaunchCommandBuilder.cs"),
            Path.Combine("Services", "OracleService.cs"),
            Path.Combine("Services", "TerminalSpawner.cs"),
        };

        /// <summary>
        /// Launch paths that load the plugin but are KNOWN to omit the channel flag — i.e. their
        /// terminals run with channels dead. This is a defect list, not an exemption list: it is
        /// asserted positively by
        /// <see cref="MainForm_is_recorded_as_a_known_defect_rather_than_merely_omitted"/> so the gap
        /// cannot hide inside a green run as a mere absence from
        /// <see cref="ExpectedChannelFlagFiles"/>. Tracked as task 5999a182.
        /// </summary>
        private static readonly string[] KnownMissingChannelFlagFiles =
        {
            "MainForm.cs",
        };

        [Fact]
        public void Every_channel_flag_site_in_source_uses_the_inline_marketplace()
        {
            var offenders = ChannelFlagLines()
                .Where(line => !line.Text.Contains(RequiredMarketplaceSuffix, StringComparison.Ordinal))
                .Select(line => $"{line.RelativePath}:{line.LineNumber}: {line.Text.Trim()}")
                .ToList();

            Assert.True(
                offenders.Count == 0,
                "Every channel-flag site must emit the '@inline' marketplace sentinel. A plugin loaded "
                + "via --plugin-dir is sourced as name@inline, and channel registration skips on a "
                + "marketplace mismatch BEFORE the dev-flag check — so a different string disables push "
                + "delivery silently, with polling still working. Offending site(s):"
                + Environment.NewLine + string.Join(Environment.NewLine, offenders));
        }

        [Fact]
        public void Channel_flag_sites_are_exactly_the_known_launch_paths()
        {
            var actual = ChannelFlagLines()
                .Select(line => line.RelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var expected = ExpectedChannelFlagFiles
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Assert.True(
                expected.SequenceEqual(actual, StringComparer.OrdinalIgnoreCase),
                "The set of launch paths that authorize the channel server changed. This is a census, "
                + "not a style rule: a launch path that loads the plugin but omits this flag runs with "
                + "channels dead and nothing reports it (see task 5999a182). Update "
                + nameof(ExpectedChannelFlagFiles) + " in the same commit as the intended change."
                + Environment.NewLine + "expected: " + string.Join(", ", expected)
                + Environment.NewLine + "actual:   " + string.Join(", ", actual));
        }

        [Fact]
        public void Census_falsifies_on_the_marketplace_string_that_would_break_channels()
        {
            // Negative fixture: run the REAL scanner over a file carrying the regression and prove it
            // is caught. An earlier version of this fact built a string and asserted Contains on it
            // without ever calling the scanner — so it stayed green even if the scanner were deleted,
            // which is the exact vacuous-pass class it is named for. Caught in review; fixed here.
            // Assembled from parts so this source file is not itself flagged by the census.
            string offending =
                "flags += \" " + ChannelFlag + " plugin:{pluginName}" + "@multiterminal-marketplace\";";
            string benign =
                "flags += \" " + ChannelFlag + " plugin:{pluginName}" + RequiredMarketplaceSuffix + "\";";

            string dir = Path.Combine(Path.GetTempPath(), "mt-channel-census-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string file = Path.Combine(dir, "Fake.cs");
                File.WriteAllLines(file, new[] { "// a comment mentioning " + ChannelFlag, offending, benign });

                var flagged = ScanFile(file).ToList();

                // The comment line is skipped; both code lines are seen.
                Assert.Equal(2, flagged.Count);

                // And the offending one — and only it — fails the rule the census enforces.
                var offenders = flagged
                    .Where(line => !line.Text.Contains(RequiredMarketplaceSuffix, StringComparison.Ordinal))
                    .ToList();
                Assert.Single(offenders);
                Assert.Contains("@multiterminal-marketplace", offenders[0].Text, StringComparison.Ordinal);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void MainForm_is_recorded_as_a_known_defect_rather_than_merely_omitted()
        {
            // A whitelist expresses a known bug as an ABSENCE, so a green run reads as "every launch
            // path is correct" when one demonstrably is not. State the defect positively instead: this
            // fact fails the day MainForm gains the flag, forcing whoever fixes task 5999a182 to delete
            // the excuse rather than leave a stale exemption behind.
            var withFlag = ChannelFlagLines()
                .Select(line => line.RelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (string knownMissing in KnownMissingChannelFlagFiles)
            {
                Assert.False(
                    withFlag.Contains(knownMissing, StringComparer.OrdinalIgnoreCase),
                    $"'{knownMissing}' now builds the channel flag — task 5999a182 appears to be fixed. "
                    + $"Remove it from {nameof(KnownMissingChannelFlagFiles)} and add it to "
                    + $"{nameof(ExpectedChannelFlagFiles)} in the same commit.");

                string path = Path.Combine(RepoRoot(), knownMissing);
                Assert.True(File.Exists(path), $"Known-defect file '{knownMissing}' no longer exists at '{path}'.");
                Assert.Contains("--plugin-dir", File.ReadAllText(path), StringComparison.Ordinal);
            }
        }

        [Fact]
        public void Census_actually_finds_the_known_sites_rather_than_matching_nothing()
        {
            // The other half of the vacuous-pass guard: prove the scanner reaches real source.
            var found = ChannelFlagLines().ToList();

            Assert.True(
                found.Count >= ExpectedChannelFlagFiles.Length,
                $"Scanner found only {found.Count} channel-flag line(s); expected at least "
                + $"{ExpectedChannelFlagFiles.Length}. A census that matches nothing passes every "
                + "assertion above while proving nothing.");
        }

        [Fact]
        public void Docs_state_the_same_marketplace_string_the_code_emits()
        {
            // channels.html documents the name@inline rule as fact. It is correct today, and it is
            // the page a reader consults before "fixing" the warning — so it must not drift from
            // the code independently.
            string docs = File.ReadAllText(RequiredFile("docs", "html", "channels.html"));

            Assert.Contains("plugin:multiterminal" + RequiredMarketplaceSuffix, docs, StringComparison.Ordinal);
            Assert.DoesNotContain("plugin:multiterminal@multiterminal-marketplace", docs, StringComparison.Ordinal);
        }

        private readonly struct SourceLine
        {
            public SourceLine(string relativePath, int lineNumber, string text)
            {
                this.RelativePath = relativePath;
                this.LineNumber = lineNumber;
                this.Text = text;
            }

            public string RelativePath { get; }

            public int LineNumber { get; }

            public string Text { get; }
        }

        /// <summary>
        /// Every line of first-party C# source that <em>emits</em> the channel-authorization flag.
        /// Comment lines are skipped: the three launch sites each explain the flag in prose directly
        /// above the code that builds it, and a census of emission sites must not count the
        /// explanation as a site. A file whose only mention is a comment is not a launch path.
        /// </summary>
        private static IEnumerable<SourceLine> ChannelFlagLines()
        {
            string repoRoot = RepoRoot();

            foreach (string file in EnumerateFirstPartySources(repoRoot))
            {
                foreach (SourceLine line in ScanFile(file, repoRoot))
                {
                    yield return line;
                }
            }
        }

        /// <summary>
        /// The scanner itself, over a single file. Split out so the negative fixture can drive the
        /// REAL predicate over a controlled file instead of re-implementing it — a fixture that
        /// re-implements the thing it is checking proves only that the copy agrees with itself.
        /// </summary>
        private static IEnumerable<SourceLine> ScanFile(string file, string repoRoot = null)
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(ChannelFlag, StringComparison.Ordinal)
                    && !IsCommentLine(lines[i]))
                {
                    yield return new SourceLine(
                        repoRoot == null ? Path.GetFileName(file) : Path.GetRelativePath(repoRoot, file),
                        i + 1,
                        lines[i]);
                }
            }
        }

        /// <summary>
        /// True for a whole-line comment (<c>//</c>, <c>///</c>, or a <c>*</c> continuation of a
        /// block comment). Deliberately conservative: it only skips lines that BEGIN as a comment,
        /// so a trailing <c>// …</c> on a line that also emits the flag is still inspected.
        /// </summary>
        private static bool IsCommentLine(string line)
        {
            string trimmed = line.TrimStart();
            return trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith('*');
        }

        /// <summary>
        /// C# sources belonging to the app itself: build output, nested worktrees, vendored code and
        /// the test project are all excluded. The test project matters — this very file names the
        /// flag, and scanning it would make the census assert against itself.
        /// </summary>
        /// <remarks>
        /// Exclusions are matched against the path RELATIVE to <paramref name="repoRoot"/>, never
        /// against the absolute path. When the suite runs inside an MT task worktree the repo root
        /// is itself <c>…\.claude\worktrees\&lt;id&gt;</c>, so an absolute-substring test for
        /// <c>.claude</c> excludes every file in the repository and the census silently matches
        /// nothing. That is not hypothetical — it is how this method first behaved, and the
        /// vacuity guard above is what caught it.
        /// </remarks>
        private static IEnumerable<string> EnumerateFirstPartySources(string repoRoot)
        {
            string[] excludedTopSegments =
            {
                "bin",
                "obj",
                "node_modules",
                ".claude",
                "MultiTerminal.Tests",
            };

            return Directory
                .EnumerateFiles(repoRoot, "*.cs", SearchOption.AllDirectories)
                .Where(file =>
                {
                    string[] segments = Path.GetRelativePath(repoRoot, file)
                        .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                    return !segments.Any(segment => excludedTopSegments.Contains(
                        segment, StringComparer.OrdinalIgnoreCase));
                });
        }

        private static string RequiredFile(params string[] relativeParts)
        {
            string path = Path.Combine(new[] { RepoRoot() }.Concat(relativeParts).ToArray());
            Assert.True(File.Exists(path), $"Could not locate '{Path.Combine(relativeParts)}' at '{path}'.");
            return path;
        }

        private static string RepoRoot([CallerFilePath] string thisFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", ".."));
    }
}
