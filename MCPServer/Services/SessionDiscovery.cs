using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using MultiTerminal.Services;

namespace MultiTerminal.MCPServer.Services
{
    /// <summary>
    /// Represents a discovered Claude Code session with identity information.
    /// </summary>
    public class DiscoveredSession
    {
        public string SessionId { get; set; }
        public string IdentityName { get; set; }
        public string ProjectPath { get; set; }
        public string FirstPrompt { get; set; }
        public string Summary { get; set; }
        public DateTime Created { get; set; }
        public DateTime Modified { get; set; }
        public int MessageCount { get; set; }

        /// <summary>The Claude profile whose config folder the transcript was found in ("Default" for
        /// the built-in one; ticket c8ee0eec). Null when the discovery was pointed at one fixed folder.</summary>
        public string ClaudeProfile { get; set; }
    }

    /// <summary>
    /// Discovers Claude Code sessions and matches them to MultiTerminal identities.
    ///
    /// <para>WHICH config folder (ticket c8ee0eec): a call that names a project looks only in the
    /// folder of the Claude profile that project runs under; the one call that names none
    /// (<see cref="DiscoverAllSessionsForIdentity"/>) looks in every configured profile's folder and
    /// keeps one row per session id.</para>
    ///
    /// <para>Source of truth is the raw session JSONL under
    /// <c>&lt;claude config folder&gt;/projects/&lt;proj&gt;/*.jsonl</c>. Each <c>&lt;uuid&gt;.jsonl</c>
    /// file IS a session; its metadata is derived on demand via
    /// <see cref="SessionSyncService.ParseSessionFile"/>. A
    /// <c>sessions-index.json</c> is used ONLY as a fast-path when it is present
    /// AND fresh (newer than every JSONL in the folder) — the Claude CLI stopped
    /// writing that index in early 2026, so in practice discovery derives from
    /// JSONL and the index is transparently ignored once stale (task cd8ca48c).</para>
    ///
    /// <para>Derivation is mtime-gated in-process (a static per-file cache keyed
    /// by path+mtime) so repeated calls re-parse only the JSONL files that
    /// actually changed — this class is instantiated ad-hoc per call, so the
    /// cache is static to survive across those instances.</para>
    ///
    /// <para>Session METADATA is derived from the transcript, but the terminal
    /// IDENTITY is resolved from the authoritative <c>session_agent_map</c> store via
    /// an injected <c>Func&lt;sessionId,string&gt;</c> (a narrow resolver, not a raw
    /// DB connection) — the transcript does not reliably carry the terminal's own
    /// name. Transcript parsing (<see cref="ExtractIdentity"/>) is the fallback when
    /// no resolver is wired or a session is unknown to it (task 4558fa6b).</para>
    /// </summary>
    public class SessionDiscovery
    {
        // A projects folder plus the profile it belongs to (null for the fixed-folder constructor).
        private sealed record ScanRoot(string ProjectsDir, string ProfileName);

        // Every projects folder to scan when the caller names no project.
        private readonly Func<IReadOnlyList<ScanRoot>> _allRoots;

        // Project path → the one projects folder that project's sessions live in, or null (its profile
        // is not configured here, has no folder, or cannot be decided: nothing is read).
        private readonly Func<string, ScanRoot> _rootForProject;

        // Used only for ParseSessionFile, which takes a full path and needs no root.
        private readonly SessionSyncService _sync = new SessionSyncService();

        // Authoritative identity source: maps a session id to the terminal identity
        // that owned it (register_session -> session_agent_map; see TaskDatabase.
        // GetSessionAgentName). Injected as a narrow Func so the discovery layer
        // stays testable and takes no raw DB connection. Null in contexts without
        // the DB (tests, headless) — then identity falls back to transcript parsing.
        private readonly Func<string, string> _identityResolver;

        // NOTE (task 4558fa6b, security): identity ownership is NOT inferred from
        // free-form transcript text. Two spoofable patterns were removed — a leading
        // "[Alice]:" (means Alice SENT to this terminal, not that it IS Alice) and a
        // free-form "register as X" (any transcript containing "please register as
        // Alice" would self-attribute). Both let a foreign/unregistered/crafted
        // transcript spoof a team identity in the dropdown. Ownership now comes from
        // the authoritative session_agent_map resolver; the ONLY transcript signal
        // still trusted is the system-generated SessionStart hook marker below.

