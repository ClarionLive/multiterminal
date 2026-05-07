using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Ships MultiTerminal's Codex startup scaffolding — the prompt template
    /// that tells a Codex terminal its name and first actions, plus a PowerShell
    /// launcher that expands placeholders and invokes <c>codex</c> with the
    /// result as the initial user message.
    ///
    /// Files live under <c>%APPDATA%\multiterminal\codex\</c> and are written
    /// (or migrated) on every Codex launch by <see cref="EnsureStartupFiles"/>.
    ///
    /// <para>Update lifecycle: each managed default carries a marker header —
    /// <c>&lt;!-- MT-MANAGED-PROMPT v=N --&gt;</c> for the prompt and
    /// <c># MT-MANAGED-LAUNCHER v=N</c> for the launcher script. On launch:
    /// <list type="bullet">
    /// <item>Marker present + older version → overwrite with current default.</item>
    /// <item>Marker present + same/newer version → leave alone.</item>
    /// <item>No marker but a known legacy fingerprint matches (e.g. the Run-4-era
    /// bug string <c>name="{{NAME}}"</c> or the pre-marker MT-shipped opening line)
    /// → overwrite. Treats the file as a pre-marker MT-managed default.</item>
    /// <item>No marker and no legacy fingerprint → preserve verbatim. Treats
    /// the file as user-customized.</item>
    /// </list></para>
    ///
    /// To customize the template, remove the MT-MANAGED-* marker line — MT
    /// will then leave the file alone. To pick up a fresh current default,
    /// delete the file.
    /// </summary>
    public static class CodexPromptService
    {
        private const string StartupPromptFileName = "startup-prompt.md";
        private const string LaunchScriptFileName = "codex-launch.ps1";

        // Bump these when the corresponding default below changes. The number
        // is meaningful only when compared against an on-disk MT-MANAGED-*
        // marker that was written by an earlier shipping version of MT.
        private const int CurrentPromptVersion = 2;
        private const int CurrentLauncherVersion = 3;

        private static readonly Regex PromptMarkerRegex = new Regex(
            @"^<!--\s*MT-MANAGED-PROMPT\s+v=(\d+)\s*-->",
            RegexOptions.Compiled);

        private static readonly Regex LauncherMarkerRegex = new Regex(
            @"^#\s*MT-MANAGED-LAUNCHER\s+v=(\d+)",
            RegexOptions.Compiled);

        // Two-signal matchers that identify a pre-marker file as MT-managed.
        // A file matches when it contains <c>MustContain</c> AND (when set)
        // does NOT contain <c>MustNotContain</c>. The negative-signal half is
        // what makes the documented opt-out ("remove the marker line to keep
        // your edits") actually work for v=N defaults: a string that lives in
        // the v=N body is paired with a v=N-only sentinel so that v=N files
        // (with or without their marker) never match the pre-marker matcher.
        private static readonly (string MustContain, string MustNotContain)[] LegacyPromptMatchers =
        {
            // Run-4-era buggy prompts: presence of the wrong-param-name string
            // is uniquely diagnostic. No user would write this verbatim.
            ("name=\"{{NAME}}\"", null),

            // Pre-marker MT-shipped prompts (Run-5-era fix-but-no-marker, etc.):
            // every MT default opens with this exact line, but the v=2 default
            // is the first one to add the Pre-flight Check section. Pairing the
            // two means: v=2-with-or-without-marker → no match (Pre-flight is
            // present); pre-marker MT default → match (Pre-flight absent).
            (
                "You are **{{NAME}}**, a team member in a MultiTerminal coordination session.",
                "## Pre-flight check (read first)"
            ),
        };

        private static readonly (string MustContain, string MustNotContain)[] LegacyLauncherMatchers =
        {
            // The 'Unnamed' literal was the silly fallback for an empty
            // MULTITERMINAL_NAME in pre-marker launchers. The v=2/v=3 launchers
            // removed it (the new fail-closed empty-check covers that path), so
            // 'Unnamed' is a precise pre-marker fingerprint.
            ("'Unnamed'", null),
        };

        /// <summary>
        /// Returns the absolute path to the startup prompt template.
        /// </summary>
        public static string GetStartupPromptPath()
        {
            return Path.Combine(GetCodexScaffoldingDir(), StartupPromptFileName);
        }

        /// <summary>
        /// Returns the absolute path to the launcher PowerShell script.
        /// </summary>
        public static string GetLaunchScriptPath()
        {
            return Path.Combine(GetCodexScaffoldingDir(), LaunchScriptFileName);
        }

        /// <summary>
        /// Writes (or migrates) the managed startup prompt and launcher script
        /// under <c>%APPDATA%\multiterminal\codex\</c>. Safe to call on every
        /// Codex launch — the work is bounded (two files, both read fully into
        /// memory) and idempotent for already-current installs.
        ///
        /// See the class summary for the full marker / fingerprint / preserve
        /// decision tree.
        /// </summary>
        public static void EnsureStartupFiles()
        {
            string dir = GetCodexScaffoldingDir();
            Directory.CreateDirectory(dir);

            EnsureManagedFile(
                Path.Combine(dir, StartupPromptFileName),
                DefaultStartupPrompt,
                CurrentPromptVersion,
                PromptMarkerRegex,
                LegacyPromptMatchers);

            EnsureManagedFile(
                Path.Combine(dir, LaunchScriptFileName),
                DefaultLaunchScript,
                CurrentLauncherVersion,
                LauncherMarkerRegex,
                LegacyLauncherMatchers);
        }

        /// <summary>
        /// Replace-or-preserve write for a single MT-managed scaffold file.
        ///
        /// Concurrent-safe: the destination write goes via a uniquely-named
        /// temp file and an atomic <see cref="File.Replace(string,string,string)"/>
        /// (or <see cref="File.Move(string,string)"/> when the destination
        /// does not yet exist). Concurrent first launches that race on
        /// creation tolerate the resulting <see cref="IOException"/> because
        /// the loser's intended content is byte-identical to the winner's.
        /// </summary>
        private static void EnsureManagedFile(
            string path,
            string current,
            int currentVersion,
            Regex markerRegex,
            (string MustContain, string MustNotContain)[] legacyMatchers)
        {
            string onDisk;
            try
            {
                if (!File.Exists(path))
                {
                    AtomicWrite(path, current);
                    return;
                }

                onDisk = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (IOException ex)
            {
                // Fail-closed on read failure. We can't tell whether the
                // on-disk content is stale buggy MT-managed scaffolding (in
                // which case continuing to use it is wrong) or
                // user-customized (in which case preserving it is right), so
                // we surface the failure to BuildCodexCommand and let the
                // launch fail closed. The user retries; transient AV locks
                // resolve in well under a second.
                System.Diagnostics.Debug.WriteLine(
                    $"[CodexPromptService] Read failure on managed file '{path}': {ex.Message}");
                throw;
            }

            if (string.Equals(onDisk, current, StringComparison.Ordinal))
                return;

            Match match = markerRegex.Match(onDisk);
            if (match.Success)
            {
                if (int.TryParse(match.Groups[1].Value, out int onDiskVersion)
                    && onDiskVersion < currentVersion)
                {
                    AtomicWrite(path, current);
                }
                return;
            }

            foreach ((string mustContain, string mustNotContain) in legacyMatchers)
            {
                if (onDisk.IndexOf(mustContain, StringComparison.Ordinal) >= 0
                    && (mustNotContain == null
                        || onDisk.IndexOf(mustNotContain, StringComparison.Ordinal) < 0))
                {
                    AtomicWrite(path, current);
                    return;
                }
            }

            // No marker, no legacy match — preserve as user-customized.
        }

        /// <summary>
        /// Write <paramref name="content"/> to a uniquely-named temp file in
        /// the same directory, then atomically rename it over
        /// <paramref name="path"/>. UTF-8 encoded, no BOM. Avoids the
        /// half-written-file window a naive truncate-and-write leaves.
        ///
        /// <para>Failure semantics: an <see cref="IOException"/> from the
        /// rename step is swallowed only when we can verify a concurrent
        /// launcher already wrote byte-identical content (the race-winner
        /// outcome — both writers wrote <c>current</c>). Any other failure
        /// (write fault, permission denial, race-winner-wrote-different-content,
        /// out-of-disk) propagates so callers can fail-closed. The
        /// <c>finally</c> block best-efforts the orphan temp file in every
        /// path, including <see cref="UnauthorizedAccessException"/> and
        /// <see cref="System.Security.SecurityException"/>.</para>
        /// </summary>
        private static void AtomicWrite(string path, string content)
        {
            string dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir))
                throw new ArgumentException("Path must include a directory.", nameof(path));

            string tmp = Path.Combine(
                dir,
                Path.GetFileName(path) + ".tmp." + Guid.NewGuid().ToString("N").Substring(0, 8));

            try
            {
                File.WriteAllBytes(tmp, Encoding.UTF8.GetBytes(content));
                if (File.Exists(path))
                {
                    File.Replace(tmp, path, null);
                }
                else
                {
                    File.Move(tmp, path);
                }
            }
            catch (IOException) when (RaceWinnerWroteSameContent(path, content))
            {
                // Verified concurrent-rename loser: the destination already
                // holds byte-identical content. Idempotent success — no rethrow.
            }
            finally
            {
                // Best-effort orphan cleanup. After a successful rename the
                // temp no longer exists at this path and the Delete is a
                // no-op; after IOException-with-verified-race or any
                // propagating exception the temp may still exist and would
                // otherwise accumulate in %APPDATA%\multiterminal\codex\.
                if (File.Exists(tmp))
                {
                    try { File.Delete(tmp); }
                    catch (IOException) { /* best-effort */ }
                    catch (UnauthorizedAccessException) { /* best-effort */ }
                }
            }
        }

        /// <summary>
        /// Read <paramref name="path"/> and compare byte-for-byte against the
        /// UTF-8 encoding of <paramref name="expected"/>. Used as the filter
        /// on <see cref="AtomicWrite"/>'s <see cref="IOException"/> catch so
        /// that only verified race-winner outcomes are swallowed; any other
        /// IOException (genuine write failure, permission denial, race-winner
        /// wrote different content) propagates to the caller.
        /// </summary>
        private static bool RaceWinnerWroteSameContent(string path, string expected)
        {
            try
            {
                if (!File.Exists(path)) return false;
                byte[] onDisk = File.ReadAllBytes(path);
                byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
                if (onDisk.Length != expectedBytes.Length) return false;
                for (int i = 0; i < onDisk.Length; i++)
                {
                    if (onDisk[i] != expectedBytes[i]) return false;
                }
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static string GetCodexScaffoldingDir()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "multiterminal", "codex");
        }

        private const string DefaultStartupPrompt = @"<!-- MT-MANAGED-PROMPT v=2 -->
