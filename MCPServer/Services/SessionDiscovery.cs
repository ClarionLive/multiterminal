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
    }

    /// <summary>
    /// Discovers Claude Code sessions and matches them to MultiTerminal identities.
    ///
    /// <para>Source of truth is the raw session JSONL under
    /// <c>~/.claude/projects/&lt;proj&gt;/*.jsonl</c>. Each <c>&lt;uuid&gt;.jsonl</c>
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
        private readonly string _claudeProjectsPath;
        private readonly SessionSyncService _sync = new SessionSyncService();

        // Authoritative identity source: maps a session id to the terminal identity
        // that owned it (register_session -> session_agent_map; see TaskDatabase.
        // GetSessionAgentName). Injected as a narrow Func so the discovery layer
        // stays testable and takes no raw DB connection. Null in contexts without
        // the DB (tests, headless) — then identity falls back to transcript parsing.
        private readonly Func<string, string> _identityResolver;

        // NOTE (task 4558fa6b, security): a leading "[Alice]:" in a transcript means
        // Alice SENT a message to this terminal — NOT that this terminal IS Alice.
        // Using it to attribute ownership mis-labels any session the resolver can't
        // validate (foreign/unregistered/crafted), so the received-message pattern is
        // deliberately NOT part of identity determination. Only ownership-safe markers
        // below (self-registration / system-hook) are used.

        // Pattern 1: Explicit registration instruction
        private static readonly Regex RegisterPattern = new Regex(
            @"register\s+(?:as|with\s+name)\s+[""']?(\w+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Pattern 2: System hook injection (if it appears in firstPrompt)
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

        public SessionDiscovery(Func<string, string> identityResolver = null)
        {
            // Default Claude projects path
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _claudeProjectsPath = Path.Combine(userProfile, ".claude", "projects");
            _identityResolver = identityResolver;
        }

        public SessionDiscovery(string claudeProjectsPath, Func<string, string> identityResolver = null)
        {
            _claudeProjectsPath = claudeProjectsPath;
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
        /// Extract the terminal's OWN identity from a firstPrompt using only
        /// ownership-safe markers: the system-hook "registered as X" injection and
        /// an explicit "register as X" instruction. The received-message pattern
        /// ("[Alice]:") is deliberately NOT used — it means Alice sent to this
        /// terminal, not that the terminal is Alice (task 4558fa6b, security). Returns
        /// null when no ownership-safe marker is present; callers must treat null as
        /// "unknown", never guessing an owner from who messaged the session.
        /// </summary>
        public static string ExtractIdentity(string firstPrompt)
        {
            if (string.IsNullOrEmpty(firstPrompt))
                return null;

            // System-hook injection: "MULTITERMINAL: You are registered as X" — the
            // terminal IS X (ownership-safe).
            var hookMatch = SystemHookPattern.Match(firstPrompt);
            if (hookMatch.Success)
                return hookMatch.Groups[1].Value;

            // Explicit self-registration: "register as X" — the terminal IS X
            // (ownership-safe).
            var registerMatch = RegisterPattern.Match(firstPrompt);
            if (registerMatch.Success)
                return registerMatch.Groups[1].Value;

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

            foreach (var entry in GetProjectSessionEntries(projectPath))
            {
                var extractedIdentity = ResolveIdentity(entry);
                if (extractedIdentity != null &&
                    extractedIdentity.Equals(identityName, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(ToDiscoveredSession(entry, extractedIdentity, projectPath));
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
            return GetProjectSessionEntries(projectPath)
                .Select(e => ToDiscoveredSession(e, ResolveIdentity(e), projectPath))
                .OrderByDescending(s => s.Modified)
                .ToList();
        }

        /// <summary>
        /// Discover all sessions for an identity across all projects.
        /// </summary>
        public List<DiscoveredSession> DiscoverAllSessionsForIdentity(string identityName)
        {
            var results = new List<DiscoveredSession>();

            if (!Directory.Exists(_claudeProjectsPath))
                return results;

            foreach (var projectFolder in Directory.GetDirectories(_claudeProjectsPath))
            {
                foreach (var entry in GetEntriesForFolder(projectFolder, null))
                {
                    var extractedIdentity = ResolveIdentity(entry);
                    if (extractedIdentity != null &&
                        extractedIdentity.Equals(identityName, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(ToDiscoveredSession(entry, extractedIdentity, null));
                    }
                }
            }

            return results.OrderByDescending(s => s.Modified).ToList();
        }

        /// <summary>
        /// Scan a project's sessions and return all identities found.
        /// Useful for discovering which identities have been used in a project.
        /// </summary>
        public Dictionary<string, DiscoveredSession> DiscoverIdentitiesInProject(string projectPath)
        {
            var identities = new Dictionary<string, DiscoveredSession>(StringComparer.OrdinalIgnoreCase);

            var entries = GetProjectSessionEntries(projectPath);
            foreach (var entry in entries.OrderByDescending(e => ParseDateTime(e.Modified)))
            {
                var extractedIdentity = ResolveIdentity(entry);
                if (extractedIdentity != null && !identities.ContainsKey(extractedIdentity))
                {
                    identities[extractedIdentity] = ToDiscoveredSession(entry, extractedIdentity, projectPath);
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
        internal List<SessionEntry> GetProjectSessionEntries(string projectPath)
        {
            var folderName = GetProjectFolderName(projectPath);
            if (folderName == null)
                return new List<SessionEntry>();

            var projectFolder = Path.Combine(_claudeProjectsPath, folderName);
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

        private static DiscoveredSession ToDiscoveredSession(SessionEntry entry, string identity, string fallbackProjectPath)
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
                MessageCount = entry.MessageCount
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
