using System;
using System.Collections.Generic;
using System.IO;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// The outcome of <see cref="ClaudeProfileResolver.Resolve(string, string)"/>: either the profile to
    /// launch under, or the reason the launch must not happen (Owner decision 2026-10-10: a profile
    /// name this machine has no profile for, or a profile whose folder is missing or invalid, REFUSES
    /// the launch. There is no fallback to Default and no "launch anyway").
    /// </summary>
    /// <param name="Profile">The profile to use. Null exactly when the launch is blocked, so a caller
    /// that ignores <see cref="CanLaunch"/> cannot start anything on a profile nobody chose.</param>
    /// <param name="RequestedName">The non-blank name that was asked for (override, else project), or null.</param>
    /// <param name="BlockedReason">Why the launch is refused (one user-facing sentence), or null when it may proceed.</param>
    public sealed record ClaudeProfileResolution(ClaudeProfile Profile, string RequestedName, string BlockedReason)
    {
        /// <summary>True when the launch may proceed under <see cref="Profile"/>.</summary>
        public bool CanLaunch => string.IsNullOrEmpty(BlockedReason);
    }

    /// <summary>One problem found by <see cref="ClaudeProfileResolver.Validate(IReadOnlyList{ClaudeProfile})"/>.</summary>
    /// <param name="Index">Zero-based index of the offending profile in the list that was validated.</param>
    /// <param name="Message">User-facing description.</param>
    public sealed record ClaudeProfileValidationError(int Index, string Message)
    {
        public override string ToString() => Message;
    }

    /// <summary>
    /// Everything the resolver reads from the outside world, injected so every decision is testable
    /// without touching settings.txt, the process environment or the disk.
    /// </summary>
    /// <param name="Configured">The stored profiles (never includes Default).</param>
    /// <param name="InheritedConfigDir">MT's own CLAUDE_CONFIG_DIR at startup, or null.</param>
    /// <param name="UserProfile">%USERPROFILE%.</param>
    /// <param name="DirectoryExists">Directory probe.</param>
    internal sealed record ClaudeProfileContext(
        IReadOnlyList<ClaudeProfile> Configured,
        string InheritedConfigDir,
        string UserProfile,
        Func<string, bool> DirectoryExists);

    /// <summary>
    /// Ticket c8ee0eec (GitHub #21): which Claude Code config directory a terminal runs under, and the
    /// environment that puts it there. Static and pure: each public method reads the live machine state
    /// and delegates to an internal overload that takes a <see cref="ClaudeProfileContext"/>.
    /// <para><b>The trap this class exists to avoid:</b> when <c>CLAUDE_CONFIG_DIR</c> is set, Claude Code
    /// reads <c>.claude.json</c> from INSIDE that directory instead of <c>%USERPROFILE%\.claude.json</c>.
    /// So setting it to <c>%USERPROFILE%\.claude</c> to "explicitly select the default" silently moves
    /// the user's login, onboarding and global MCP state. The Default profile therefore never sets the
    /// variable to that folder: it repeats MT's inherited value if there was one, and otherwise REMOVES
    /// the variable, so a shell profile that sets it cannot leak another profile in.</para>
    /// </summary>
    public static class ClaudeProfileResolver
    {
        /// <summary>The variable Claude Code reads its config directory from.</summary>
        public const string ConfigDirVariable = "CLAUDE_CONFIG_DIR";

        /// <summary>Set to <c>1</c> on every launch (Owner decision Q5), so agent teams do not depend on
        /// each profile's settings.json.</summary>
        public const string AgentTeamsVariable = "CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS";

        /// <summary>
        /// MT's own <c>CLAUDE_CONFIG_DIR</c>, read once when this class is first used. Nothing in MT sets
        /// that variable in its own process, so this is what MT was started with. Null when unset or blank.
        /// </summary>
        public static string InheritedConfigDir { get; } = NullIfBlank(Environment.GetEnvironmentVariable(ConfigDirVariable));

        // ── Public API (live machine state) ───────────────────────────────────

        /// <summary>The implicit Default profile. Not stored, not editable; a new instance per call.</summary>
        public static ClaudeProfile DefaultProfile => GetDefaultProfile(CurrentContext());

        /// <summary>Default first, then every stored profile in stored order (for pickers and dropdowns).</summary>
        public static IReadOnlyList<ClaudeProfile> GetAllProfiles() => GetAllProfiles(CurrentContext());

        /// <summary>
        /// Which profile to launch under. Precedence: <paramref name="launchOverrideName"/> →
        /// <paramref name="projectProfileName"/> → Default (blank counts as not given; "Default" selects
        /// Default). A name this machine has no profile for, or a profile whose folder is invalid or does
        /// not exist, is BLOCKED: <see cref="ClaudeProfileResolution.CanLaunch"/> is false and no profile
        /// is returned.
        /// </summary>
        public static ClaudeProfileResolution Resolve(string projectProfileName, string launchOverrideName) =>
            Resolve(projectProfileName, launchOverrideName, CurrentContext());

        /// <summary>
        /// The profile's config directory as a canonical full path without a trailing separator. For
        /// Default (or null): the inherited CLAUDE_CONFIG_DIR, else %USERPROFILE%\.claude.
        /// </summary>
        /// <exception cref="ArgumentException">A non-default profile whose ConfigDir is not an absolute path.</exception>
        public static string GetConfigDir(ClaudeProfile profile) => GetConfigDir(profile, CurrentContext());

        /// <summary>
        /// PowerShell statements for the pane launch prologue, in the same shape as ConPtyTerminal's
        /// <c>envSetup</c>: each statement ends in <c>"; "</c>, so the string can be appended directly.
        /// Always two statements: the config-dir one (<c>$env:CLAUDE_CONFIG_DIR = '&lt;dir&gt;'; </c> or
        /// <c>Remove-Item Env:CLAUDE_CONFIG_DIR -ErrorAction SilentlyContinue; </c>), then
        /// <c>$env:CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS = '1'; </c>.
        /// </summary>
        public static string GetPrologue(ClaudeProfile profile) => GetPrologue(profile, CurrentContext());

        /// <summary>
        /// The same decision as <see cref="GetPrologue(ClaudeProfile)"/> as variable/value pairs for a
        /// <c>ProcessStartInfo.Environment</c>. A null value means REMOVE the variable from the child's
        /// environment (do not set it to null or empty).
        /// </summary>
        public static IReadOnlyList<KeyValuePair<string, string>> GetEnvironment(ClaudeProfile profile) =>
            GetEnvironment(profile, CurrentContext());

        /// <summary>
        /// Every config directory MT may read from: Default's plus each stored profile's, as canonical full
        /// paths WITH a trailing separator (so <c>.claude-other\</c> does not prefix <c>.claude-otherX</c>),
        /// de-duplicated case-insensitively. Built only from MT's settings and startup environment, never
        /// from caller input. A drive root, %USERPROFILE% itself or any ancestor of it, and any non-absolute
        /// path are dropped, so a bad setting can never widen a path guard to the whole profile or disk.
        /// </summary>
        public static IReadOnlyList<string> GetAllRoots() => GetAllRoots(CurrentContext());

        /// <summary>
        /// Validates a complete list of stored profiles (the editor's whole grid). Empty result = valid.
        /// Rules: name non-blank, unique (case-insensitive), not "Default"; directory absolute, free of
        /// typographic single quotes (PowerShell reads them as the end of a string), existing, not a drive
        /// root, not %USERPROFILE% or an ancestor of it, not %USERPROFILE%\.claude, not the Default
        /// profile's directory, and not shared with another profile.
        /// </summary>
        public static IReadOnlyList<ClaudeProfileValidationError> Validate(IReadOnlyList<ClaudeProfile> profiles) =>
            Validate(profiles, CurrentContext());

        /// <summary>
        /// Null when <paramref name="profile"/> has run Claude Code on this machine, else one sentence
        /// saying that it has not and which file is missing. Asked before a launch nobody is watching
        /// (<see cref="ClaudeProfileLaunchPolicy.Decide"/>). Reads the disk; creates nothing.
        /// </summary>
        public static string DescribeNeverUsed(ClaudeProfile profile) =>
            DescribeNeverUsed(profile, CurrentContext(), File.Exists);

        /// <summary>
        /// <see cref="Validate(IReadOnlyList{ClaudeProfile})"/> without the "folder must exist" rule. The
        /// Settings dialog uses it because it creates a missing folder on save (Owner decision
        /// 2026-10-16: typing a name must be enough). Every other rule still applies.
        /// </summary>
        public static IReadOnlyList<ClaudeProfileValidationError> ValidateAllowingMissingFolders(IReadOnlyList<ClaudeProfile> profiles) =>
            ValidateAllowingMissingFolders(profiles, CurrentContext());

        internal static IReadOnlyList<ClaudeProfileValidationError> ValidateAllowingMissingFolders(
            IReadOnlyList<ClaudeProfile> profiles, ClaudeProfileContext ctx) =>
            Validate(profiles, ctx with { DirectoryExists = _ => true });

        /// <summary>
        /// True when Claude Code has run with <c>CLAUDE_CONFIG_DIR</c> set to <paramref name="configDir"/>:
        /// its <see cref="StateFileName"/> (or the older <see cref="LegacyStateFileName"/>) is inside it.
        /// For the Settings dialog's per-row status, where the profile may not be stored yet. False for a
        /// blank, relative or missing folder.
        /// </summary>
        public static bool IsSignedIn(string configDir) => IsSignedIn(configDir, File.Exists);

        // ── Pure core ─────────────────────────────────────────────────────────

        /// <summary>The file name Claude Code keeps its login, onboarding, folder-trust and
        /// skip-permissions state in.</summary>
        internal const string StateFileName = ".claude.json";

        /// <summary>Believed to be the name older Claude Code versions used for <see cref="StateFileName"/>.</summary>
        internal const string LegacyStateFileName = ".config.json";

        internal static bool IsSignedIn(string configDir, Func<string, bool> fileExists)
        {
            string dir = Canonicalize(configDir);
            if (dir == null || fileExists == null)
                return false;

            try
            {
                return fileExists(Path.Combine(dir, StateFileName))
                    || fileExists(Path.Combine(dir, LegacyStateFileName));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return false;
            }
        }

        /// <summary>
        /// Where Claude Code keeps <see cref="StateFileName"/> for a launch under
        /// <paramref name="profile"/>. This follows the variable the launch will actually get
        /// (<see cref="ConfigDirValue"/>): INSIDE the config directory when CLAUDE_CONFIG_DIR is set,
        /// and in %USERPROFILE% itself (next to <c>.claude</c>, not inside it) when the variable is
        /// removed, which is the Default profile on a machine where MT inherited none. Null when
        /// %USERPROFILE% is needed and unknown.
        /// </summary>
        internal static string GetStateFilePath(ClaudeProfile profile, ClaudeProfileContext ctx)
        {
            string dir = ConfigDirValue(profile, ctx) ?? Canonicalize(ctx.UserProfile);
            return dir == null ? null : Path.Combine(dir, StateFileName);
        }

        /// <summary>
        /// True when <paramref name="profile"/> has run Claude Code before: its
        /// <see cref="StateFileName"/> exists. A <c>.config.json</c> inside the config directory counts
        /// too (believed to be the name older Claude Code versions used; accepting it can only avoid a
        /// wrong "never used"). Also true when the file's place cannot be worked out, because this
        /// answer is used to refuse a launch and "cannot tell" is not "never used".
        /// </summary>
        internal static bool HasBeenUsed(ClaudeProfile profile, ClaudeProfileContext ctx, Func<string, bool> fileExists)
        {
            string path = GetStateFilePath(profile, ctx);
            if (path == null || fileExists == null)
                return true;

            if (fileExists(path))
                return true;

            string configDir = GetConfigDir(profile, ctx);
            return configDir != null && fileExists(Path.Combine(configDir, LegacyStateFileName));
        }

        internal static string DescribeNeverUsed(ClaudeProfile profile, ClaudeProfileContext ctx, Func<string, bool> fileExists)
        {
            if (HasBeenUsed(profile, ctx, fileExists))
                return null;

            string name = profile == null || profile.IsDefault ? ClaudeProfile.DefaultName : profile.Name;
            return $"The Claude profile '{name}' has never been used on this machine (there is no '{GetStateFilePath(profile, ctx)}').";
        }

        internal static ClaudeProfileContext CurrentContext() =>
            new ClaudeProfileContext(
                SettingsService.Default.GetClaudeProfiles(),
                InheritedConfigDir,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Directory.Exists);

        internal static ClaudeProfile GetDefaultProfile(ClaudeProfileContext ctx) =>
            new ClaudeProfile(ClaudeProfile.DefaultName, DefaultConfigDir(ctx));

        internal static IReadOnlyList<ClaudeProfile> GetAllProfiles(ClaudeProfileContext ctx)
        {
            var result = new List<ClaudeProfile> { GetDefaultProfile(ctx) };
            foreach (var p in Stored(ctx))
                result.Add(new ClaudeProfile(p.Name.Trim(), p.ConfigDir));
            return result;
        }

        internal static ClaudeProfileResolution Resolve(string projectProfileName, string launchOverrideName, ClaudeProfileContext ctx)
        {
            // First non-blank wins, THEN the Default rule: an explicit "Default" override selects Default
            // rather than falling through to the project's profile.
            string asked = NullIfBlank(launchOverrideName) ?? NullIfBlank(projectProfileName);
            string requested = ClaudeProfile.NormalizeName(asked);
            if (requested == null)
                return new ClaudeProfileResolution(GetDefaultProfile(ctx), asked, null);

            foreach (var p in Stored(ctx))
            {
                if (!string.Equals(p.Name.Trim(), requested, StringComparison.OrdinalIgnoreCase))
                    continue;

                string name = p.Name.Trim();
                string dir = Canonicalize(p.ConfigDir);
                if (dir == null)
                    return new ClaudeProfileResolution(null, requested, $"Claude profile '{name}' has an invalid folder ('{p.ConfigDir}').");

                // Never a launch into a folder that is not there: Claude Code would create it and start
                // with no login or settings, which looks like a working session on the wrong account.
                if (!SafeExists(ctx, dir))
                    return new ClaudeProfileResolution(null, requested, $"Claude profile '{name}' points at '{dir}', which does not exist.");

                return new ClaudeProfileResolution(new ClaudeProfile(name, p.ConfigDir), requested, null);
            }

            return new ClaudeProfileResolution(null, requested, $"Claude profile '{requested}' is not configured on this machine.");
        }

        internal static string GetConfigDir(ClaudeProfile profile, ClaudeProfileContext ctx)
        {
            if (profile == null || profile.IsDefault)
                return DefaultConfigDir(ctx);

            return Canonicalize(profile.ConfigDir)
                ?? throw new ArgumentException($"Claude profile '{profile.Name}' has no absolute config folder ('{profile.ConfigDir}').", nameof(profile));
        }

        internal static string GetPrologue(ClaudeProfile profile, ClaudeProfileContext ctx)
        {
            string value = ConfigDirValue(profile, ctx);
            string configLine = value == null
                ? $"Remove-Item Env:{ConfigDirVariable} -ErrorAction SilentlyContinue; "
                : $"$env:{ConfigDirVariable} = '{EscapeForSingleQuotedPowerShell(value)}'; ";
            return configLine + $"$env:{AgentTeamsVariable} = '1'; ";
        }

        internal static IReadOnlyList<KeyValuePair<string, string>> GetEnvironment(ClaudeProfile profile, ClaudeProfileContext ctx) =>
            new[]
            {
                new KeyValuePair<string, string>(ConfigDirVariable, ConfigDirValue(profile, ctx)),
                new KeyValuePair<string, string>(AgentTeamsVariable, "1"),
            };

        internal static IReadOnlyList<string> GetAllRoots(ClaudeProfileContext ctx)
        {
            var roots = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var dirs = new List<string> { DefaultConfigDir(ctx) };
            foreach (var p in Stored(ctx))
                dirs.Add(p.ConfigDir);

            foreach (string raw in dirs)
            {
                string dir = Canonicalize(raw);
                if (dir == null || IsVolumeRoot(dir) || IsUserProfileOrAncestor(dir, ctx.UserProfile))
                    continue;

                string root = dir + Path.DirectorySeparatorChar;
                if (seen.Add(root))
                    roots.Add(root);
            }

            return roots;
        }

        internal static IReadOnlyList<ClaudeProfileValidationError> Validate(IReadOnlyList<ClaudeProfile> profiles, ClaudeProfileContext ctx)
        {
            var errors = new List<ClaudeProfileValidationError>();
            if (profiles == null)
                return errors;

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string defaultDir = DefaultConfigDir(ctx);
            string homeClaude = HomeClaudeDir(ctx);

            for (int i = 0; i < profiles.Count; i++)
            {
                var p = profiles[i];
                string name = NullIfBlank(p?.Name);
                string label = name ?? $"#{i + 1}";

                if (name == null)
                    errors.Add(new ClaudeProfileValidationError(i, $"Profile {label}: a name is required."));
                else if (ClaudeProfile.NormalizeName(name) == null)
                    errors.Add(new ClaudeProfileValidationError(i, $"'{ClaudeProfile.DefaultName}' is reserved for the built-in profile; choose another name."));
                else if (!names.Add(name))
                    errors.Add(new ClaudeProfileValidationError(i, $"There is more than one profile named '{name}'."));

                string rawDir = NullIfBlank(p?.ConfigDir);
                string dir = Canonicalize(rawDir);
                if (dir == null)
                {
                    errors.Add(new ClaudeProfileValidationError(i, $"Profile {label}: the folder must be an absolute path."));
                    continue;
                }

                if (ContainsPowerShellQuoteLookalike(dir))
                    errors.Add(new ClaudeProfileValidationError(i, $"Profile {label}: the folder path contains a curly quote (\u2018 \u2019 \u201A or \u201B), which PowerShell reads as the end of a string; rename the folder or choose another."));
                else if (IsVolumeRoot(dir))
                    errors.Add(new ClaudeProfileValidationError(i, $"Profile {label}: the folder cannot be a drive root ({dir})."));
                else if (IsUserProfileOrAncestor(dir, ctx.UserProfile))
                    errors.Add(new ClaudeProfileValidationError(i, $"Profile {label}: the folder cannot be your user folder or a folder above it ({dir})."));
                else if (homeClaude != null && SamePath(dir, homeClaude))
                    errors.Add(new ClaudeProfileValidationError(i, $"Profile {label}: {dir} is the Default profile's folder; use the Default profile for it."));
                else if (defaultDir != null && SamePath(dir, defaultDir))
                    errors.Add(new ClaudeProfileValidationError(i, $"Profile {label}: {dir} is the Default profile's folder."));
                else if (dirs.TryGetValue(dir, out string other))
                    errors.Add(new ClaudeProfileValidationError(i, $"Profile {label}: {dir} is already used by profile '{other}'."));
                else if (!SafeExists(ctx, dir))
                    errors.Add(new ClaudeProfileValidationError(i, $"Profile {label}: the folder {dir} does not exist."));

                if (!dirs.ContainsKey(dir))
                    dirs[dir] = label;
            }

            return errors;
        }

        /// <summary>
        /// Structural rules only (no filesystem): what <see cref="SettingsService.GetClaudeProfiles"/>
        /// keeps when reading settings. True when the entry is usable.
        /// </summary>
        internal static bool IsStorable(ClaudeProfile p) =>
            p != null
            && NullIfBlank(p.Name) != null
            && !p.IsDefault
            && Canonicalize(p.ConfigDir) != null;

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// The value CLAUDE_CONFIG_DIR must have in the child, or null to remove it. The single place the
        /// trap rule lives: never point the variable at %USERPROFILE%\.claude unless MT itself was started
        /// with exactly that value.
        /// </summary>
        private static string ConfigDirValue(ClaudeProfile profile, ClaudeProfileContext ctx)
        {
            string inherited = Canonicalize(ctx.InheritedConfigDir);
            if (profile == null || profile.IsDefault)
                return inherited;

            string dir = GetConfigDir(profile, ctx);
            string homeClaude = HomeClaudeDir(ctx);
            if (homeClaude != null && SamePath(dir, homeClaude))
                return inherited != null && SamePath(inherited, homeClaude) ? inherited : null;

            return dir;
        }

        private static string DefaultConfigDir(ClaudeProfileContext ctx) =>
            Canonicalize(ctx.InheritedConfigDir) ?? HomeClaudeDir(ctx);

        private static string HomeClaudeDir(ClaudeProfileContext ctx) =>
            NullIfBlank(ctx.UserProfile) == null ? null : Canonicalize(Path.Combine(ctx.UserProfile, ".claude"));

        private static IEnumerable<ClaudeProfile> Stored(ClaudeProfileContext ctx)
        {
            if (ctx.Configured == null)
                yield break;
            foreach (var p in ctx.Configured)
            {
                if (IsStorable(p))
                    yield return p;
            }
        }

        /// <summary>
        /// The one folder canonicaliser for Claude config directories (also used by the token meter's
        /// transcript guard): full path without a trailing separator, or null for blank / relative /
        /// malformed input. Surrounding whitespace and double quotes are dropped first. Does not touch
        /// the disk and does not resolve symlinks or junctions.
        /// </summary>
        public static string Canonicalize(string path)
        {
            string trimmed = NullIfBlank(path)?.Trim('"');
            if (trimmed == null)
                return null;
            try
            {
                if (!Path.IsPathFullyQualified(trimmed))
                    return null;
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return null;
            }
        }

        /// <summary>
        /// True when <paramref name="canonical"/> (from <see cref="Canonicalize"/>) is a whole volume: a
        /// drive root or a UNC share root.
        /// </summary>
        public static bool IsVolumeRoot(string canonical)
        {
            if (string.IsNullOrEmpty(canonical))
                return false;
            string root = Path.GetPathRoot(canonical);
            return root != null && SamePath(Path.TrimEndingDirectorySeparator(root), canonical);
        }

        // PowerShell accepts these four as single-quote characters too, so one inside a single-quoted
        // string ends it exactly as an ASCII apostrophe would.
        private const string PowerShellQuoteLookalikes = "\u2018\u2019\u201A\u201B";

        internal static bool ContainsPowerShellQuoteLookalike(string value) =>
            value != null && value.IndexOfAny(PowerShellQuoteLookalikes.ToCharArray()) >= 0;

        /// <summary>
        /// Escapes a value for the inside of a single-quoted PowerShell string by doubling every character
        /// PowerShell accepts as a single quote: the ASCII apostrophe and the four typographic ones.
        /// Validate rejects the typographic ones in a profile folder; this covers a value stored before
        /// that rule existed, or written into settings.txt by hand.
        /// </summary>
        internal static string EscapeForSingleQuotedPowerShell(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            var sb = new System.Text.StringBuilder(value.Length + 8);
            foreach (char c in value)
            {
                sb.Append(c);
                if (c == '\'' || PowerShellQuoteLookalikes.IndexOf(c) >= 0)
                    sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>True when <paramref name="canonical"/> is %USERPROFILE% or a folder that contains it.</summary>
        private static bool IsUserProfileOrAncestor(string canonical, string userProfile)
        {
            string home = Canonicalize(userProfile);
            if (home == null)
                return false;
            return SamePath(canonical, home)
                || home.StartsWith(canonical + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static bool SafeExists(ClaudeProfileContext ctx, string dir)
        {
            try
            {
                return ctx.DirectoryExists?.Invoke(dir) == true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return false;
            }
        }

        private static string NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