You are **{{NAME}}**, a team member in a MultiTerminal coordination session.

## Pre-flight check (read first)

If `{{NAME}}` or `{{DOC_ID}}` rendered as empty above (e.g. the line ""You are **{{NAME}}**"" rendered as ""You are ****"", or the registration step below shows `docId:` with nothing after the colon), MultiTerminal startup wiring is incomplete. In that case:

- Do NOT call `register_terminal`, `register_session`, `get_messages`, or `get_inbox`.
- Do NOT invent identity values.
- Tell the user, verbatim: ""MultiTerminal startup wiring is incomplete (missing NAME or DOC_ID env var). I cannot register or poll for messages until the host re-launches me with valid identity values.""
- Stop the startup actions. Wait for the user.

Only continue past this section if BOTH `{{NAME}}` and `{{DOC_ID}}` rendered as non-empty values.

## Your first action

Before answering any user requests, register yourself so the team can see you:

1. Call the `register_terminal` tool (from the `multiterminal` MCP server) with:
   - `name`: `{{NAME}}`
   - `docId`: `{{DOC_ID}}`
2. If the session id placeholder below is non-empty, call `register_session` with:
   - `sessionId`: `{{SESSION_ID}}`
   - `agentName`: `{{NAME}}`
   - `projectPath`: `{{CWD}}`
   (If `{{SESSION_ID}}` is empty, skip this step — session-id injection for Codex is not fully wired yet. Do NOT invent a sessionId.)
