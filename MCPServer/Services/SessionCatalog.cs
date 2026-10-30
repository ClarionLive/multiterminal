using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MultiTerminal.MCPServer.Services
{
    /// <summary>
    /// Whether a past session looks safe to resume from this MultiTerminal.
    /// </summary>
    public enum SessionCatalogStatus
    {
        /// <summary>The transcript has been quiet for at least <see cref="SessionCatalog.RecentWriteWindow"/>.</summary>
        Available = 0,

        /// <summary>The transcript was written within <see cref="SessionCatalog.RecentWriteWindow"/>: some other window may still own it.</summary>
        PossiblyOpenElsewhere = 1,
    }

    /// <summary>
    /// One resumable Claude Code session, derived from its transcript on disk (task ec2ed636).
    /// </summary>
    public sealed class SessionCatalogEntry
    {
        public string SessionId { get; set; }

        // The directory a `claude --resume <SessionId>` must be started from (see SessionCatalog.ResolveCwd).
        public string Cwd { get; set; }

        public string TranscriptPath { get; set; }

        // Transcript file last-write time, UTC.
        public DateTime LastActivityUtc { get; set; }

        // Null when unknown.
        public string AgentName { get; set; }

        public bool IsHelper { get; set; }

        // Null when unknown or not a helper.
        public string SpawnedBy { get; set; }

        // One line, at most 160 chars; null when nothing usable was found.
        public string Summary { get; set; }

        // e.g. "5d94ac91" when Cwd is under <projectRoot>\.claude\worktrees\<id> (or the repo root's), else null.
        public string WorktreeId { get; set; }

        // The git repo root whose .claude\worktrees the cwd may be under, when the project folder is a
        // subfolder of its repo (ticket 3fd3c9a7), else null. Derived from the project root, never from
        // the transcript; pass it back to SessionCatalog.ValidateResumeCwd when re-checking the cwd.
        public string WorktreeBase { get; set; }

        public SessionCatalogStatus Status { get; set; }

        // Context in use at the session's LAST main-thread assistant turn: input_tokens +
        // cache_read_input_tokens + cache_creation_input_tokens of that turn's message.usage. Null when
        // the bounded tail holds no such turn (see SessionCatalog.TryReadContextUsage).
        public int? ContextTokens { get; set; }

        // The context window of the model that produced that turn (SessionCatalog.ResolveContextWindow),
        // or null when the model is unrecognised or the tokens exceed the guessed window.
        public int? ContextWindow { get; set; }
    }

    /// <summary>
    /// Lists the past Claude Code sessions of a project that can be offered for resume at launch
    /// (task ec2ed636).
    ///
    /// <para>The source of truth is Claude Code's own transcripts,
    /// <c>~/.claude/projects/&lt;encoded dir&gt;/&lt;sessionId&gt;.jsonl</c>, NOT MT's
    /// <c>session_lineage</c> table: lineage has no rows for spawned helpers and keeps a stale
    /// transcript path once a session runs EnterWorktree (Claude Code then MOVES the transcript into
    /// the worktree's folder). So this scans the project root's folder plus every
    /// <c>&lt;encoded root&gt;--claude-worktrees-*</c> folder.</para>
    ///
    /// <para>Transcripts can exceed 100MB, so only a bounded HEAD and TAIL of each file is read.
    /// No database access happens here: the agent-name and summary lookups are injected, which is
    /// what makes the class unit-testable against synthetic transcript folders.</para>
    /// </summary>
    public sealed class SessionCatalog
    {
        internal const int HeadMaxLines = 200;
        internal const int HeadMaxBytes = 256 * 1024;
        internal const int TailBytes = 64 * 1024;
        internal const int SummaryMaxChars = 160;

        /// <summary>
        /// Second, larger tail read, made only when the <see cref="TailBytes"/> window holds no
        /// assistant turn with usage (typically one big tool result at the end). Measured
        /// 2026-10-18 on 108 real MT transcripts: 3 had no usage in the last 64KB, 1 in the last 512KB.
        /// </summary>
        internal const int ContextTailBytes = 512 * 1024;

        /// <summary>
        /// How far into a HELPER's transcript to look for its get_my_spawn_job result. Measured
        /// 2026-10-18 on five real helper transcripts (c133b27d, e20e2ce0): the result sits at lines
        /// 53-55, 630-690KB in, behind the SessionStart hook output, so the 256KB head never reaches it.
        /// </summary>
        internal const int SpawnJobScanMaxBytes = 1536 * 1024;

        internal const int SpawnJobScanMaxLines = 400;

        public const int StandardContextWindow = 200_000;

        public const int LargeContextWindow = 1_000_000;

        // claude-<family>-<major>[-<minor>], where a minor is one or two digits (an 8-digit date
        // suffix such as claude-sonnet-4-20250514 is not a minor version).
        // WHEN A NEW MODEL ID SHIPS: a new family name goes into the alternation below (an unknown
        // family is null, so the dialog shows tokens with no window); a window that differs from the
        // rules in ContextWindowFor is changed there, together with the
        // SessionCatalogTests.Context_window_from_the_model_id theory.
        private static readonly Regex ModelIdPattern = new Regex(
            @"^claude-(?<family>opus|sonnet|haiku|fable|mythos)-(?<major>\d{1,2})(?:-(?<minor>\d{1,2})(?!\d))?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Pre-4 naming: claude-3-5-sonnet-20241022, claude-3-opus-20240229.
        private static readonly Regex LegacyModelIdPattern = new Regex(
            @"^claude-[1-3](?:-\d)?-(?:opus|sonnet|haiku)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // mcp/index.js get_my_spawn_job: "<clipboard emoji> YOUR JOB from <spawner> (you are <name>). ...\n\n<job>".
        private static readonly Regex SpawnJobHeaderPattern = new Regex(
            @"^\W*YOUR JOB from\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex NameMarkerPattern = new Regex(
            @"^\s*MULTITERMINAL_NAME=([^\s'""<>`]+)",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex TerminalIdentityPattern = new Regex(
            @"Terminal Identity:\s*([^\s'""<>`]+)",
            RegexOptions.Compiled);

        private static readonly Regex SpawnedAgentPattern = new Regex(
            @"^\s*##\s*Spawned Agent:\s*([^\s'""<>`]+)",
            RegexOptions.Compiled | RegexOptions.Multiline);

        // Real helper transcript (5b8431ec, 2026-10-16): "Spawned by: Alice" and
        // "[DEBUG] MULTITERMINAL_SPAWNER = 'Alice' (isSpawnedAgent: true)". A non-helper prints
        // MULTITERMINAL_SPAWNER = 'undefined', which must not count.
        private static readonly Regex SpawnedByPattern = new Regex(
            @"Spawned by:\s*([^\s'""<>`]+)",
            RegexOptions.Compiled);

        private static readonly Regex SpawnerEnvPattern = new Regex(
            @"MULTITERMINAL_SPAWNER\s*=\s*'?([^\s'""<>`]+)'?",
            RegexOptions.Compiled);

        private static readonly Regex SystemReminderPattern = new Regex(
            @"<system-reminder>.*?</system-reminder>",
            RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex WhitespacePattern = new Regex(@"\s+", RegexOptions.Compiled);

        // Fallback only, for a tail that holds no complete line (one giant last line): the
        // top-level "cwd" of a transcript line is serialized after its message, so the LAST raw
        // match in the window is that line's own cwd.
        private static readonly Regex RawCwdPattern = new Regex(
            @"""cwd"":""((?:[^""\\]|\\.)*)""",
            RegexOptions.Compiled);

        private static readonly Regex RawTurnPattern = new Regex(
            @"""type"":""(?:user|assistant)""",
            RegexOptions.Compiled);

        private static readonly string[] NotAPromptPrefixes =
        {
            "[MultiTerminal message",
            "initializing...",
            "Caveat:",
            "[Request interrupted",
        };

        private readonly string _claudeProjectsRoot;
        private readonly Func<string, string> _agentNameLookup;
        private readonly Func<string, string> _summaryLookup;

        public SessionCatalog(string claudeProjectsRoot, Func<string, string> agentNameLookup, Func<string, string> summaryLookup)
        {
            _claudeProjectsRoot = claudeProjectsRoot;
            _agentNameLookup = agentNameLookup;
            _summaryLookup = summaryLookup;
        }

        /// <summary>How many transcripts one <see cref="List"/> call may read, as a multiple of its <c>max</c>.</summary>
        internal const int ReadBudgetFactor = 3;

        /// <summary>
        /// Test seam (InternalsVisibleTo): the directory-existence check. Every path it is handed has
        /// already passed <see cref="ValidateResumeCwd"/> or is derived from the (validated) project
        /// root, which is what lets a test assert that no transcript-supplied path reaches the
        /// filesystem unvalidated.
        /// </summary>
        internal Func<string, bool> DirectoryExists { get; set; } = Directory.Exists;

        /// <summary>
        /// Test seam: the git repo root of a project folder (<see cref="FindRepoRoot"/>). Ticket 3fd3c9a7:
        /// a project registered at a subfolder of its repo (ClarionAssistant at
        /// <c>H:\DevLaptop\ClarionAssistant\ClarionAssistant</c>, repo at <c>H:\DevLaptop\ClarionAssistant</c>)
        /// has its worktrees at the REPO root, so a session that ended in one is filed under
        /// <c>&lt;encoded repo root&gt;--claude-worktrees-&lt;id&gt;</c>, which the project root's own prefix never matches.
        /// </summary>
        internal Func<string, string> RepoRootOf { get; set; } = FindRepoRoot;

        /// <summary>
        /// Why the last <see cref="TryGetEntry"/> call returned null, as a phrase that completes "its
        /// session cannot be resumed: ...", or null after a hit (ticket 3fd3c9a7: the skip message used to
        /// list every possible reason at once).
        /// </summary>
        public string LastMissReason { get; private set; }

        /// <summary>Test seam: how many transcripts the last <see cref="List"/> call opened.</summary>
        internal int TranscriptsReadLastList { get; private set; }

        /// <summary>
        /// Default for <see cref="ReadByteBudget"/>. The catalog runs synchronously on the UI thread, and
        /// without a byte cap the worst case is max * <see cref="ReadBudgetFactor"/> transcripts each read
        /// head + tail + <see cref="ContextTailBytes"/> + <see cref="SpawnJobScanMaxBytes"/> (~45 x 2.3MB).
        /// </summary>
        internal const long DefaultReadByteBudget = 24L * 1024 * 1024;

        /// <summary>
        /// Bytes one <see cref="List"/> call may read before the OPTIONAL reads stop: the
        /// <see cref="ContextTailBytes"/> usage fallback and the helper spawn-job scan. An optional read
        /// is made only when it fits in what is left; otherwise the entry is still listed, with no
        /// context or with the fallback summary. The mandatory head and tail reads are always made (and
        /// counted), so a List call reads at most max(budget, its mandatory reads). Test seam.
        /// </summary>
        internal long ReadByteBudget { get; set; } = DefaultReadByteBudget;

        /// <summary>Test seam: how many transcript bytes the last <see cref="List"/> call read.</summary>
        internal long BytesReadLastList { get; private set; }

        /// <summary>
        /// Gets how recently a transcript may have been written before it is reported as
        /// <see cref="SessionCatalogStatus.PossiblyOpenElsewhere"/>.
        /// </summary>
        public static TimeSpan RecentWriteWindow { get; } = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Claude Code's project-folder encoding: every char outside [A-Za-z0-9] becomes '-'.
        /// Verified against the real folders on 2026-10-17, e.g.
        /// <c>H:\DevLaptop\ClarionPowerShell\MultiTerminal\.claude\worktrees\5d94ac91</c> is
        /// <c>H--DevLaptop-ClarionPowerShell-MultiTerminal--claude-worktrees-5d94ac91</c> and a space
        /// in <c>John Hickey</c> becomes '-'. Older CLI builds left '.' and ' ' alone; that form is
        /// <see cref="SessionDiscovery.GetProjectFolderName"/>, which <see cref="List"/> also scans.
        /// </summary>
        public static string EncodeProjectPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            var sb = new StringBuilder(path.Length);
            foreach (var c in path)
            {
                var keep = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
                sb.Append(keep ? c : '-');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Lists resumable sessions for <paramref name="projectRoot"/>, newest first, at most
        /// <paramref name="max"/>. Never throws for a malformed line or file: those are skipped.
        /// </summary>
        public IReadOnlyList<SessionCatalogEntry> List(string projectRoot, ISet<string> liveSessionIds, DateTime utcNow, int max = 15)
        {
            var results = new List<SessionCatalogEntry>();
            if (max <= 0)
                return results;

            TranscriptsReadLastList = 0;
            BytesReadLastList = 0;

            if (!TryGetRoot(projectRoot, out var root))
                return results;

            var live = ToLiveSet(liveSessionIds);
            var worktreeBase = WorktreeBaseFor(root);

            var candidates = new List<(string Path, string FolderName, DateTime LastWriteUtc)>();
            foreach (var folder in GetCandidateFolders(root, worktreeBase))
            {
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(folder, "*.jsonl", SearchOption.TopDirectoryOnly).ToList();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SessionCatalog] cannot enumerate {folder}: {ex.Message}");
                    continue;
                }

                var folderName = Path.GetFileName(folder);
                foreach (var file in files)
                {
                    if (TryGetCandidate(file, live, out var lastWriteUtc))
                        candidates.Add((file, folderName, lastWriteUtc));
                }
            }

            // Newest first, and parse lazily: only as many transcripts as it takes to fill `max`, and
            // never more than max * ReadBudgetFactor of them. This runs on the UI thread while the user
            // waits; a project whose newest transcripts are all unresumable (aborted, empty, deleted
            // worktree) must not make it read every transcript the project ever wrote.
            // The first occurrence of an id wins (the newest copy, if a session is in two folders).
            var readBudget = (long)max * ReadBudgetFactor;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var meter = new ReadMeter(ReadByteBudget);
            foreach (var candidate in candidates.OrderByDescending(c => c.LastWriteUtc))
            {
                if (results.Count >= max || TranscriptsReadLastList >= readBudget)
                    break;

                var id = Path.GetFileNameWithoutExtension(candidate.Path);
                if (!seen.Add(id))
                    continue;

                TranscriptsReadLastList++;
                var entry = TryBuildEntry(root, worktreeBase, candidate.Path, candidate.FolderName, candidate.LastWriteUtc, utcNow, meter, out _);
                BytesReadLastList = meter.Used;
                if (entry != null)
                    results.Add(entry);
            }

            return results;
        }

        /// <summary>
        /// The one session <paramref name="sessionId"/> of <paramref name="projectRoot"/>, exactly as
        /// <see cref="List"/> would describe it, or null when List would not offer it (task 7d20e94c,
        /// resuming a PM together with its helpers). Never throws for a bad id, file or line.
        ///
        /// <para>The id is checked to be a GUID ("D" form) BEFORE any path is built from it or any
        /// filesystem call is made, so an id such as <c>..\x</c> or <c>a/b</c> can never name a file
        /// outside the project's transcript folders. Then the live-id check, then the same folders
        /// <see cref="List"/> scans (a deleted worktree's folder is skipped). When the session is in
        /// two folders, the newest copy decides, as in List: if that copy is rejected, the older one is
        /// not offered instead. The entry goes through the same <see cref="BuildEntry"/> rejections
        /// (cwd encoding and validation, directory existence, sanitising, context, helper summary).
        /// Reads are metered against a fresh <see cref="ReadByteBudget"/>; List's counters are not
        /// touched.</para>
        /// </summary>
        public SessionCatalogEntry TryGetEntry(string projectRoot, string sessionId, ISet<string> liveSessionIds, DateTime utcNow)
        {
            LastMissReason = null;
            if (!Guid.TryParseExact(sessionId, "D", out _))
                return Miss("no session id was recorded for it");
            if (!TryGetRoot(projectRoot, out var root))
                return Miss("the project's folder is not a local drive folder");
            var live = ToLiveSet(liveSessionIds);
            if (live.Contains(sessionId))
                return Miss("it is already open in this MultiTerminal");

            var worktreeBase = WorktreeBaseFor(root);
            string newestPath = null;
            string newestFolder = null;
            var newestWriteUtc = DateTime.MinValue;
            foreach (var folder in GetCandidateFolders(root, worktreeBase))
            {
                List<string> files;
                try
                {
                    // The id is a validated GUID, so it is a literal file name with no wildcard or
                    // separator. Enumerating (rather than File.Exists on a built path) yields the name
                    // as spelled on disk, which is what List reports.
                    files = Directory.EnumerateFiles(folder, sessionId + ".jsonl", SearchOption.TopDirectoryOnly).ToList();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SessionCatalog] cannot enumerate {folder}: {ex.Message}");
                    continue;
                }

                foreach (var file in files)
                {
                    if (!string.Equals(Path.GetFileNameWithoutExtension(file), sessionId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (TryGetCandidate(file, live, out var lastWriteUtc) && (newestPath == null || lastWriteUtc > newestWriteUtc))
                    {
                        newestPath = file;
                        newestFolder = Path.GetFileName(folder);
                        newestWriteUtc = lastWriteUtc;
                    }
                }
            }

            if (newestPath == null)
                return Miss("its transcript was not found in this project's session folders (it may have run in another project's folder)");
            var entry = TryBuildEntry(root, worktreeBase, newestPath, newestFolder, newestWriteUtc, utcNow, new ReadMeter(ReadByteBudget), out var why);
            return entry ?? Miss(why ?? "its transcript could not be read");
        }

        private SessionCatalogEntry Miss(string reason)
        {
            LastMissReason = reason;
            return null;
        }

        /// <summary>
        /// The git repo root of <paramref name="projectRoot"/>: the nearest folder at or above it holding a
        /// <c>.git</c> (directory or gitlink file), canonical, or null. Filesystem calls on the PROJECT root
        /// only, which comes from the project database, never from a transcript.
        /// </summary>
        public static string FindRepoRoot(string projectRoot)
        {
            if (!TryCanonicalLocalPath(projectRoot, out var cursor))
                return null;
            try
            {
                for (var i = 0; i < 64 && !string.IsNullOrEmpty(cursor); i++)
                {
                    var git = Path.Combine(cursor, ".git");
                    if (Directory.Exists(git) || File.Exists(git))
                        return TrimDir(cursor);
                    cursor = Path.GetDirectoryName(cursor);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionCatalog] cannot find the repo root of '{projectRoot}': {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// The repo root whose <c>.claude\worktrees</c> also holds this project's worktrees, when it is a
        /// strict ancestor of <paramref name="root"/>; else null (the project root is the repo root, or
        /// no repo). A repo root that is a drive root is not used.
        /// </summary>
        private string WorktreeBaseFor(string root)
        {
            string repoRoot;
            try
            {
                repoRoot = RepoRootOf?.Invoke(root);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionCatalog] repo root lookup for '{root}' failed: {ex.Message}");
                return null;
            }
            if (!TryCanonicalLocalPath(repoRoot, out var canonical) || canonical.EndsWith(":\\", StringComparison.Ordinal))
                return null;
            return root.StartsWith(canonical + "\\", StringComparison.OrdinalIgnoreCase) ? canonical : null;
        }

        /// <summary>
        /// The canonical project root, or false when nothing can be listed for it. Only a local drive
        /// root can produce a resumable cwd (<see cref="ValidateResumeCwd"/>), so a UNC or device-path
        /// project yields nothing, without reading a single transcript.
        /// </summary>
        private bool TryGetRoot(string projectRoot, out string root)
        {
            root = null;
            if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrEmpty(_claudeProjectsRoot))
                return false;
            if (!TryCanonicalLocalPath(projectRoot, out root))
            {
                Debug.WriteLine($"[SessionCatalog] project root '{projectRoot}' is not a local drive path; nothing to list");
                return false;
            }
            return true;
        }

        // Copy into an ordinal-ignore-case set: the caller's set may use any comparer, and session ids
        // are GUIDs whose case is not meaningful.
        private static HashSet<string> ToLiveSet(ISet<string> liveSessionIds) =>
            new HashSet<string>(liveSessionIds ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Whether <paramref name="file"/> is a session transcript that may be offered: its name is a
        /// GUID that no live session holds, and its last-write time can be read.
        /// </summary>
        private static bool TryGetCandidate(string file, HashSet<string> live, out DateTime lastWriteUtc)
        {
            lastWriteUtc = default;
            var id = Path.GetFileNameWithoutExtension(file);
            if (!Guid.TryParseExact(id, "D", out _) || live.Contains(id))
                return false;
            try
            {
                lastWriteUtc = File.GetLastWriteTimeUtc(file);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionCatalog] cannot stat {file}: {ex.Message}");
                return false;
            }
        }

        private SessionCatalogEntry TryBuildEntry(string root, string worktreeBase, string path, string folderName, DateTime lastWriteUtc, DateTime utcNow, ReadMeter meter, out string missReason)
        {
            missReason = null;
            try
            {
                return BuildEntry(root, worktreeBase, path, folderName, lastWriteUtc, utcNow, meter, out missReason);
            }
            catch (Exception ex)
            {
                // Belt and braces: one bad transcript must never take down the whole list.
                Debug.WriteLine($"[SessionCatalog] skipped {path}: {ex.Message}");
                missReason = "its transcript could not be read";
                return null;
            }
        }

        private IEnumerable<string> GetCandidateFolders(string root, string worktreeBase)
        {
            if (!Directory.Exists(_claudeProjectsRoot))
                return Array.Empty<string>();

            var exactNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                EncodeProjectPath(root),
                SessionDiscovery.GetProjectFolderName(root),
            };
            // Each prefix with the base whose .claude\worktrees it names: the project root always, and the
            // repo root when the project is a subfolder of its repo (ticket 3fd3c9a7).
            var prefixes = new List<(string Prefix, string Base)>();
            foreach (var b in worktreeBase == null ? new[] { root } : new[] { root, worktreeBase })
            {
                var worktreesPrefix = b + @"\.claude\worktrees\";
                prefixes.Add((EncodeProjectPath(worktreesPrefix), b));
                prefixes.Add((SessionDiscovery.GetProjectFolderName(worktreesPrefix), b));
            }

            try
            {
                return Directory.EnumerateDirectories(_claudeProjectsRoot)
                    .Where(d =>
                    {
                        var name = Path.GetFileName(d);
                        if (exactNames.Contains(name))
                            return true;
                        var match = prefixes.FirstOrDefault(p => name.Length > p.Prefix.Length && name.StartsWith(p.Prefix, StringComparison.OrdinalIgnoreCase));
                        return match.Prefix != null && WorktreeMayExist(match.Base, name.Substring(match.Prefix.Length));
                    })
                    .ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionCatalog] cannot enumerate {_claudeProjectsRoot}: {ex.Message}");
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// False only when <paramref name="encodedRest"/> (the part of a transcript folder name after
        /// the encoded <c>&lt;root&gt;\.claude\worktrees\</c> prefix) decodes UNAMBIGUOUSLY to a worktree
        /// id and <c>&lt;root&gt;\.claude\worktrees\&lt;id&gt;</c> does not exist: every session in that
        /// folder ran in a deleted worktree, so none of its transcripts is worth reading. Both encodings
        /// turn '\' into '-', so a rest of letters and digits only is exactly one segment, spelled as on
        /// disk; anything else ('-', '.', a subfolder) could be several paths and is kept.
        /// </summary>
        private bool WorktreeMayExist(string root, string encodedRest)
        {
            if (encodedRest.Length == 0 || !encodedRest.All(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')))
                return true;
            try
            {
                return DirectoryExists(Path.Combine(root, ".claude", "worktrees", encodedRest));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionCatalog] cannot check worktree '{encodedRest}': {ex.Message}");
                return true;
            }
        }

        private SessionCatalogEntry BuildEntry(string root, string worktreeBase, string path, string folderName, DateTime lastWriteUtc, DateTime utcNow, ReadMeter meter, out string missReason)
        {
            missReason = null;
            var scan = TranscriptScan.Read(path, meter);
            if (scan == null || !scan.HasTurns)
            {
                missReason = "its transcript holds no conversation";
                return null; // empty or aborted session: nothing to resume
            }

            // ResolveCwd returns only a path that passed ValidateResumeCwd, so the existence check below
            // is the first filesystem call any transcript-supplied path reaches (pipeline Run 1).
            var cwd = ResolveCwd(root, worktreeBase, folderName, scan);
            if (string.IsNullOrEmpty(cwd))
            {
                missReason = "it ran in a folder that is not this project's folder or one of its worktrees";
                return null;
            }
            if (!DirectoryExists(cwd))
            {
                missReason = $"the folder it ran in no longer exists ({ToDisplayText(cwd)})";
                return null; // e.g. a deleted worktree: `claude --resume` could not start there
            }

            // Only now, for an entry that will be listed, the optional (budgeted) reads.
            var isHelper = scan.SpawnedBy != null || scan.IsSpawnedAgent;
            scan.ReadOptional(path, meter, isHelper);

            var sessionId = Path.GetFileNameWithoutExtension(path);
            var spawnedBy = ToDisplayText(scan.SpawnedBy);

            return new SessionCatalogEntry
            {
                SessionId = sessionId,
                Cwd = cwd,
                TranscriptPath = path,
                LastActivityUtc = lastWriteUtc,
                // Names are transcript or DB text, sanitised like the summary (no control, bidi or
                // zero-width characters). The name also prefills the launch name, which is wanted: a
                // name that renders as something other than what it contains is not one to launch under.
                AgentName = ToDisplayText(CleanLookup(_agentNameLookup, sessionId)) ?? ToDisplayText(scan.AgentName),
                IsHelper = isHelper,
                SpawnedBy = spawnedBy,
                // A helper's first prompt is only its "collect your job" instruction; the job itself
                // (the get_my_spawn_job result) says what the session was for.
                Summary = (isHelper ? scan.SpawnJobSummary : null) ??
                          ToSummaryLine(CleanLookup(_summaryLookup, sessionId)) ??
                          ToSummaryLine(scan.FirstPrompt),
                WorktreeId = GetWorktreeId(root, cwd) ?? (worktreeBase == null ? null : GetWorktreeId(worktreeBase, cwd)),
                WorktreeBase = worktreeBase,
                Status = utcNow - lastWriteUtc < RecentWriteWindow
                    ? SessionCatalogStatus.PossiblyOpenElsewhere
                    : SessionCatalogStatus.Available,
                ContextTokens = scan.ContextTokens,
                ContextWindow = ResolveContextWindow(scan.ContextTokens, scan.ContextModel, scan.ContextRequestedModel),
            };
        }

        /// <summary>
        /// The directory to resume from. Claude Code finds a transcript by encoding the launch
        /// directory, so the right cwd is the LAST recorded cwd whose encoding names the folder the
        /// transcript lives in. That is the plain "last cwd" for the EnterWorktree case (the
        /// transcript moved with the session), but NOT for a session that merely cd'd: a real PM
        /// transcript (99fede3b, 2026-10-16) sits in the project-root folder while its last cwd is
        /// <c>.claude\worktrees\c8ee0eec\Docking</c>, and resuming from there would not find it.
        ///
        /// <para>Every recorded cwd is untrusted transcript text: only one that passes
        /// <see cref="ValidateResumeCwd"/> is considered at all. Order: matching tail cwd, then matching
        /// head cwd. A cwd that does not encode to the folder is NEVER returned (pipeline Run 1): Claude
        /// would answer "No conversation found" from it. The one fallback is the project root for a
        /// transcript stored in the root's own folder, because the root's encoding IS that folder's
        /// name. Otherwise the session is dropped. Deliberately no "decode the folder name" fallback:
        /// the encoding is lossy, and a derived path would also mask a reader that never looked at the
        /// tail.</para>
        /// </summary>
        private static string ResolveCwd(string root, string worktreeBase, string folderName, TranscriptScan scan)
        {
            bool EncodesToFolder(string dir) =>
                string.Equals(EncodeProjectPath(dir), folderName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(SessionDiscovery.GetProjectFolderName(dir), folderName, StringComparison.OrdinalIgnoreCase);

            string LastMatching(List<string> cwds)
            {
                for (var i = cwds.Count - 1; i >= 0; i--)
                {
                    var valid = ValidateResumeCwd(root, cwds[i], worktreeBase);
                    if (valid != null && EncodesToFolder(valid))
                        return valid;
                }
                return null;
            }

            return LastMatching(scan.TailCwds) ??
                   LastMatching(scan.HeadCwds) ??
                   (EncodesToFolder(root) ? root : null);
        }

        /// <summary>
        /// The canonical form of a transcript-recorded <paramref name="cwd"/> if a resume may be
        /// launched from it, else null (pipeline Run 1, security). Decided on the STRING alone, before
        /// any filesystem call: a UNC cwd handed to <c>Directory.Exists</c> would already have sent this
        /// user's credentials to that host just by opening the launch prompt.
        ///
        /// <para>Accepted: a fully-qualified local drive path (<c>X:\...</c>), canonicalised with
        /// <see cref="Path.GetFullPath(string)"/>, that is exactly the project root or lies at or below
        /// <c>&lt;root&gt;\.claude\worktrees\&lt;one segment&gt;</c> (a session may have started in a
        /// subfolder of its worktree). Rejected: UNC (<c>\\host\share</c>), device paths (<c>\\?\</c>,
        /// <c>\\.\</c>), relative and drive-relative (<c>C:foo</c>) paths, any ':' past the drive
        /// (alternate data streams), <c>&lt;root&gt;\.claude\worktrees</c> itself, and everything else
        /// under or outside the root. Comparison is ordinal-ignore-case on the canonical forms, with a
        /// separator guard so <c>C:\proj2</c> is not "under" <c>C:\proj</c>.</para>
        ///
        /// <para>Reparse points (junctions, symlinks) are NOT resolved: a junction inside a worktree can
        /// point anywhere. That is the local-trust model: whoever can create a junction in the project
        /// can already run code as this user. Chasing one would mean filesystem calls on an unvalidated
        /// target, which is what this method exists to prevent.</para>
        /// <para>Ticket 3fd3c9a7: <paramref name="repoRoot"/> (the entry's <see cref="SessionCatalogEntry.WorktreeBase"/>)
        /// also admits <c>&lt;repoRoot&gt;\.claude\worktrees\&lt;one segment&gt;</c>, for a project registered at
        /// a subfolder of its repo, whose worktrees live at the repo root. Used only when it is a local
        /// drive path that is a strict ancestor of the project root and not a drive root; anything else
        /// is ignored, never widened.</para>
        /// </summary>
        public static string ValidateResumeCwd(string projectRoot, string cwd, string repoRoot = null)
        {
            if (!TryCanonicalLocalPath(projectRoot, out var root) || !TryCanonicalLocalPath(cwd, out var dir))
                return null;
            if (string.Equals(dir, root, StringComparison.OrdinalIgnoreCase))
                return dir;

            if (IsUnderWorktrees(root, dir))
                return dir;
            if (TryCanonicalLocalPath(repoRoot, out var repo)
                && !repo.EndsWith(":\\", StringComparison.Ordinal)
                && root.StartsWith(repo + "\\", StringComparison.OrdinalIgnoreCase)
                && IsUnderWorktrees(repo, dir))
                return dir;
            return null;
        }

        // dir is at or below <baseDir>\.claude\worktrees\<one non-empty segment>.
        private static bool IsUnderWorktrees(string baseDir, string dir)
        {
            var worktrees = baseDir.TrimEnd('\\') + @"\.claude\worktrees\";
            if (!dir.StartsWith(worktrees, StringComparison.OrdinalIgnoreCase))
                return false;
            return dir.Substring(worktrees.Length).Split('\\')[0].Length > 0;
        }

        /// <summary>
        /// A fully-qualified local drive path, canonicalised and without a trailing separator (a drive
        /// root keeps its own), or false. String checks only: <see cref="Path.GetFullPath(string)"/> is
        /// path arithmetic for a fully-qualified input and touches no file.
        /// </summary>
        internal static bool TryCanonicalLocalPath(string path, out string canonical)
        {
            canonical = null;
            if (!IsDriveRooted(path))
                return false;

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return false;
            }

            // Re-check the canonical form: canonicalisation must not have produced anything else.
            if (!IsDriveRooted(full))
                return false;
            canonical = TrimDir(full);
            return true;
        }

        // X:\ or X:/ with no further ':' and no NUL. Rejects UNC and device paths (they start with a
        // separator), relative and drive-relative paths, and alternate-data-stream colons.
        private static bool IsDriveRooted(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length < 3)
                return false;
            var d = path[0];
            var isLetter = (d >= 'A' && d <= 'Z') || (d >= 'a' && d <= 'z');
            return isLetter && path[1] == ':' && (path[2] == '\\' || path[2] == '/')
                && path.IndexOf(':', 2) < 0 && path.IndexOf('\0', StringComparison.Ordinal) < 0;
        }

        private static string GetWorktreeId(string root, string cwd)
        {
            string full;
            try
            {
                full = NormalizeDir(cwd);
            }
            catch (Exception)
            {
                return null;
            }

            var prefix = Path.Combine(root, ".claude", "worktrees") + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return null;

            var id = full.Substring(prefix.Length).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }

        private static string NormalizeDir(string dir) => TrimDir(Path.GetFullPath(dir));

        private static string TrimDir(string dir)
        {
            if (string.IsNullOrEmpty(dir))
                return dir;
            var trimmed = dir.TrimEnd('\\', '/');
            // Keep a drive root ("C:\") intact.
            return trimmed.Length == 0 || trimmed.EndsWith(':') ? dir : trimmed;
        }

        private static string CleanLookup(Func<string, string> lookup, string sessionId)
        {
            if (lookup == null)
                return null;
            try
            {
                var value = lookup(sessionId);
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
            catch (Exception ex)
            {
                // A lookup failure (transient DB trouble) degrades this field to the transcript fallback.
                Debug.WriteLine($"[SessionCatalog] lookup threw for {sessionId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// The context window for a model id, or null when the id is not recognised (the dialog then
        /// shows tokens only). A <c>[1m]</c> suffix (Claude Code's 1M-context selector) means 1M.
        /// Otherwise, per the model table cached 2026-10-06: Fable and Mythos, every generation-5
        /// model, and Opus/Sonnet 4.6 and later are 1M; Haiku 4.x, Opus/Sonnet before 4.6 and every
        /// pre-4 model are 200k. An unrecognised id (including <c>&lt;synthetic&gt;</c>) is null.
        /// </summary>
        public static int? ContextWindowFor(string modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId))
                return null;
            var id = modelId.Trim();
            if (id.EndsWith("[1m]", StringComparison.OrdinalIgnoreCase))
                return LargeContextWindow;
            if (LegacyModelIdPattern.IsMatch(id))
                return StandardContextWindow;

            var m = ModelIdPattern.Match(id);
            if (!m.Success)
                return null;
            var family = m.Groups["family"].Value.ToLowerInvariant();
            var major = int.Parse(m.Groups["major"].Value, System.Globalization.CultureInfo.InvariantCulture);
            var minor = m.Groups["minor"].Success
                ? int.Parse(m.Groups["minor"].Value, System.Globalization.CultureInfo.InvariantCulture)
                : 0;

            if (family == "fable" || family == "mythos" || major >= 5)
                return LargeContextWindow;
            if (major == 4 && (family == "opus" || family == "sonnet") && minor >= 6)
                return LargeContextWindow;
            return StandardContextWindow;
        }

        /// <summary>
        /// The window to show for <paramref name="tokens"/>: the larger of what the response model and
        /// the requested model (which may carry the <c>[1m]</c> suffix) imply. Null when neither is
        /// recognised, or when the tokens already exceed the guess: the transcript then proves the
        /// guess wrong (e.g. a 200k model run with a 1M beta), and "250k / 200k (125%)" would be a lie.
        /// </summary>
        internal static int? ResolveContextWindow(int? tokens, string model, string requestedModel)
        {
            if (!tokens.HasValue)
                return null;
            var a = ContextWindowFor(model);
            var b = ContextWindowFor(requestedModel);
            int? window = a.HasValue && b.HasValue ? Math.Max(a.Value, b.Value) : a ?? b;
            return window.HasValue && tokens.Value > window.Value ? null : window;
        }

        /// <summary>
        /// The context in use recorded by one transcript line, or false when the line is not a
        /// main-thread assistant turn with usage. Real shape (Claude Code 2.1.301, 2026-10-16):
        /// <c>{"type":"assistant","isSidechain":false,"requestedModel":"claude-opus-5-5",
        /// "message":{"model":"claude-opus-5-5","usage":{"input_tokens":2,
        /// "cache_creation_input_tokens":61,"cache_read_input_tokens":221579,"output_tokens":439,...}}}</c>.
        /// Context = input + cache_read + cache_creation (output tokens are not yet context). Sidechain
        /// turns, <c>&lt;synthetic&gt;</c> error turns and all-zero usage are rejected.
        /// </summary>
        internal static bool TryReadContextUsage(string jsonLine, out int tokens, out string model, out string requestedModel)
        {
            tokens = 0;
            model = null;
            requestedModel = null;
            if (string.IsNullOrWhiteSpace(jsonLine))
                return false;
            try
            {
                using var doc = JsonDocument.Parse(jsonLine);
                return TryReadContextUsage(doc.RootElement, out tokens, out model, out requestedModel);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool TryReadContextUsage(JsonElement obj, out int tokens, out string model, out string requestedModel)
        {
            tokens = 0;
            model = null;
            requestedModel = null;
            if (obj.ValueKind != JsonValueKind.Object || !IsString(obj, "type", "assistant"))
                return false;
            if (obj.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True)
                return false;
            if (!obj.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                return false;
            if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                return false;

            model = message.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            if (string.Equals(model, "<synthetic>", StringComparison.Ordinal))
                return false;
            requestedModel = obj.TryGetProperty("requestedModel", out var rm) && rm.ValueKind == JsonValueKind.String ? rm.GetString() : null;

            long total = 0;
            foreach (var field in new[] { "input_tokens", "cache_read_input_tokens", "cache_creation_input_tokens" })
            {
                if (usage.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n > 0)
                    total += n;
            }
            if (total <= 0 || total > int.MaxValue)
                return false;
            tokens = (int)total;
            return true;
        }

        private static bool IsString(JsonElement obj, string name, string expected) =>
            obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String &&
            string.Equals(v.GetString(), expected, StringComparison.Ordinal);

        /// <summary>
        /// The summary line for a helper, from the text of its get_my_spawn_job tool result: the first
        /// non-blank line of the job body, after the "YOUR JOB from ..." header paragraph, with
        /// leading markdown markers removed. Null for anything else (an error, "no job", a result
        /// without the header): untrusted transcript text that does not look like a job is not shown.
        /// </summary>
        internal static string SpawnJobSummaryFrom(string toolResultText)
        {
            if (string.IsNullOrWhiteSpace(toolResultText))
                return null;
            var text = toolResultText.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart();
            if (!SpawnJobHeaderPattern.IsMatch(text))
                return null;
            var split = text.IndexOf("\n\n", StringComparison.Ordinal);
            if (split < 0)
                return null;
            foreach (var raw in text.Substring(split + 2).Split('\n'))
            {
                var line = raw.Trim().TrimStart('#', '>', '*', '-', ' ', '\t').Trim();
                var summary = ToSummaryLine(line);
                if (summary != null)
                    return summary;
            }
            return null;
        }

        /// <summary>
        /// One display line from untrusted text: control and format characters removed (including
        /// bidi overrides and zero-width characters, which could make a line render as something other
        /// than what it contains), whitespace collapsed, capped at <see cref="SummaryMaxChars"/>.
        /// </summary>
        internal static string ToSummaryLine(string text) => ToDisplayText(text, SummaryMaxChars);

        /// <summary>
        /// Untrusted text (transcript- or database-derived) made safe to DISPLAY on one line: control
        /// and format characters removed (bidi overrides and isolates, zero-width characters), all
        /// whitespace collapsed to single spaces, trimmed, and, when <paramref name="maxChars"/> is
        /// given, capped with an ellipsis. Null when nothing visible is left. For displayed copies only:
        /// the cwd a resume starts in keeps its exact form.
        /// </summary>
        public static string ToDisplayText(string text, int? maxChars = null)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            var line = WhitespacePattern.Replace(StripControlChars(text), " ").Trim();
            if (line.Length == 0)
                return null;
            if (!maxChars.HasValue || line.Length <= maxChars.Value)
                return line;
            return line.Substring(0, Math.Max(0, maxChars.Value - 1)).TrimEnd() + "\u2026";
        }

        /// <summary>Test seam: the helper-job search over already-split transcript lines.</summary>
        internal static string SpawnJobSummaryFromLines(IEnumerable<string> lines) =>
            TranscriptScan.SpawnJobSummaryFromLines(lines);

        private static string StripControlChars(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    sb.Append(' ');
                    continue;
                }
                var cat = char.GetUnicodeCategory(c);
                if (cat == System.Globalization.UnicodeCategory.Control || cat == System.Globalization.UnicodeCategory.Format)
                    continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static bool IsPlaceholderName(string name) =>
            string.IsNullOrWhiteSpace(name) ||
            name.Equals("undefined", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("null", StringComparison.OrdinalIgnoreCase);

        private static string FirstCapture(Regex pattern, IEnumerable<string> texts)
        {
            foreach (var text in texts)
            {
                foreach (Match m in pattern.Matches(text))
                {
                    var value = m.Groups[1].Value.Trim();
                    if (!IsPlaceholderName(value))
                        return value;
                }
            }
            return null;
        }

        /// <summary>The bytes one <see cref="List"/> call has read, against its <see cref="ReadByteBudget"/>.</summary>
        private sealed class ReadMeter
        {
            private readonly long _budget;

            public ReadMeter(long budget) => _budget = budget;

            public long Used { get; private set; }

            public bool CanAfford(long bytes) => Used + bytes <= _budget;

            public void Charge(long bytes) => Used += Math.Max(0, bytes);
        }

        /// <summary>
        /// What one bounded read of a transcript yields. Built only from the head (first
        /// <see cref="HeadMaxLines"/> lines within <see cref="HeadMaxBytes"/>) and the tail (last
        /// <see cref="TailBytes"/>).
        /// </summary>
        private sealed class TranscriptScan
        {
            public List<string> HeadCwds { get; } = new List<string>();

            public List<string> TailCwds { get; } = new List<string>();

            public bool HasTurns { get; set; }

            public string AgentName { get; set; }

            public string SpawnedBy { get; set; }

            public bool IsSpawnedAgent { get; set; }

            public string FirstPrompt { get; set; }

            public int? ContextTokens { get; set; }

            public string ContextModel { get; set; }

            public string ContextRequestedModel { get; set; }

            public string SpawnJobSummary { get; set; }

            // The file length at the mandatory read, and the head bytes and complete head lines, kept
            // so the optional spawn-job scan starts where the head ended instead of re-reading it.
            private long _length;
            private byte[] _headBytes;
            private List<string> _headLines;

            public static TranscriptScan Read(string path, ReadMeter meter)
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var length = fs.Length;
                if (length == 0)
                    return null;
                var headBytes = ReadBytes(fs, 0, (int)Math.Min(length, HeadMaxBytes), meter);
                var headText = Encoding.UTF8.GetString(headBytes);
                var tailText = ReadTail(fs, length, TailBytes, meter);

                var headLines = headText.Split('\n').ToList();
                if (length > HeadMaxBytes && headLines.Count > 0)
                    headLines.RemoveAt(headLines.Count - 1); // partial line at the window edge

                var scan = new TranscriptScan { _length = length, _headBytes = headBytes, _headLines = headLines };
                var hookTexts = new List<string>();
                string agentNameRecord = null;

                foreach (var raw in headLines.Take(HeadMaxLines))
                    scan.ScanHeadLine(raw, hookTexts, ref agentNameRecord);

                var parsedTailLine = false;
                foreach (var raw in tailText.Split('\n'))
                {
                    if (scan.ScanTailLine(raw))
                        parsedTailLine = true;
                }
                if (!parsedTailLine)
                {
                    // One giant last line: fall back to the raw last "cwd" in the window.
                    var last = RawCwdPattern.Matches(tailText).Cast<Match>().LastOrDefault();
                    var decoded = last == null ? null : DecodeJsonString(last.Groups[1].Value);
                    if (!string.IsNullOrEmpty(decoded))
                        scan.TailCwds.Add(decoded);
                }
                if (!scan.HasTurns)
                {
                    // Same giant-line case for the turn check: a head of nothing but hook output plus a
                    // tail inside one huge line would otherwise read as an empty session.
                    scan.HasTurns = RawTurnPattern.IsMatch(tailText);
                }

                // Only SessionStart hook output is trusted for identity: the same strings appear in
                // file reads of CLAUDE.md (e.g. "MULTITERMINAL_NAME=CA-<slug>"), so a whole-head scan
                // would attribute documentation to the session.
                scan.AgentName =
                    FirstCapture(NameMarkerPattern, hookTexts) ??
                    FirstCapture(TerminalIdentityPattern, hookTexts) ??
                    FirstCapture(SpawnedAgentPattern, hookTexts) ??
                    (IsPlaceholderName(agentNameRecord) ? null : agentNameRecord.Trim());
                scan.SpawnedBy =
                    FirstCapture(SpawnedByPattern, hookTexts) ??
                    FirstCapture(SpawnerEnvPattern, hookTexts);
                scan.IsSpawnedAgent = hookTexts.Any(t => t.Contains("isSpawnedAgent: true", StringComparison.Ordinal));
                return scan;
            }

            /// <summary>
            /// The reads made only for an entry that passed every cheap rejection check, each only when
            /// it fits in <paramref name="meter"/>'s remaining budget: the larger usage tail when the
            /// mandatory tail held no usage, and a helper's spawn-job scan.
            /// </summary>
            public void ReadOptional(string path, ReadMeter meter, bool isHelper)
            {
                var wantUsage = !ContextTokens.HasValue && _length > TailBytes;
                if (isHelper)
                {
                    // The head is already in memory: a job found there costs no read at all.
                    SpawnJobSummary = SpawnJobSummaryFromLines(_headLines.Take(SpawnJobScanMaxLines));
                }
                var wantJobScan = isHelper && SpawnJobSummary == null && _length > HeadMaxBytes;
                if (!wantUsage && !wantJobScan)
                    return;

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (wantUsage)
                {
                    // No assistant turn with usage in the last TailBytes (one big tool result at the
                    // end): look once more over a larger tail, for usage only.
                    var size = Math.Min(_length, ContextTailBytes);
                    if (meter.CanAfford(size))
                    {
                        foreach (var raw in ReadTail(fs, _length, ContextTailBytes, meter).Split('\n'))
                            ScanUsageOnly(raw);
                    }
                }
                if (wantJobScan)
                    SpawnJobSummary = FindSpawnJobSummaryBeyondHead(fs, meter);
            }

            private static string ReadTail(FileStream fs, long length, int bytes, ReadMeter meter)
            {
                var tailStart = Math.Max(0, length - bytes);
                var text = Encoding.UTF8.GetString(ReadBytes(fs, tailStart, (int)(length - tailStart), meter));
                if (tailStart == 0)
                    return text;
                // The first segment of the tail window is a partial line.
                var nl = text.IndexOf('\n', StringComparison.Ordinal);
                return nl < 0 ? string.Empty : text.Substring(nl + 1);
            }

            /// <summary>
            /// A helper's job when it is not in the head (measured: it usually is not): the bytes from
            /// the end of the head read up to <see cref="SpawnJobScanMaxBytes"/> are appended to the head
            /// bytes already in memory, so the head is never read twice and a line (or a UTF-8
            /// character) straddling the head boundary is reassembled. Bounded by
            /// <see cref="SpawnJobScanMaxLines"/>; only lines naming the tool or a pending id are parsed.
            /// Skipped (null) when the read does not fit the budget.
            /// </summary>
            private string FindSpawnJobSummaryBeyondHead(FileStream fs, ReadMeter meter)
            {
                var start = _headBytes.Length;
                var count = (int)(Math.Min(_length, SpawnJobScanMaxBytes) - start);
                if (count <= 0 || !meter.CanAfford(count))
                    return null;
                var rest = ReadBytes(fs, start, count, meter);
                var all = new byte[start + rest.Length];
                Buffer.BlockCopy(_headBytes, 0, all, 0, start);
                Buffer.BlockCopy(rest, 0, all, start, rest.Length);

                var lines = Encoding.UTF8.GetString(all).Split('\n').ToList();
                if (_length > SpawnJobScanMaxBytes && lines.Count > 0)
                    lines.RemoveAt(lines.Count - 1); // partial line at the window edge
                return SpawnJobSummaryFromLines(lines.Take(SpawnJobScanMaxLines));
            }

            internal static string SpawnJobSummaryFromLines(IEnumerable<string> lines)
            {
                var toolUseIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var raw in lines)
                {
                    var namesTool = raw.Contains("get_my_spawn_job", StringComparison.Ordinal);
                    if (!namesTool && !toolUseIds.Any(id => raw.Contains(id, StringComparison.Ordinal)))
                        continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(raw.Trim());
                        var obj = doc.RootElement;
                        if (obj.ValueKind != JsonValueKind.Object ||
                            !obj.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
                            !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }
                        var type = GetString(obj, "type");
                        foreach (var block in content.EnumerateArray())
                        {
                            if (block.ValueKind != JsonValueKind.Object)
                                continue;
                            var blockType = GetString(block, "type");
                            if (type == "assistant" && blockType == "tool_use")
                            {
                                var name = GetString(block, "name");
                                var id = GetString(block, "id");
                                if (name != null && !string.IsNullOrEmpty(id) && name.EndsWith("get_my_spawn_job", StringComparison.Ordinal))
                                    toolUseIds.Add(id);
                            }
                            else if (type == "user" && blockType == "tool_result")
                            {
                                var id = GetString(block, "tool_use_id");
                                if (id == null || !toolUseIds.Contains(id))
                                    continue;
                                var summary = SpawnJobSummaryFrom(ToolResultText(block));
                                if (summary != null)
                                    return summary;
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // Malformed line: skip it.
                    }
                }
                return null;
            }

            private static string ToolResultText(JsonElement block)
            {
                if (!block.TryGetProperty("content", out var content))
                    return null;
                if (content.ValueKind == JsonValueKind.String)
                    return content.GetString();
                if (content.ValueKind != JsonValueKind.Array)
                    return null;
                var sb = new StringBuilder();
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.Object && GetString(part, "type") == "text")
                        sb.Append(GetString(part, "text")).Append('\n');
                }
                return sb.ToString();
            }

            private void ScanUsageOnly(string raw)
            {
                if (TryReadContextUsage(raw, out var tokens, out var model, out var requested))
                {
                    ContextTokens = tokens;
                    ContextModel = model;
                    ContextRequestedModel = requested;
                }
            }

            private static byte[] ReadBytes(FileStream fs, long offset, int count, ReadMeter meter)
            {
                var buffer = new byte[count];
                fs.Seek(offset, SeekOrigin.Begin);
                var total = 0;
                while (total < count)
                {
                    var n = fs.Read(buffer, total, count - total);
                    if (n <= 0)
                        break;
                    total += n;
                }
                meter.Charge(total);
                if (total < count)
                    Array.Resize(ref buffer, total);
                return buffer;
            }

            private static string DecodeJsonString(string escaped)
            {
                try
                {
                    return JsonSerializer.Deserialize<string>("\"" + escaped + "\"");
                }
                catch (JsonException)
                {
                    return null;
                }
            }

            private void ScanHeadLine(string raw, List<string> hookTexts, ref string agentNameRecord)
            {
                var line = raw.Trim();
                if (line.Length == 0)
                    return;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var obj = doc.RootElement;
                    if (obj.ValueKind != JsonValueKind.Object)
                        return;

                    var cwd = GetString(obj, "cwd");
                    if (!string.IsNullOrEmpty(cwd))
                        HeadCwds.Add(cwd);

                    var type = GetString(obj, "type");
                    switch (type)
                    {
                        case "user":
                        case "assistant":
                            HasTurns = true;
                            if (type == "user" && FirstPrompt == null)
                                FirstPrompt = GetPromptText(obj);
                            break;
                        case "agent-name":
                            agentNameRecord ??= GetString(obj, "agentName");
                            break;
                        case "attachment":
                            CollectSessionStartHookText(obj, hookTexts);
                            break;
                    }
                }
                catch (JsonException)
                {
                    // Malformed line: skip it.
                }
            }

            private bool ScanTailLine(string raw)
            {
                var line = raw.Trim();
                if (line.Length == 0)
                    return false;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var obj = doc.RootElement;
                    if (obj.ValueKind != JsonValueKind.Object)
                        return false;
                    var cwd = GetString(obj, "cwd");
                    if (!string.IsNullOrEmpty(cwd))
                        TailCwds.Add(cwd);
                    var type = GetString(obj, "type");
                    if (type == "user" || type == "assistant")
                        HasTurns = true;
                    // Lines are in file order, so the last one that qualifies wins.
                    if (TryReadContextUsage(obj, out var tokens, out var model, out var requested))
                    {
                        ContextTokens = tokens;
                        ContextModel = model;
                        ContextRequestedModel = requested;
                    }
                    return true;
                }
                catch (JsonException)
                {
                    return false;
                }
            }

            private static void CollectSessionStartHookText(JsonElement obj, List<string> hookTexts)
            {
                if (!obj.TryGetProperty("attachment", out var att) || att.ValueKind != JsonValueKind.Object)
                    return;
                var hookEvent = GetString(att, "hookEvent");
                var hookName = GetString(att, "hookName");
                var isSessionStart =
                    string.Equals(hookEvent, "SessionStart", StringComparison.Ordinal) ||
                    (hookName != null && hookName.StartsWith("SessionStart", StringComparison.Ordinal));
                if (!isSessionStart)
                    return;

                var content = GetString(att, "content");
                if (!string.IsNullOrEmpty(content))
                    hookTexts.Add(content);
                var stdout = GetString(att, "stdout");
                if (!string.IsNullOrEmpty(stdout) && !string.Equals(stdout, content, StringComparison.Ordinal))
                    hookTexts.Add(stdout);
            }

            /// <summary>
            /// The text of a genuine human prompt, or null for hook output, system reminders,
            /// command wrappers, tool results, meta lines and MultiTerminal message events.
            /// </summary>
            private static string GetPromptText(JsonElement obj)
            {
                if (obj.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True)
                    return null;
                if (obj.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True)
                    return null;
                if (!obj.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                    return null;
                if (!message.TryGetProperty("content", out var content))
                    return null;

                string text;
                if (content.ValueKind == JsonValueKind.String)
                {
                    text = content.GetString();
                }
                else if (content.ValueKind == JsonValueKind.Array)
                {
                    var sb = new StringBuilder();
                    foreach (var block in content.EnumerateArray())
                    {
                        if (block.ValueKind != JsonValueKind.Object)
                            continue;
                        var blockType = GetString(block, "type");
                        if (blockType == "tool_result")
                            return null;
                        if (blockType == "text")
                            sb.Append(GetString(block, "text")).Append(' ');
                    }
                    text = sb.ToString();
                }
                else
                {
                    return null;
                }

                if (string.IsNullOrWhiteSpace(text))
                    return null;

                text = SystemReminderPattern.Replace(text, " ").Trim();
                if (text.Length == 0 || text[0] == '<')
                    return null; // command wrappers (<command-name>, <local-command-stdout>, ...)
                foreach (var prefix in NotAPromptPrefixes)
                {
                    if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return null;
                }
                return text;
            }

            private static string GetString(JsonElement obj, string name) =>
                obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
        }
    }
}
