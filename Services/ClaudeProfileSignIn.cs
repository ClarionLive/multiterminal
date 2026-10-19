using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Ticket c8ee0eec (Owner decision 2026-10-16, "easy is better"): the Settings dialog's Sign in
    /// button. Opens a plain console window running <c>claude</c> with <c>CLAUDE_CONFIG_DIR</c> pointed at
    /// a profile's folder, so the user signs in and answers Claude Code's first-run questions there.
    /// <para>Deliberately NOT an MT pane: a pane would load the MT plugin and MCP server and register a
    /// terminal, none of which a sign-in needs, and the Settings dialog is modal, so a pane could not be
    /// used until the dialog closed. A separate window works while Settings stays open, and the dialog
    /// notices the sign-in on its own (<see cref="ClaudeProfileResolver.IsSignedIn(string)"/>).</para>
    /// <para>The window starts in a neutral folder MT owns (<see cref="NeutralFolder"/>), never a project
    /// folder, so Claude's "do you trust this folder" question is about a folder that holds nothing.</para>
    /// </summary>
    internal static class ClaudeProfileSignIn
    {
        /// <summary>Where the sign-in window starts: %LOCALAPPDATA%\MultiTerminal\profile-sign-in.</summary>
        public static string NeutralFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiTerminal", "profile-sign-in");

        /// <summary>
        /// Opens the sign-in window for the profile <paramref name="profileName"/> whose folder is
        /// <paramref name="configDir"/> (absolute; created if missing). Throws on failure; the caller
        /// shows the message.
        /// </summary>
        public static void Start(string profileName, string configDir)
        {
            string dir = ClaudeProfileResolver.Canonicalize(configDir)
                ?? throw new ArgumentException($"The folder '{configDir}' is not an absolute path.", nameof(configDir));

            Directory.CreateDirectory(dir);
            string workDir = NeutralFolder;
            Directory.CreateDirectory(workDir);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = workDir,
            };
            foreach (string arg in BuildArguments(profileName))
                psi.ArgumentList.Add(arg);

            ApplyEnvironment(psi.Environment, dir);

            // MT is a GUI process with no console, so the console child gets a window of its own.
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Windows did not start PowerShell.");
        }

        /// <summary>
        /// The PowerShell arguments: a short banner naming the profile, then <c>claude</c>, then a line
        /// saying the window can be closed. The window stays open (-NoExit) so an error stays readable.
        /// </summary>
        internal static IReadOnlyList<string> BuildArguments(string profileName)
        {
            string name = ClaudeProfileResolver.EscapeForSingleQuotedPowerShell(
                string.IsNullOrWhiteSpace(profileName) ? "new profile" : profileName.Trim());

            string script =
                $"$Host.UI.RawUI.WindowTitle = 'Sign in: Claude profile {name}'; " +
                $"Write-Host 'Signing in to the Claude profile ''{name}''.' -ForegroundColor Cyan; " +
                "Write-Host 'Sign in and answer the first-run questions. MultiTerminal notices when you are done.'; " +
                "Write-Host ''; " +
                "claude; " +
                "Write-Host ''; " +
                "Write-Host 'You can close this window.' -ForegroundColor Cyan";

            return new[] { "-NoLogo", "-NoProfile", "-NoExit", "-Command", script };
        }

        /// <summary>
        /// Points the child at <paramref name="configDir"/> and strips MT's per-terminal identity
        /// variables (MULTITERMINAL_*), so nothing in the sign-in session can pass for an MT terminal.
        /// </summary>
        internal static void ApplyEnvironment(IDictionary<string, string> environment, string configDir)
        {
            ArgumentNullException.ThrowIfNull(environment);

            var identity = new List<string>();
            foreach (string key in environment.Keys)
            {
                if (key.StartsWith("MULTITERMINAL_", StringComparison.OrdinalIgnoreCase))
                    identity.Add(key);
            }

            foreach (string key in identity)
                environment.Remove(key);

            environment[ClaudeProfileResolver.ConfigDirVariable] = configDir;
        }
    }
}
