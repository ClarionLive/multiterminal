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
    /// Enumerates every place production code opens a SQLite connection, and requires each one to be a
    /// DECLARED site whose path comes from a guarded resolver (task 2ddfc32f).
    ///
    /// <para><b>The hole this closes.</b> <see cref="MultiTerminal.Services.ProductionDataGuard"/> makes
    /// it impossible for a test host to open the three production databases we know about. It does
    /// nothing about a FOURTH. Someone adding <c>analytics.db</c> next month writes a path resolver, opens
    /// a connection, and bypasses the guard entirely — no test fails, nothing logs, and the first symptom
    /// is a test run mutating live data. That is the same "fixed once, not made impossible" shape the
    /// ticket exists to remove, so leaving it would have meant shipping the argument without the
    /// guarantee.</para>
    ///
    /// <para><b>Why a roster census is exact here rather than heuristic.</b> Production code opens a
    /// SQLite connection in exactly THREE members. A census over "files that look like they resolve a
    /// database path" would need to guess at intent; a census over <c>new SQLiteConnection(</c> does not
    /// guess at all — it is a syntactic fact, and the roster is small enough to enumerate by hand. A new
    /// site fails until someone declares it, and declaring it is what forces them to look at the
    /// guard.</para>
    ///
    /// <para><b>Why METHOD-granular and not file-granular.</b> This codebase has now been bitten by the
    /// file-granular version twice in two days. <c>ChannelFlagContractTests</c> listed <em>files</em>, so
    /// <c>MainForm.cs</c> being on a known-defect list for one method hid a second, worse method in the
    /// same file (task 77d1182f). Then this very ticket's planning census was keyed on one environment
    /// variable name and reported "no leaking file" while two classes were opening the production
    /// <c>messages.db</c>. A file-granular census here would hide a second, unguarded connection site in
    /// an already-listed file — the identical failure, a third time.</para>
    /// </summary>
    public class SqliteConnectionSiteCensusTests
    {
        /// <summary>The syntactic fact this census is built on.</summary>
        private const string ConnectionType = "SQLiteConnection";

        /// <summary>
        /// Every production member permitted to open a SQLite connection, with the guarded source its
        /// path comes from. Adding an entry here is deliberate work: you must name how the path is
        /// guarded, which is the moment someone discovers <c>ProductionDataGuard</c> exists.
        /// </summary>
        private static readonly Dictionary<string, string> DeclaredSites = new(StringComparer.Ordinal)
        {
            [@"Services\MultiterminalDb.cs::Open"] =
                "path from TaskDatabase.GetDatabasePath(), which routes through ProductionDataGuard",
            [@"Services\MessageQueueDatabase.cs::InitializeDatabase"] =
                "path from MessageQueueDatabase.GetDatabasePath(), which routes through ProductionDataGuard",
            [@"Services\GatewayIntegrationService.cs::GetConnection"] =
                "path from _gatewayDbPath, guarded in the constructor (no override; untestable by design)",
        };

        /// <summary>
        /// Calls that count as "this file obtained its path from a guarded resolver". A declared site's
        /// file must INVOKE at least one, so deleting the guard from a resolver fails the census rather
        /// than silently leaving a declared-but-unguarded site behind.
        /// </summary>
        /// <remarks>
        /// Every entry is METHOD-qualified, not a bare type name. The first draft listed
        /// <c>"ProductionDataGuard"</c> — a type — which can never match an invoked expression
        /// (<c>ProductionDataGuard.Guard(…)</c> invokes <c>Guard</c>, not the type), so the entry was
        /// dead weight that matched nothing while looking like coverage. A second entry's trailing
        /// member (<c>GetDatabasePath</c>) happened to match one file for unrelated reasons, which is
        /// exactly how a broken matcher survives: it is right often enough not to be noticed.
        /// </remarks>
        private static readonly string[] GuardedPathSources =
        {
            "ProductionDataGuard.Guard",
            "TaskDatabase.GetDatabasePath",
            "MessageQueueDatabase.GetDatabasePath",
        };

        /// <summary>
        /// No production member may open a SQLite connection unless it is declared above.
        /// </summary>
        [Fact]
        public void Every_sqlite_connection_site_is_declared()
        {
            var found = ScanConnectionSites().Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

            var undeclared = found.Where(k => !DeclaredSites.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();

            Assert.True(
                undeclared.Count == 0,
                "Undeclared SQLite connection site(s):\n  " + string.Join("\n  ", undeclared)
                + "\n\nA new database bypasses ProductionDataGuard unless its path comes from a guarded "
                + "resolver. Route the path through ProductionDataGuard.Guard (see TaskDatabase."
                + "ResolveDatabasePath for the shape), then add the site to DeclaredSites naming how it "
                + "is guarded. Do not add the entry first — the entry is the claim, the routing is the fix.");
        }

        /// <summary>
        /// A declared site that no longer exists is a stale exemption, and a stale exemption is an excuse
        /// nobody re-examines. Fail so it must be deleted.
        /// </summary>
        [Fact]
        public void No_declared_site_is_stale()
        {
            var found = ScanConnectionSites().Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

            var stale = DeclaredSites.Keys.Where(k => !found.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();

            Assert.True(
                stale.Count == 0,
                "DeclaredSites lists member(s) that no longer open a SQLite connection:\n  "
                + string.Join("\n  ", stale)
                + "\n\nDelete the entry. A roster that outlives its sites stops describing the code.");
        }

        /// <summary>
        /// Each declared site's file must actually CALL a guarded path source. Without this the roster
        /// would be a list of names: removing the guard from a resolver would leave its site declared,
        /// the census green, and the database unprotected.
        /// </summary>
        /// <remarks>
        /// ⚠️ This was a whole-file <c>text.Contains("ProductionDataGuard")</c> and two pipeline gates
        /// rejected it, one with the proof: <c>Services/MultiterminalDb.cs</c> carries
        /// <c>&lt;see cref="TaskDatabase.GetDatabasePath"/&gt;</c> inside its class XML doc, so that file
        /// satisfied the old check on a COMMENT alone. Replacing the real call with a hardcoded
        /// <c>%APPDATA%</c> path left the token present, the site declared, the census green and the
        /// database unguarded — precisely the failure this fact's own summary claims it prevents. A
        /// vacuity check that is itself vacuous is worse than none, because it is counted as coverage.
        ///
        /// <para>Now matched on the syntax tree: comments and doc-comments are TRIVIA, not invocation
        /// expressions, so prose cannot satisfy it. Same discipline as
        /// <c>ChannelFlagContractTests.ScanLaunchSites</c>, which walks string tokens for the same reason.</para>
        /// </remarks>
        [Fact]
        public void Every_declared_site_draws_its_path_from_a_guarded_source()
        {
            string repoRoot = RepoRoot();
            var offenders = new List<string>();

            foreach (string key in DeclaredSites.Keys)
            {
                string[] parts = key.Split("::");
                string full = Path.Combine(repoRoot, parts[0]);
                Assert.True(File.Exists(full), $"Declared site points at a missing file: {full}");

                if (!CallsAGuardedSource(full, parts[1]))
                    offenders.Add(key);
            }

            Assert.True(
                offenders.Count == 0,
                "Declared site(s) whose file contains no CALL to a guarded path source:\n  "
                + string.Join("\n  ", offenders)
                + "\n\nExpected an invocation of one of: " + string.Join(", ", GuardedPathSources)
                + "\nA mention in a comment or doc-comment does not count — that is what this check exists to catch.");
        }

        /// <summary>
        /// True when the DECLARED MEMBER contains a real, qualified invocation of a guarded path source.
        /// </summary>
        /// <remarks>
        /// <para>⚠️ Two defects lived here and both are worth remembering, because each made this check
        /// pass for the wrong reason while the class summary above argued against exactly that.</para>
        ///
        /// <para><b>It was FILE-granular.</b> It took <c>key.Split("::")[0]</c> and asked whether anything
        /// anywhere in the file called a guarded source — so a second, unguarded connection added to an
        /// already-declared file was satisfied by the FIRST site's guarded call. It now scans the
        /// declaring TYPE of the named member.</para>
        ///
        /// <para><b>Why the TYPE and not the member, which is what the review recommended.</b> Two of the
        /// three declared sites do not call a guarded source themselves and correctly should not:
        /// <c>MessageQueueDatabase.InitializeDatabase</c> and <c>GatewayIntegrationService.GetConnection</c>
        /// both read a field (<c>_databasePath</c>, <c>_gatewayDbPath</c>) that the CONSTRUCTOR resolved
        /// through the guard. Resolve-once-in-the-ctor, use-in-a-method is the legitimate shape here, so a
        /// member-scoped check would fail two real sites and the obvious way to "fix" that would be to
        /// weaken it back to the file. Type scope matches the shape while still being narrower than a file,
        /// which can hold several types.</para>
        ///
        /// <para><b>⚠️ BOUNDED CLAIM — what this does NOT prove.</b> It cannot show that THIS member's path
        /// came from the guarded call; that needs dataflow analysis. A second method added to an
        /// already-declared TYPE, opening a connection on a hardcoded path, still satisfies this check via
        /// the constructor's guarded call. The protection against that is
        /// <see cref="Every_sqlite_connection_site_is_declared"/>, which sees any new member and fails
        /// until someone declares it — not this fact. What this fact actually asserts is narrower than its
        /// name suggests: the type owning a declared site obtains a path from a guarded source.</para>
        ///
        /// <para><b>It accepted a bare member name.</b> A fallback matched any callee ending in
        /// <c>GetDatabasePath</c> or <c>Guard</c>, justified in a comment by "MessageQueueDatabase calls
        /// its own GetDatabasePath() unqualified". That justification was FALSE — all three declared
        /// members already invoke the qualified form, so the fallback was never the deciding match. Its
        /// only effect was to let the next database class, copied from TaskDatabase and calling its own
        /// unqualified <c>GetDatabasePath()</c>, satisfy this check with no guard anywhere. Requiring the
        /// qualified form makes the census strictly stronger and costs nothing.</para>
        /// </remarks>
        private static bool CallsAGuardedSource(string file, string memberName)
        {
            SyntaxNode root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();

            // The type(s) declaring a member of this name — normally exactly one.
            var owningTypes = root.DescendantNodes()
                .OfType<TypeDeclarationSyntax>()
                .Where(t => t.Members.Any(m => string.Equals(MemberName(m), memberName, StringComparison.Ordinal)))
                .ToList();

            Assert.True(
                owningTypes.Count > 0,
                $"No type in '{file}' declares a member named '{memberName}'. The DeclaredSites roster is "
                + "out of step with the source — fix the key rather than loosening this check.");

            return owningTypes.Any(type => type.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Select(i => i.Expression.ToString())
                .Any(callee => GuardedPathSources.Any(
                    source => callee.EndsWith(source, StringComparison.Ordinal))));
        }

        /// <summary>
        /// The roster is exactly the three databases documented on task 2ddfc32f. If this number moves,
        /// the exposure of the whole ticket has changed and someone should say so out loud.
        /// </summary>
        [Fact]
        public void The_roster_is_the_three_known_databases()
        {
            Assert.Equal(3, DeclaredSites.Count);
        }

        /// <summary>
        /// The scan domain must match the project's COMPILE domain, not a hand-picked folder list
        /// (pipeline Run 1, three gates). This proves the two specific places the old six-directory
        /// allow-list could not see: a root-level file, and a panel folder.
        /// </summary>
        /// <remarks>
        /// <c>MainForm.cs</c> is not an arbitrary example — it already constructs a
        /// <see cref="MultiTerminal.Services.TaskDatabase"/>, so it is a plausible home for the next
        /// database and was invisible to the census as originally written.
        /// </remarks>
        [Theory]
        [InlineData("MainForm.cs")]
        [InlineData("TasksPanel")]
        [InlineData("Dialogs")]
        [InlineData("Models")]
        public void The_scan_domain_covers_the_whole_compile_surface(string expectedSegment)
        {
            string repoRoot = RepoRoot();
            var scanned = ProductionSourceFiles(repoRoot)
                .Select(f => Path.GetRelativePath(repoRoot, f))
                .ToList();

            Assert.True(
                scanned.Count > 0,
                "The scan matched NO production source at all — almost certainly the relative-vs-absolute "
                + "exclusion trap documented on ProductionSourceFiles. Every roster fact would pass "
                + "vacuously in this state.");

            Assert.True(
                scanned.Any(p => p.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                  .Contains(expectedSegment, StringComparer.OrdinalIgnoreCase)),
                $"The census never walks '{expectedSegment}', so a SQLite connection opened there would "
                + "not be seen and every roster assertion would still pass.");
        }

        /// <summary>
        /// The test project must stay OUT of the scan: this file and ProductionDataGuardTests both name
        /// SQLiteConnection-adjacent symbols, and scanning them would make the census assert against
        /// itself. The sibling census documents the same exclusion for the same reason.
        /// </summary>
        [Fact]
        public void The_scan_excludes_the_test_project()
        {
            string repoRoot = RepoRoot();

            Assert.DoesNotContain(
                ProductionSourceFiles(repoRoot).Select(f => Path.GetRelativePath(repoRoot, f)),
                p => p.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                      .Contains("MultiTerminal.Tests", StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Walks production C# and yields "relativePath::MemberName" for every member that constructs a
        /// SQLiteConnection. Leaf members only, so a type's own tokens are not re-counted for each member
        /// inside it — the same discipline as ChannelFlagContractTests.ScanLaunchSites.
        /// </summary>
        private static IEnumerable<KeyValuePair<string, string>> ScanConnectionSites()
        {
            string repoRoot = RepoRoot();

            foreach (string file in ProductionSourceFiles(repoRoot))
            {
                string relative = Path.GetRelativePath(repoRoot, file);
                SyntaxNode root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();

                foreach (MemberDeclarationSyntax member in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
                {
                    string name = MemberName(member);
                    if (name == null) continue;

                    bool opens = member.DescendantNodes()
                        .OfType<BaseObjectCreationExpressionSyntax>()
                        .Any(o => CreatedTypeName(o).EndsWith(ConnectionType, StringComparison.Ordinal));

                    if (opens)
                        yield return new KeyValuePair<string, string>(relative + "::" + name, relative);
                }
            }
        }

        /// <summary>
        /// Every first-party production C# source: the whole repository minus build output, vendored
        /// code, tooling, and the test projects.
        /// </summary>
        /// <remarks>
        /// <para>⚠️ This was an ALLOW-LIST of six directories (Services, MCPServer, API, Controls,
        /// Docking, Terminal) and three separate pipeline gates independently rejected it. The project is
        /// SDK-style: it compiles every <c>.cs</c> under the repo root by default, so the scan domain has
        /// to match the COMPILE domain, not a hand-picked subset. Under the old list a
        /// <c>new SQLiteConnection(</c> in <c>MainForm.cs</c> — which already constructs a
        /// <see cref="MultiTerminal.Services.TaskDatabase"/> — or in any of the ~16 panel/dialog folders
        /// was invisible, so every roster assertion could pass while a fourth database bypassed the
        /// guard. That is the exact failure this census claims to prevent, one level up.</para>
        ///
        /// <para>⚠️ Exclusions are matched against the path RELATIVE to <paramref name="repoRoot"/>, never
        /// the absolute path — the trap <c>ChannelFlagContractTests.EnumerateFirstPartySources</c>
        /// documents from experience. When the suite runs inside an MT task worktree the repo root is
        /// itself <c>…\.claude\worktrees\&lt;id&gt;</c>, so an absolute-substring test for <c>.claude</c>
        /// excludes every file in the repository and the census silently matches nothing.</para>
        /// </remarks>
        private static IEnumerable<string> ProductionSourceFiles(string repoRoot)
        {
            string[] excludedSegments =
            {
                "bin",
                "obj",
                "node_modules",
                ".claude",
                "MultiTerminal.Tests",
                "AgentProcessTest",
            };

            // IgnoreInaccessible: the SearchOption overload resolves to EnumerationOptions.CompatibleRecursive,
            // which sets IgnoreInaccessible = false for .NET Framework compatibility — so ONE unreadable
            // directory anywhere under the repo root aborts the whole walk with UnauthorizedAccessException
            // and fails four facts. Not reproducible on this machine, which is precisely why it is worth
            // pre-empting: it would appear only on someone else's checkout, as an unrelated-looking failure.
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
            };

            return Directory
                .EnumerateFiles(repoRoot, "*.cs", options)
                .Where(file =>
                {
                    string[] segments = Path.GetRelativePath(repoRoot, file)
                        .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                    return !segments.Any(segment => excludedSegments.Contains(
                        segment, StringComparer.OrdinalIgnoreCase));
                });
        }

        /// <summary>
        /// The created type's written name, for both <c>new SQLiteConnection(cs)</c> and the target-typed
        /// <c>SQLiteConnection c = new(cs);</c>.
        /// </summary>
        /// <remarks>
        /// ⚠️ The census originally matched only <c>ObjectCreationExpressionSyntax</c> and read its
        /// <c>.Type</c>. Since C# 9, <c>SQLiteConnection c = new(cs);</c> parses as
        /// <c>ImplicitObjectCreationExpressionSyntax</c> — a SIBLING node under
        /// <c>BaseObjectCreationExpressionSyntax</c> that carries no <c>.Type</c> at all — so it was
        /// invisible, and a fourth database opened that way would have left every roster fact green.
        /// That was not a theoretical gap: <c>= new()</c> is already the prevailing idiom here (~20 uses,
        /// e.g. TerminalStreamService, WriteContentionDiagnostics, MainForm). One keystroke defeated the
        /// census. For the implicit form the type comes from the declaration or assignment it initialises.
        /// </remarks>
        private static string CreatedTypeName(BaseObjectCreationExpressionSyntax creation)
        {
            if (creation is ObjectCreationExpressionSyntax explicitNew)
                return explicitNew.Type.ToString();

            // Target-typed `new(...)`: the type is on the thing being initialised.
            for (SyntaxNode node = creation.Parent; node != null; node = node.Parent)
            {
                switch (node)
                {
                    case VariableDeclarationSyntax declaration:
                        return declaration.Type.ToString();

                    case AssignmentExpressionSyntax assignment:
                        // `_conn = new(cs);` — Left is an EXPRESSION (`_conn`), not a type. Returning it
                        // verbatim meant this branch never matched anything, so a connection assigned to
                        // a pre-declared field was invisible: the same hole the target-typed fix was
                        // meant to close, one shape over. It survived because the A/B probe that proved
                        // that fix only ever used the DECLARATION shape, so this line was never walked.
                        return ResolveDeclaredTypeOf(assignment.Left, creation);

                    case PropertyDeclarationSyntax property:
                        return property.Type.ToString();

                    case FieldDeclarationSyntax field:
                        return field.Declaration.Type.ToString();

                    case MethodDeclarationSyntax method:
                        // `return new(cs);` in a method whose return type is the connection.
                        return method.ReturnType.ToString();
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// The declared type of an assignment target (<c>_conn</c>, <c>this.Conn</c>, a local), found by
        /// name within the enclosing type. Syntax-only — no semantic model — so it resolves the shapes
        /// that occur here and returns empty rather than guessing on anything else.
        /// </summary>
        private static string ResolveDeclaredTypeOf(ExpressionSyntax target, SyntaxNode from)
        {
            string name = target switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                MemberAccessExpressionSyntax member when member.Expression is ThisExpressionSyntax
                    => member.Name.Identifier.Text,
                _ => null,
            };

            if (name == null) return string.Empty;

            // Locals first — an inner declaration shadows a field of the same name.
            for (SyntaxNode node = from; node != null; node = node.Parent)
            {
                foreach (VariableDeclarationSyntax declaration in node.ChildNodes()
                             .OfType<LocalDeclarationStatementSyntax>()
                             .Select(l => l.Declaration))
                {
                    if (declaration.Variables.Any(v => v.Identifier.Text == name))
                        return declaration.Type.ToString();
                }

                if (node is TypeDeclarationSyntax type)
                {
                    foreach (MemberDeclarationSyntax member in type.Members)
                    {
                        switch (member)
                        {
                            case FieldDeclarationSyntax field
                                when field.Declaration.Variables.Any(v => v.Identifier.Text == name):
                                return field.Declaration.Type.ToString();

                            case PropertyDeclarationSyntax property
                                when property.Identifier.Text == name:
                                return property.Type.ToString();
                        }
                    }
                }
            }

            return string.Empty;
        }

        private static string MemberName(MemberDeclarationSyntax member) => member switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            ConstructorDeclarationSyntax c => c.Identifier.Text,
            PropertyDeclarationSyntax p => p.Identifier.Text,
            FieldDeclarationSyntax f => f.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
            _ => null,
        };

        private static string RepoRoot([CallerFilePath] string thisFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", ".."));
    }
}
