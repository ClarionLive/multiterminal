using System;
using System.Collections.Generic;
using System.IO;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>One Claude config folder MT may read session data from, and the profile it belongs to.</summary>
    /// <param name="ProfileName">The profile's name ("Default" for the built-in one).</param>
    /// <param name="ConfigDir">Canonical full path without a trailing separator.</param>
    internal sealed record ClaudeDataRoot(string ProfileName, string ConfigDir)
    {
        /// <summary><c>&lt;ConfigDir&gt;\projects</c>, where Claude Code keeps one folder per working directory.</summary>
        public string ProjectsDir => Path.Combine(ConfigDir, "projects");
    }

    /// <summary>
    /// The Claude profile of a PANE, as a session-data reader or writer is told it. A null reference
    /// means "there is no pane" (the caller then decides by folder); an instance means there IS one,
    /// and its profile is the only one that pane's data may be read from or written to.
    /// </summary>
    /// <param name="ProfileName">The profile the pane was launched under; null or blank = Default.</param>
    /// <param name="Known">False when a pane may exist but its profile could not be read (the lookup
    /// threw). Data is then skipped: "could not tell" is neither Default nor the folder's profile.</param>
    internal sealed record ClaudePaneProfile(string ProfileName, bool Known = true)
    {
        /// <summary>A pane whose profile could not be read. Yields no folder.</summary>
        internal static readonly ClaudePaneProfile Unknown = new ClaudePaneProfile(null, false);
    }

    /// <summary>One running pane as a session-data pass over the open terminals sees it (taken on the
    /// UI thread, used off it).</summary>
    /// <param name="AgentName">The pane's agent name; may be blank.</param>
    /// <param name="Folder">The pane's working folder.</param>
    /// <param name="Profile">The Claude profile the pane was launched under; null when the pane has no
    /// recorded launch.</param>
    internal sealed record ClaudePaneSnapshot(string AgentName, string Folder, ClaudePaneProfile Profile);

    /// <summary>One "index this folder's sessions under this profile folder" step of a flush.</summary>
    internal sealed record ClaudeSessionFlush(ClaudeDataRoot Root, string Folder, string AgentName);

    /// <summary>
    /// Ticket c8ee0eec items 10 and 11: which Claude config folder session data is read from and written
    /// to. Three questions, three answers, and no caller builds a <c>.claude</c> path itself:
    /// <list type="bullet">
    ///   <item><see cref="ForProfileName(string)"/>: the caller has a PANE and knows the profile it was
    ///   launched under. That profile's folder only.</item>
    ///   <item><see cref="ForFolder(string)"/> / <see cref="ForProject(string, string)"/>: the caller has
    ///   a project or a working folder. The profile comes from
    ///   <see cref="ClaudeProfileLaunchPolicy.Decide"/>, the same decision a launch in that folder gets,
    ///   so ownership is not re-implemented here. That profile's folder only.</item>
    ///   <item><see cref="AllExisting()"/>: the caller has no context. Every configured folder.</item>
    /// </list>
    /// A caller that MAY have a pane (it holds an agent name, or it is a HUD tab that belongs to a
    /// terminal) asks <see cref="ForPaneOrFolder(ClaudePaneProfile, string)"/> or
    /// <see cref="ForAgentOrFolder(string, string)"/>: the pane's profile when there is one, the folder
    /// decision only when there is not. This matters for a folder no project owns: the folder decision
    /// answers Default there, while the pane may be running under a profile it inherited from its
    /// spawner or that the human picked.
    /// A profile that is not configured, or whose folder is missing, yields null (or is left out of the
    /// list) and is logged once: a reader skips it and a writer refuses. Nothing falls back to Default,
    /// and nothing here creates a folder. Settings are re-read on every call, so a profile added or
    /// removed in Settings takes effect at the next read.
    /// </summary>
    internal static class ClaudeProfileDataRoots
    {
        private const int MaxLoggedMessages = 200;

        private static readonly object Sync = new object();
        private static readonly HashSet<string> Logged = new HashSet<string>(StringComparer.Ordinal);
        private static Action<string> _warn;

        /// <summary>
        /// Called once by MainForm: where each skip/refusal message goes (once per message). The
        /// project registry itself is <see cref="ClaudeProfileProjectLookup"/>, the same lookup a launch
        /// uses; until MainForm has configured that, <see cref="ForFolder(string)"/> and
        /// <see cref="ForProject(string, string)"/> answer null: with no project registry to ask,
        /// "which profile owns this folder" is unknown, and unknown is not Default.
        /// </summary>
        internal static void Configure(Action<string> warn)
        {
            lock (Sync)
                _warn = warn;
        }

        // ── Live machine state ────────────────────────────────────────────────

        /// <summary>Every configured profile folder that exists, Default first. For readers with no
        /// pane, project or folder to go by.</summary>
        internal static IReadOnlyList<ClaudeDataRoot> AllExisting() => AllExisting(ClaudeProfileResolver.CurrentContext());

        /// <summary>The folder of the profile a pane was launched under (null or blank = Default), or
        /// null when that profile is no longer configured or its folder is missing.</summary>
        internal static ClaudeDataRoot ForProfileName(string profileName) =>
            ForProfileName(profileName, ClaudeProfileResolver.CurrentContext());

        /// <summary>The folder of the profile that a launch in <paramref name="folder"/> would run
        /// under, or null when that cannot be decided or honoured.</summary>
        internal static ClaudeDataRoot ForFolder(string folder) => ForProject(null, folder);

        /// <summary>As <see cref="ForFolder(string)"/>, for a caller that also knows the project id (its
        /// database row then decides, as it does for a launch that names the project).</summary>
        internal static ClaudeDataRoot ForProject(string projectId, string folder) =>
            ForProject(projectId, folder, ClaudeProfileProjectLookup.Current(), ClaudeProfileResolver.CurrentContext());

        /// <summary>
        /// For a caller that may have a pane: <paramref name="pane"/>'s profile folder when there is a
        /// pane (null when that profile cannot be honoured; never the folder's profile and never
        /// Default instead), else the folder decision of <see cref="ForFolder(string)"/>.
        /// </summary>
        internal static ClaudeDataRoot ForPaneOrFolder(ClaudePaneProfile pane, string folder) =>
            ForPaneOrFolder(pane, folder, pane == null ? ClaudeProfileProjectLookup.Current() : null, ClaudeProfileResolver.CurrentContext());

        /// <summary>
        /// As <see cref="ForPaneOrFolder(ClaudePaneProfile, string)"/>, for a caller that holds an agent
        /// name: the pane is the live terminal registered under that name
        /// (<see cref="ClaudeProfilePaneLookup.ForAgent(string)"/>), when there is one.
        /// </summary>
        internal static ClaudeDataRoot ForAgentOrFolder(string agentName, string folder) =>
            ForPaneOrFolder(ClaudeProfilePaneLookup.ForAgent(agentName), folder);

        /// <summary>
        /// True when <paramref name="path"/> is a session transcript MT may index: a <c>.jsonl</c> file
        /// inside the <c>projects</c> folder of a configured profile (not anywhere else under the root).
        /// Links are not resolved (ticket 47484cc4).
        /// </summary>
        internal static bool IsSessionTranscriptPath(string path, out string fullPath) =>
            IsSessionTranscriptPath(path, ClaudeProfileResolver.GetAllRoots(), out fullPath);

        /// <summary>
        /// True when <paramref name="path"/> lies inside a configured profile folder (inside its
        /// <paramref name="subfolder"/>, when one is given). The roots come only from MT's settings
        /// (<see cref="ClaudeProfileResolver.GetAllRoots()"/>), never from the path being checked.
        /// </summary>
        /// <param name="fullPath">The canonical form of <paramref name="path"/>, or null when it is malformed.</param>
        internal static bool IsUnderAnyRoot(string path, string subfolder, out string fullPath) =>
            IsUnderAnyRoot(path, subfolder, ClaudeProfileResolver.GetAllRoots(), out fullPath);

        /// <summary>
        /// The <c>.claude.json</c> the DEFAULT profile uses: inside MT's inherited CLAUDE_CONFIG_DIR when
        /// MT was started with one, else <c>%USERPROFILE%\.claude.json</c> (beside the <c>.claude</c>
        /// folder, not inside it).
        /// </summary>
        internal static string DefaultClaudeJsonPath() => DefaultClaudeJsonPath(ClaudeProfileResolver.CurrentContext());

        // ── Pure core ─────────────────────────────────────────────────────────

        internal static IReadOnlyList<ClaudeDataRoot> AllExisting(ClaudeProfileContext ctx)
        {
            // GetAllRoots is the authority on WHICH folders are safe to read (it drops a drive root,
            // the user folder and anything above it); GetAllProfiles supplies the names.
            var allowed = new HashSet<string>(ClaudeProfileResolver.GetAllRoots(ctx), StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<ClaudeDataRoot>();

            foreach (var profile in ClaudeProfileResolver.GetAllProfiles(ctx))
            {
                string dir = ClaudeProfileResolver.Canonicalize(profile.ConfigDir);
                if (dir == null || !allowed.Contains(dir + Path.DirectorySeparatorChar) || !seen.Add(dir))
                    continue;

                if (!Exists(ctx, dir))
                {
                    WarnOnce($"Claude profile '{profile.Name}': folder '{dir}' does not exist; its sessions are skipped.");
                    continue;
                }

                result.Add(new ClaudeDataRoot(profile.Name, dir));
            }

            return result;
        }

        internal static ClaudeDataRoot ForProfileName(string profileName, ClaudeProfileContext ctx)
        {
            var resolution = ClaudeProfileResolver.Resolve(profileName, null, ctx);
            if (!resolution.CanLaunch)
            {
                WarnOnce($"Session data skipped: {resolution.BlockedReason}");
                return null;
            }

            return ToRoot(resolution.Profile, ctx);
        }

        internal static ClaudeDataRoot ForProject(string projectId, string folder, ClaudeProfileLookup lookup, ClaudeProfileContext ctx)
        {
            if (string.IsNullOrWhiteSpace(folder) && string.IsNullOrWhiteSpace(projectId))
                return null;

            if (lookup == null)
            {
                WarnOnce("Session data skipped: the project registry is not available yet, so the Claude profile of a folder cannot be decided.");
                return null;
            }

            var decision = ClaudeProfileLaunchPolicy.Decide(
                projectId, folder, null, $"session data for '{folder ?? projectId}'", lookup,
                name => ClaudeProfileResolver.Resolve(name, null, ctx));

            if (!decision.CanLaunch)
            {
                WarnOnce($"Session data skipped. {decision.Refusal}");
                return null;
            }

            return ToRoot(decision.Profile, ctx);
        }

        internal static ClaudeDataRoot ForPaneOrFolder(ClaudePaneProfile pane, string folder, ClaudeProfileLookup lookup, ClaudeProfileContext ctx)
        {
            if (pane == null)
                return ForProject(null, folder, lookup, ctx);

            if (!pane.Known)
            {
                WarnOnce($"Session data skipped for '{folder}': the Claude profile of its terminal could not be read.");
                return null;
            }

            return ForProfileName(pane.ProfileName, ctx);
        }

        /// <summary>
        /// Pure: the de-duplication key for "this project folder's sessions under this profile folder".
        /// The root is part of it, so two panes in one folder under two profiles are two entries.
        /// Keep the keys in a set built with <see cref="FlushKeyComparer"/>. Null when either part is
        /// missing.
        /// </summary>
        internal static string FlushKey(ClaudeDataRoot root, string folder)
        {
            if (root == null || string.IsNullOrWhiteSpace(folder))
                return null;

            // '|' cannot appear in a Windows path, so the two parts cannot run into each other.
            return root.ConfigDir + "|" + (ClaudeProfileResolver.Canonicalize(folder) ?? folder);
        }

        /// <summary>The comparer a set of <see cref="FlushKey"/> values must use.</summary>
        internal static StringComparer FlushKeyComparer => StringComparer.OrdinalIgnoreCase;

        /// <summary>
        /// Pure: the session flush for a set of running panes. Each pane's sessions are under ITS
        /// profile (<paramref name="rootForPane"/>, production <see cref="ForProfileName(string)"/> on
        /// the pane's profile), once per distinct (profile folder, working folder). A pane with no
        /// recorded launch, or whose profile has no usable folder, is left out and reported to
        /// <paramref name="skipped"/>: it is never flushed under Default or the folder's profile.
        /// </summary>
        internal static IReadOnlyList<ClaudeSessionFlush> PlanPaneFlush(
            IEnumerable<ClaudePaneSnapshot> panes, Func<ClaudePaneProfile, ClaudeDataRoot> rootForPane, Action<string> skipped)
        {
            var plan = new List<ClaudeSessionFlush>();
            var seen = new HashSet<string>(FlushKeyComparer);
            foreach (var pane in panes ?? Array.Empty<ClaudePaneSnapshot>())
            {
                if (pane == null || string.IsNullOrWhiteSpace(pane.Folder))
                    continue;

                string who = string.IsNullOrEmpty(pane.AgentName) ? "an unnamed terminal" : $"'{pane.AgentName}'";
                if (pane.Profile == null || !pane.Profile.Known)
                {
                    skipped?.Invoke($"Session flush skipped for {who} in '{pane.Folder}': the Claude profile it runs under is not known.");
                    continue;
                }

                var root = rootForPane(pane.Profile);
                if (root == null)
                {
                    skipped?.Invoke($"Session flush skipped for {who} in '{pane.Folder}': its Claude profile '{pane.Profile.ProfileName ?? ClaudeProfile.DefaultName}' has no usable folder on this machine.");
                    continue;
                }

                if (seen.Add(FlushKey(root, pane.Folder)))
                    plan.Add(new ClaudeSessionFlush(root, pane.Folder, pane.AgentName));
            }

            return plan;
        }

        internal static bool IsSessionTranscriptPath(string path, IReadOnlyList<string> roots, out string fullPath)
        {
            if (!IsUnderAnyRoot(path, "projects", roots, out fullPath))
            {
                fullPath = null;
                return false;
            }

            // A ':' after the drive letter names an alternate data stream ("notes.txt:x.jsonl"), which
            // would pass the extension check while opening another file's stream.
            if (fullPath.IndexOf(':', 2) >= 0 || !fullPath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            {
                fullPath = null;
                return false;
            }

            return true;
        }

        internal static bool IsUnderAnyRoot(string path, string subfolder, IReadOnlyList<string> roots, out string fullPath)
        {
            fullPath = null;
            if (string.IsNullOrWhiteSpace(path) || roots == null)
                return false;

            try
            {
                if (!Path.IsPathFullyQualified(path))
                    return false;
                fullPath = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return false;
            }

            foreach (string root in roots)
            {
                // Roots end in a separator, and so does the prefix: ".claude\projects\" must not match
                // ".claude\projectsX".
                string prefix = string.IsNullOrEmpty(subfolder) ? root : root + subfolder + Path.DirectorySeparatorChar;
                if (fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        internal static string DefaultClaudeJsonPath(ClaudeProfileContext ctx)
        {
            string inherited = ClaudeProfileResolver.Canonicalize(ctx.InheritedConfigDir);
            if (inherited != null)
                return Path.Combine(inherited, ".claude.json");

            return string.IsNullOrWhiteSpace(ctx.UserProfile) ? null : Path.Combine(ctx.UserProfile, ".claude.json");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static ClaudeDataRoot ToRoot(ClaudeProfile profile, ClaudeProfileContext ctx)
        {
            string dir = ClaudeProfileResolver.GetConfigDir(profile, ctx);
            if (dir == null)
                return null;

            // Resolve has already checked a named profile's folder. Default's is checked here: a
            // machine where Claude Code has never run has no folder to read, and a writer must not
            // be the one that creates it.
            if (!Exists(ctx, dir))
            {
                WarnOnce($"Claude profile '{profile.Name}': folder '{dir}' does not exist; its session data is skipped.");
                return null;
            }

            return new ClaudeDataRoot(profile.Name, dir);
        }

        private static bool Exists(ClaudeProfileContext ctx, string dir)
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

        /// <summary>Several of these readers run on a timer, so each distinct message is reported once
        /// per run of the app (the set is emptied when it fills, which only repeats a message).</summary>
        private static void WarnOnce(string message)
        {
            Action<string> warn;
            lock (Sync)
            {
                if (Logged.Count >= MaxLoggedMessages)
                    Logged.Clear();
                if (!Logged.Add(message))
                    return;
                warn = _warn;
            }

            if (warn != null)
                warn(message);
            else
                System.Diagnostics.Debug.WriteLine($"[ClaudeProfileDataRoots] {message}");
        }
    }
}