        // The system-generated hook injection ("MULTITERMINAL: You are registered as
        // X"). Unlike free-form prose this is emitted by MT's own SessionStart hook,
        // so it's a trusted ownership marker (fallback for sessions the resolver
        // doesn't know); genuinely-unknown sessions resolve to null (Unknown).
        private static readonly Regex SystemHookPattern = new Regex(
            @"MULTITERMINAL:\s*You\s+are\s+registered\s+as\s+(\w+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // A Claude session transcript is named "<session-uuid>.jsonl". Anything
        // else in the project folder (partial writes with odd names, tooling
        // scratch) is NOT a session — the misclassification guard rejects it.
        private static readonly Regex SessionFileNamePattern = new Regex(
            @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            RegexOptions.Compiled);

        // Process-wide, mtime-gated derivation cache. Keyed by absolute JSONL
        // path; the entry is reused only while the file's mtime is unchanged.
        private static readonly Dictionary<string, CachedJsonlEntry> JsonlCache =
            new Dictionary<string, CachedJsonlEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly object CacheLock = new object();

        // Bound the derivation cost + the process-wide cache so a large or hostile
        // project folder can't drive unbounded parse/memory growth. These are
        // generous relative to real usage (the projects folder is the user's own
        // local transcripts) but cap the pathological case.
        private const long MaxJsonlBytes = 64L * 1024 * 1024; // 64 MB per transcript
        private const int MaxCacheEntries = 4000;

        /// <summary>Reads from the configured Claude profile folders, decided at each call.</summary>
        public SessionDiscovery(Func<string, string> identityResolver = null)
            : this(ClaudeProfileDataRoots.AllExisting, ClaudeProfileDataRoots.ForFolder, identityResolver)
        {
        }

        /// <summary>Points the discovery at ONE projects folder, whatever profiles are configured.</summary>
        public SessionDiscovery(string claudeProjectsPath, Func<string, string> identityResolver = null)
        {
            var only = new ScanRoot(claudeProjectsPath, null);
            _allRoots = () => new[] { only };
            _rootForProject = _ => only;
            _identityResolver = identityResolver;
        }

        /// <summary>Test seam for the profile-aware paths: the config folders to scan when no project is
        /// named, and the config folder for a project path (null = none).</summary>
        internal SessionDiscovery(
            Func<IReadOnlyList<ClaudeDataRoot>> allRoots,
            Func<string, ClaudeDataRoot> rootForProject,
            Func<string, string> identityResolver = null)
        {
            if (allRoots == null) throw new ArgumentNullException(nameof(allRoots));
            if (rootForProject == null) throw new ArgumentNullException(nameof(rootForProject));

            _allRoots = () => allRoots().Select(r => new ScanRoot(r.ProjectsDir, r.ProfileName)).ToList();
            _rootForProject = projectPath =>
            {
                var root = rootForProject(projectPath);
                return root == null ? null : new ScanRoot(root.ProjectsDir, root.ProfileName);
            };
            _identityResolver = identityResolver;
        }

        /// <summary>
        /// Convert a project path to Claude's folder name format.
        /// Example: "H:\DevLaptop\Project" -> "H--DevLaptop-Project"
        /// </summary>
        public static string GetProjectFolderName(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath))
                return null;

            return projectPath
                .Replace(":\\", "--")
                .Replace("\\", "-")
                .Replace("/", "-");
        }

        /// <summary>
        /// Extract the terminal's OWN identity from a firstPrompt using ONLY the
        /// system-generated SessionStart hook marker ("MULTITERMINAL: You are
        /// registered as X"). Free-form transcript patterns are deliberately NOT used
        /// for ownership — neither the received-message "[Alice]:" (Alice sent here ≠
        /// is Alice) nor a free-form "register as X" (spoofable by incidental prose) —
        /// because a foreign/crafted transcript could otherwise impersonate a team
        /// identity (task 4558fa6b, security). Returns null when the trusted marker is
        /// absent; callers MUST treat null as "unknown" (the authoritative owner comes
        /// from session_agent_map, not the transcript).
        /// </summary>
        public static string ExtractIdentity(string firstPrompt)
        {
            if (string.IsNullOrEmpty(firstPrompt))
                return null;

            // The only trusted transcript signal: MT's own SessionStart hook injection.
            var hookMatch = SystemHookPattern.Match(firstPrompt);
            if (hookMatch.Success)
                return hookMatch.Groups[1].Value;

            return null;
        }

