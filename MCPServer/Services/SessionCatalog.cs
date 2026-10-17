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

        // e.g. "5d94ac91" when Cwd is under <projectRoot>\.claude\worktrees\<id>, else null.
        public string WorktreeId { get; set; }

        public SessionCatalogStatus Status { get; set; }
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

        /// <summary>Test seam: how many transcripts the last <see cref="List"/> call opened.</summary>
        internal int TranscriptsReadLastList { get; private set; }

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
            if (string.IsNullOrWhiteSpace(projectRoot) || max <= 0 ||
                string.IsNullOrEmpty(_claudeProjectsRoot))
            {
                return results;
            }

            TranscriptsReadLastList = 0;

            // Only a local drive root can produce a resumable cwd (ValidateResumeCwd), so a UNC or
            // device-path project lists nothing, without reading a single transcript.
            if (!TryCanonicalLocalPath(projectRoot, out var root))
            {
                Debug.WriteLine($"[SessionCatalog] project root '{projectRoot}' is not a local drive path; nothing to list");
                return results;
            }

            // Copy into an ordinal-ignore-case set: the caller's set may use any comparer, and
            // session ids are GUIDs whose case is not meaningful.
            var live = new HashSet<string>(liveSessionIds ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            var candidates = new List<(string Path, string FolderName, DateTime LastWriteUtc)>();
            foreach (var folder in GetCandidateFolders(root))
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
                    var id = Path.GetFileNameWithoutExtension(file);
                    if (!Guid.TryParseExact(id, "D", out _) || live.Contains(id))
                        continue;
                    try
                    {
                        candidates.Add((file, folderName, File.GetLastWriteTimeUtc(file)));
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[SessionCatalog] cannot stat {file}: {ex.Message}");
                    }
                }
            }

            // Newest first, and parse lazily: only as many transcripts as it takes to fill `max`, and
            // never more than max * ReadBudgetFactor of them. This runs on the UI thread while the user
            // waits; a project whose newest transcripts are all unresumable (aborted, empty, deleted
            // worktree) must not make it read every transcript the project ever wrote.
            // The first occurrence of an id wins (the newest copy, if a session is in two folders).
            var readBudget = (long)max * ReadBudgetFactor;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates.OrderByDescending(c => c.LastWriteUtc))
            {
                if (results.Count >= max || TranscriptsReadLastList >= readBudget)
                    break;

                var id = Path.GetFileNameWithoutExtension(candidate.Path);
                if (!seen.Add(id))
                    continue;

                TranscriptsReadLastList++;
                try
                {
                    var entry = BuildEntry(root, candidate.Path, candidate.FolderName, candidate.LastWriteUtc, utcNow);
                    if (entry != null)
                        results.Add(entry);
                }
                catch (Exception ex)
                {
                    // Belt and braces: one bad transcript must never take down the whole list.
                    Debug.WriteLine($"[SessionCatalog] skipped {candidate.Path}: {ex.Message}");
                }
            }

            return results;
        }

        private IEnumerable<string> GetCandidateFolders(string root)
        {
            if (!Directory.Exists(_claudeProjectsRoot))
                return Array.Empty<string>();

            var worktreesPrefix = root + @"\.claude\worktrees\";
            var exactNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                EncodeProjectPath(root),
                SessionDiscovery.GetProjectFolderName(root),
            };
            var prefixes = new[]
            {
                EncodeProjectPath(worktreesPrefix),
                SessionDiscovery.GetProjectFolderName(worktreesPrefix),
            };

            try
            {
                return Directory.EnumerateDirectories(_claudeProjectsRoot)
                    .Where(d =>
                    {
                        var name = Path.GetFileName(d);
                        if (exactNames.Contains(name))
                            return true;
                        var prefix = prefixes.FirstOrDefault(p => name.Length > p.Length && name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                        return prefix != null && WorktreeMayExist(root, name.Substring(prefix.Length));
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

        private SessionCatalogEntry BuildEntry(string root, string path, string folderName, DateTime lastWriteUtc, DateTime utcNow)
        {
            var scan = TranscriptScan.Read(path);
            if (scan == null || !scan.HasTurns)
                return null; // empty or aborted session: nothing to resume

            // ResolveCwd returns only a path that passed ValidateResumeCwd, so the existence check below
            // is the first filesystem call any transcript-supplied path reaches (pipeline Run 1).
            var cwd = ResolveCwd(root, folderName, scan);
            if (string.IsNullOrEmpty(cwd) || !DirectoryExists(cwd))
                return null; // e.g. a deleted worktree: `claude --resume` could not start there

            var sessionId = Path.GetFileNameWithoutExtension(path);
            var spawnedBy = scan.SpawnedBy;

            return new SessionCatalogEntry
            {
                SessionId = sessionId,
                Cwd = cwd,
                TranscriptPath = path,
                LastActivityUtc = lastWriteUtc,
                AgentName = CleanLookup(_agentNameLookup, sessionId) ?? scan.AgentName,
                IsHelper = spawnedBy != null || scan.IsSpawnedAgent,
                SpawnedBy = spawnedBy,
                Summary = ToSummaryLine(CleanLookup(_summaryLookup, sessionId)) ?? ToSummaryLine(scan.FirstPrompt),
                WorktreeId = GetWorktreeId(root, cwd),
                Status = utcNow - lastWriteUtc < RecentWriteWindow
                    ? SessionCatalogStatus.PossiblyOpenElsewhere
                    : SessionCatalogStatus.Available,
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
        private static string ResolveCwd(string root, string folderName, TranscriptScan scan)
        {
            bool EncodesToFolder(string dir) =>
                string.Equals(EncodeProjectPath(dir), folderName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(SessionDiscovery.GetProjectFolderName(dir), folderName, StringComparison.OrdinalIgnoreCase);

            string LastMatching(List<string> cwds)
            {
                for (var i = cwds.Count - 1; i >= 0; i--)
                {
                    var valid = ValidateResumeCwd(root, cwds[i]);
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
        /// </summary>
        public static string ValidateResumeCwd(string projectRoot, string cwd)
        {
            if (!TryCanonicalLocalPath(projectRoot, out var root) || !TryCanonicalLocalPath(cwd, out var dir))
                return null;
            if (string.Equals(dir, root, StringComparison.OrdinalIgnoreCase))
                return dir;

            var worktrees = root.TrimEnd('\\') + @"\.claude\worktrees\";
            if (!dir.StartsWith(worktrees, StringComparison.OrdinalIgnoreCase))
                return null;
            var segment = dir.Substring(worktrees.Length).Split('\\')[0];
            return segment.Length == 0 ? null : dir;
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

        internal static string ToSummaryLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            var line = WhitespacePattern.Replace(text, " ").Trim();
            if (line.Length == 0)
                return null;
            return line.Length <= SummaryMaxChars ? line : line.Substring(0, SummaryMaxChars - 1).TrimEnd() + "\u2026";
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

            public static TranscriptScan Read(string path)
            {
                string headText;
                string tailText;
                long length;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    length = fs.Length;
                    if (length == 0)
                        return null;
                    headText = ReadChunk(fs, 0, (int)Math.Min(length, HeadMaxBytes));
                    var tailStart = Math.Max(0, length - TailBytes);
                    tailText = ReadChunk(fs, tailStart, (int)(length - tailStart));
                    if (tailStart > 0)
                    {
                        // The first segment of the tail window is a partial line.
                        var nl = tailText.IndexOf('\n', StringComparison.Ordinal);
                        tailText = nl < 0 ? string.Empty : tailText.Substring(nl + 1);
                    }
                }

                var headLines = headText.Split('\n').ToList();
                if (length > HeadMaxBytes && headLines.Count > 0)
                    headLines.RemoveAt(headLines.Count - 1); // partial line at the window edge

                var scan = new TranscriptScan();
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

            private static string ReadChunk(FileStream fs, long offset, int count)
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
                return Encoding.UTF8.GetString(buffer, 0, total);
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
