using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Ticket 6c688883: the <c>mtclaude &lt;Name&gt;</c> command. Typed in PowerShell or cmd instead of
    /// <c>claude</c>, it starts an ordinary Claude Code session that messages like a terminal MT launched.
    /// </summary>
    /// <remarks>
    /// <para>What an MT pane gets that a standalone session does not, and how the command supplies it:</para>
    /// <list type="bullet">
    /// <item>The cross-session address: <c>claude -n &lt;Name&gt;</c>, as <c>ConPtyTerminal.ApplySessionName</c> adds.</item>
    /// <item>MT's MCP server: <c>--mcp-config %APPDATA%\multiterminal\.mcp.json</c>, as <c>LaunchCommandBuilder</c> adds.</item>
    /// <item>The MT identity the MCP server and the plugin hooks read: <c>MULTITERMINAL_NAME</c>, plus
    /// <c>MULTITERMINAL_STANDALONE=1</c>, which makes the MCP server register the session by name the
    /// way it registers a ClarionAssistant tab (mcp/index.js <c>selfRegisterTerminal</c>). Every other
    /// inherited <c>MULTITERMINAL_*</c> is cleared: a pane id or launch nonce from the shell it was typed
    /// in is not this session's.</item>
    /// <item>Messages without the approval prompt: <c>--settings</c> with <c>crossSessionInbound: accept</c>,
    /// for this session only. Claude Code otherwise holds a message whose sender runs in another permission
    /// class (MT panes skip permissions; this session keeps the user's prompts).</item>
    /// </list>
    /// <para>MT writes the files at startup into <see cref="ResolveBinDirectory"/> and adds that folder to the
    /// user PATH once (Owner decision, 2026-11-15). It is NOT the shims folder: that one holds the <c>gh</c>
    /// launcher that gives agent panes the bot identity, and on the user's own PATH it would make the
    /// Owner's <c>gh</c> run as the bot.</para>
    /// <para>Only <c>mtclaude.cmd</c> matches the command name. The PowerShell script it runs is called
    /// <c>mtclaude-launch.ps1</c>, so PowerShell never tries to run a <c>.ps1</c> directly, which the default
    /// execution policy can refuse; the <c>.cmd</c> starts it with <c>-ExecutionPolicy Bypass</c>.</para>
    /// </remarks>
    public static class StandaloneLauncher
    {
        internal const string CommandFileName = "mtclaude.cmd";
        internal const string ScriptFileName = "mtclaude-launch.ps1";
        internal const string SettingsFileName = "mtclaude-settings.json";

        /// <summary>The settings passed with <c>--settings</c>: accept cross-session messages without asking.</summary>
        internal const string SettingsContents = "{\n  \"crossSessionInbound\": \"accept\"\n}\n";

        /// <summary>Where the command is written: outside the app and the repository, and not the agents' shims folder.</summary>
        public static string ResolveBinDirectory() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "multiterminal", "bin");

        /// <summary>
        /// Writes the three files into <paramref name="binDirectory"/> (only those that changed, atomically) and,
        /// when <paramref name="addToUserPath"/>, adds the folder to the user PATH if it is not there. Returns
        /// true when the files are in place. Never throws.
        /// </summary>
        public static bool EnsureInstalled(string binDirectory, bool addToUserPath, Action<string> log = null)
        {
            try
            {
                Directory.CreateDirectory(binDirectory);
                AgentGitIdentityWiring.WriteLauncherIfChanged(Path.Combine(binDirectory, CommandFileName), BuildCommandContents(), log);
                AgentGitIdentityWiring.WriteLauncherIfChanged(Path.Combine(binDirectory, ScriptFileName), BuildScriptContents(), log);
                AgentGitIdentityWiring.WriteLauncherIfChanged(Path.Combine(binDirectory, SettingsFileName), SettingsContents, log);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log?.Invoke($"Could not write the mtclaude command: {ex.Message}");
                return false;
            }

            if (addToUserPath)
            {
                try
                {
                    if (EnsureOnUserPath(binDirectory))
                    {
                        log?.Invoke($"Added {binDirectory} to the user PATH (new terminal windows see mtclaude)");
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                {
                    log?.Invoke($"Could not add {binDirectory} to the user PATH: {ex.Message}");
                }
            }

            return true;
        }

        /// <summary>
        /// The user PATH with <paramref name="directory"/> appended, or null when it is already there (compared
        /// ignoring case, surrounding spaces and quotes, and a trailing backslash). Pure.
        /// </summary>
        internal static string PathWithEntry(string currentPath, string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return null;
            }

            string wanted = Normalize(directory);
            var entries = (currentPath ?? string.Empty).Split(';');
            if (entries.Any(e => string.Equals(Normalize(e), wanted, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            string trimmed = (currentPath ?? string.Empty).TrimEnd(';');
            return trimmed.Length == 0 ? directory : trimmed + ";" + directory;
        }

        private static string Normalize(string entry) => entry.Trim().Trim('"').TrimEnd('\\');

        // HKCU\Environment\Path, read WITHOUT expanding %VARS% and written back with the same value kind, so a
        // PATH that uses %USERPROFILE% stays expandable (Environment.SetEnvironmentVariable would flatten it).
        private static bool EnsureOnUserPath(string directory)
        {
            using var key = Registry.CurrentUser.OpenSubKey("Environment", writable: true);
            if (key == null)
            {
                return false;
            }

            string current = key.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            string merged = PathWithEntry(current, directory);
            if (merged == null)
            {
                return false;
            }

            RegistryValueKind kind = current == null ? RegistryValueKind.ExpandString : key.GetValueKind("Path");
            key.SetValue("Path", merged, kind == RegistryValueKind.String ? RegistryValueKind.String : RegistryValueKind.ExpandString);
            BroadcastEnvironmentChange();
            return true;
        }

        // Tells Explorer (and so every terminal opened from now on) that the environment changed.
        private static void BroadcastEnvironmentChange()
        {
            const int HWND_BROADCAST = 0xffff;
            const uint WM_SETTINGCHANGE = 0x001A;
            const uint SMTO_ABORTIFHUNG = 0x0002;
            _ = NativeMethods.SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, UIntPtr.Zero, "Environment", SMTO_ABORTIFHUNG, 2000, out _);
        }

        private static class NativeMethods
        {
            [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            internal static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
        }

        /// <summary>The <c>mtclaude.cmd</c> on PATH: runs the PowerShell script next to it with every argument.</summary>
        internal static string BuildCommandContents() =>
            "@echo off\r\n" +
            "rem mtclaude <Name> [claude arguments]: written by MultiTerminal (ticket 6c688883); MT rewrites it, do not edit.\r\n" +
            "powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File \"%~dp0" + ScriptFileName + "\" %*\r\n" +
            "exit /b %ERRORLEVEL%\r\n";

        /// <summary>The PowerShell script behind <c>mtclaude</c>.</summary>
        internal static string BuildScriptContents() => """
            # mtclaude <Name> [claude arguments]: written by MultiTerminal (ticket 6c688883); MT rewrites it, do not edit.
            # Starts Claude Code so it messages like a terminal MultiTerminal launched: answers to <Name>, has MT's
            # MCP server, registers with MT by name, and takes cross-session messages without the approval prompt.
            param(
                [Parameter(Position = 0)][string]$Name,
                [Parameter(Position = 1, ValueFromRemainingArguments = $true)][string[]]$ClaudeArgs
            )

            if ([string]::IsNullOrWhiteSpace($Name) -or $Name -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$') {
                Write-Host 'Usage: mtclaude <Name> [claude arguments]'
                Write-Host 'The name: 1 to 64 letters, digits, - _ or . (starting with a letter or digit), e.g. mtclaude Filbert'
                exit 2
            }
            if ($Name -ieq 'Unassigned') {
                Write-Host "'Unassigned' is MultiTerminal's placeholder name; pick another."
                exit 2
            }

            $api = if ($env:MTCLAUDE_API) { $env:MTCLAUDE_API } else { 'http://localhost:5050' }
            try {
                $terminals = Invoke-RestMethod -Uri "$api/api/messaging/terminals" -TimeoutSec 3
                $held = @($terminals | Where-Object { $_.isConnected -and $_.name -ieq $Name })
                if ($held.Count -gt 0) {
                    Write-Host "The name '$Name' is in use by a connected MultiTerminal terminal. Pick another name."
                    exit 3
                }
            } catch {
                Write-Host 'MultiTerminal is not running. Claude starts, but joins MultiTerminal messaging only if MultiTerminal starts within a minute.'
            }

            $claude = Get-Command claude -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($null -eq $claude) {
                Write-Host 'Claude Code (claude) was not found on PATH.'
                exit 4
            }
            if ([IO.Path]::GetFileName($claude.Source) -ine 'claude.exe') {
                Write-Host "Note: claude resolves to $($claude.Source), not claude.exe. MultiTerminal registers a session by name only when its MCP server runs under claude.exe."
            }

            # This session's MT identity. Anything else inherited (a pane id, a launch nonce) is not this session's.
            Get-ChildItem Env: | Where-Object { $_.Name -like 'MULTITERMINAL_*' } | ForEach-Object { Remove-Item -LiteralPath "Env:$($_.Name)" }
            $env:MULTITERMINAL_NAME = $Name
            $env:MULTITERMINAL_STANDALONE = '1'

            $launch = @('-n', $Name)
            $mcp = Join-Path $env:APPDATA 'multiterminal\.mcp.json'
            if (Test-Path -LiteralPath $mcp) { $launch += @('--mcp-config', $mcp) }
            else { Write-Host "MultiTerminal's MCP config was not found at $mcp; starting without MultiTerminal's tools." }
            $launch += @('--settings', (Join-Path $PSScriptRoot 'mtclaude-settings.json'))
            if ($ClaudeArgs) { $launch += $ClaudeArgs }

            & $claude.Source @launch
            exit $LASTEXITCODE
            """.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n";
    }
}