        /// <summary>
        /// Resolve the terminal identity that owned a session. The AUTHORITATIVE
        /// source is <c>session_agent_map</c> (via the injected resolver, keyed by
        /// session id) — MT records the identity at register_session time, whereas
        /// the transcript does NOT reliably contain the terminal's own name (the
        /// SessionStart-hook block is the literal <c>MULTITERMINAL_NAME=&lt;name&gt;</c>
        /// placeholder, and a leading "[Alice]:" means Alice SENT to this terminal,
        /// not that the terminal IS Alice). Transcript parsing
        /// (<see cref="ExtractIdentity"/>) is therefore only a fallback for sessions
        /// the resolver doesn't know (foreign/unregistered), or when no resolver is
        /// wired (tests/headless).
        /// </summary>
        private string ResolveIdentity(SessionEntry entry)
        {
            if (_identityResolver != null && !string.IsNullOrEmpty(entry.SessionId))
            {
                try
                {
                    var resolved = _identityResolver(entry.SessionId);
                    if (!string.IsNullOrWhiteSpace(resolved))
                        return resolved;
                }
                catch (Exception ex)
                {
                    // A resolver failure (e.g. transient DB unavailability) must degrade
                    // THIS session to transcript fallback — never abort the whole
                    // discovery pass and blank every identity.
                    Debug.WriteLine($"[SessionDiscovery] identity resolver threw for {entry.SessionId}: {ex.Message}");
                }
            }
            return ExtractIdentity(entry.FirstPrompt);
        }

