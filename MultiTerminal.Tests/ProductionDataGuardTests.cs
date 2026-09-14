using System;
using System.IO;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Covers <see cref="ProductionDataGuard"/> (task 2ddfc32f).
    ///
    /// <para>Every fact here drives the PURE overload, passing <c>underTestHost</c> and
    /// <c>productionRoot</c> in by hand. Nothing in this file reads or writes an environment variable,
    /// and nothing touches <c>%APPDATA%</c>. The decisive reason is that the "natural" falsification —
    /// strip the override from a real test class and see whether it throws — performs the exact
    /// production write the guard exists to prevent, on precisely the run where the guard is broken.
    /// Mutating a process-wide variable for test setup is separately undesirable because the value
    /// outlives the class that set it.</para>
    ///
    /// <para>⚠️ This paragraph previously gave a second reason — that such mutation "races every test
    /// class xUnit runs in parallel". It does not: <c>AssemblyInfo.cs</c> sets
    /// <c>DisableTestParallelization = true</c> for the whole assembly, so classes run serially. The
    /// claim was asserted from memory during this ticket and corrected by the Run-1 debugger gate. Left
    /// recorded rather than silently deleted, because the same wrong premise was also used to justify
    /// dropping a planned module initializer.</para>
    /// </summary>
    public class ProductionDataGuardTests
    {
        private const string Root = @"C:\Users\Someone\AppData\Roaming\multiterminal";
        private const string ProdDb = @"C:\Users\Someone\AppData\Roaming\multiterminal\multiterminal.db";

        /// <summary>
        /// ⚠️ THE LOAD-BEARING FACT. Every other test in this file passes <c>underTestHost: true</c> by
        /// hand, so all of them would stay green if the real detector were broken — the guard would ship
        /// looking tested while protecting nothing in the one situation it exists for.
        ///
        /// <para>This codebase keeps finding that shape: task 77d1182f's inbox defect survived 1001 green
        /// tests because a stub returned <c>Success=true</c> unconditionally, so the assertions never
        /// touched the real path. This asserts the detector against reality — the assembly list of the
        /// process actually running this line, which by definition contains a test host.</para>
        /// </summary>
        [Fact]
        public void Detects_the_test_host_it_is_currently_running_under()
        {
            Assert.True(
                ProductionDataGuard.IsUnderTestHost(),
                "IsUnderTestHost() returned false while running inside xUnit. Every negative fixture in "
                + "this file would now pass vacuously and the guard would protect nothing. Fix the "
                + "detector — do not weaken this assertion.");
        }

        [Fact]
        public void Refuses_a_production_path_under_a_test_host()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                ProductionDataGuard.Guard(
                    ProdDb, "MULTITERMINAL_TEST_DB", "TaskDatabase.GetDatabasePath",
                    underTestHost: true, productionRoot: Root));

            // The message has to diagnose itself: someone hitting this is usually not the person who
            // wrote the resolver, and the fix is always "set this one variable".
            Assert.Contains("MULTITERMINAL_TEST_DB", ex.Message, StringComparison.Ordinal);
            Assert.Contains("TaskDatabase.GetDatabasePath", ex.Message, StringComparison.Ordinal);
            Assert.Contains(ProdDb, ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Allows_a_temp_path_under_a_test_host()
        {
            string temp = Path.Combine(Path.GetTempPath(), "mt-test-" + Guid.NewGuid().ToString("N") + ".db");

            Assert.Equal(
                temp,
                ProductionDataGuard.Guard(
                    temp, "MULTITERMINAL_TEST_DB", "TaskDatabase.GetDatabasePath",
                    underTestHost: true, productionRoot: Root));
        }

        /// <summary>
        /// The shipped app must be completely unaffected. If this ever fails, the guard has become a
        /// production behaviour change rather than a test-time safety net.
        /// </summary>
        [Fact]
        public void Returns_the_production_path_unchanged_when_not_under_a_test_host()
        {
            Assert.Equal(
                ProdDb,
                ProductionDataGuard.Guard(
                    ProdDb, "MULTITERMINAL_TEST_DB", "TaskDatabase.GetDatabasePath",
                    underTestHost: false, productionRoot: Root));
        }

        /// <summary>
        /// A sibling directory whose name merely STARTS WITH the production root is not inside it. The
        /// obvious implementation — <c>fullPath.StartsWith(fullRoot)</c> — gets this wrong and throws on
        /// paths that are not production at all, which would make the guard obstructive and get it
        /// weakened or removed.
        /// </summary>
        [Theory]
        [InlineData(@"C:\Users\Someone\AppData\Roaming\multiterminal-backup\multiterminal.db")]
        [InlineData(@"C:\Users\Someone\AppData\Roaming\multiterminal2\messages.db")]
        public void Does_not_mistake_a_name_prefixed_sibling_for_the_production_root(string path)
        {
            Assert.Equal(
                path,
                ProductionDataGuard.Guard(
                    path, "MULTITERMINAL_TEST_DB", "TaskDatabase.GetDatabasePath",
                    underTestHost: true, productionRoot: Root));
        }

        /// <summary>
        /// Nested production files (gateway.db lives one directory down) must be caught too — the guard
        /// is about the production ROOT, not one known filename.
        /// </summary>
        [Fact]
        public void Refuses_a_nested_production_path()
        {
            Assert.Throws<InvalidOperationException>(() =>
                ProductionDataGuard.Guard(
                    Path.Combine(Root, "gateway", "gateway.db"),
                    "MULTITERMINAL_TEST_GATEWAYDB", "GatewayIntegrationService",
                    underTestHost: true, productionRoot: Root));
        }

        /// <summary>
        /// Path comparison is normalised, so a traversal that lands back inside production is still
        /// refused. A guard fooled by <c>..\</c> would be trivially bypassable by accident.
        /// </summary>
        [Fact]
        public void Refuses_a_denormalised_path_that_resolves_into_production()
        {
            Assert.Throws<InvalidOperationException>(() =>
                ProductionDataGuard.Guard(
                    Path.Combine(Root, "gateway", "..", "multiterminal.db"),
                    "MULTITERMINAL_TEST_DB", "TaskDatabase.GetDatabasePath",
                    underTestHost: true, productionRoot: Root));
        }

        /// <summary>
        /// Windows extended-length and device prefixes name the SAME file, and
        /// <c>Path.GetFullPath</c> returns them VERBATIM — it treats the extended form as already
        /// normalised. So before this fixture, <c>\\?\C:\…\multiterminal.db</c> compared against a plain
        /// <c>C:\…\multiterminal\</c> root did not match, and the guard ALLOWED a real production path.
        /// Found independently by the security and debugger gates in Run 2; both verified it by running
        /// GetFullPath rather than reasoning about it.
        /// </summary>
        [Theory]
        [InlineData(@"\\?\C:\Users\Someone\AppData\Roaming\multiterminal\multiterminal.db")]
        [InlineData(@"\\.\C:\Users\Someone\AppData\Roaming\multiterminal\messages.db")]
        [InlineData(@"\\?\C:\Users\Someone\AppData\Roaming\multiterminal\gateway\gateway.db")]
        public void Refuses_a_production_path_written_in_an_extended_or_device_namespace(string path)
        {
            Assert.Throws<InvalidOperationException>(() =>
                ProductionDataGuard.Guard(
                    path, "MULTITERMINAL_TEST_DB", "TaskDatabase.GetDatabasePath",
                    underTestHost: true, productionRoot: Root));
        }

        /// <summary>
        /// Every separator and casing spelling of the extended namespace names the SAME file, so each
        /// must be refused. Two of these were live bypasses found by the delta review:
        /// <c>\\?\unc\…</c> (the UNC match was case-SENSITIVE, so it fell through to the generic strip,
        /// became a RELATIVE path, and resolved against the working directory) and <c>//?/…</c> (the
        /// strip ran only BEFORE normalisation, and <c>GetFullPath</c> then re-created the prefix).
        /// </summary>
        [Theory]
        [InlineData(@"//?/C:\Users\Someone\AppData\Roaming\multiterminal\multiterminal.db")]
        [InlineData(@"\\?/C:\Users\Someone\AppData\Roaming\multiterminal\multiterminal.db")]
        [InlineData(@"/\?\C:\Users\Someone\AppData\Roaming\multiterminal\multiterminal.db")]
        [InlineData(@"//./C:\Users\Someone\AppData\Roaming\multiterminal\messages.db")]
        public void Refuses_a_production_path_in_any_separator_spelling_of_the_extended_namespace(string path)
        {
            Assert.Throws<InvalidOperationException>(() =>
                ProductionDataGuard.Guard(
                    path, "MULTITERMINAL_TEST_DB", "TaskDatabase.GetDatabasePath",
                    underTestHost: true, productionRoot: Root));
        }

        /// <summary>
        /// A device path that is NOT a drive path must be left exactly as it came in. Turning
        /// <c>\\.\pipe\foo</c> into the relative <c>pipe\foo</c> would have GetFullPath resolve it
        /// against the working directory — the silent reinterpretation that caused the UNC bypass.
        /// </summary>
        [Theory]
        [InlineData(@"\\.\pipe\some-name")]
        [InlineData(@"\\?\")]
        public void Leaves_a_non_drive_device_path_alone(string path)
        {
            Assert.Equal(
                path,
                ProductionDataGuard.Guard(
                    path, "MULTITERMINAL_TEST_DB", "TaskDatabase.GetDatabasePath",
                    underTestHost: true, productionRoot: Root));
        }

        /// <summary>
        /// The prefix strip must not turn a NON-production extended path into a false positive — the
        /// sibling-directory trap, in the extended namespace.
        /// </summary>
        [Fact]
        public void Allows_an_extended_namespace_path_outside_production()
        {
            const string Path_ = @"\\?\C:\Users\Someone\AppData\Roaming\multiterminal-backup\multiterminal.db";

            Assert.Equal(
                Path_,
                ProductionDataGuard.Guard(
                    Path_, "MULTITERMINAL_TEST_DB", "TaskDatabase.GetDatabasePath",
                    underTestHost: true, productionRoot: Root));
        }

        /// <summary>
        /// Windows filesystem comparison is case-insensitive, so a differently-cased production path is
        /// the same file and must be refused.
        /// </summary>
        [Fact]
        public void Refuses_a_production_path_in_a_different_case()
        {
            Assert.Throws<InvalidOperationException>(() =>
                ProductionDataGuard.Guard(
                    ProdDb.ToUpperInvariant(), "MULTITERMINAL_TEST_DB", "TaskDatabase.GetDatabasePath",
                    underTestHost: true, productionRoot: Root));
        }

        /// <summary>
        /// A malformed path cannot be the production database, so the guard has nothing to refuse and
        /// must not throw its own exception over the caller's. Whatever opens it next fails on its own
        /// terms, with a message about the real problem.
        /// </summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void Passes_through_a_path_it_cannot_parse(string path)
        {
            Assert.Equal(
                path,
                ProductionDataGuard.Guard(
                    path, "MULTITERMINAL_TEST_DB", "TaskDatabase.GetDatabasePath",
                    underTestHost: true, productionRoot: Root));
        }

        // ---- Resolver wiring ------------------------------------------------------------------
        //
        // A guarded resolver's guarded branch only runs when its override is ABSENT, and every test in
        // the suite sets the override. So that branch never executes during a normal run, and a typo
        // leaving the guard call unreachable would still show a fully green suite. These facts assert the
        // wiring directly, through each resolver's pure seam — no environment mutation, no %APPDATA%.

        /// <summary>
        /// The resolver behind the original incident must refuse, not return, the live database.
        /// </summary>
        [Fact]
        public void TaskDatabase_resolver_refuses_production_when_the_override_is_missing()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                TaskDatabase.ResolveDatabasePath(
                    testDbOverride: null, underTestHost: true, productionRoot: Root));

            Assert.Contains("MULTITERMINAL_TEST_DB", ex.Message, StringComparison.Ordinal);
            Assert.Contains("TaskDatabase.GetDatabasePath", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// An empty override is "unset", not "use the empty path" — the original resolver treated it that
        /// way and the guard must not change that.
        /// </summary>
        [Fact]
        public void TaskDatabase_resolver_treats_an_empty_override_as_unset()
        {
            Assert.Throws<InvalidOperationException>(() =>
                TaskDatabase.ResolveDatabasePath(
                    testDbOverride: "", underTestHost: true, productionRoot: Root));
        }

        /// <summary>
        /// ⚠️ THE FINDING ALL FOUR PIPELINE GATES RAISED (Run 1). The resolver used to return the
        /// override BEFORE the guard ran, so the only externally supplied path was the only path never
        /// checked: MULTITERMINAL_TEST_DB set to the live database handed a test host exactly what this
        /// ticket exists to prevent, with the guard installed and silent.
        ///
        /// <para>The adversary's account of why the original 22 facts missed it is the lesson: they only
        /// proved that a TEMP override is honoured and a MISSING override is refused. Neither is the
        /// branch an accident takes. Environment variables are process-wide and inheritable, so a stale
        /// shell value, a CI setting, or a developer "reproducing against real data" reaches it.</para>
        /// </summary>
        [Theory]
        [InlineData(ProdDb)]
        [InlineData(@"C:\Users\Someone\AppData\Roaming\multiterminal\nested\other.db")]
        public void TaskDatabase_resolver_refuses_an_override_that_points_at_production(string override_)
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                TaskDatabase.ResolveDatabasePath(override_, underTestHost: true, productionRoot: Root));

            Assert.Contains("MULTITERMINAL_TEST_DB", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>Same hole, same fix, the second database.</summary>
        [Fact]
        public void MessageQueue_resolver_refuses_an_override_that_points_at_production()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                MessageQueueDatabase.ResolveDatabasePath(
                    Path.Combine(Root, "messages.db"), underTestHost: true, productionRoot: Root));

            Assert.Contains("MULTITERMINAL_TEST_MSGDB", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The fix must not make a production-pointing override throw in the SHIPPED app — outside a test
        /// host the guard stays a no-op, whatever the override says.
        /// </summary>
        [Fact]
        public void A_production_override_is_returned_unchanged_outside_a_test_host()
        {
            Assert.Equal(
                ProdDb,
                TaskDatabase.ResolveDatabasePath(ProdDb, underTestHost: false, productionRoot: Root));
        }

        [Fact]
        public void TaskDatabase_resolver_honours_the_override()
        {
            string temp = Path.Combine(Path.GetTempPath(), "mt-" + Guid.NewGuid().ToString("N") + ".db");

            Assert.Equal(
                temp,
                TaskDatabase.ResolveDatabasePath(temp, underTestHost: true, productionRoot: Root));
        }

        /// <summary>
        /// Production must be byte-identical to before this ticket: outside a test host the resolver
        /// returns the same <c>%APPDATA%\multiterminal\multiterminal.db</c> it always did.
        /// </summary>
        [Fact]
        public void TaskDatabase_resolver_returns_the_production_path_outside_a_test_host()
        {
            Assert.Equal(
                Path.Combine(Root, "multiterminal.db"),
                TaskDatabase.ResolveDatabasePath(
                    testDbOverride: null, underTestHost: false, productionRoot: Root));
        }

        /// <summary>
        /// messages.db hides behind a DIFFERENT variable name. That is the whole reason this resolver is
        /// in scope: the incident census and the ticket were both written around MULTITERMINAL_TEST_DB,
        /// so a guard scoped to that name would have reported the class closed while leaving this
        /// production database wide open.
        /// </summary>
        [Fact]
        public void MessageQueue_resolver_refuses_production_when_its_own_override_is_missing()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                MessageQueueDatabase.ResolveDatabasePath(
                    testDbOverride: null, underTestHost: true, productionRoot: Root));

            Assert.Contains("MULTITERMINAL_TEST_MSGDB", ex.Message, StringComparison.Ordinal);
            Assert.Contains("messages.db", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void MessageQueue_resolver_honours_its_own_override()
        {
            string temp = Path.Combine(Path.GetTempPath(), "mt-msg-" + Guid.NewGuid().ToString("N") + ".db");

            Assert.Equal(
                temp,
                MessageQueueDatabase.ResolveDatabasePath(temp, underTestHost: true, productionRoot: Root));
        }

        [Fact]
        public void MessageQueue_resolver_returns_the_production_path_outside_a_test_host()
        {
            Assert.Equal(
                Path.Combine(Root, "messages.db"),
                MessageQueueDatabase.ResolveDatabasePath(
                    testDbOverride: null, underTestHost: false, productionRoot: Root));
        }

        /// <summary>
        /// gateway.db has NO override, so the message must not tell the reader to set one. A remedy that
        /// cannot be followed is worse than no remedy: it sends someone hunting for a fault in their own
        /// setup instead of telling them the seam does not exist yet.
        /// </summary>
        [Fact]
        public void A_resolver_with_no_override_says_so_instead_of_naming_a_variable()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                ProductionDataGuard.Guard(
                    Path.Combine(Root, "gateway", "gateway.db"),
                    envVarName: null,
                    caller: "GatewayIntegrationService",
                    underTestHost: true,
                    productionRoot: Root));

            Assert.Contains("NO test override", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("to a temp file before constructing", ex.Message, StringComparison.Ordinal);
            Assert.Contains("GatewayIntegrationService", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The third resolver, and the only one with no pure seam to aim at: gateway.db is resolved
        /// inside <see cref="GatewayIntegrationService"/>'s constructor. So the wiring is asserted the
        /// only way it can be — by constructing the type and requiring a refusal.
        ///
        /// <para>Every OTHER fact in this file drives a pure function with <c>underTestHost: true</c>
        /// passed by hand, which means none of them would notice if a resolver stopped calling the guard.
        /// This one runs the real constructor through the real detector, so it is the only end-to-end
        /// proof in the file. It is also safe to run: the guard throws before the constructor touches the
        /// filesystem, so nothing opens the live gateway.db even when the assertion fails.</para>
        ///
        /// <para>It doubles as the executable statement that this type is currently UNTESTABLE by design
        /// — it has no override (task 2ddfc32f, Owner declined adding one). If someone later adds a seam,
        /// this fact is what they must deliberately change, rather than discovering the constraint by
        /// watching a test write to production.</para>
        /// </summary>
        [Fact]
        public void Constructing_the_gateway_service_under_a_test_host_is_refused()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => new GatewayIntegrationService());

            Assert.Contains("gateway.db", ex.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(GatewayIntegrationService), ex.Message, StringComparison.Ordinal);
            Assert.Contains("NO test override", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The production root resolves under the current user's AppData. Cheap, but it is the only
        /// assertion that the convenience overload points at the right place at all.
        /// </summary>
        [Fact]
        public void Production_root_is_the_appdata_multiterminal_folder()
        {
            string expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "multiterminal");

            Assert.Equal(expected, ProductionDataGuard.ProductionRoot);
        }
    }
}
