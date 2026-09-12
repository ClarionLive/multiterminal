using System;
using System.IO;
using System.Text;

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

        /// <summary>The launcher PATH picks up under the name <c>gh</c>.</summary>
        internal const string GhLauncherFileName = "gh.cmd";

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
        /// Creates (or refreshes) the <c>gh</c> launcher. Idempotent: it rewrites only when the content
        /// differs, so a terminal launch does not touch the disk in the common case, and a moved
        /// install is corrected automatically rather than silently pointing at a path that is gone.
        /// </summary>
        /// <returns>The shim directory, or null when the launcher could not be written.</returns>
        public static string EnsureGhLauncher(string scriptsDirectory, string shimDirectory, Action<string> log = null)
        {
            if (!ScriptsArePresent(scriptsDirectory)) return null;

            try
            {
                Directory.CreateDirectory(shimDirectory);

                string launcherPath = Path.Combine(shimDirectory, GhLauncherFileName);
                string desired = BuildGhLauncherContents(Path.Combine(scriptsDirectory, GhShimScript));

                // Read-then-compare rather than always writing: an unconditional write on every launch
                // would rewrite a file that other processes may be executing at that moment.
                if (!File.Exists(launcherPath) || !string.Equals(File.ReadAllText(launcherPath), desired, StringComparison.Ordinal))
                {
                    File.WriteAllText(launcherPath, desired);
                    log?.Invoke($"Wrote the gh launcher to {launcherPath}");
                }

                return shimDirectory;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // A terminal that cannot have the bot identity is worth launching anyway: gh keeps
                // working exactly as it does today. Failing the launch over this would be worse than
                // the problem being solved.
                log?.Invoke($"Could not write the gh launcher: {ex.Message}");
                return null;
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
            string helperValue = $"!node \"{helperPath}\"";

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