3. Call `get_messages` with `terminalId=""{{DOC_ID}}""` to see direct messages other terminals have sent you.
4. Call `get_inbox` with `userId=""{{NAME}}""` to see PM notifications (task-ready-for-testing, escalations, helper requests).
5. Briefly report to the user: ""Registered as {{NAME}}."" — then summarize your terminal messages and PM inbox (or say ""all clear"" if both are empty).

## Role

You are a team member, not the Owner. Don't:
- Claim kanban tasks on your own — wait for the user or team lead to assign work.
- Make architectural decisions or start coding unprompted.

If a task is assigned to you, confirm with the user before starting.

## Tools you have via the multiterminal MCP server

MultiTerminal has TWO distinct message stores. They are separate queues — polling one will NOT show messages from the other.

- **Terminal messages** (direct chat between agents). Key: your `terminalId` (= your `docId` `{{DOC_ID}}`).
  - `send_message`, `reply`, `get_messages`
- **PM inbox** (notifications from the project manager / Owner — testing alerts, escalations, helper requests). Key: your `userId` (= your name `{{NAME}}`).
  - `get_inbox`, `reply_to_inbox`, `mark_inbox_read`
- Kanban: `list_tasks`, `get_task_detail`, `claim_task`, `update_task_checklist`
- Knowledge base: `query_knowledge`, `add_knowledge`
- Team roster: `get_team_roster`

