using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Per-session live token accumulator for the terminal status-bar meter (task f2702f69).
    ///
    /// Folds each assistant message's <c>message.usage</c> block from a Claude Code transcript
    /// into a running total, keyed by the persistent Claude <c>sessionId</c> (NOT the ephemeral
    /// MT docId), so a <c>--resume</c> keeps the running total intact rather than resetting it.
    ///
    /// <para>
    /// Messages are DEDUPED by <c>message.id</c>: Claude Code can append a message's JSONL line
    /// more than once as a turn streams, and the latest append carries the complete usage. We keep
    /// the LATEST usage seen per id (subtract the prior value, add the new) and sum over unique
    /// ids, so totals are O(1) per line and never double-count — this is token-dashboard's core
    /// correctness fix (item [6] verifies it against a known transcript).
    /// </para>
    ///
    /// <para>
    /// Pricing lives elsewhere (PricingTable, item [2]); this service only COUNTS tokens, but it
    /// records a per-model breakdown so the model named on each message can be priced correctly
    /// even across a mid-session <c>/model</c> switch. Subagent tokens (item [1]) are folded into
    /// the same session total with a separate subtotal so a heavy fan-out is never undercounted.
    /// </para>
    ///
    /// Thread-safety: <see cref="ProcessTranscriptFile"/> may run on background poll threads, so
    /// every per-session mutation is guarded by that session's own lock.
    /// </summary>
    public sealed class TokenMeterService
    {
        /// <summary>
        /// Rolling window (minutes) over which the burn rate (tokens/min) is averaged. A short
        /// window tracks the current pace; too short and a single idle gap zeroes it out.
        /// </summary>
        private const double BurnWindowMinutes = 5.0;

        /// <summary>Cap on bytes read per incremental tail call, so a single poll can't read an
        /// unbounded backlog into memory at once (it catches up over subsequent polls).</summary>
        private const int MaxTailBytesPerCall = 4 * 1024 * 1024;

        /// <summary>Hard cap on distinct message ids retained per session — a safety bound against a
        /// crafted/runaway transcript emitting endless unique ids into the singleton's dedup ledger
        /// (codex-security HIGH). 100k turns is far beyond any real session.</summary>
        private const int MaxMessagesPerSession = 100_000;

        /// <summary>Reject an absurdly long message.id rather than retain attacker-sized strings.</summary>
        private const int MaxMessageIdLength = 256;

        /// <summary>Model id is truncated to this length before it keys the per-model breakdown.</summary>
        private const int MaxModelLength = 64;

        private readonly ConcurrentDictionary<string, SessionAccumulator> _sessions =
            new ConcurrentDictionary<string, SessionAccumulator>(StringComparer.Ordinal);

        // Per-file tail state (offset + file identity) — the incremental-tail ledger (item [7]).
        private readonly ConcurrentDictionary<string, FileTailState> _fileState =
            new ConcurrentDictionary<string, FileTailState>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Current immutable snapshot for a session, or null if nothing counted yet.</summary>
        public TokenMeterSnapshot GetSnapshot(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return null;
            if (!_sessions.TryGetValue(sessionId, out SessionAccumulator acc)) return null;
            lock (acc.Lock)
            {
                return acc.ToSnapshot(BurnWindowMinutes);
            }
        }

        /// <summary>
        /// Drop a session's accumulated state (its dedup ledger, per-model totals, and burn samples)
        /// and any file-tail offsets under it. Called when a terminal closes so the singleton doesn't
        /// retain every session for the app's lifetime (codex-security HIGH / code-reviewer MINOR).
        /// </summary>
        public void Forget(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            _sessions.TryRemove(sessionId, out _);

            // Purge EVERY file-tail offset owned by this session (main transcript AND every subagent
            // agent-*.jsonl), not just the main path. Otherwise, after close+reopen of a resumed
            // session, the main transcript re-scans from 0 but stale subagent offsets stay at EOF and
            // the subagent tokens are never replayed — a silent undercount. [codex-adversary HIGH]
            foreach (KeyValuePair<string, FileTailState> kv in _fileState)
            {
                if (string.Equals(kv.Value.SessionId, sessionId, StringComparison.Ordinal))
                {
                    _fileState.TryRemove(kv.Key, out _);
                }
            }
        }

        /// <summary>
        /// Incrementally tail a transcript file: read only the bytes appended since the last call
        /// for this path, fold each complete line into <paramref name="sessionId"/>'s total, and
        /// return the fresh snapshot (or null if nothing new). The byte-offset ledger means a long
        /// session is never re-scanned per poll (item [7]); a partial trailing line is left for the
        /// next call by only advancing past the last newline. <paramref name="isSubagent"/> routes
        /// an <c>agent-*.jsonl</c> into the subagent subtotal (item [1]).
        /// </summary>
        public TokenMeterSnapshot ProcessTranscriptFile(string sessionId, string filePath, bool isSubagent = false)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(filePath)) return null;

            // Confine to a Claude transcript JSONL under ~/.claude, even if a caller is tricked (by a
            // writable temp file) into passing some other path — the meter's own trust-boundary guard,
            // in addition to the caller's sessionId validation. The extension alone only narrows the
            // arbitrary-file-open; the canonical-root check actually bounds it. [codex-security A01/A04]
            if (!filePath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return null;
            string canonicalPath;
            try
            {
                canonicalPath = Path.GetFullPath(filePath);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return null;
            }

            string claudeRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
                + Path.DirectorySeparatorChar;
            if (!canonicalPath.StartsWith(claudeRoot, StringComparison.OrdinalIgnoreCase)) return null;

            FileTailState prevState = _fileState.TryGetValue(filePath, out FileTailState st) ? st : null;
            long startOffset = prevState?.Offset ?? 0;
            byte[] buffer;
            long creationTicks, lastWriteTicks;
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                long length = fs.Length;
                creationTicks = File.GetCreationTimeUtc(filePath).Ticks;
                lastWriteTicks = File.GetLastWriteTimeUtc(filePath).Ticks;

                // Detect a file REPLACED at the same path (resume / compaction / rotation), not a
                // pure append: a new creation time, the file shrank, or a same-size-or-smaller file
                // written more recently than our last read. Any of these means the bytes before our
                // offset are no longer the bytes we already counted — re-scan from 0. The dedup-by-id
                // ledger makes the re-read idempotent, so re-scanning can't double-count.
                // [codex-security/adversary HIGH; debugger MEDIUM — same-size-rewrite skip]
                if (prevState != null
                    && (creationTicks != prevState.CreationUtcTicks
                        || length < startOffset
                        || (length <= startOffset && lastWriteTicks > prevState.LastWriteUtcTicks)))
                {
                    startOffset = 0;
                }

                if (length == startOffset)
                {
                    // Nothing new to read. Persist identity so a later same-size rewrite is detected.
                    _fileState[filePath] = new FileTailState(sessionId, startOffset, creationTicks, lastWriteTicks);
                    return null;
                }

                fs.Seek(startOffset, SeekOrigin.Begin);
                int toRead = (int)Math.Min(length - startOffset, MaxTailBytesPerCall);
                buffer = new byte[toRead];
                int read = fs.Read(buffer, 0, toRead);
                if (read <= 0) return null;
                if (read != toRead) Array.Resize(ref buffer, read);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null; // locked/torn — try again next poll.
            }

            // Consume only up to the last complete line; a trailing partial line is re-read next
            // call. Newline is ASCII 0x0A, so cutting here never splits a multi-byte UTF-8 char.
            int lastNl = Array.LastIndexOf(buffer, (byte)'\n');
            if (lastNl < 0)
            {
                // No complete line yet. Don't re-read the same bytes forever if the chunk was capped
                // (a pathologically long line exceeding the cap) — skip past it.
                if (buffer.Length >= MaxTailBytesPerCall)
                {
                    _fileState[filePath] = new FileTailState(sessionId, startOffset + buffer.Length, creationTicks, lastWriteTicks);
                }

                return null;
            }

            _fileState[filePath] = new FileTailState(sessionId, startOffset + lastNl + 1, creationTicks, lastWriteTicks);
            string text = Encoding.UTF8.GetString(buffer, 0, lastNl + 1);

            SessionAccumulator acc = _sessions.GetOrAdd(sessionId, id => new SessionAccumulator(id));
            bool any = false;
            foreach (string line in text.Split('\n'))
            {
                if (line.Length == 0) continue;
                ParsedUsage? parsed = TryParseUsage(line);
                if (parsed == null) continue;
                lock (acc.Lock)
                {
                    if (acc.Apply(parsed.Value, isSubagent, BurnWindowMinutes)) any = true;
                }
            }

            if (!any) return null;

            lock (acc.Lock)
            {
                return acc.ToSnapshot(BurnWindowMinutes);
            }
        }

        /// <summary>
        /// Parse a JSONL line into its billable usage, or null when the line carries none.
        /// Only assistant messages with a <c>message.usage</c> block and a <c>message.id</c>
        /// (the dedup key) are counted.
        /// </summary>
        private static ParsedUsage? TryParseUsage(string rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson)) return null;

            try
            {
                using JsonDocument doc = JsonDocument.Parse(rawJson);
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;

                // Only assistant messages carry billable usage; skip user/system/tool lines fast.
                if (root.TryGetProperty("type", out JsonElement typeEl)
                    && typeEl.ValueKind == JsonValueKind.String
                    && !string.Equals(typeEl.GetString(), "assistant", StringComparison.Ordinal))
                {
                    return null;
                }

                if (!root.TryGetProperty("message", out JsonElement message)
                    || message.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                string id = GetString(message, "id");
                if (string.IsNullOrEmpty(id)) return null;          // no dedup key → can't count safely.
                if (id.Length > MaxMessageIdLength) return null;     // reject attacker-sized ids (codex-security HIGH).

                if (!message.TryGetProperty("usage", out JsonElement usage)
                    || usage.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                long input = GetLong(usage, "input_tokens") ?? 0;
                long output = GetLong(usage, "output_tokens") ?? 0;
                long cacheRead = GetLong(usage, "cache_read_input_tokens") ?? 0;
                long cacheCreate = GetLong(usage, "cache_creation_input_tokens") ?? 0;

                // A line with a usage object but all-zero counts (rare partial) carries no signal.
                if (input == 0 && output == 0 && cacheRead == 0 && cacheCreate == 0) return null;

                string model = GetString(message, "model") ?? "unknown";
                if (model.Length > MaxModelLength) model = model.Substring(0, MaxModelLength); // bound retained string size.

                return new ParsedUsage
                {
                    MessageId = id,
                    Model = model,
                    SessionId = GetString(root, "sessionId") ?? string.Empty,
                    InputTokens = input,
                    OutputTokens = output,
                    CacheReadTokens = cacheRead,
                    CacheCreationTokens = cacheCreate,
                    TimestampUtc = ParseTimestamp(root),
                };
            }
            catch (JsonException)
            {
                // Torn/partial line from a concurrent writer — skip; the complete append follows.
                return null;
            }
        }

        private static DateTime ParseTimestamp(JsonElement root)
        {
            string ts = GetString(root, "timestamp");
            if (!string.IsNullOrEmpty(ts)
                && DateTime.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed))
            {
                return parsed;
            }

            return DateTime.UtcNow;
        }

        private static string GetString(JsonElement obj, string prop) =>
            obj.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static long? GetLong(JsonElement obj, string prop) =>
            obj.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n)
                ? n
                : (long?)null;

        /// <summary>Immutable parse result for one transcript line.</summary>
        private struct ParsedUsage
        {
            public string MessageId;
            public string Model;
            public string SessionId;
            public long InputTokens;
            public long OutputTokens;
            public long CacheReadTokens;
            public long CacheCreationTokens;
            public DateTime TimestampUtc;

            // Set when the record is stored, so a later streaming back-out adjusts the right bucket.
            public bool IsSubagent;

            public long Total => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;
        }

        /// <summary>
        /// Per-file tail position plus the file identity at that position, so a file replaced at the
        /// same path (resume / compaction / rotation) is detected and re-scanned rather than tailed
        /// from a now-meaningless offset.
        /// </summary>
        private sealed class FileTailState
        {
            public FileTailState(string sessionId, long offset, long creationUtcTicks, long lastWriteUtcTicks)
            {
                this.SessionId = sessionId;
                this.Offset = offset;
                this.CreationUtcTicks = creationUtcTicks;
                this.LastWriteUtcTicks = lastWriteUtcTicks;
            }

            /// <summary>Session this file's tokens are folded into — lets Forget(sessionId) sweep it.</summary>
            public string SessionId { get; }

            public long Offset { get; }

            public long CreationUtcTicks { get; }

            public long LastWriteUtcTicks { get; }
        }

        /// <summary>
        /// Mutable running total for one session. All access is under <see cref="Lock"/>.
        /// Totals are maintained incrementally (subtract-old/add-new on a repeated message id)
        /// so applying a line is O(1) regardless of session length.
        /// </summary>
        private sealed class SessionAccumulator
        {
            private readonly string _sessionId;

            // Last usage seen per message id — the dedup ledger. Repeats overwrite (latest wins).
            private readonly Dictionary<string, ParsedUsage> _byId = new Dictionary<string, ParsedUsage>(StringComparer.Ordinal);

            // Per-model running totals (for pricing across a mid-session /model switch, item [2]).
            private readonly Dictionary<string, ModelTokenTotals> _byModel = new Dictionary<string, ModelTokenTotals>(StringComparer.Ordinal);

            // Rolling per-turn (timestamp, totalTokens) samples for the burn-rate window.
            private readonly LinkedList<(DateTime When, long Tokens)> _burnSamples = new LinkedList<(DateTime, long)>();

            private long _input;
            private long _output;
            private long _cacheRead;
            private long _cacheCreate;
            private long _subagentTotal;
            private DateTime? _lastTurnUtc;

            public SessionAccumulator(string sessionId) => _sessionId = sessionId;

            public object Lock { get; } = new object();

            /// <summary>
            /// Fold a line into the totals. Returns false (no event) when a repeated id carried
            /// identical usage, so a re-emitted line never triggers a redundant UI push.
            /// </summary>
            public bool Apply(ParsedUsage u, bool isSubagent, double burnWindowMinutes)
            {
                // Bound the dedup ledger: once a session has retained an absurd number of distinct
                // ids, stop adding NEW ones (existing ids still update). Guards against a crafted or
                // runaway transcript bloating the singleton (codex-security HIGH).
                if (!_byId.ContainsKey(u.MessageId) && _byId.Count >= MaxMessagesPerSession) return false;

                if (_byId.TryGetValue(u.MessageId, out ParsedUsage prev))
                {
                    if (UsageEquals(prev, u)) return false; // identical re-emit — nothing changed.

                    // Same id, grown usage (streaming): back out the prior contribution first.
                    _input -= prev.InputTokens;
                    _output -= prev.OutputTokens;
                    _cacheRead -= prev.CacheReadTokens;
                    _cacheCreate -= prev.CacheCreationTokens;
                    if (prev.IsSubagent) _subagentTotal -= prev.Total;
                    AddToModel(prev.Model, -prev.InputTokens, -prev.OutputTokens, -prev.CacheReadTokens, -prev.CacheCreationTokens);
                }

                _input += u.InputTokens;
                _output += u.OutputTokens;
                _cacheRead += u.CacheReadTokens;
                _cacheCreate += u.CacheCreationTokens;
                if (isSubagent) _subagentTotal += u.Total;
                AddToModel(u.Model, u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheCreationTokens);

                // Stamp the subagent flag onto the stored record so a later back-out is correct.
                u.IsSubagent = isSubagent;
                _byId[u.MessageId] = u;

                if (_lastTurnUtc == null || u.TimestampUtc > _lastTurnUtc) _lastTurnUtc = u.TimestampUtc;

                long deltaTokens = u.Total - (prev.MessageId == null ? 0 : prev.Total);
                if (deltaTokens > 0)
                {
                    _burnSamples.AddLast((u.TimestampUtc, deltaTokens));
                    TrimBurnSamples(u.TimestampUtc, burnWindowMinutes);
                }

                return true;
            }

            public TokenMeterSnapshot ToSnapshot(double burnWindowMinutes)
            {
                // Age burn samples against WALL CLOCK, not the last transcript timestamp, so an idle
                // session's rate decays to 0 instead of freezing at the last active pace.
                // [codex-adversary MEDIUM — burn never ages out]
                TrimBurnSamples(DateTime.UtcNow, burnWindowMinutes);

                var byModel = _byModel.ToDictionary(
                    kv => kv.Key,
                    kv => new ModelTokenTotals
                    {
                        InputTokens = kv.Value.InputTokens,
                        OutputTokens = kv.Value.OutputTokens,
                        CacheReadTokens = kv.Value.CacheReadTokens,
                        CacheCreationTokens = kv.Value.CacheCreationTokens,
                    },
                    StringComparer.Ordinal);

                return new TokenMeterSnapshot
                {
                    SessionId = _sessionId,
                    InputTokens = _input,
                    OutputTokens = _output,
                    CacheReadTokens = _cacheRead,
                    CacheCreationTokens = _cacheCreate,
                    SubagentTokens = _subagentTotal,
                    TokensPerMinute = ComputeBurn(burnWindowMinutes),
                    LastTurnUtc = _lastTurnUtc,
                    ByModel = byModel,
                };
            }

            private void AddToModel(string model, long input, long output, long cacheRead, long cacheCreate)
            {
                if (!_byModel.TryGetValue(model, out ModelTokenTotals t))
                {
                    t = new ModelTokenTotals();
                    _byModel[model] = t;
                }

                t.InputTokens += input;
                t.OutputTokens += output;
                t.CacheReadTokens += cacheRead;
                t.CacheCreationTokens += cacheCreate;
            }

            private void TrimBurnSamples(DateTime now, double windowMinutes)
            {
                DateTime cutoff = now.AddMinutes(-windowMinutes);
                while (_burnSamples.First != null && _burnSamples.First.Value.When < cutoff)
                {
                    _burnSamples.RemoveFirst();
                }
            }

            private double ComputeBurn(double windowMinutes)
            {
                // A rate needs at least two points in time. With a single sample the elapsed span is
                // zero and any floor denominator yields an absurd spike (~300x), so report no rate
                // until a second turn establishes a real interval. [debugger/adversary LOW]
                if (_burnSamples.Count < 2) return 0;

                long tokens = 0;
                foreach ((DateTime _, long t) in _burnSamples) tokens += t;

                // Average over the actual elapsed span (capped at the window) so a short burst
                // right after start doesn't report an absurd rate from a near-zero denominator.
                DateTime first = _burnSamples.First!.Value.When;
                DateTime last = _burnSamples.Last!.Value.When;
                double spanMin = Math.Max((last - first).TotalMinutes, 0);
                double denom = Math.Min(Math.Max(spanMin, 1.0 / 60.0), windowMinutes);
                return tokens / denom;
            }

            private static bool UsageEquals(ParsedUsage a, ParsedUsage b) =>
                a.InputTokens == b.InputTokens
                && a.OutputTokens == b.OutputTokens
                && a.CacheReadTokens == b.CacheReadTokens
                && a.CacheCreationTokens == b.CacheCreationTokens
                && string.Equals(a.Model, b.Model, StringComparison.Ordinal);
        }
    }

    /// <summary>Per-model token subtotals so item [2] can price each model separately.</summary>
    public sealed class ModelTokenTotals
    {
        public long InputTokens { get; set; }

        public long OutputTokens { get; set; }

        public long CacheReadTokens { get; set; }

        public long CacheCreationTokens { get; set; }

        public long Total => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;
    }

    /// <summary>
    /// Immutable point-in-time view of a session's token totals, handed to the status bar.
    /// All counts are cumulative for the session (subagents included); <see cref="SubagentTokens"/>
    /// is the subagent slice of that total, not an additional amount.
    /// </summary>
    public sealed class TokenMeterSnapshot
    {
        public string SessionId { get; set; } = string.Empty;

        public long InputTokens { get; set; }

        public long OutputTokens { get; set; }

        public long CacheReadTokens { get; set; }

        public long CacheCreationTokens { get; set; }

        /// <summary>Subagent slice of the total (item [1]); included in <see cref="TotalTokens"/>.</summary>
        public long SubagentTokens { get; set; }

        /// <summary>Rolling burn rate in tokens/min over the service's burn window.</summary>
        public double TokensPerMinute { get; set; }

        public DateTime? LastTurnUtc { get; set; }

        /// <summary>Per-model breakdown for pricing (item [2]); keyed by the message's model id.</summary>
        public IReadOnlyDictionary<string, ModelTokenTotals> ByModel { get; set; } =
            new Dictionary<string, ModelTokenTotals>(StringComparer.Ordinal);

        /// <summary>All billable token volume: input + output + cache-read + cache-creation.</summary>
        public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;

        /// <summary>
        /// Cache slice of the total (cache-read + cache-creation). On long sessions this dominates
        /// because every turn re-reads the cached prompt prefix, so the banner shows it separately
        /// from <see cref="NonCacheTokens"/> to explain why the cumulative total dwarfs context size.
        /// </summary>
        public long CacheTokens => CacheReadTokens + CacheCreationTokens;

        /// <summary>Fresh (non-cache) token volume: input + output. The "real work" slice of the total.</summary>
        public long NonCacheTokens => InputTokens + OutputTokens;
    }
}