        /// <summary>
        /// Discover sessions for a specific identity in a specific project.
        /// Returns sessions sorted by modified date (most recent first).
        /// </summary>
        public List<DiscoveredSession> DiscoverSessionsForIdentity(string identityName, string projectPath)
        {
            var results = new List<DiscoveredSession>();

            foreach (var entry in GetProjectSessionEntries(projectPath, out string profileName))
            {
                var extractedIdentity = ResolveIdentity(entry);
                if (extractedIdentity != null &&
                    extractedIdentity.Equals(identityName, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(ToDiscoveredSession(entry, extractedIdentity, projectPath, profileName));
                }
            }

            return results.OrderByDescending(s => s.Modified).ToList();
        }

        /// <summary>
        /// Discover the most recent session for an identity in a project.
        /// </summary>
        public DiscoveredSession DiscoverLatestSession(string identityName, string projectPath)
        {
            var sessions = DiscoverSessionsForIdentity(identityName, projectPath);
            return sessions.FirstOrDefault();
        }

        /// <summary>
        /// Discover ALL sessions in a project (no identity filter, one
        /// <see cref="DiscoveredSession"/> per JSONL transcript). The row count
        /// this returns equals the number of valid session JSONL files in the
        /// project folder — the observable freshness invariant the JSONL-derived
        /// index is verified against.
        /// </summary>
        public List<DiscoveredSession> DiscoverAllSessionsInProject(string projectPath)
        {
            return GetProjectSessionEntries(projectPath, out string profileName)
                .Select(e => ToDiscoveredSession(e, ResolveIdentity(e), projectPath, profileName))
                .OrderByDescending(s => s.Modified)
                .ToList();
        }

        /// <summary>
        /// Discover all sessions for an identity across all projects, in every configured Claude
        /// profile's folder (the caller names no project, so there is no single profile to ask).
        /// One row per session id: when the same id has a transcript under two profile folders (a
        /// copied config folder), the more recently modified transcript is kept, and on an exact tie
        /// the one from the earlier root (Default first). The row's
        /// <see cref="DiscoveredSession.ClaudeProfile"/> says which folder it came from.
        /// </summary>
        public List<DiscoveredSession> DiscoverAllSessionsForIdentity(string identityName)
        {
            var bySessionId = new Dictionary<string, DiscoveredSession>(StringComparer.OrdinalIgnoreCase);
            var withoutId = new List<DiscoveredSession>();

            foreach (var root in _allRoots())
            {
                string[] projectFolders;
                try
                {
                    if (string.IsNullOrEmpty(root.ProjectsDir) || !Directory.Exists(root.ProjectsDir))
                        continue;
                    projectFolders = Directory.GetDirectories(root.ProjectsDir);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Debug.WriteLine($"[SessionDiscovery] cannot list {root.ProjectsDir}: {ex.Message}");
                    continue;
                }

                foreach (var projectFolder in projectFolders)
                {
                    foreach (var entry in GetEntriesForFolder(projectFolder, null))
                    {
                        var extractedIdentity = ResolveIdentity(entry);
                        if (extractedIdentity == null ||
                            !extractedIdentity.Equals(identityName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var session = ToDiscoveredSession(entry, extractedIdentity, null, root.ProfileName);
                        if (string.IsNullOrEmpty(session.SessionId))
                            withoutId.Add(session);
                        else if (!bySessionId.TryGetValue(session.SessionId, out var kept) || session.Modified > kept.Modified)
                            bySessionId[session.SessionId] = session;
                    }
                }
            }

            return bySessionId.Values.Concat(withoutId).OrderByDescending(s => s.Modified).ToList();
        }

        /// <summary>
        /// Scan a project's sessions and return all identities found.
        /// Useful for discovering which identities have been used in a project.
        /// </summary>
        public Dictionary<string, DiscoveredSession> DiscoverIdentitiesInProject(string projectPath)
        {
            var identities = new Dictionary<string, DiscoveredSession>(StringComparer.OrdinalIgnoreCase);

            var entries = GetProjectSessionEntries(projectPath, out string profileName);
            foreach (var entry in entries.OrderByDescending(e => ParseDateTime(e.Modified)))
            {
                var extractedIdentity = ResolveIdentity(entry);
                if (extractedIdentity != null && !identities.ContainsKey(extractedIdentity))
                {
                    identities[extractedIdentity] = ToDiscoveredSession(entry, extractedIdentity, projectPath, profileName);
                }
            }

            // Observable signal: make "fresh sessions but zero identities" loud so it
            // isn't mistaken for "no sessions." With the resolver wired this should be
            // rare — it means none of the sessions had a session_agent_map owner AND
            // none carried an ownership-safe transcript marker.
            if (identities.Count == 0 && entries.Count > 0)
            {
                Debug.WriteLine(
                    $"[SessionDiscovery] {entries.Count} sessions in '{projectPath}' but 0 identities resolved — no session_agent_map owner (resolver) and no ownership-safe transcript marker.");
            }

            return identities;
        }

        /// <summary>
        /// Resolve the session entries for a project path — fresh index fast-path
        /// or JSONL derivation. Internal so the falsifiable file-count == row-count
        /// test can assert against the raw entry set.
        /// </summary>
        internal List<SessionEntry> GetProjectSessionEntries(string projectPath) =>
            GetProjectSessionEntries(projectPath, out _);

        /// <summary>
        /// As above, also reporting which Claude profile's folder was read. The project's sessions are
        /// looked for ONLY in the folder of the profile it runs under; when that profile has no usable
        /// folder the result is empty rather than another profile's sessions.
        /// </summary>
        private List<SessionEntry> GetProjectSessionEntries(string projectPath, out string profileName)
        {
            profileName = null;
            var folderName = GetProjectFolderName(projectPath);
            if (folderName == null)
                return new List<SessionEntry>();

            var root = _rootForProject(projectPath);
            if (root == null || string.IsNullOrEmpty(root.ProjectsDir))
                return new List<SessionEntry>();

            profileName = root.ProfileName;
            var projectFolder = Path.Combine(root.ProjectsDir, folderName);
            if (!Directory.Exists(projectFolder))
                return new List<SessionEntry>();

            return GetEntriesForFolder(projectFolder, projectPath);
        }

        /// <summary>
        /// Core resolver: use <c>sessions-index.json</c> only when it is BOTH fresh
        /// AND complete (see <see cref="IndexIsTrustworthy"/>); otherwise derive from
        /// the raw JSONL transcripts. Logs which path served.
        /// </summary>
        private List<SessionEntry> GetEntriesForFolder(string projectFolder, string fallbackProjectPath)
        {
            var indexPath = Path.Combine(projectFolder, "sessions-index.json");
            var indexEntries = TryLoadIndexEntries(indexPath);
            if (indexEntries != null && IndexIsTrustworthy(projectFolder, indexPath, indexEntries))
            {
                Debug.WriteLine(
                    $"[SessionDiscovery] {Path.GetFileName(projectFolder)}: served {indexEntries.Count} entries from FRESH+COMPLETE sessions-index.json");
                return indexEntries;
            }

            var derived = DeriveEntriesFromJsonl(projectFolder, fallbackProjectPath);
            Debug.WriteLine(
                $"[SessionDiscovery] {Path.GetFileName(projectFolder)}: served {derived.Count} entries DERIVED from JSONL (index stale/incomplete/absent)");
            return derived;
        }

        /// <summary>
        /// The index may be trusted as a fast-path ONLY when it is both fresh AND
        /// complete:
        /// <list type="bullet">
        /// <item>FRESH — STRICTLY newer than every JSONL in the folder. An mtime tie
        /// is treated as stale: a JSONL written in the same clock tick as the index
        /// may not be reflected in it.</item>
        /// <item>COMPLETE — every session-UUID JSONL on disk appears in the index. A
        /// fresh-but-partial index (regenerated, lagging, or clock-skewed) would
        /// otherwise be served as authoritative and silently HIDE real sessions —
        /// exactly the source-of-truth hole this repair exists to close. Freshness
        /// (mtime) alone is not a proxy for coverage.</item>
        /// </list>
        /// Either check failing → derive from JSONL.
        /// </summary>
        private static bool IndexIsTrustworthy(string projectFolder, string indexPath, List<SessionEntry> indexEntries)
        {
            long indexMtime;
            try
            {
                indexMtime = File.GetLastWriteTimeUtc(indexPath).Ticks;
            }
            catch
            {
                return false;
            }

            var indexedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in indexEntries)
            {
                if (!string.IsNullOrEmpty(e.SessionId))
                    indexedIds.Add(e.SessionId);
            }

            var diskIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var jsonl in Directory.EnumerateFiles(projectFolder, "*.jsonl"))
                {
                    // Tie or newer JSONL => the index is stale.
                    if (File.GetLastWriteTimeUtc(jsonl).Ticks >= indexMtime)
                        return false;

                    // A session JSONL the index doesn't list => the index is incomplete.
                    var id = Path.GetFileNameWithoutExtension(jsonl);
                    if (SessionFileNamePattern.IsMatch(id))
                    {
                        diskIds.Add(id);
                        if (!indexedIds.Contains(id))
                            return false;
                    }
                }
            }
            catch
            {
                return false;
            }

            // Trust must be EXACT, not just a superset: an index entry whose JSONL
            // no longer exists on disk is a phantom session (deleted/stale) — since
            // JSONL is the source of truth, returning it would fabricate a session
            // with no transcript. Any indexed id not backed by a file => derive.
            foreach (var id in indexedIds)
            {
                if (!diskIds.Contains(id))
                    return false;
            }

            return true;
        }

