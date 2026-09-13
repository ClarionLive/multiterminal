using System;
using System.IO;
using System.Threading.Tasks;
using MultiTerminal.Services;
using MultiTerminal.Terminal;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Facts for <see cref="AgentGitIdentityWiring"/> (task b42b1883, item 6) — the wiring that points
    /// an MT-launched terminal's git and gh at the bot identity.
    ///
    /// <para>The load-bearing one is <see cref="Adds_nothing_that_needs_redacting"/>. The item's own
    /// wording is "ASSERT THE NEGATIVE": no GitHub token in any environment variable, and nothing new
    /// needing redaction in the launch command line. That claim is only worth anything as a test,
    /// because the launch line is written to a log agents can read.</para>
    /// </summary>
    public sealed class AgentGitIdentityWiringTests : IDisposable
    {
        private readonly string _scriptsDir;
        private readonly string _shimDir;

        public AgentGitIdentityWiringTests()
        {
            string root = Path.Combine(Path.GetTempPath(), "mt-wiring-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            _scriptsDir = Path.Combine(root, "scripts");
            _shimDir = Path.Combine(root, "shims");
            Directory.CreateDirectory(Path.Combine(_scriptsDir, "lib"));

            // Stand-ins: the wiring only cares that the files exist.
            File.WriteAllText(Path.Combine(_scriptsDir, "git-credential-multiterminal.mjs"), "// helper");
            File.WriteAllText(Path.Combine(_scriptsDir, "gh-multiterminal.mjs"), "// shim");
        }

        public void Dispose()
        {
            try { Directory.Delete(Path.GetDirectoryName(_scriptsDir), recursive: true); } catch { /* temp */ }
            GC.SuppressFinalize(this);
        }

        [Fact]
        public void Adds_nothing_that_needs_redacting()
        {
            string setup = AgentGitIdentityWiring.BuildEnvironmentSetup(_scriptsDir, _shimDir);

            // The whole point of the credential-helper design: a token is fetched per operation and
            // never lives in an environment variable, so the launch line cannot leak one.
            Assert.DoesNotContain("GH_TOKEN", setup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("GITHUB_TOKEN", setup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ghs_", setup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ghp_", setup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PRIVATE KEY", setup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NONCE", setup, StringComparison.OrdinalIgnoreCase);

            // And the existing redactor is a no-op on it: nothing here matches a secret assignment,
            // which is the mechanical form of "no new redaction rule is needed".
            Assert.Equal(setup, ConPtyTerminal.RedactLaunchNonceForLog(setup));
        }

        [Fact]
        public void Claims_github_only_and_never_the_global_helper_key()
        {
            string setup = AgentGitIdentityWiring.BuildEnvironmentSetup(_scriptsDir, _shimDir);

            Assert.Contains("credential.https://github.com.helper", setup);

            // A bare credential.helper would claim EVERY host inside MT terminals, breaking stored
            // credentials for Bitbucket and anything else the Owner uses.
            Assert.DoesNotContain("'credential.helper'", setup);
        }

        [Fact]
        public void Clears_the_inherited_helper_list_before_installing_ours()
        {
            // Git asks helpers in order and takes the first answer, and Windows Credential Manager
            // already holds a GitHub credential for the Owner. Without the empty value FIRST, agents
            // keep pushing as the Owner while everything appears to work.
            string setup = AgentGitIdentityWiring.BuildEnvironmentSetup(_scriptsDir, _shimDir);

            int clearIndex = setup.IndexOf("$env:GIT_CONFIG_VALUE_0 = ''", StringComparison.Ordinal);
            int installIndex = setup.IndexOf("$env:GIT_CONFIG_VALUE_1 = '!node", StringComparison.Ordinal);

            Assert.True(clearIndex >= 0, "the helper list must be cleared for this URL");
            Assert.True(installIndex >= 0, "our helper must be installed for this URL");
            Assert.True(clearIndex < installIndex, "the clear must come BEFORE the install, or it erases our own helper");

            // Both pairs must name the same key, or the clear applies somewhere else entirely.
            Assert.Contains("$env:GIT_CONFIG_KEY_0 = 'credential.https://github.com.helper'", setup);
            Assert.Contains("$env:GIT_CONFIG_KEY_1 = 'credential.https://github.com.helper'", setup);
            Assert.Contains("$env:GIT_CONFIG_COUNT = '2'", setup);
        }

        [Fact]
        public void Helper_path_uses_forward_slashes_because_git_reads_it_through_a_shell()
        {
            string setup = AgentGitIdentityWiring.BuildEnvironmentSetup(_scriptsDir, _shimDir);

            int start = setup.IndexOf("$env:GIT_CONFIG_VALUE_1 = '", StringComparison.Ordinal);
            int end = setup.IndexOf("';", start, StringComparison.Ordinal);
            string value = setup.Substring(start, end - start);

            Assert.Contains("git-credential-multiterminal.mjs", value);
            Assert.DoesNotContain("\\", value);
        }

        /// <summary>
        /// Two terminals launching at once must both end up with a usable shim directory.
        /// <para>Before the fix this used <c>File.WriteAllText</c>, which opens with
        /// <c>FileShare.Read</c>: a second concurrent writer throws <c>IOException</c>, which
        /// <c>EnsureGhLauncher</c>'s catch converts into "no shim directory" — so PATH is never
        /// prepended and that terminal silently falls back to the real <c>gh</c>, publishing under the
        /// Owner's account. The Run-1 defect returning as a race.</para>
        /// <para>⚠️ A race test cannot prove absence, and this one does not claim to: it drives enough
        /// concurrent callers on genuinely differing content that the unfixed code fails most runs,
        /// which makes it a regression tripwire rather than a proof. Keeping it is worth more than the
        /// flakiness it risks, and if it ever goes red intermittently the answer is to look at the
        /// write path, not to loosen the assertion.</para>
        /// </summary>
        [Fact]
        public void Concurrent_callers_all_get_a_shim_directory_rather_than_one_losing_the_race()
        {
            // Force the content to differ from whatever is on disk, so every caller takes the write
            // path rather than the read-then-compare early return.
            string freshScripts = Path.Combine(Path.GetTempPath(), "mt-race-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(freshScripts, "lib"));
            try
            {
                foreach (string f in Directory.GetFiles(_scriptsDir))
                    File.Copy(f, Path.Combine(freshScripts, Path.GetFileName(f)));
                foreach (string f in Directory.GetFiles(Path.Combine(_scriptsDir, "lib")))
                    File.Copy(f, Path.Combine(freshScripts, "lib", Path.GetFileName(f)));

                var results = new string[16];
                Parallel.For(0, results.Length, i =>
                {
                    results[i] = AgentGitIdentityWiring.EnsureGhLauncher(freshScripts, _shimDir);
                });

                Assert.All(results, r => Assert.False(string.IsNullOrEmpty(r),
                    "every concurrent caller must get a shim directory; a null means one lost the race "
                    + "and that terminal would silently use the real gh"));

                // Both launchers must exist and be complete — an atomic move never leaves a half file.
                Assert.True(File.Exists(Path.Combine(_shimDir, "gh.cmd")));
                string posix = File.ReadAllText(Path.Combine(_shimDir, "gh"));
                Assert.StartsWith("#!/bin/sh\n", posix, StringComparison.Ordinal);
                Assert.Contains("gh-multiterminal.mjs", posix, StringComparison.Ordinal);

                // No temp files survive a successful run.
                Assert.Empty(Directory.GetFiles(_shimDir, "*.tmp"));
            }
            finally
            {
                try { Directory.Delete(freshScripts, recursive: true); } catch { }
            }
        }

        /// <summary>
        /// The fragment must contain NO double quote, because every caller embeds it inside a
        /// PowerShell <c>-Command "…"</c> argument and <c>CommandLineToArgvW</c> eats inner double
        /// quotes on the way to the child.
        /// <para>MEASURED in a running MT terminal before the fix: the source built
        /// <c>!node "H:/…/git-credential-multiterminal.mjs"</c> and
        /// <c>$env:GIT_CONFIG_VALUE_1</c> arrived with the quotes GONE. git runs a '!' helper through
        /// sh, so on any path containing a space sh word-splits it, the helper never runs, and git
        /// falls back to the OS credential manager — the Owner's identity, silently. The single
        /// outcome this whole feature exists to prevent.</para>
        /// <para>⚠️ This asserts the ABSENCE of a character rather than the presence of an escape, and
        /// that is deliberate: escaping at each of the three call sites would be a convention a fourth
        /// site could forget. A fragment that cannot contain a double quote cannot be broken by a new
        /// caller. If this test starts failing because someone reintroduced <c>"</c>, the fix is to
        /// remove it here, not to escape it there.</para>
        /// </summary>
        [Fact]
        public void Helper_value_carries_no_double_quote_because_the_launch_command_would_eat_it()
        {
            string setup = AgentGitIdentityWiring.BuildEnvironmentSetup(_scriptsDir, _shimDir);

            Assert.DoesNotContain("\"", setup);
        }

        /// <summary>
        /// A scripts directory containing a space must still arrive as ONE argument to sh.
        /// <para>This is the regression that mattered: the defect was invisible on a machine whose
        /// path had no space, which is exactly why item 12's live pass did not catch it. %APPDATA% on
        /// this machine is <c>C:\Users\John Hickey\…</c>, so the failing case is one relocation away.</para>
        /// </summary>
        [Fact]
        public void A_scripts_directory_containing_a_space_survives_as_one_shell_argument()
        {
            string spaced = Path.Combine(Path.GetTempPath(), "mt wiring " + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(Path.Combine(spaced, "lib"));
            try
            {
                foreach (string f in Directory.GetFiles(_scriptsDir))
                    File.Copy(f, Path.Combine(spaced, Path.GetFileName(f)));
                foreach (string f in Directory.GetFiles(Path.Combine(_scriptsDir, "lib")))
                    File.Copy(f, Path.Combine(spaced, "lib", Path.GetFileName(f)));

                string setup = AgentGitIdentityWiring.BuildEnvironmentSetup(spaced, _shimDir);
                Assert.NotEqual(string.Empty, setup);

                int start = setup.IndexOf("$env:GIT_CONFIG_VALUE_1 = '", StringComparison.Ordinal);
                Assert.True(start >= 0, "the helper pair must be present for a spaced scripts directory");
                int end = setup.IndexOf("';", start, StringComparison.Ordinal);
                string assignment = setup.Substring(start, end - start);

                // The PowerShell literal doubles the quotes; what git receives is the single-quoted form.
                Assert.Contains("''", assignment);

                // And what sh ultimately sees groups the whole path, spaces included.
                string value = assignment.Substring("$env:GIT_CONFIG_VALUE_1 = '".Length).Replace("''", "'");
                Assert.StartsWith("!node '", value, StringComparison.Ordinal);
                Assert.EndsWith("git-credential-multiterminal.mjs'", value, StringComparison.Ordinal);
                Assert.Contains(" ", value.Substring("!node '".Length));   // the space really is inside
            }
            finally
            {
                try { Directory.Delete(spaced, recursive: true); } catch { }
            }
        }

        [Fact]
        public void Prepends_the_shim_directory_without_discarding_PATH()
        {
            string setup = AgentGitIdentityWiring.BuildEnvironmentSetup(_scriptsDir, _shimDir);

            Assert.Contains("$env:PATH = '", setup);
            Assert.Contains("' + $env:PATH", setup);
            Assert.Contains(_shimDir, setup);

            // The shim must be told which directory to skip, or it finds the launcher named `gh` that
            // we just put first on PATH and recurses into itself.
            Assert.Contains("$env:MULTITERMINAL_GH_SHIM_DIR = '", setup);
        }

        [Fact]
        public void Without_a_shim_directory_it_still_wires_git_and_clears_the_stale_variable()
        {
            string setup = AgentGitIdentityWiring.BuildEnvironmentSetup(_scriptsDir, shimDirectory: null);

            Assert.Contains("GIT_CONFIG_VALUE_1", setup);
            Assert.Contains("$env:MULTITERMINAL_GH_SHIM_DIR = $null", setup);
            Assert.DoesNotContain("$env:PATH", setup);
        }

        [Fact]
        public void Missing_scripts_produce_no_wiring_at_all()
        {
            string empty = Path.Combine(Path.GetTempPath(), "mt-wiring-absent-" + Guid.NewGuid().ToString("N").Substring(0, 8));

            Assert.False(AgentGitIdentityWiring.ScriptsArePresent(empty));
            Assert.Equal(string.Empty, AgentGitIdentityWiring.BuildEnvironmentSetup(empty, _shimDir));
            Assert.Null(AgentGitIdentityWiring.EnsureGhLauncher(empty, _shimDir));
        }

        [Fact]
        public void Clear_form_removes_every_variable_the_setup_sets()
        {
            string clear = AgentGitIdentityWiring.BuildEnvironmentClear();

            // Anti-inheritance: an un-wired terminal must not keep MT's own values. Every variable the
            // setup can introduce has to appear here, or one survives into a child that cannot use it.
            foreach (string name in new[]
            {
                "GIT_CONFIG_COUNT", "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0",
                "GIT_CONFIG_KEY_1", "GIT_CONFIG_VALUE_1", "MULTITERMINAL_GH_SHIM_DIR",
            })
            {
                Assert.Contains($"$env:{name} = $null", clear);
            }
        }

        [Fact]
        public void Escapes_single_quotes_in_paths()
        {
            // A directory containing a quote would otherwise close the PowerShell string early and the
            // rest of the launch line would be parsed as code.
            string quoted = Path.Combine(Path.GetTempPath(), "mt-wiring-o'brien-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(quoted, "lib"));
            File.WriteAllText(Path.Combine(quoted, "git-credential-multiterminal.mjs"), "// helper");
            File.WriteAllText(Path.Combine(quoted, "gh-multiterminal.mjs"), "// shim");

            try
            {
                string setup = AgentGitIdentityWiring.BuildEnvironmentSetup(quoted, quoted);

                Assert.Contains("o''brien", setup);
                Assert.DoesNotContain("o'brien", setup.Replace("o''brien", string.Empty));
            }
            finally
            {
                try { Directory.Delete(quoted, recursive: true); } catch { /* temp */ }
            }
        }

        [Fact]
        public void Gh_launcher_is_written_once_and_refreshed_only_when_it_changes()
        {
            string result = AgentGitIdentityWiring.EnsureGhLauncher(_scriptsDir, _shimDir);
            string launcher = Path.Combine(_shimDir, "gh.cmd");

            Assert.Equal(_shimDir, result);
            Assert.True(File.Exists(launcher));

            string contents = File.ReadAllText(launcher);
            Assert.Contains("gh-multiterminal.mjs", contents);
            Assert.Contains("%*", contents);                 // every argument is forwarded
            Assert.Contains("exit /b %ERRORLEVEL%", contents); // and the exit code survives

            // Idempotent: an unchanged launcher is not rewritten, so a launch does not touch a file
            // another process may be executing at that moment.
            DateTime firstWrite = File.GetLastWriteTimeUtc(launcher);
            AgentGitIdentityWiring.EnsureGhLauncher(_scriptsDir, _shimDir);
            Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(launcher));

            // A stale launcher pointing at a path that no longer exists IS corrected.
            File.WriteAllText(launcher, "@echo off\r\nnode \"C:\\gone\\gh-multiterminal.mjs\" %*\r\n");
            AgentGitIdentityWiring.EnsureGhLauncher(_scriptsDir, _shimDir);
            Assert.Contains(_scriptsDir.Replace("\\", "\\"), File.ReadAllText(launcher));
        }

        /// <summary>
        /// ⚠️ THE REGRESSION THIS FILE EXISTS TO PREVENT FROM RECURRING (pipeline Run 1, task
        /// b42b1883). Only <c>gh.cmd</c> was ever written, and PATHEXT resolution of an
        /// extension-less name is a cmd.exe / PowerShell behaviour — MSYS <c>execvp</c> appends
        /// <c>.exe</c> and never <c>.cmd</c>. So in Git Bash, which is what the agent Bash tool runs,
        /// the shims directory sat first on PATH and a bare <c>gh</c> still resolved to the real
        /// GitHub CLI. Comments published under the Owner's account with no token, no signature and
        /// no warning, because the warn-and-continue notice lives inside a shim that never executed.
        /// <para>Nothing failed and no test went red, which is exactly why this one is worth having.</para>
        /// </summary>
        [Fact]
        public void Both_gh_launchers_are_written_because_one_shell_family_cannot_see_the_other()
        {
            AgentGitIdentityWiring.EnsureGhLauncher(_scriptsDir, _shimDir);

            string cmdLauncher = Path.Combine(_shimDir, "gh.cmd");
            string posixLauncher = Path.Combine(_shimDir, "gh");

            Assert.True(File.Exists(cmdLauncher), "gh.cmd is what cmd.exe and PowerShell resolve via PATHEXT.");
            Assert.True(File.Exists(posixLauncher), "the extension-less gh is the ONLY one MSYS/Git Bash can find.");

            // Read as bytes: the two properties that break the shebang are invisible in a string.
            byte[] raw = File.ReadAllBytes(posixLauncher);

            // No UTF-8 BOM. A BOM before #! makes the kernel refuse the interpreter.
            Assert.False(raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF,
                "a BOM ahead of the shebang breaks exec in the same way a CRLF does.");

            string posix = System.Text.Encoding.UTF8.GetString(raw);

            Assert.StartsWith("#!/bin/sh\n", posix, StringComparison.Ordinal);

            // LF ONLY. A CRLF after the shebang makes the interpreter "/bin/sh\r", and the resulting
            // error names a path that looks correct — it reads as a broken install, not a line-ending
            // bug, which is what makes it expensive to diagnose.
            Assert.DoesNotContain("\r", posix, StringComparison.Ordinal);

            Assert.Contains("gh-multiterminal.mjs", posix, StringComparison.Ordinal);

            // exec: the child's exit code becomes the script's, with no wrapper left to swallow it.
            // "$@" QUOTED: the unquoted form re-splits a comment body on whitespace, which for this
            // tool is the normal case rather than an edge case.
            Assert.Contains("exec node ", posix, StringComparison.Ordinal);
            Assert.Contains("\"$@\"", posix, StringComparison.Ordinal);

            // Forward slashes: the path sits inside a double-quoted shell word, where a Windows
            // backslash is an escape character.
            Assert.DoesNotContain("\\", posix, StringComparison.Ordinal);

            // Idempotent, like its cmd sibling: a launch must not rewrite a file another process may
            // be executing at that moment.
            DateTime firstWrite = File.GetLastWriteTimeUtc(posixLauncher);
            AgentGitIdentityWiring.EnsureGhLauncher(_scriptsDir, _shimDir);
            Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(posixLauncher));

            // And a stale one is corrected rather than left pointing at a path that is gone.
            File.WriteAllText(posixLauncher, "#!/bin/sh\nexec node \"C:/gone/gh-multiterminal.mjs\" \"$@\"\n");
            AgentGitIdentityWiring.EnsureGhLauncher(_scriptsDir, _shimDir);
            Assert.Contains(_scriptsDir.Replace('\\', '/'), File.ReadAllText(posixLauncher), StringComparison.Ordinal);
        }
    }
}