## Project context

Your working directory is `{{CWD}}`. If an `AGENTS.md` file exists at the project root, read it for project-specific guidance.
";

        private const string DefaultLaunchScript = @"# MT-MANAGED-LAUNCHER v=3
# MultiTerminal Codex launcher.
# Reads the startup prompt template, substitutes env-var placeholders,
# and invokes `codex` with the expanded prompt as the initial user message.
#
# This file is generated by MultiTerminal on first Codex launch and on
# updates that bump the marker version above. To customize, remove the
# MT-MANAGED-LAUNCHER marker line — MT will then preserve the file.
# Delete the file to get the current default on the next launch.

param()

$ErrorActionPreference = 'Continue'

# Fail-closed: refuse to launch when MT startup wiring is incomplete.
# Empty NAME/DOC_ID would let Codex call register_terminal with empty
# values or poll the wrong inbox key — silent wrong-answer paths.
if ([string]::IsNullOrWhiteSpace($env:MULTITERMINAL_NAME)) {
    Write-Error ""[MultiTerminal] Refusing to launch Codex: MULTITERMINAL_NAME env var is not set.""
    exit 1
}
if ([string]::IsNullOrWhiteSpace($env:MULTITERMINAL_DOC_ID)) {
    Write-Error ""[MultiTerminal] Refusing to launch Codex: MULTITERMINAL_DOC_ID env var is not set.""
    exit 1
}

# Fail-closed: refuse to launch when the managed startup prompt cannot be
# loaded or expanded. Falling back to bare `codex` would drop Codex into
# an unwired shell with no register_terminal instructions — another
# silent wrong-answer path the host expects to never happen.
$promptPath = Join-Path $PSScriptRoot 'startup-prompt.md'
if (-not (Test-Path -Path $promptPath)) {
    Write-Error ""[MultiTerminal] Refusing to launch Codex: managed startup prompt not found at $promptPath. Re-launch from MultiTerminal to regenerate.""
    exit 1
}

$prompt = $null
try {
    $raw = Get-Content -Path $promptPath -Raw -ErrorAction Stop

    $name = $env:MULTITERMINAL_NAME
    $docId = $env:MULTITERMINAL_DOC_ID
    $sessionId = if ([string]::IsNullOrWhiteSpace($env:MULTITERMINAL_CODEX_SESSION_ID)) { '' } else { $env:MULTITERMINAL_CODEX_SESSION_ID }
    $cwd = (Get-Location).Path

    $prompt = $raw.Replace('{{NAME}}', $name).Replace('{{DOC_ID}}', $docId).Replace('{{SESSION_ID}}', $sessionId).Replace('{{CWD}}', $cwd)
} catch {
    Write-Error ""[MultiTerminal] Refusing to launch Codex: failed to load startup prompt: $_""
    exit 1
}

if ([string]::IsNullOrWhiteSpace($prompt)) {
    Write-Error ""[MultiTerminal] Refusing to launch Codex: startup prompt expansion produced empty output.""
    exit 1
}

$codexBin = if ([string]::IsNullOrWhiteSpace($env:MULTITERMINAL_CODEX_BIN)) { 'codex' } else { $env:MULTITERMINAL_CODEX_BIN }
& $codexBin $prompt
";
    }
}