        private static List<SessionEntry> TryLoadIndexEntries(string indexPath)
        {
            try
            {
                var json = File.ReadAllText(indexPath);
                var index = JsonSerializer.Deserialize<SessionsIndex>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                return index?.Entries;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionDiscovery] Error reading {indexPath}: {ex.Message}");
                return null;
            }
        }

        private List<SessionEntry> DeriveEntriesFromJsonl(string projectFolder, string fallbackProjectPath)
        {
            var entries = new List<SessionEntry>();

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(projectFolder, "*.jsonl");
            }
            catch
            {
                return entries;
            }

            foreach (var jsonlPath in files)
            {
                var entry = BuildEntryFromJsonl(jsonlPath, fallbackProjectPath);
                if (entry != null)
                    entries.Add(entry);
            }

            return entries;
        }

        /// <summary>
        /// Derive one <see cref="SessionEntry"/> from a single JSONL transcript.
        /// Returns null when the file is not a session (name isn't a UUID, or it
        /// has no parseable messages — the schema sniff). A torn last line is
        /// tolerated by <see cref="SessionSyncService.ParseSessionFile"/>, which
        /// skips unparseable lines rather than failing the whole file.
        /// </summary>
        private SessionEntry BuildEntryFromJsonl(string jsonlPath, string fallbackProjectPath)
        {
            var sessionId = Path.GetFileNameWithoutExtension(jsonlPath);
            if (string.IsNullOrEmpty(sessionId) || !SessionFileNamePattern.IsMatch(sessionId))
                return null;

            long mtimeTicks;
            long sizeBytes;
            try
            {
                var fi = new FileInfo(jsonlPath);
                mtimeTicks = fi.LastWriteTimeUtc.Ticks;
                sizeBytes = fi.Length;
            }
            catch
            {
                return null;
            }

            // Resource-exhaustion guard: skip a pathologically large transcript
            // rather than fully materializing it (CWE-400). Bounded by the same
            // mtime key, so a later shrink re-derives normally.
            if (sizeBytes > MaxJsonlBytes)
            {
                Debug.WriteLine(
                    $"[SessionDiscovery] skipping oversized transcript ({sizeBytes} bytes > {MaxJsonlBytes}): {Path.GetFileName(jsonlPath)}");
                return null;
            }

            // mtime-gated cache: reuse the derived entry while the file is unchanged.
            lock (CacheLock)
            {
                if (JsonlCache.TryGetValue(jsonlPath, out var cached) && cached.MtimeTicks == mtimeTicks)
                    return cached.Entry;
            }

            var messages = _sync.ParseSessionFile(jsonlPath);
            if (messages == null || messages.Count == 0)
                return null; // schema sniff: nothing parseable -> not a usable session

            string firstPrompt = null;
            DateTime created = DateTime.MinValue;
            foreach (var m in messages)
            {
                if (created == DateTime.MinValue && m.Timestamp != default)
                    created = m.Timestamp;
                if (firstPrompt == null &&
                    string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(m.Content))
                {
                    firstPrompt = m.Content;
                }
            }
            firstPrompt ??= messages[0].Content;

            DateTime modified;
            try
            {
                modified = File.GetLastWriteTime(jsonlPath);
            }
            catch
            {
                modified = created;
            }

            if (created == DateTime.MinValue)
            {
                try
                {
                    created = File.GetCreationTime(jsonlPath);
                }
                catch
                {
                    created = modified;
                }
            }

            var entry = new SessionEntry
            {
                SessionId = sessionId,
                FullPath = jsonlPath,
                FileMtime = mtimeTicks,
                FirstPrompt = firstPrompt,
                Summary = null, // derived: CLI summary generation is out of scope
                MessageCount = messages.Count,
                Created = created.ToString("o"),
                Modified = modified.ToString("o"),
                ProjectPath = fallbackProjectPath,
                IsSidechain = false
            };

            lock (CacheLock)
            {
                // Bound process-wide growth. A full clear is acceptable: the mtime
                // gate re-derives on demand, so this only forfeits a one-time reparse
                // of whatever was cached — never correctness.
                if (JsonlCache.Count >= MaxCacheEntries)
                    JsonlCache.Clear();
                JsonlCache[jsonlPath] = new CachedJsonlEntry { MtimeTicks = mtimeTicks, Entry = entry };
            }

            return entry;
        }

        private static DiscoveredSession ToDiscoveredSession(SessionEntry entry, string identity, string fallbackProjectPath, string profileName)
        {
            return new DiscoveredSession
            {
                SessionId = entry.SessionId,
                IdentityName = identity,
                ProjectPath = entry.ProjectPath ?? fallbackProjectPath,
                FirstPrompt = entry.FirstPrompt,
                Summary = entry.Summary,
                Created = ParseDateTime(entry.Created),
                Modified = ParseDateTime(entry.Modified),
                MessageCount = entry.MessageCount,
                ClaudeProfile = profileName
            };
        }

        private static DateTime ParseDateTime(string dateStr)
        {
            if (DateTime.TryParse(dateStr, out var result))
                return result;
            return DateTime.MinValue;
        }

        #region JSON Models

        private sealed class CachedJsonlEntry
        {
            public long MtimeTicks { get; set; }
            public SessionEntry Entry { get; set; }
        }

        private class SessionsIndex
        {
            public int Version { get; set; }
            public List<SessionEntry> Entries { get; set; }
            public string OriginalPath { get; set; }
        }

        internal class SessionEntry
        {
            public string SessionId { get; set; }
            public string FullPath { get; set; }
            public long FileMtime { get; set; }
            public string FirstPrompt { get; set; }
            public string Summary { get; set; }
            public int MessageCount { get; set; }
            public string Created { get; set; }
            public string Modified { get; set; }
            public string GitBranch { get; set; }
            public string ProjectPath { get; set; }
            public bool IsSidechain { get; set; }
        }

        #endregion
    }
}
