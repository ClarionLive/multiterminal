using System;
using System.IO;
using System.Text.Json;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Reads the per-terminal usage stats that <c>scripts/statusline.js</c> writes
    /// to the temp dir — the same files the HUD status bar polls (see
    /// <see cref="MultiTerminal.Docking.TerminalDocument"/>'s StatusLinePoll). Two
    /// sources (task e855c051):
    /// <list type="bullet">
    ///   <item><b>Per-terminal</b> <c>%TEMP%/mt-statusline-{name}-{docId}.json</c> —
    ///   carries the context-window fill (<c>contextPct</c>) plus a per-terminal copy
    ///   of the quota numbers and a write <c>timestamp</c>.</item>
    ///   <item><b>Shared account quota</b> <c>%TEMP%/mt-statusline-quota.json</c> —
    ///   the authoritative 5h/7d numbers, written by whichever terminal rendered most
    ///   recently so every terminal shows identical account usage.</item>
    /// </list>
    ///
    /// <para>This is the single C# source of truth for the temp-file path convention
    /// and parsing, so callers (the REST stats endpoint now, a phase-2 background
    /// context-threshold monitor later) don't each re-derive it. Context is exposed
    /// as a PERCENTAGE — statusline.js only persists Claude Code's
    /// <c>context_window.used_percentage</c>, not raw token counts.</para>
    ///
    /// <para>Failure-tolerant by design (mirrors statusline.js's self-heal stance): a
    /// missing per-terminal file yields <see cref="TerminalUsageStats.Available"/> ==
    /// false; a corrupt/torn file is treated as unavailable rather than throwing, so a
    /// transient write collision never faults the caller. Stateless; the temp dir and
    /// clock are injectable for deterministic tests.</para>
    /// </summary>
    public sealed class StatusLineStatsReader
    {
        private const string SharedQuotaFileName = "mt-statusline-quota.json";

        /// <summary>
        /// Age (seconds) past which a per-terminal reading is flagged
        /// <see cref="TerminalUsageStats.Stale"/>. statusline.js rewrites on each
        /// Claude Code render, so a fresh, active terminal updates frequently; an idle
        /// one legitimately goes quiet. The flag is advisory — callers decide what to
        /// do with a stale reading.
        /// </summary>
        public const double DefaultStaleThresholdSeconds = 60.0;

        private readonly string _tempDir;
        private readonly Func<DateTimeOffset> _clock;
        private readonly double _staleThresholdSeconds;

        public StatusLineStatsReader(
            string tempDir = null,
            Func<DateTimeOffset> clock = null,
            double staleThresholdSeconds = DefaultStaleThresholdSeconds)
        {
            _tempDir = string.IsNullOrEmpty(tempDir) ? Path.GetTempPath() : tempDir;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            _staleThresholdSeconds = staleThresholdSeconds;
        }

        /// <summary>The per-terminal stats file path for a given name + docId.</summary>
        public string PerTerminalPath(string terminalName, string docId) =>
            Path.Combine(_tempDir, $"mt-statusline-{terminalName}-{docId}.json");

        /// <summary>The shared account-quota file path.</summary>
        public string SharedQuotaPath() => Path.Combine(_tempDir, SharedQuotaFileName);

        /// <summary>
        /// Read usage stats for a terminal. When <paramref name="docId"/> is supplied
        /// the exact per-terminal file is used; otherwise the newest
        /// <c>mt-statusline-{name}-*.json</c> (by the data's own <c>timestamp</c>) is
        /// selected, so a caller that only knows its name still resolves the right
        /// file (and skips a stale sibling/zombie).
        /// </summary>
        public TerminalUsageStats ReadFor(string terminalName, string docId = null)
        {
            var result = new TerminalUsageStats { TerminalName = terminalName };
            if (string.IsNullOrEmpty(terminalName)) return result;

            // SECURITY (CA3003): terminalName/docId reach here straight from the REST
            // route, then become part of a temp-file path. Reject anything outside a
            // strict identifier whitelist so a hostile value can't contain path
            // separators or '..' dot-segments and traverse out of the temp dir. After
            // this gate the only reachable paths are mt-statusline-{safe}-{safe}.json
            // (or the constant shared-quota file) inside _tempDir.
            if (!IsSafeSegment(terminalName)) return result;
            if (!string.IsNullOrEmpty(docId) && !IsSafeSegment(docId)) return result;

            string perTerminalPath = string.IsNullOrEmpty(docId)
                ? FindNewestPerTerminalFile(terminalName)
                : PerTerminalPath(terminalName, docId);

            // CA3003: perTerminalPath is built only from whitelist-validated segments
            // (above) joined to _tempDir — not traversable by request input.
#pragma warning disable CA3003
            if (perTerminalPath == null || !File.Exists(perTerminalPath)) return result;
#pragma warning restore CA3003

            if (!TryReadJson(perTerminalPath, out JsonElement perTerminal)) return result;

            result.Available = true;
            result.Model = GetString(perTerminal, "model");
            // contextPct is read with the fractional-tolerant getter: statusline.js
            // writes Claude Code's used_percentage verbatim, WITHOUT the Math.floor it
            // applies to the quota fields, so a fractional percentage must still parse.
            result.ContextPercent = GetNumberAsInt(perTerminal, "contextPct");
            long? ts = GetLong(perTerminal, "timestamp");
            result.SourceTimestampMs = ts;
            if (ts.HasValue)
            {
                double age = (_clock().ToUnixTimeMilliseconds() - ts.Value) / 1000.0;
                result.AgeSeconds = age;
                // age < 0 ⇒ future-dated file (clock skew or a planted file): a legit
                // file is always written before we read it, so treat future-dated as
                // stale rather than "fresh". [security A04, e855c051]
                result.Stale = age < 0 || age > _staleThresholdSeconds;
            }

            // Account quota: prefer the shared file (authoritative, identical across
            // terminals) ONLY when its own timestamp is present, not future-dated, and
            // within the stale window. A zombie shared file — left behind when the
            // writer stops, or when later renders drop rate-limit data so it is never
            // refreshed — must NOT be presented as a live rate-cap reading. Otherwise
            // fall back to this terminal's own quota copy and inherit its freshness.
            // Quota staleness is surfaced separately (QuotaStale) so a caller never
            // reads a cached quota as current. [adversary HIGH, e855c051]
            bool usedShared = false;
            if (TryReadJson(SharedQuotaPath(), out JsonElement shared))
            {
                long? sharedTs = GetLong(shared, "timestamp");
                if (sharedTs.HasValue)
                {
                    double sharedAge = (_clock().ToUnixTimeMilliseconds() - sharedTs.Value) / 1000.0;
                    // A fresh shared file is preferred only when it actually carries
                    // primary quota data (5h or 7d). statusline.js emits the shared file
                    // whenever rate_limits is present even if its subfields are absent,
                    // so a fresh-but-empty shared file must NOT overwrite a usable
                    // per-terminal quota copy with nulls. [adversary medium, run 2]
                    bool sharedHasQuota = GetNumberAsInt(shared, "quota5h").HasValue
                        || GetNumberAsInt(shared, "quota7d").HasValue;
                    if (sharedAge >= 0 && sharedAge <= _staleThresholdSeconds && sharedHasQuota)
                    {
                        result.QuotaSource = "shared";
                        result.QuotaSourceTimestampMs = sharedTs;
                        result.QuotaAgeSeconds = sharedAge;
                        result.QuotaStale = false;
                        ApplyQuota(shared, result);
                        usedShared = true;
                    }
                }
            }

            if (!usedShared)
            {
                // Shared file missing, corrupt, untimestamped, future-dated, or stale →
                // use this terminal's own quota copy and inherit its freshness.
                result.QuotaSource = "per-terminal";
                result.QuotaSourceTimestampMs = result.SourceTimestampMs;
                result.QuotaAgeSeconds = result.AgeSeconds;
                result.QuotaStale = result.Stale;
                ApplyQuota(perTerminal, result);
            }

            return result;
        }

        private static void ApplyQuota(JsonElement src, TerminalUsageStats result)
        {
            result.FiveHourPercent = GetNumberAsInt(src, "quota5h");
            result.SevenDayPercent = GetNumberAsInt(src, "quota7d");
            result.FiveHourPace = GetNumberAsInt(src, "pace5h");
            result.SevenDayPace = GetNumberAsInt(src, "pace7d");
            result.FiveHourResetIn = GetString(src, "resetIn5h");
            result.IsOffPeak = GetBool(src, "isOffPeak");
        }

        private string FindNewestPerTerminalFile(string terminalName)
        {
            string newest = null;
            long newestTs = long.MinValue;
            string[] candidates;
            try
            {
                // CA3003: terminalName is whitelist-validated by the ReadFor caller
                // before this runs; the glob is confined to _tempDir.
#pragma warning disable CA3003
                candidates = Directory.GetFiles(_tempDir, $"mt-statusline-{terminalName}-*.json");
#pragma warning restore CA3003
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }

            long nowMs = _clock().ToUnixTimeMilliseconds();
            foreach (var path in candidates)
            {
                if (!TryReadJson(path, out JsonElement el)) continue;
                long? tsOpt = GetLong(el, "timestamp");
                if (!tsOpt.HasValue) continue; // can't assess freshness — skip a malformed/untimestamped sibling.
                long ts = tsOpt.Value;
                // Ignore future-dated candidates: a file ahead of our clock is skewed
                // or planted and must not pin selection ahead of the genuine newest
                // file. [security A04, e855c051]
                if (ts > nowMs) continue;
                if (ts > newestTs)
                {
                    newestTs = ts;
                    newest = path;
                }
            }
            return newest;
        }

        /// <summary>
        /// Reads + parses a JSON object, sharing the file (statusline.js may be
        /// mid-rename) and swallowing missing/torn/corrupt files into a clean
        /// <c>false</c> — never throws.
        /// </summary>
        private static bool TryReadJson(string path, out JsonElement element)
        {
            element = default;
            try
            {
                // CA3003: every path reaching here is either the constant shared-quota
                // file or a path built from whitelist-validated segments (see ReadFor),
                // or a real entry returned by Directory.GetFiles over _tempDir.
#pragma warning disable CA3003
                if (!File.Exists(path)) return false;
                string content;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs))
                {
                    content = sr.ReadToEnd();
                }
#pragma warning restore CA3003
                if (string.IsNullOrWhiteSpace(content)) return false;
                using var doc = JsonDocument.Parse(content);
                element = doc.RootElement.Clone();
                return element.ValueKind == JsonValueKind.Object;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// True when <paramref name="s"/> is a safe path segment — letters, digits,
        /// '-' or '_' only. Excludes path separators, ':' and '.' so neither a
        /// directory jump nor a '..' dot-segment is expressible. Matches the actual
        /// shape of terminal names + docIds (WorktreeNaming slugs / hex ids).
        /// </summary>
        private static bool IsSafeSegment(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                    || (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (!ok) return false;
            }
            return true;
        }

        private static string GetString(JsonElement obj, string prop) =>
            obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        /// <summary>
        /// Reads a JSON number as an int, tolerating a fractional value by rounding.
        /// Distinct from a strict int parse because System.Text.Json's
        /// <c>TryGetInt32</c> returns false for ANY number written with a decimal point
        /// (even "43.0"). statusline.js floors the quota fields but writes
        /// <c>contextPct</c> straight from Claude Code's <c>used_percentage</c>, so a
        /// fractional percentage would otherwise silently parse to null and dark the
        /// clear/handoff signal. [debugger/adversary, e855c051]
        /// </summary>
        private static int? GetNumberAsInt(JsonElement obj, string prop)
        {
            if (!obj.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double d))
                return null;
            // Guard the double→int cast: a planted/corrupt temp file could carry an
            // out-of-range value (e.g. 1e30) that would otherwise cast to a garbage int.
            // The strict TryGetInt32 this replaced returned null for out-of-range
            // numbers — preserve that. (NaN can't arrive via System.Text.Json, but the
            // check is cheap insurance.) [debugger LOW, e855c051]
            double rounded = Math.Round(d, MidpointRounding.AwayFromZero);
            if (double.IsNaN(rounded) || rounded < int.MinValue || rounded > int.MaxValue) return null;
            return (int)rounded;
        }

        private static long? GetLong(JsonElement obj, string prop) =>
            obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : (long?)null;

        private static bool? GetBool(JsonElement obj, string prop) =>
            obj.TryGetProperty(prop, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : (bool?)null;
    }

    /// <summary>
    /// Usage stats for one terminal (task e855c051). CONTEXT (window fill →
    /// clear/handoff decision) is deliberately separate from the 5h/7d QUOTA
    /// (rate-cap signal) — they imply different actions and must not be conflated.
    /// All numeric fields are nullable: null means "not reported" (e.g. a non-Claude
    /// terminal, or rate-limit data absent on older Claude Code).
    /// </summary>
    public sealed class TerminalUsageStats
    {
        /// <summary>False when no per-terminal stats file was found/readable.</summary>
        public bool Available { get; set; }

        public string TerminalName { get; set; }

        public string Model { get; set; }

        /// <summary>Context-window fill, 0–100 (Claude Code's used_percentage). The clear/handoff signal.</summary>
        public int? ContextPercent { get; set; }

        /// <summary>5-hour rolling quota used, 0–100.</summary>
        public int? FiveHourPercent { get; set; }

        /// <summary>7-day rolling quota used, 0–100.</summary>
        public int? SevenDayPercent { get; set; }

        /// <summary>5h burn pace vs elapsed window (positive = ahead of pace).</summary>
        public int? FiveHourPace { get; set; }

        /// <summary>7d burn pace vs elapsed window.</summary>
        public int? SevenDayPace { get; set; }

        /// <summary>Human-readable 5h reset countdown (e.g. "2h 15m").</summary>
        public string FiveHourResetIn { get; set; }

        public bool? IsOffPeak { get; set; }

        /// <summary>"shared" (authoritative account file) or "per-terminal" (fallback), or null when unavailable.</summary>
        public string QuotaSource { get; set; }

        /// <summary>Epoch-ms timestamp of the quota reading's source file (shared or per-terminal).</summary>
        public long? QuotaSourceTimestampMs { get; set; }

        /// <summary>Seconds since the quota reading's source file was written (null if no timestamp).</summary>
        public double? QuotaAgeSeconds { get; set; }

        /// <summary>
        /// True when the quota reading is stale — the shared file was too old or
        /// future-dated and we fell back to a per-terminal copy that is itself stale.
        /// Distinct from <see cref="Stale"/> (which tracks the context reading): a
        /// terminal can have a fresh context fill but a stale rate-cap reading.
        /// </summary>
        public bool QuotaStale { get; set; }

        /// <summary>Epoch-ms timestamp the per-terminal file was written.</summary>
        public long? SourceTimestampMs { get; set; }

        /// <summary>Seconds since the per-terminal file was written (null if no timestamp).</summary>
        public double? AgeSeconds { get; set; }

        /// <summary>True when <see cref="AgeSeconds"/> exceeds the reader's stale threshold.</summary>
        public bool Stale { get; set; }
    }
}
