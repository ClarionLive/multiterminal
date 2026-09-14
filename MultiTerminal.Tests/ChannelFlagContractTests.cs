using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Pins the channel-authorization flag string across every launch path (task c9285d2a, item 4),
    /// and — since task 77d1182f — pins it per METHOD rather than per file.
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
    /// <c>LaunchCommandBuilder.BuildFlags</c> (private) and hand-rolled copies, each of which
    /// calls <c>GetMtPluginPath()</c> and probes the real filesystem — so a behavioural assertion
    /// would pass or fail on whether the machine happens to have a plugin checkout, which is exactly
    /// the kind of environment-dependence CI cannot rely on. The actual risk is a string repeated
    /// across files that no compiler checks, so it is pinned where it lives. Same discipline as
    /// <c>PanelPersistRosterTests</c> and the cross-file checks in <c>BoardHudDoorwayTests</c>.</para>
    ///
    /// <para>WHY THE CENSUS IS METHOD-GRANULAR (task 77d1182f). The first version of this file
    /// listed <em>files</em>. <c>MainForm.cs</c> sat in a known-defect list for ONE method
    /// (<c>OnLaunchAsIdentityRequested</c>, task 5999a182), and that single entry hid a SECOND,
    /// worse method in the same file — <c>OnSpawnRequested</c> hardcoded a bare
    /// <c>claude --dangerously-skip-permissions</c> with neither flag, so every API-spawned
    /// terminal booted without the plugin or the channel. A green run was compatible with any
    /// number of additional broken methods in an already-listed file. The census now enumerates
    /// (file, method) pairs, attributes each site with Roslyn rather than a line heuristic, and
    /// classifies every method that touches a launch flag or hand-rolls a launch literal. A defect
    /// is recorded as a positive (file, method, ticket) entry that FAILS the day it is fixed — so
    /// the excuse must be deleted rather than left as a stale exemption — and an undeclared site
    /// fails loudly with its flag profile.</para>
    /// </summary>
    public class ChannelFlagContractTests
    {
        /// <summary>The CLI flag that authorizes a development (inline) channel server.</summary>
        private const string ChannelFlag = "--dangerously-load-development-channels";

        /// <summary>The CLI flag that session-loads the MultiTerminal plugin.</summary>
        private const string PluginDirFlag = "--plugin-dir";

        /// <summary>
        /// A launch command written out by hand instead of built by <c>LaunchCommandBuilder</c>.
        /// Every hand-rolled site to date starts with exactly this text; a site that emits it and
        /// nothing else launches a terminal with no plugin and no channel.
        /// </summary>
        private const string HandRolledLaunchLiteral = "claude --dangerously-skip-permissions";

        /// <summary>
        /// The sentinel marketplace Claude Code assigns to a plugin loaded via <c>--plugin-dir</c>.
        /// Load-bearing: channel registration matches on it.
        /// </summary>
        private const string RequiredMarketplaceSuffix = "@inline";

        /// <summary>
        /// Launch sites that are CORRECT: each method both loads the plugin and authorizes the
        /// channel. Kept as an explicit census so a new site is a deliberate decision rather than a
        /// copy-paste that drifts. A method listed here that stops emitting either flag fails
        /// <see cref="Every_launch_site_is_either_correct_or_a_declared_defect"/>.
        /// </summary>
        private static readonly LaunchSiteKey[] CorrectLaunchSites =
        {
            new LaunchSiteKey(Path.Combine("Services", "LaunchCommandBuilder.cs"), "BuildFlags"),
            new LaunchSiteKey(Path.Combine("Services", "OracleService.cs"), "BuildAutoRunCommand"),
        };

        /// <summary>
        /// Launch sites KNOWN to load the plugin but omit the channel flag — their terminals run
        /// with channels dead. This is a defect list, not an exemption list: each entry is asserted
        /// positively by <see cref="Known_defects_are_still_defective_rather_than_stale_exemptions"/>,
        /// so it fails the day the defect is fixed and forces whoever fixes it to delete the entry.
        /// </summary>
        /// <remarks>
        /// <c>MainForm.OnSpawnRequested</c> was the defect that motivated this rewrite (task
        /// 77d1182f) and was deliberately NOT listed here while it was broken: listing it would have
        /// turned this file into an excuse for the very defect that ticket existed to fix. The census
        /// was left RED on it until the routing fix landed in the same change set — the proof that
        /// the census can see a second broken method in an already-listed file. It now routes
        /// through <c>LaunchCommandBuilder</c> and emits no launch literal, so it is not a site at all.
        /// </remarks>
        private static readonly KnownDefect[] KnownDefectiveLaunchSites =
        {
            new KnownDefect("MainForm.cs", "OnLaunchAsIdentityRequested", "5999a182"),
        };

        /// <summary>
        /// Methods that contain the hand-rolled launch literal but do NOT launch anything: they
        /// populate a settings dropdown of command presets. Declared positively so the literal
        /// trigger cannot count a menu item as a launch path, and asserted by
        /// <see cref="Presets_are_not_launch_paths"/> so a preset that starts emitting real flags is
        /// reclassified rather than silently exempt.
        /// </summary>
        private static readonly LaunchSiteKey[] LaunchLiteralPresets =
        {
            new LaunchSiteKey(Path.Combine("Services", "SettingsService.cs"), "GetClaudeCommands"),
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
        public void Every_launch_site_is_either_correct_or_a_declared_defect()
        {
            var sites = LaunchSites().ToList();

            var problems = ClassificationProblems(
                sites, CorrectLaunchSites, KnownDefectiveLaunchSites, LaunchLiteralPresets);

            Assert.True(
                problems.Count == 0,
                "Every method that loads the plugin, authorizes the channel, or hand-rolls a launch "
                + "command must be a correct launch site (BOTH flags) or a declared (file, method, "
                + "ticket) defect. This is a census, not a style rule: a launch path that omits the "
                + "channel flag runs with channels dead and nothing reports it; one that omits BOTH "
                + "flags boots a bare Claude session with no MT identity at all (task 77d1182f). "
                + "Update the rosters in the same commit as the intended change."
                + Environment.NewLine + string.Join(Environment.NewLine, problems));
        }

        [Fact]
        public void Declared_launch_site_rosters_match_source_exactly()
        {
            // A roster entry that no longer exists in source is a stale claim — either the method
            // was renamed (and the census silently stopped watching it) or it was deleted (and the
            // excuse should go with it).
            var found = LaunchSites().Select(site => site.Key).ToHashSet(StringComparer.Ordinal);

            var declared = CorrectLaunchSites
                .Concat(KnownDefectiveLaunchSites.Select(defect => defect.Site))
                .Concat(LaunchLiteralPresets)
                .Select(key => key.ToString())
                .ToList();

            var missing = declared.Where(key => !found.Contains(key)).ToList();

            Assert.True(
                missing.Count == 0,
                "Declared launch site(s) not found in source — renamed, deleted, or the roster is "
                + "wrong. Fix the roster in the same commit:"
                + Environment.NewLine + string.Join(Environment.NewLine, missing));
        }

        [Fact]
        public void Known_defects_are_still_defective_rather_than_stale_exemptions()
        {
            // A whitelist expresses a known bug as an ABSENCE, so a green run reads as "every launch
            // path is correct" when one demonstrably is not. State the defect positively instead:
            // this fact fails the day the listed method gains the channel flag, forcing whoever fixes
            // the ticket to delete the excuse rather than leave a stale exemption behind.
            var byKey = LaunchSites().ToDictionary(site => site.Key, StringComparer.Ordinal);

            foreach (KnownDefect defect in KnownDefectiveLaunchSites)
            {
                Assert.True(
                    byKey.TryGetValue(defect.Site.ToString(), out LaunchSite site),
                    $"Known-defect site '{defect.Site}' (task {defect.Ticket}) was not found in source.");

                Assert.False(
                    site.AuthorizesChannel,
                    $"'{defect.Site}' now authorizes the channel — task {defect.Ticket} appears to be "
                    + $"fixed. Remove it from {nameof(KnownDefectiveLaunchSites)} and add it to "
                    + $"{nameof(CorrectLaunchSites)} in the same commit.");

                Assert.True(
                    site.LoadsPlugin,
                    $"'{defect.Site}' no longer loads the plugin either. That is a different defect "
                    + $"from the one task {defect.Ticket} records (plugin yes, channel no); "
                    + "re-describe it rather than let the entry drift.");
            }
        }

        [Fact]
        public void Presets_are_not_launch_paths()
        {
            var byKey = LaunchSites().ToDictionary(site => site.Key, StringComparer.Ordinal);

            foreach (LaunchSiteKey preset in LaunchLiteralPresets)
            {
                Assert.True(
                    byKey.TryGetValue(preset.ToString(), out LaunchSite site),
                    $"Preset site '{preset}' was not found in source.");

                Assert.True(
                    site.HandRollsLaunch && !site.LoadsPlugin && !site.AuthorizesChannel,
                    $"'{preset}' is declared a preset (literal only), but it now loads the plugin or "
                    + "authorizes the channel — it has become a launch path. Reclassify it.");
            }
        }

        [Fact]
        public void Method_census_falsifies_on_a_second_broken_method_in_an_already_listed_file()
        {
            // Negative fixture for the exact shape that hid task 77d1182f: one file, one method
            // declared correct, and further methods in the SAME file that drop one or both flags.
            // Drives the REAL scanner and the REAL classification predicate over a controlled file,
            // so it proves the census can see a second broken method rather than proving that a
            // re-implementation agrees with itself. Assembled from parts so this source file is not
            // itself flagged.
            string both =
                "        public string A() { return \"claude\" + \" " + PluginDirFlag + " x\" + \" "
                + ChannelFlag + " plugin:p" + RequiredMarketplaceSuffix + "\"; }";
            string pluginOnly =
                "        public string B() { return \"claude\" + \" " + PluginDirFlag + " x\"; }";
            string literalOnly =
                "        public string C() { string cmd = \"" + HandRolledLaunchLiteral + "\"; return cmd; }";
            string commentOnly =
                "        public string D() { // " + PluginDirFlag + " " + ChannelFlag + " " + HandRolledLaunchLiteral
                + Environment.NewLine + "            return \"unrelated\"; }";
            // Sites that are NOT methods — the shapes a method-only walk would miss (pipeline Run 1).
            string propertyOnly =
                "        public string E => \"" + HandRolledLaunchLiteral + "\";";
            string fieldOnly =
                "        private const string F = \" " + PluginDirFlag + " x\";";

            string dir = Path.Combine(Path.GetTempPath(), "mt-launch-census-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string file = Path.Combine(dir, "Fake.cs");
                File.WriteAllLines(file, new[]
                {
                    "namespace Fixture",
                    "{",
                    "    public class Launcher",
                    "    {",
                    both,
                    pluginOnly,
                    literalOnly,
                    commentOnly,
                    propertyOnly,
                    fieldOnly,
                    "    }",
                    "}",
                });

                var sites = ScanLaunchSites(file).ToList();

                // Attribution: the three code methods, the property and the field are seen and named;
                // the comment-only method is NOT a site, because comments are trivia and the scanner
                // walks tokens; the class itself is not a site, because only leaf members are walked.
                Assert.Equal(
                    new[] { "A", "B", "C", "E (property)", "F (field)" },
                    sites.Select(s => s.Method).OrderBy(m => m, StringComparer.Ordinal));
                Assert.DoesNotContain(sites, s => s.Method == "D");

                LaunchSite a = sites.Single(s => s.Method == "A");
                LaunchSite b = sites.Single(s => s.Method == "B");
                LaunchSite c = sites.Single(s => s.Method == "C");
                LaunchSite e = sites.Single(s => s.Method == "E (property)");
                LaunchSite f = sites.Single(s => s.Method == "F (field)");
                Assert.True(a.LoadsPlugin && a.AuthorizesChannel);
                Assert.True(b.LoadsPlugin && !b.AuthorizesChannel);
                Assert.True(c.HandRollsLaunch && !c.LoadsPlugin && !c.AuthorizesChannel);
                Assert.True(e.HandRollsLaunch && !e.LoadsPlugin && !e.AuthorizesChannel);
                Assert.True(f.LoadsPlugin && !f.AuthorizesChannel && !f.HandRollsLaunch);

                // Classification: with only A declared correct, every other site in the already-listed
                // file — the two methods, the property and the field — must surface, and A must not.
                var problems = ClassificationProblems(
                    sites,
                    new[] { new LaunchSiteKey("Fake.cs", "A") },
                    Array.Empty<KnownDefect>(),
                    Array.Empty<LaunchSiteKey>());

                Assert.Equal(4, problems.Count);
                Assert.Contains(problems, p => p.Contains("Fake.cs::B", StringComparison.Ordinal));
                Assert.Contains(problems, p => p.Contains("Fake.cs::C", StringComparison.Ordinal));
                Assert.Contains(problems, p => p.Contains("Fake.cs::E (property)", StringComparison.Ordinal));
                Assert.Contains(problems, p => p.Contains("Fake.cs::F (field)", StringComparison.Ordinal));
                Assert.DoesNotContain(problems, p => p.Contains("Fake.cs::A", StringComparison.Ordinal));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
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
        public void Census_actually_finds_the_known_sites_rather_than_matching_nothing()
        {
            // The other half of the vacuous-pass guard: prove both scanners reach real source.
            var lines = ChannelFlagLines().ToList();
            var sites = LaunchSites().ToList();
            int declared = CorrectLaunchSites.Length + KnownDefectiveLaunchSites.Length + LaunchLiteralPresets.Length;

            Assert.True(
                lines.Count >= CorrectLaunchSites.Length,
                $"Line scanner found only {lines.Count} channel-flag line(s); expected at least "
                + $"{CorrectLaunchSites.Length}. A census that matches nothing passes every assertion "
                + "above while proving nothing.");

            Assert.True(
                sites.Count >= declared,
                $"Method scanner found only {sites.Count} launch site(s); expected at least {declared}.");
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

        /// <summary>A (file, method) pair. The string form <c>path::Method</c> is the comparison key.</summary>
        private readonly struct LaunchSiteKey
        {
            public LaunchSiteKey(string relativePath, string method)
            {
                this.RelativePath = relativePath;
                this.Method = method;
            }

            public string RelativePath { get; }

            public string Method { get; }

            public override string ToString() => this.RelativePath + "::" + this.Method;
        }

        /// <summary>A launch site known to be broken, with the ticket that tracks it.</summary>
        private readonly struct KnownDefect
        {
            public KnownDefect(string relativePath, string method, string ticket)
            {
                this.Site = new LaunchSiteKey(relativePath, method);
                this.Ticket = ticket;
            }

            public LaunchSiteKey Site { get; }

            public string Ticket { get; }
        }

        /// <summary>One method in first-party source that touches a launch flag or literal.</summary>
        private readonly struct LaunchSite
        {
            public LaunchSite(string relativePath, string method, bool loadsPlugin, bool authorizesChannel, bool handRollsLaunch)
            {
                this.RelativePath = relativePath;
                this.Method = method;
                this.LoadsPlugin = loadsPlugin;
                this.AuthorizesChannel = authorizesChannel;
                this.HandRollsLaunch = handRollsLaunch;
            }

            public string RelativePath { get; }

            public string Method { get; }

            public bool LoadsPlugin { get; }

            public bool AuthorizesChannel { get; }

            public bool HandRollsLaunch { get; }

            public string Key => this.RelativePath + "::" + this.Method;

            public string Profile =>
                $"plugin:{(this.LoadsPlugin ? "yes" : "no")} channel:{(this.AuthorizesChannel ? "yes" : "no")} hand-rolled:{(this.HandRollsLaunch ? "yes" : "no")}";
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
        /// The classification predicate behind
        /// <see cref="Every_launch_site_is_either_correct_or_a_declared_defect"/>, factored out so
        /// the negative fixture drives the real rule. Returns one human-readable line per problem.
        /// </summary>
        private static List<string> ClassificationProblems(
            IEnumerable<LaunchSite> sites,
            IEnumerable<LaunchSiteKey> correct,
            IEnumerable<KnownDefect> defects,
            IEnumerable<LaunchSiteKey> presets)
        {
            var correctKeys = correct.Select(key => key.ToString()).ToHashSet(StringComparer.Ordinal);
            var defectKeys = defects.Select(defect => defect.Site.ToString()).ToHashSet(StringComparer.Ordinal);
            var presetKeys = presets.Select(key => key.ToString()).ToHashSet(StringComparer.Ordinal);

            var problems = new List<string>();
            foreach (LaunchSite site in sites)
            {
                if (presetKeys.Contains(site.Key) || defectKeys.Contains(site.Key))
                {
                    continue; // asserted positively by their own facts
                }

                if (!correctKeys.Contains(site.Key))
                {
                    problems.Add($"UNDECLARED {site.Key} ({site.Profile})");
                }
                else if (!site.LoadsPlugin || !site.AuthorizesChannel)
                {
                    problems.Add($"DECLARED CORRECT BUT INCOMPLETE {site.Key} ({site.Profile})");
                }
            }

            return problems;
        }

        /// <summary>Every launch site across first-party source, attributed to its method.</summary>
        private static IEnumerable<LaunchSite> LaunchSites()
        {
            string repoRoot = RepoRoot();

            foreach (string file in EnumerateFirstPartySources(repoRoot))
            {
                foreach (LaunchSite site in ScanLaunchSites(file, repoRoot))
                {
                    yield return site;
                }
            }
        }

        /// <summary>
        /// The method-level scanner over a single file. Parses with Roslyn and walks the string
        /// tokens of each method (ordinary, interpolated, verbatim and raw literals alike). Comments
        /// are trivia, not tokens, so a method that only <em>mentions</em> a flag in prose is not a
        /// site — the same rule <see cref="IsCommentLine"/> approximates for the line scanner, but
        /// enforced by the parser rather than by a leading-<c>//</c> heuristic.
        /// </summary>
        private static IEnumerable<LaunchSite> ScanLaunchSites(string file, string repoRoot = null)
        {
            string relativePath = repoRoot == null ? Path.GetFileName(file) : Path.GetRelativePath(repoRoot, file);
            SyntaxNode root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();

            // Leaf members only — a type's DescendantTokens would re-count every member inside it.
            // Methods and constructors cover the launch paths that exist today; properties (including
            // expression-bodied and accessor bodies) and fields/consts are walked too, because a launch
            // literal parked in one of those is the same "one shape hides a site" class this census
            // exists to close (pipeline Run 1, code-reviewer + debugger).
            foreach (MemberDeclarationSyntax member in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
            {
                string memberName = MemberName(member);
                if (memberName == null)
                {
                    continue; // namespaces, types, enums, events: containers or non-sites
                }

                bool loadsPlugin = false;
                bool authorizesChannel = false;
                bool handRollsLaunch = false;

                foreach (SyntaxToken token in member.DescendantTokens())
                {
                    if (!token.IsKind(SyntaxKind.StringLiteralToken)
                        && !token.IsKind(SyntaxKind.InterpolatedStringTextToken)
                        && !token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken)
                        && !token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken))
                    {
                        continue;
                    }

                    string text = token.Text;
                    loadsPlugin |= text.Contains(PluginDirFlag, StringComparison.Ordinal);
                    authorizesChannel |= text.Contains(ChannelFlag, StringComparison.Ordinal);
                    handRollsLaunch |= text.Contains(HandRolledLaunchLiteral, StringComparison.Ordinal);
                }

                if (loadsPlugin || authorizesChannel || handRollsLaunch)
                {
                    yield return new LaunchSite(relativePath, memberName, loadsPlugin, authorizesChannel, handRollsLaunch);
                }
            }
        }

        /// <summary>
        /// The census key for a leaf member, or null for members that are not sites (types,
        /// namespaces, events, delegates). Properties and fields are named so a site parked in one
        /// reads as <c>Prop (property)</c> / <c>Field (field)</c> in a failure message.
        /// </summary>
        private static string MemberName(MemberDeclarationSyntax member)
        {
            switch (member)
            {
                case MethodDeclarationSyntax m:
                    return m.Identifier.Text;
                case ConstructorDeclarationSyntax c:
                    return c.Identifier.Text + " (ctor)";
                case PropertyDeclarationSyntax p:
                    return p.Identifier.Text + " (property)";
                case FieldDeclarationSyntax f:
                    return string.Join(",", f.Declaration.Variables.Select(v => v.Identifier.Text)) + " (field)";
                default:
                    return null;
            }
        }

        /// <summary>
        /// Every line of first-party C# source that <em>emits</em> the channel-authorization flag.
        /// Comment lines are skipped: the launch sites each explain the flag in prose directly
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
        /// The line scanner itself, over a single file. Split out so the negative fixture can drive
        /// the REAL predicate over a controlled file instead of re-implementing it — a fixture that
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
