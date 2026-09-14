using System;
using System.IO;
using System.Linq;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Refuses to hand a test host a path to one of MultiTerminal's PRODUCTION databases (task 2ddfc32f).
    ///
    /// <para><b>The incident this exists for.</b> During task 77d1182f's pipeline Run 4, a `dotnet test`
    /// run in a worktree migrated the LIVE <c>%APPDATA%\multiterminal\multiterminal.db</c>: it wrote a
    /// <c>MigrateUserInboxTaskIdNullable</c> row and REBUILT the <c>user_inbox</c> table (3,819 rows)
    /// while the deployed binary was <c>main @ a02c2f5</c>, which contains no such migration. The route
    /// was mundane — <see cref="MCPServer.Services.MessageBroker"/> lazily constructs its own
    /// <see cref="TaskDatabase"/>, whose path resolver falls back to <c>%APPDATA%</c> whenever
    /// <c>MULTITERMINAL_TEST_DB</c> is unset, and one test file had never set it. That had been true of
    /// every run since the file existed; it went unnoticed only because every earlier migration was
    /// additive and idempotent. The first table REBUILD made it visible.</para>
    ///
    /// <para><b>Why a guard and not just the fix.</b> 77d1182f set the variable in that one file, and a
    /// census over <c>MultiTerminal.Tests</c> is clean today. But "clean today" is a property of a grep,
    /// re-established by hand every time someone adds a test. The guard converts a convention every
    /// future author must remember into a failure they cannot miss.</para>
    ///
    /// <para><b>Deliberately independent of our own wiring.</b> Detection keys on the TEST HOST being in
    /// the process, not on any MultiTerminal opt-in flag. A brand-new test project that knows nothing
    /// about <c>MULTITERMINAL_TEST_DB</c> — and therefore has no module initializer of ours — is still
    /// caught. A guard that required our own setup would be absent from exactly the projects most
    /// likely to get this wrong.</para>
    ///
    /// <para><b>Production is untouched.</b> <see cref="IsUnderTestHost"/> is false in the shipped app,
    /// so <see cref="Guard(string, string, string)"/> returns its input unchanged and costs one cached
    /// boolean read.</para>
    /// </summary>
    internal static class ProductionDataGuard
    {
        /// <summary>
        /// Assembly names that mean "a test host is running in this process". Matched case-insensitively
        /// against a prefix, so <c>xunit.runner.visualstudio</c> and <c>testhost.x86</c> both count.
        /// </summary>
        private static readonly string[] TestHostAssemblyPrefixes =
        {
            "xunit.core",
            "xunit.execution",
            "xunit.runner",

            // xunit v3 renames its assemblies (xunit.v3.core, xunit.v3.runner.*) and, under
            // Microsoft.Testing.Platform, there is no `testhost` process at all — so a v3 project would
            // have matched NONE of the prefixes above. That is the exact "brand-new test project that
            // knows nothing about our wiring" case this list claims to cover, and such a project would
            // not carry Detects_the_test_host_it_is_currently_running_under to go red and say so.
            "xunit.v3",
            "Microsoft.Testing.Platform",

            "testhost",
            "Microsoft.TestPlatform",
            "Microsoft.VisualStudio.TestPlatform",
            "nunit.framework",
            "MSTest",
            "Microsoft.VisualStudio.TestTools.UnitTesting",
        };

        // Detection walks the loaded-assembly list, so it is cached: the answer cannot change in a
        // meaningful way over a process lifetime (a test host does not un-load itself), and the
        // resolvers that call this sit on hot paths like "open a database connection".
        private static readonly Lazy<bool> UnderTestHost = new Lazy<bool>(DetectTestHost);

        /// <summary>
        /// True when a test host is present in this process.
        /// </summary>
        /// <remarks>
        /// ⚠️ <b>This is the load-bearing member.</b> If it ever returned false under a test runner, every
        /// negative fixture in <c>ProductionDataGuardTests</c> would pass VACUOUSLY and the guard would
        /// ship as green dead code that protects nothing. <c>Detects_the_test_host_it_is_currently_running_under</c>
        /// asserts it directly for that reason — it is the one fact the others all rest on.
        /// </remarks>
        internal static bool IsUnderTestHost() => UnderTestHost.Value;

        /// <summary>
        /// The directory holding MultiTerminal's production databases
        /// (<c>multiterminal.db</c>, <c>messages.db</c>, <c>gateway/gateway.db</c>).
        /// </summary>
        internal static string ProductionRoot =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "multiterminal");

        /// <summary>
        /// Returns <paramref name="resolvedPath"/> unchanged, or throws when a test host is about to open
        /// a production database. Call this from every resolver that can return a <c>%APPDATA%</c> path.
        /// </summary>
        /// <param name="resolvedPath">The path the resolver was about to return.</param>
        /// <param name="envVarName">
        /// The override variable the caller SHOULD have set (e.g. <c>MULTITERMINAL_TEST_DB</c>). Named in
        /// the exception so the failure diagnoses itself instead of sending someone into the resolver.
        /// <para>Pass <c>null</c> for a resolver that has NO override yet (today: <c>gateway.db</c>). The
        /// message then says so, instead of instructing the reader to set a variable that does not
        /// exist — a remedy that cannot be followed is worse than no remedy, because it sends someone
        /// looking for a bug in their own setup.</para>
        /// </param>
        /// <param name="caller">Resolver name for the message, e.g. <c>TaskDatabase.GetDatabasePath</c>.</param>
        internal static string Guard(string resolvedPath, string envVarName, string caller) =>
            Guard(resolvedPath, envVarName, caller, IsUnderTestHost(), ProductionRoot);

        /// <summary>
        /// The whole decision, as a pure function — no environment reads, no filesystem, no statics.
        /// </summary>
        /// <remarks>
        /// This overload exists so the guard can be PROVEN without being TRIGGERED. The obvious
        /// falsification — strip the override from a real test class and watch it throw — is unsafe: if
        /// the guard does not fire, the experiment performs the exact production write this class exists
        /// to prevent. Mutating a process-wide environment variable to set up that experiment is also
        /// poor practice regardless of scheduling, since the variable outlives the test that set it.
        /// Passing the two inputs in directly removes both problems, so the tests can assert on a
        /// production-SHAPED path without ever going near <c>%APPDATA%</c>.
        ///
        /// <para>An earlier version of this remark justified the design by saying such mutation "races
        /// every test class xUnit is running in parallel". That is FALSE for this suite:
        /// <c>MultiTerminal.Tests/AssemblyInfo.cs</c> carries
        /// <c>[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]</c>, so classes run
        /// serially. The design is right for the reasons above; the parallelism claim was not one of them
        /// and is recorded here so it is not reintroduced from memory.</para>
        /// </remarks>
        /// <param name="underTestHost">Whether a test host is present. Injected rather than detected.</param>
        /// <param name="productionRoot">The directory to treat as production. Injected rather than read.</param>
        internal static string Guard(
            string resolvedPath,
            string envVarName,
            string caller,
            bool underTestHost,
            string productionRoot)
        {
            if (!underTestHost) return resolvedPath;
            if (!IsUnderRoot(resolvedPath, productionRoot)) return resolvedPath;

            string remedy = string.IsNullOrWhiteSpace(envVarName)
                ? "This resolver has NO test override yet: add one, with a seam to inject it, before "
                  + "writing a test that constructs it."
                : $"Set {envVarName} to a temp file before constructing anything that opens it.";

            throw new InvalidOperationException(
                $"{caller} resolved to the PRODUCTION database '{resolvedPath}' while running under a test "
                + $"host. {remedy} "
                + "This is refused rather than allowed because a test run once migrated the live database "
                + "and rebuilt a table under a binary that knew nothing about the change (task 2ddfc32f).");
        }

        /// <summary>
        /// Whether <paramref name="path"/> sits strictly beneath <paramref name="root"/>, compared on
        /// normalised full paths after stripping Windows extended-length and device prefixes.
        /// </summary>
        /// <remarks>
        /// <para>The trailing-separator step is not cosmetic: a plain <c>StartsWith</c> would treat a
        /// sibling directory named <c>multiterminal-backup</c> as being inside <c>multiterminal</c>, so a
        /// guard written the obvious way would throw on paths that are not production at all. Comparison
        /// is ordinal-case-insensitive to match Windows filesystem semantics. (The root itself never
        /// matches — it is a directory, never a database file.)</para>
        ///
        /// <para>⚠️ <b>WHAT THIS DOES NOT DO — read before trusting it.</b> This is TEXTUAL containment
        /// after normalisation, not filesystem identity. It covers the extended and device namespaces
        /// (<c>\\?\</c>, <c>\\?\UNC\</c>, <c>\\.\</c>) in any separator spelling and any casing, stripped
        /// on both sides of normalisation. It does NOT cover other routes to the same bytes: hard links,
        /// directory junctions and symlinks, 8.3 short names, <c>SUBST</c>'d drives, or a UNC path to an
        /// admin share. Real identity would need handle-based comparison
        /// (<c>GetFinalPathNameByHandle</c>), which means opening a handle to the very file this guard
        /// exists to avoid touching — so it is deliberately not attempted.</para>
        ///
        /// <para><b>The bound, stated exactly.</b> A test host cannot reach production by a path whose
        /// NORMALISED, PREFIX-STRIPPED form lies under the normalised production root. It says nothing
        /// about paths that reach the same file by another name.</para>
        ///
        /// <para>⚠️ This paragraph is on its THIRD wording and the first two were both wrong, which is
        /// worth more than the paragraph itself. The original claimed coverage of <c>\\?\UNC\</c> while
        /// the match was case-SENSITIVE, so <c>\\?\unc\…</c> bypassed it. The second claimed "any path
        /// that TEXTUALLY resolves under the production root", which <c>//?/C:\…</c> falsified — the
        /// strip ran only before normalisation, and <c>GetFullPath</c> re-created the prefix afterwards.
        /// Both were written in a pass whose stated purpose was to stop this file overstating itself.
        /// The lesson is not "be careful": it is that a bound written from intent rather than from a
        /// measurement is just a confident-sounding guess, and reads as MORE authoritative than no bound
        /// at all. Every clause above now corresponds to a fixture in <c>ProductionDataGuardTests</c>.</para>
        /// </remarks>
        private static bool IsUnderRoot(string path, string root)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;

            string fullPath;
            string fullRoot;
            try
            {
                // Strip on BOTH sides of normalisation. GetFullPath can ADD the very prefix the first
                // strip removed: `//?/C:\…` is not matched pre-normalisation by a backslash-only test,
                // and GetFullPath then emits `\\?\C:\…` — so a single leading strip left an extended
                // path to compare against a plain root, and the guard allowed it. Measured on .NET 8,
                // not reasoned about. The second call is cheap and idempotent: GetFullPath output is
                // already absolute, so stripping it again either removes a prefix or returns it whole.
                fullPath = StripExtendedPrefix(Path.GetFullPath(StripExtendedPrefix(path)));
                fullRoot = StripExtendedPrefix(Path.GetFullPath(StripExtendedPrefix(root)));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // An unparseable path cannot be the production database, so there is nothing to refuse.
                // Whatever the caller does next will fail on its own terms, with its own message.
                return false;
            }

            if (!fullRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                fullRoot += Path.DirectorySeparatorChar;

            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Removes a Windows extended-length or device prefix so the path compares in the same namespace
        /// as the production root.
        /// </summary>
        /// <remarks>
        /// Measured on .NET 8 (pipeline Run 2, security + debugger, independently): <c>Path.GetFullPath</c>
        /// returns <c>\\?\C:\…</c> and <c>\\.\C:\…</c> UNCHANGED, because the extended-length form is
        /// defined as already normalised. The guard's prefix comparison against a plain <c>C:\…</c> root
        /// therefore returned false — allow — for a spelling of the LIVE database file. Stripping first
        /// puts both operands in one namespace. <c>\\?\UNC\server\share</c> re-roots to
        /// <c>\\server\share</c>, which is what <c>GetFullPath</c> would have produced anyway.
        /// </remarks>
        private static string StripExtendedPrefix(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length < 4) return path;

            // Win32 treats '/' and '\' interchangeably inside the prefix, so `//?/C:\…` is the SAME
            // extended path as `\\?\C:\…`. Testing the four characters individually covers every mixed
            // spelling; a literal StartsWith(@"\\?\") covers exactly one.
            if (!(IsSeparator(path[0]) && IsSeparator(path[1])
                  && (path[2] == '?' || path[2] == '.')
                  && IsSeparator(path[3])))
            {
                return path;
            }

            string rest = path[4..];

            // UNC form: \\?\UNC\server\share → \\server\share. Matched case-INSENSITIVELY — Windows paths
            // are, and an Ordinal test let `\\?\unc\…` fall through to the generic strip below, where it
            // became the RELATIVE path `unc\server\share` and GetFullPath resolved it against the current
            // directory. That is worse than failing to match: the guard silently reinterpreted one path as
            // a completely different one, and allowed a spelling of the live database. Reachable wherever
            // %APPDATA% is a redirected UNC roaming profile, which is ordinary in managed environments.
            const string Unc = "UNC";
            if (rest.Length > Unc.Length
                && rest.StartsWith(Unc, StringComparison.OrdinalIgnoreCase)
                && IsSeparator(rest[Unc.Length]))
            {
                return @"\\" + rest[(Unc.Length + 1)..];
            }

            // Only strip when what remains is an absolute drive path. Anything else (a device name like
            // `\\.\pipe\foo`, a truncated `\\?\`) is left EXACTLY as it came in, because turning it into a
            // relative path hands GetFullPath something that resolves against the working directory — a
            // silent reinterpretation, which is the failure mode above.
            if (rest.Length >= 2 && char.IsLetter(rest[0]) && rest[1] == ':')
                return rest;

            return path;
        }

        private static bool IsSeparator(char c) =>
            c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;

        /// <summary>
        /// Answers true or false — never throws (pipeline Run 1, debugger).
        /// </summary>
        /// <remarks>
        /// The catch-all is deliberate and the narrow <c>AppDomainUnloadedException</c> it replaced was a
        /// latent outage. <see cref="Lazy{T}"/> defaults to <c>ExecutionAndPublication</c>, which memoizes
        /// a FAULTING factory: one unexpected exception here — <c>GetAssemblies()</c> racing an assembly
        /// load, or <c>GetName()</c> on a dynamic or collectible assembly — would propagate out of
        /// <see cref="IsUnderTestHost"/> and then be rethrown on EVERY subsequent call, so every
        /// <c>GetDatabasePath()</c> in the shipped app would throw for the remainder of the process. A
        /// guard that exists to be a no-op in production must not be able to take production down; the
        /// only safe failure direction here is "not a test host", which restores exactly the pre-guard
        /// behaviour.
        /// </remarks>
        private static bool DetectTestHost()
        {
            try
            {
                return AppDomain.CurrentDomain.GetAssemblies()
                    .Select(SafeAssemblyName)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Any(n => TestHostAssemblyPrefixes.Any(
                        p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// <c>Assembly.GetName()</c> can throw on dynamic or collectible assemblies; one such assembly in
        /// the process must not decide the answer for all of them.
        /// </summary>
        private static string SafeAssemblyName(System.Reflection.Assembly assembly)
        {
            try
            {
                return assembly?.GetName()?.Name;
            }
            catch
            {
                return null;
            }
        }
    }
}
