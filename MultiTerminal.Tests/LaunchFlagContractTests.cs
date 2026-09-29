using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Pins the launch flags across every launch path, per METHOD (tasks c9285d2a and 77d1182f; split
    /// and renamed from <c>ChannelFlagContractTests</c> by ticket 0ff1b520, item 16). Two rules:
    /// <list type="number">
    /// <item>Every launch site loads the MultiTerminal plugin with <c>--plugin-dir</c>. Without it a
    /// terminal boots a bare Claude session: no hooks, so no MT identity, no activity, and no native
    /// messaging credentials posted, which means MT cannot deliver to it at all.</item>
    /// <item>No launch site authorizes a development channel (<c>--dangerously-load-development-channels</c>,
    /// or a <c>plugin:…@inline</c> channel entry). MT delivers through Claude Code's native
    /// cross-session messaging now. The flag also puts a warning dialog in front of every session,
    /// which MT used to answer by typing "1" on a match that included the generic "enter to confirm";
    /// that auto-accept went in the same change, so a returning flag would leave every terminal stuck
    /// on the dialog.</item>
    /// </list>
    ///
    /// <para>HOW IT IS PINNED. Two ways, because neither alone is enough.
    /// <see cref="The_built_claude_command_loads_the_plugin_and_authorizes_no_channel"/> builds the real
    /// command, but the flags depend on whether this machine has a plugin checkout, so on a machine
    /// without one it cannot tell the old code from the new. The census below is machine-independent:
    /// it parses first-party source with Roslyn and reads only STRING TOKENS, so a comment is trivia
    /// and cannot satisfy or trip it. That matters for rule 2 in particular: "do not add
    /// --dangerously-load-development-channels here" above the code is exactly the comment this
    /// codebase's style invites, and a text census would fail on it.</para>
    ///
    /// <para>WHY THE CENSUS IS METHOD-GRANULAR (task 77d1182f). The first version listed <em>files</em>.
    /// <c>MainForm.cs</c> sat in a known-defect list for ONE method, and that entry hid a SECOND, worse
    /// method in the same file that launched with neither flag. The census enumerates (file, method)
    /// pairs and classifies every member that touches a launch flag or hand-rolls a launch literal.
    /// A defect is a positive (file, method, ticket) entry that FAILS the day it is fixed, and an
    /// undeclared site fails loudly with its flag profile.</para>
    /// </summary>
    public class LaunchFlagContractTests
    {
        /// <summary>The CLI flag that authorizes a development channel server. Must appear nowhere.</summary>
        private const string ChannelFlag = "--dangerously-load-development-channels";

        /// <summary>The marketplace sentinel of a <c>--plugin-dir</c> plugin's channel entry. Must appear nowhere.</summary>
        private const string InlineChannelSuffix = "@inline";

        /// <summary>The CLI flag that session-loads the MultiTerminal plugin.</summary>
        private const string PluginDirFlag = "--plugin-dir";

        /// <summary>
        /// A launch command written out by hand instead of built by <c>LaunchCommandBuilder</c>.
        /// Every hand-rolled site to date starts with exactly this text.
        /// </summary>
        private const string HandRolledLaunchLiteral = "claude --dangerously-skip-permissions";

        /// <summary>
        /// Launch sites that are CORRECT: each loads the plugin and authorizes no channel. A new site is
        /// a deliberate decision, not a copy-paste that drifts.
        /// </summary>
        private static readonly LaunchSiteKey[] CorrectLaunchSites =
        {
            new LaunchSiteKey(Path.Combine("Services", "LaunchCommandBuilder.cs"), "BuildFlags"),
            new LaunchSiteKey(Path.Combine("Services", "OracleService.cs"), "BuildAutoRunCommand"),
        };

        /// <summary>
        /// Launch sites KNOWN to omit <c>--plugin-dir</c>. A defect list, not an exemption list: each entry
        /// is asserted positively by <see cref="Known_defects_are_still_defective_rather_than_stale_exemptions"/>,
        /// so it fails the day the defect is fixed.
        /// </summary>
        /// <remarks>
        /// Empty since task cb4883b6 routed <c>MainForm.OnLaunchAsIdentityRequested</c> through the builder.
        /// Keep the list: the next hand-rolled site is declared here, not quietly exempted. A site that
        /// authorizes a channel can never be declared here; rule 2 has no exemptions.
        /// </remarks>
        private static readonly KnownDefect[] KnownDefectiveLaunchSites = Array.Empty<KnownDefect>();

        /// <summary>
        /// Methods that contain the hand-rolled launch literal but do NOT launch anything: they populate a
        /// settings dropdown of command presets.
        /// </summary>
        private static readonly LaunchSiteKey[] LaunchLiteralPresets =
        {
            new LaunchSiteKey(Path.Combine("Services", "SettingsService.cs"), "GetClaudeCommands"),
        };

        [Fact]
        public void The_built_claude_command_loads_the_plugin_and_authorizes_no_channel()
        {
            // Behavioural, through the public builder that docked and spawned terminals both use.
            // Rule 2 holds on every machine. It DISCRIMINATES only where the plugin checkout exists
            // with a server directory, which is when the removed code emitted the flag. Once the
            // plugin ships without server/ (ticket 0ff1b520 item 17) that is nowhere, so the census
            // below is the only guard that can go red; this fact stays as the end-to-end statement.
            string dir = Path.Combine(Path.GetTempPath(), "mt-launch-flags-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string command = LaunchCommandBuilder.BuildClaudeCommand(project: null, workingDirectory: dir).AutoRunCommand;

                Assert.False(string.IsNullOrEmpty(command));
                Assert.DoesNotContain(ChannelFlag, command, StringComparison.Ordinal);
                Assert.DoesNotContain(InlineChannelSuffix, command, StringComparison.Ordinal);

                if (LaunchCommandBuilder.GetMtPluginPath() != null)
                {
                    Assert.Contains(PluginDirFlag, command, StringComparison.Ordinal);
                }
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void No_first_party_source_authorizes_a_development_channel()
        {
            // Every member in first-party source, rostered or not: rule 2 has no exemptions, so it is
            // not routed through the roster at all.
            var offenders = LaunchSites()
                .Where(site => site.AuthorizesChannel)
                .Select(site => $"{site.Key} ({site.Profile})")
                .ToList();

            Assert.True(
                offenders.Count == 0,
                "No launch path may emit '" + ChannelFlag + "' or a '" + InlineChannelSuffix + "' channel entry "
                + "(ticket 0ff1b520, item 16). MT delivers through native cross-session messaging, and nothing "
                + "answers the warning dialog the flag raises any more. Offending member(s):"
                + Environment.NewLine + string.Join(Environment.NewLine, offenders));
        }

        [Fact]
        public void Every_launch_site_is_either_correct_or_a_declared_defect()
        {
            var problems = ClassificationProblems(
                LaunchSites().ToList(), CorrectLaunchSites, KnownDefectiveLaunchSites, LaunchLiteralPresets);

            Assert.True(
                problems.Count == 0,
                "Every method that loads the plugin or hand-rolls a launch command must be a correct launch "
                + "site (plugin yes, channel no) or a declared (file, method, ticket) defect. A launch path "
                + "without --plugin-dir boots a Claude session with no MT identity and no native messaging "
                + "(task 77d1182f). Update the rosters in the same commit as the intended change."
                + Environment.NewLine + string.Join(Environment.NewLine, problems));
        }

        [Fact]
        public void Declared_launch_site_rosters_match_source_exactly()
        {
            // A roster entry that no longer exists in source is a stale claim: renamed (and the census
            // silently stopped watching it) or deleted (and the entry should go with it).
            var found = LaunchSites().Select(site => site.Key).ToHashSet(StringComparer.Ordinal);

            var missing = CorrectLaunchSites
                .Concat(KnownDefectiveLaunchSites.Select(defect => defect.Site))
                .Concat(LaunchLiteralPresets)
                .Select(key => key.ToString())
                .Where(key => !found.Contains(key))
                .ToList();

            Assert.True(
                missing.Count == 0,
                "Declared launch site(s) not found in source — renamed, deleted, or the roster is wrong:"
                + Environment.NewLine + string.Join(Environment.NewLine, missing));
        }

        [Fact]
        public void Known_defects_are_still_defective_rather_than_stale_exemptions()
        {
            var byKey = LaunchSites().ToDictionary(site => site.Key, StringComparer.Ordinal);

            foreach (KnownDefect defect in KnownDefectiveLaunchSites)
            {
                Assert.True(
                    byKey.TryGetValue(defect.Site.ToString(), out LaunchSite site),
                    $"Known-defect site '{defect.Site}' (task {defect.Ticket}) was not found in source.");

                Assert.False(
                    site.LoadsPlugin,
                    $"'{defect.Site}' now loads the plugin — task {defect.Ticket} appears to be fixed. Remove "
                    + $"it from {nameof(KnownDefectiveLaunchSites)} and add it to {nameof(CorrectLaunchSites)}.");
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
                    + "authorizes a channel — it has become a launch path. Reclassify it.");
            }
        }

        [Fact]
        public void Census_sees_code_in_every_member_shape_and_ignores_comments()
        {
            // Negative fixture driving the REAL scanner and the REAL classification over a controlled
            // file, in BOTH failure directions:
            //   - code that emits the channel flag, or only the @inline entry, IS seen (hides-a-defect);
            //   - a refusal comment naming both is NOT seen (manufactures-a-failure).
            // Plus the 77d1182f shape: further broken members in a file that already has a correct one.
            // Assembled from parts so this source file is not itself a site.
            string correct =
                "        public string A() { return \"claude\" + \" " + PluginDirFlag + " x\"; }";
            string withFlag =
                "        public string B() { return \"claude\" + \" " + PluginDirFlag + " x\" + \" "
                + ChannelFlag + " plugin:p" + InlineChannelSuffix + "\"; }";
            string inlineOnly =
                "        public string G() { return \" --channels plugin:p" + InlineChannelSuffix + "\"; }";
            string literalOnly =
                "        public string C() { string cmd = \"" + HandRolledLaunchLiteral + "\"; return cmd; }";
            string refusalComment =
                "        public string D() { // do NOT add " + ChannelFlag + " plugin:p" + InlineChannelSuffix
                + " here; " + PluginDirFlag + " " + HandRolledLaunchLiteral
                + Environment.NewLine + "            return \"unrelated\"; }";
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
                    correct,
                    withFlag,
                    inlineOnly,
                    literalOnly,
                    refusalComment,
                    propertyOnly,
                    fieldOnly,
                    "    }",
                    "}",
                });

                var sites = ScanLaunchSites(file).ToList();

                Assert.Equal(
                    new[] { "A", "B", "C", "E (property)", "F (field)", "G" },
                    sites.Select(s => s.Method).OrderBy(m => m, StringComparer.Ordinal));
                Assert.DoesNotContain(sites, s => s.Method == "D");

                LaunchSite a = sites.Single(s => s.Method == "A");
                LaunchSite b = sites.Single(s => s.Method == "B");
                LaunchSite g = sites.Single(s => s.Method == "G");
                Assert.True(a.LoadsPlugin && !a.AuthorizesChannel);
                Assert.True(b.LoadsPlugin && b.AuthorizesChannel);
                Assert.True(g.AuthorizesChannel && !g.LoadsPlugin);

                // With only A declared correct, every other member surfaces, and A does not. B is flagged
                // even though it loads the plugin, because it also authorizes a channel.
                var problems = ClassificationProblems(
                    sites,
                    new[] { new LaunchSiteKey("Fake.cs", "A"), new LaunchSiteKey("Fake.cs", "B") },
                    Array.Empty<KnownDefect>(),
                    Array.Empty<LaunchSiteKey>());

                Assert.Equal(5, problems.Count);
                Assert.Contains(problems, p => p.Contains("INCOMPLETE Fake.cs::B", StringComparison.Ordinal));
                foreach (string member in new[] { "C", "E (property)", "F (field)", "G" })
                {
                    Assert.Contains(problems, p => p.Contains("UNDECLARED Fake.cs::" + member, StringComparison.Ordinal));
                }

                Assert.DoesNotContain(problems, p => p.Contains("Fake.cs::A", StringComparison.Ordinal));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void Census_actually_reaches_the_declared_sites_rather_than_matching_nothing()
        {
            // The vacuity guard. The channel fact asserts an ABSENCE, so a scanner that found nothing
            // would pass it. Every correct site must be seen, and seen loading the plugin.
            var byKey = LaunchSites().ToDictionary(site => site.Key, StringComparer.Ordinal);

            foreach (LaunchSiteKey key in CorrectLaunchSites)
            {
                Assert.True(
                    byKey.TryGetValue(key.ToString(), out LaunchSite site) && site.LoadsPlugin,
                    $"The scanner did not see '{key}' loading the plugin. A census that matches nothing "
                    + "passes the no-channel fact while proving nothing.");
            }
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

        /// <summary>A launch site known to omit the plugin, with the ticket that tracks it.</summary>
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

        /// <summary>One member in first-party source that touches a launch flag or literal.</summary>
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

            /// <summary>Emits the channel flag or an <c>@inline</c> channel entry.</summary>
            public bool AuthorizesChannel { get; }

            public bool HandRollsLaunch { get; }

            public string Key => this.RelativePath + "::" + this.Method;

            public string Profile =>
                $"plugin:{(this.LoadsPlugin ? "yes" : "no")} channel:{(this.AuthorizesChannel ? "yes" : "no")} hand-rolled:{(this.HandRollsLaunch ? "yes" : "no")}";
        }

        /// <summary>
        /// The classification predicate behind <see cref="Every_launch_site_is_either_correct_or_a_declared_defect"/>,
        /// factored out so the negative fixture drives the real rule. One line per problem.
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
                else if (!site.LoadsPlugin || site.AuthorizesChannel)
                {
                    problems.Add($"DECLARED CORRECT BUT INCOMPLETE {site.Key} ({site.Profile})");
                }
            }

            return problems;
        }

        /// <summary>Every launch site across first-party source, attributed to its member.</summary>
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
        /// The member-level scanner over a single file. Parses with Roslyn and walks the string tokens of
        /// each leaf member (ordinary, interpolated, verbatim and raw literals alike). Comments are
        /// trivia, not tokens, so a member that only <em>mentions</em> a flag in prose is not a site.
        /// </summary>
        private static IEnumerable<LaunchSite> ScanLaunchSites(string file, string repoRoot = null)
        {
            string relativePath = repoRoot == null ? Path.GetFileName(file) : Path.GetRelativePath(repoRoot, file);
            SyntaxNode root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();

            // Leaf members only — a type's DescendantTokens would re-count every member inside it.
            // Properties and fields are walked too: a launch literal parked in one of those is the same
            // "one shape hides a site" class this census exists to close (77d1182f pipeline Run 1).
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
                    authorizesChannel |= text.Contains(ChannelFlag, StringComparison.Ordinal)
                                         || text.Contains(InlineChannelSuffix, StringComparison.Ordinal);
                    handRollsLaunch |= text.Contains(HandRolledLaunchLiteral, StringComparison.Ordinal);
                }

                if (loadsPlugin || authorizesChannel || handRollsLaunch)
                {
                    yield return new LaunchSite(relativePath, memberName, loadsPlugin, authorizesChannel, handRollsLaunch);
                }
            }
        }

        /// <summary>
        /// The census key for a leaf member, or null for members that are not sites (types, namespaces,
        /// events, delegates).
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
        /// C# sources belonging to the app itself: build output, nested worktrees, vendored code and the
        /// test project are all excluded. The test project matters — this very file names the flags.
        /// </summary>
        /// <remarks>
        /// Exclusions are matched against the path RELATIVE to <paramref name="repoRoot"/>. When the suite
        /// runs inside an MT task worktree the repo root is itself <c>…\.claude\worktrees\&lt;id&gt;</c>,
        /// so an absolute-substring test for <c>.claude</c> excludes every file and the census silently
        /// matches nothing. That is how this method first behaved; the vacuity guard caught it.
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

        private static string RepoRoot([CallerFilePath] string thisFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", ".."));
    }
}
