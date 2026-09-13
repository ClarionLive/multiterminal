using System;
using System.IO;
using System.Text;
using System.Threading;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Points an MT-launched terminal at the bot identity: git's credential helper for
    /// <c>git push</c>, and a <c>gh</c> launcher on PATH for issue comments and pull requests
    /// (task b42b1883, item 6).
    ///
    /// <para><b>Nothing here is a secret, and that is the acceptance criterion.</b> The launch command
    /// line reaches <c>DebugLogService</c>, which agents read through the <c>debug_logs</c> MCP tool —
    /// the launch-nonce leak (task fd3437e6) is the precedent, and <see cref="Terminal.ConPtyTerminal.RedactLaunchNonceForLog"/>
    /// exists because of it. Everything this class contributes is a FILE PATH or a git config KEY, so
    /// nothing new needs redacting. If a future edit makes that untrue, the design has drifted back
    /// toward a credential in the environment and must be re-examined rather than patched with another
    /// redaction rule.</para>
    ///
    /// <para><b>Why git config comes from the environment.</b> <c>GIT_CONFIG_COUNT</c>/<c>_KEY_n</c>/
    /// <c>_VALUE_n</c> apply to every git invocation in this terminal and nowhere else: no repository's
    /// <c>.git/config</c> is touched, the user's global config is untouched, and nothing persists after
    /// the terminal closes. A <c>git config --global</c> write would change the whole machine to get one
    /// terminal right.</para>
    ///
    /// <para><b>⚠️ Why the helper list is CLEARED for github.com first.</b> Git asks credential helpers
    /// in order and takes the first answer. Windows Credential Manager is already configured on this
    /// machine and already holds a GitHub credential for the Owner, so merely APPENDING our helper
    /// leaves agents pushing as the Owner while everything appears to work — the "passes for the wrong
    /// reason" failure this ticket keeps meeting. Setting the key to an empty value first resets the
    /// list; the second value installs ours. Scoped to <c>credential.https://github.com.helper</c> so
    /// every other host keeps its existing credentials: this claims GitHub inside MT terminals, not the
    /// machine.</para>
    ///
    /// <para>Consequence, stated rather than hidden: inside an MT terminal, if MT cannot mint (no App
    /// configured, MT stopped), git PROMPTS for github.com instead of silently falling back to the
    /// Owner's stored credential. That is the intended behaviour — a silent fallback to the Owner's
    /// identity is precisely what this ticket exists to end.</para>
    /// </summary>
    public static class AgentGitIdentityWiring
    {
        /// <summary>Scoped to https + github.com. Never the bare <c>credential.helper</c> key.</summary>
        internal const string CredentialHelperKey = "credential.https://github.com.helper";

        internal const string CredentialHelperScript = "git-credential-multiterminal.mjs";
        internal const string GhShimScript = "gh-multiterminal.mjs";

        /// <summary>The launcher cmd.exe and PowerShell pick up under the name <c>gh</c>, via PATHEXT.</summary>
        internal const string GhLauncherFileName = "gh.cmd";

        /// <summary>
        /// The launcher POSIX shells pick up under the name <c>gh</c>.
        ///
        /// <para><b>⚠️ This file is not a nicety — without it the whole gh half of this ticket is
        /// silently inert in the shell agents actually use</b> (task b42b1883, pipeline Run 1). Resolving
        /// an extension-less name through PATHEXT is a cmd.exe / PowerShell behaviour. MSYS (Git Bash,
        /// which is what the agent Bash tool runs) uses <c>execvp</c>, which appends <c>.exe</c> and never
        /// <c>.cmd</c>, so <see cref="GhLauncherFileName"/> alone is invisible there: the shims directory
        /// sat first on PATH and a bare <c>gh</c> still resolved to the real GitHub CLI. Comments then
        /// published under the OWNER'S account with no token, no signature and — worst of all — no
        /// warning, because the warn-and-continue notice lives inside a shim that never executed. Note
        /// <c>git push</c> was unaffected the whole time, because <c>GIT_CONFIG_*</c> is shell-agnostic;
        /// that asymmetry is exactly what made the failure so quiet.</para>
        ///
        /// <para>The two launchers coexist safely. cmd.exe and PowerShell only execute names carrying a
        /// PATHEXT extension, so they keep choosing <c>gh.cmd</c> and never this file; POSIX shells see
        /// this one. And <c>resolveRealGh</c> in the shim skips the ENTIRE shims directory rather than a
        /// single filename, so adding a second launcher here cannot make the shim find itself.</para>
        /// </summary>
        internal const string GhPosixLauncherFileName = "gh";

        /// <summary>
        /// Where the scripts live at runtime: next to the executable, copied by the csproj (the same
        /// treatment <c>scripts\statusline.js</c> already gets). NOT the repository — a deployed MT has
        /// no working tree.
        /// </summary>
        public static string ResolveScriptsDirectory() =>
            Path.Combine(AppContext.BaseDirectory, "scripts");

        /// <summary>
        /// Where the <c>gh</c> launcher is written. Outside the repository and outside the app folder,
        /// so a redeploy never leaves a stale launcher shadowing the real gh, and a checked-out working
        /// tree never contains a PATH-shadowing executable.
        /// </summary>
        public static string ResolveShimDirectory() =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "multiterminal",
                "shims");

        /// <summary>
        /// Whether both scripts are actually present. Wiring a terminal to a helper that does not exist
        /// would make every git operation invoke a missing file, so the caller skips the wiring entirely
        /// rather than producing a broken terminal.
        /// </summary>
        public static bool ScriptsArePresent(string scriptsDirectory) =>
            !string.IsNullOrEmpty(scriptsDirectory)
            && File.Exists(Path.Combine(scriptsDirectory, CredentialHelperScript))
            && File.Exists(Path.Combine(scriptsDirectory, GhShimScript));

        /// <summary>
        /// Creates (or refreshes) BOTH <c>gh</c> launchers — the cmd one for Windows shells and the
        /// POSIX one for MSYS/Git Bash. Idempotent: each rewrites only when the content differs, so a
        /// terminal launch does not touch the disk in the common case, and a moved install is corrected
        /// automatically rather than silently pointing at a path that is gone.
        ///
        /// <para><b>Both are required, and writing only one is the bug this method was fixed for</b>
        /// (task b42b1883, pipeline Run 1) — see <see cref="GhPosixLauncherFileName"/> for why a
        /// Windows-only launcher leaves agents publishing under the Owner's name with no warning.</para>
        /// </summary>
        /// <returns>The shim directory, or null when the launchers could not be written.</returns>
        public static string EnsureGhLauncher(string scriptsDirectory, string shimDirectory, Action<string> log = null)
        {
            if (!ScriptsArePresent(scriptsDirectory)) return null;

            try
            {
                Directory.CreateDirectory(shimDirectory);

                string ghShimScriptPath = Path.Combine(scriptsDirectory, GhShimScript);

                WriteLauncherIfChanged(
                    Path.Combine(shimDirectory, GhLauncherFileName),
                    BuildGhLauncherContents(ghShimScriptPath),
                    log);

                WriteLauncherIfChanged(
                    Path.Combine(shimDirectory, GhPosixLauncherFileName),
                    BuildGhPosixLauncherContents(ghShimScriptPath),
                    log);

                return shimDirectory;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // A terminal that cannot have the bot identity is worth launching anyway: gh keeps
                // working exactly as it does today. Failing the launch over this would be worse than
                // the problem being solved.
                //
                // PARTIAL WRITES ARE SAFE, AND WORTH STATING because there are two writes behind one
                // all-or-nothing return: if gh.cmd succeeds and the POSIX launcher then fails, we
                // return null, so the caller never prepends the shim directory to PATH. The freshly
                // written gh.cmd is therefore unreachable and inert — the outcome is identical to
                // having written neither, which is why one return value for two writes is honest
                // rather than lossy. Do not "improve" this into a partial success: a shim directory
                // on PATH with only one launcher in it is the Git-Bash-invisible bug (item 14) by
                // another route.
                log?.Invoke($"Could not write the gh launcher: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Read-then-compare rather than always writing: an unconditional write on every launch would
        /// rewrite a file that other processes may be executing at that moment.
        /// <para>⚠️ THE WRITE IS ATOMIC BECAUSE TWO TERMINALS CAN LAUNCH AT ONCE. <c>File.WriteAllText</c>
        /// opens with <c>FileShare.Read</c>, so a second concurrent writer throws <c>IOException</c> —
        /// which <c>EnsureGhLauncher</c>'s catch turns into "no shim directory", so PATH is never
        /// prepended and that terminal silently falls back to the real <c>gh</c> and publishes under the
        /// Owner's account. That is the Run-1 defect reappearing as a race, reachable on the first launch
        /// after a deploy when the content genuinely differs and two spawns collide. Found by pipeline
        /// Run 2's debugger gate (ticket 27002183).</para>
        /// <para>Temp-file-then-move makes the replacement atomic, so concurrent callers converge on the
        /// same bytes instead of one of them losing its shim. The temp file is created in the SAME
        /// directory because <c>File.Move</c> across volumes is a copy, not an atomic rename.</para>
        /// </summary>
        private static void WriteLauncherIfChanged(string launcherPath, string desired, Action<string> log)
        {
            // ⚠️ ATOMIC REPLACEMENT ALONE IS NOT ENOUGH, and a measured test says so: with temp-file +
            // File.Move(overwrite) and nothing else, 2 of 16 concurrent callers still ended up null.
            // Windows lets both File.ReadAllText and File.Move fail while a PEER is mid-replacement, so
            // the loser's exception reached EnsureGhLauncher's catch and became "no shim directory".
            //
            // ⭐ WHAT MAKES RETRYING CORRECT RATHER THAN PAPERING OVER A RACE: every concurrent caller
            // writes BYTE-IDENTICAL content (the desired launcher for the same scripts directory), so a
            // peer finishing first is as good as finishing ourselves. Re-reading the content after a
            // collision therefore ANSWERS the question rather than retrying blindly — which is also why
            // the check sits at the top of the loop instead of only before it.
            for (int attempt = 0; ; attempt++)
            {
                if (ContentAlreadyMatches(launcherPath, desired)) return;

                string directory = Path.GetDirectoryName(launcherPath) ?? ".";
                string temp = Path.Combine(
                    directory,
                    Path.GetFileName(launcherPath) + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");

                try
                {
                    File.WriteAllText(temp, desired);
                    // Move rather than write in place: a reader executing the launcher at this moment
                    // must never observe a half-written file.
                    File.Move(temp, launcherPath, overwrite: true);
                    log?.Invoke($"Wrote the gh launcher to {launcherPath}");
                    return;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 3)
                {
                    // A peer holds the target. Give it a moment, then let the content check above
                    // settle it. After the last attempt the exception propagates to EnsureGhLauncher,
                    // which degrades honestly rather than pretending the shim is installed.
                    Thread.Sleep(15 * (attempt + 1));
                }
                finally
                {
                    // A stray .tmp is inert — nothing resolves it — but it would accumulate on every
                    // collision, and unexplained files in the shim directory make a later reader doubt
                    // the directory's contents.
                    try { if (File.Exists(temp)) File.Delete(temp); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        /// <summary>
        /// Whether the launcher on disk already holds exactly the desired bytes.
        /// <para>A read that throws means a peer is replacing the file right now, which is neither a
        /// match nor a reason to fail: answer "not yet" and let the caller retry, where the peer's
        /// completed write will satisfy the check.</para>
        /// </summary>
        private static bool ContentAlreadyMatches(string launcherPath, string desired)
        {
            try
            {
                return File.Exists(launcherPath)
                    && string.Equals(File.ReadAllText(launcherPath), desired, StringComparison.Ordinal);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// The batch file PATH resolves as <c>gh</c>. <c>%*</c> forwards every argument, and the exit
        /// code is propagated — `gh` is scripted against, so swallowing it would turn a failed comment
        /// into an apparent success.
        /// </summary>
        internal static string BuildGhLauncherContents(string ghShimScriptPath) =>
            "@echo off\r\n"
            + "REM MultiTerminal gh shim (task b42b1883, item 5/6). Mints a short-lived bot token into\r\n"
            + "REM this one gh invocation. Generated file: edits are overwritten on the next launch.\r\n"
            + $"node \"{ghShimScriptPath}\" %*\r\n"
            + "exit /b %ERRORLEVEL%\r\n";

        /// <summary>
        /// The shell script MSYS/Git Bash resolves as <c>gh</c>.
        ///
        /// <para><b>Three details here are load-bearing rather than stylistic.</b></para>
        ///
        /// <para>(1) <b>LF line endings, and the shebang on the very first byte.</b> A CRLF after
        /// <c>#!/bin/sh</c> makes the kernel read the interpreter as <c>/bin/sh\r</c>, which does not
        /// exist; the error ("bad interpreter") names a path that looks correct, so it reads as a broken
        /// install rather than a line-ending bug. A UTF-8 BOM breaks it the same way, which is why this
        /// is written with <see cref="File.WriteAllText(string,string)"/> — .NET's default UTF-8 encoding
        /// emits no BOM, and it performs no newline translation, so the "\n" here survives to disk.</para>
        ///
        /// <para>(2) <b>Forward slashes in the script path.</b> The path is interpolated inside a
        /// double-quoted shell word, where a Windows backslash is an escape character. Node accepts
        /// forward slashes on Windows, so converting sidesteps the question — the same reasoning as the
        /// credential helper value in <see cref="BuildEnvironmentSetup"/>.</para>
        ///
        /// <para>(3) <b><c>exec</c> and <c>"$@"</c>.</b> <c>exec</c> replaces the shell process so the
        /// child's exit code is the script's own, with no wrapper left to swallow a signal. <c>"$@"</c>
        /// (quoted) forwards arguments one-for-one; the unquoted form would re-split a comment body on
        /// whitespace, which is the normal case for this tool rather than an edge case.</para>
        ///
        /// <para>No execute bit is set, and none is needed: MSYS derives executability from the file's
        /// first bytes, and a <c>#!</c> shebang is enough. .NET on Windows could not set a POSIX mode
        /// bit anyway.</para>
        /// </summary>
        internal static string BuildGhPosixLauncherContents(string ghShimScriptPath) =>
            "#!/bin/sh\n"
            + "# MultiTerminal gh shim (task b42b1883, item 5/6; POSIX half added in pipeline Run 1).\n"
            + "# Mints a short-lived bot token into this one gh invocation. Generated file: edits are\n"
            + "# overwritten on the next launch.\n"
            + $"exec node \"{ghShimScriptPath.Replace('\\', '/')}\" \"$@\"\n";

        /// <summary>
        /// The PowerShell fragment appended to a terminal's launch command.
        ///
        /// <para>Pure and internal-free of any secret, so the "nothing new needs redacting" claim is a
        /// unit test rather than a promise.</para>
        /// </summary>
        /// <param name="scriptsDirectory">Directory holding the helper scripts.</param>
        /// <param name="shimDirectory">Directory holding the gh launcher, or null to skip the gh half.</param>
        public static string BuildEnvironmentSetup(string scriptsDirectory, string shimDirectory)
        {
            if (!ScriptsArePresent(scriptsDirectory)) return string.Empty;

            var sb = new StringBuilder();

            // git reads the helper value through its shell, where a Windows backslash path is an
            // escape hazard. Forward slashes are accepted by both node and git on Windows and avoid
            // the question entirely.
            string helperPath = Path.Combine(scriptsDirectory, CredentialHelperScript).Replace('\\', '/');

            // The leading '!' tells git the value is a command rather than a built-in helper name.
            //
            // ⚠️ SINGLE QUOTES, NOT DOUBLE — AND THIS IS A CORRECTNESS FIX, NOT A STYLE CHOICE.
            // This was `!node "{helperPath}"` and the double quotes did not survive the launch. Every
            // caller embeds this fragment inside a PowerShell `-Command "…"` argument
            // (ConPtyTerminal.cs — the live path — plus both TerminalSpawner sites), and
            // CommandLineToArgvW consumes the inner double quotes on the way to the child. MEASURED in
            // a running MT terminal: the source built `!node "H:/…/git-credential-multiterminal.mjs"`
            // and $env:GIT_CONFIG_VALUE_1 arrived as `!node H:/…/git-credential-multiterminal.mjs`,
            // quotes gone. git runs a '!' helper through sh, so on any path containing a space or
            // parentheses — `C:\Program Files\…`, or %APPDATA% on a machine whose user name has a
            // space — sh word-splits it, the helper never runs, and git falls back to the OS
            // credential manager: THE OWNER'S IDENTITY, silently, which is the single outcome this
            // whole feature exists to prevent. Invisible on a machine whose scripts path happens to
            // have no space, which is why item 12's live pass did not catch it. Found by pipeline
            // Run 2's debugger gate (ticket 27002183).
            //
            // sh accepts single quotes for exactly the same grouping job, and Escape() below doubles
            // them for the PowerShell literal, so `'` round-trips to the child intact while `"` cannot.
            // ⭐ WHY HERE AND NOT AT THE THREE CALL SITES: escaping per site is a convention a fourth
            // site can forget — the same shape as the ApplyTheme chain this repo documents as a trap.
            // Producing a fragment that contains NO double quote at all removes the hazard instead of
            // handling it three times, so a new caller cannot reintroduce it.
            // ⚠️ Do NOT "simplify" by dropping the quotes altogether: they are what makes a spaced path
            // work at all. Removing them fixes this machine and guarantees the bug on every other.
            //
            // ⚠️ AND THE PATH IS ESCAPED FOR **SH**, NOT JUST FOR POWERSHELL. There are two shells in this
            // path and they need different escaping: Escape() below doubles `'` for the PowerShell string
            // literal, but git hands the finished helper string to SH, where a bare apostrophe TERMINATES
            // the quote — so `C:\Users\John O'Brien\…` would word-split, the helper would not run, and git
            // would fall back to the OS credential manager, i.e. the Owner's identity, silently. Exactly
            // the failure this whole value exists to prevent, one directory name away.
            // Flagged independently by the security-auditor and code-reviewer gates on pipeline run 1.
            // ⚠️ It had been RECORDED as a known limitation and left unfixed, on the argument that the
            // previous `"`-quoted version had the same exposure. That argument justifies not making it
            // worse; it does not justify shipping a new quoting layer with the same hole. Recording a hole
            // is not closing it.
            // `'\''` is the POSIX idiom: close the quote, emit an escaped literal quote, reopen.
            string shQuotedPath = "'" + helperPath.Replace("'", "'\\''") + "'";
            string helperValue = $"!node {shQuotedPath}";

            // Pair 0 CLEARS the inherited helper list for this URL; pair 1 installs ours. Order is the
            // whole point — see the class remarks.
            sb.Append("$env:GIT_CONFIG_COUNT = '2'; ");
            sb.Append($"$env:GIT_CONFIG_KEY_0 = '{Escape(CredentialHelperKey)}'; ");
            sb.Append("$env:GIT_CONFIG_VALUE_0 = ''; ");
            sb.Append($"$env:GIT_CONFIG_KEY_1 = '{Escape(CredentialHelperKey)}'; ");
            sb.Append($"$env:GIT_CONFIG_VALUE_1 = '{Escape(helperValue)}'; ");

            if (!string.IsNullOrEmpty(shimDirectory))
            {
                // The shim needs to know which directory to skip when it looks for the real gh:
                // PATH's first entry is now a launcher named gh, and finding that again would recurse.
                sb.Append($"$env:MULTITERMINAL_GH_SHIM_DIR = '{Escape(shimDirectory)}'; ");

                // Prepend, never replace: the terminal keeps every tool it had.
                sb.Append($"$env:PATH = '{Escape(shimDirectory)};' + $env:PATH; ");
            }
            else
            {
                // Anti-inheritance, the same rule the MULTITERMINAL_* variables follow: if MT.exe was
                // itself launched from a shell that had these set, an un-wired terminal must not
                // silently inherit a foreign shim directory.
                sb.Append("$env:MULTITERMINAL_GH_SHIM_DIR = $null; ");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Clears everything this class sets. Used when the wiring is unavailable, so a terminal cannot
        /// inherit half a configuration from MT's own environment and end up pointed at a helper script
        /// that is not there.
        /// </summary>
        public static string BuildEnvironmentClear() =>
            "$env:GIT_CONFIG_COUNT = $null; "
            + "$env:GIT_CONFIG_KEY_0 = $null; "
            + "$env:GIT_CONFIG_VALUE_0 = $null; "
            + "$env:GIT_CONFIG_KEY_1 = $null; "
            + "$env:GIT_CONFIG_VALUE_1 = $null; "
            + "$env:MULTITERMINAL_GH_SHIM_DIR = $null; ";

        /// <summary>PowerShell single-quoted strings escape a quote by doubling it.</summary>
        private static string Escape(string value) => value?.Replace("'", "''") ?? string.Empty;
    }
}
