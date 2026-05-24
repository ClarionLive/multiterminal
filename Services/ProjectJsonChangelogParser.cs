using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Phase 4b parser (task d42423e3 D3) — reads <c>.claude/project.json</c>
    /// and extracts task IDs referenced in the <c>changeLog</c> entries so the
    /// HUD Git auto-link pass can route a freshly-edited <c>project.json</c>
    /// to the right kanban task instead of the "Needs a quick task" bucket.
    ///
    /// <para>The <c>changeLog</c> field is a single markdown string whose
    /// entries each carry an authoritative <c>[Task #abcd1234]</c> trailer.
    /// We match ONLY that strict trailer form — free-form prose mentions
    /// (e.g. <c>(78d29502)</c>) are NOT accepted because a hostile
    /// <c>.claude/project.json</c> could plant arbitrary 8-hex tokens in
    /// body text to steal attribution. The trailer form is the convention
    /// emitted by changelog-catchup commits and is the only authoritative
    /// ownership tag in this schema. We de-duplicate within a single parse
    /// — a file edit that touches three entries for the same task should
    /// produce one attribution, not three.</para>
    ///
    /// <para>Ordering contract: this parser preserves the iteration order of
    /// the source <c>changeLog</c>. The shipped convention is
    /// <b>newest-on-top</b> (most-recent entry first), so the first item in
    /// the returned IEnumerable is the most-recent task ID. Callers
    /// (notably <c>BuildWorkingChanges</c>) treat that first match as the
    /// primary attribution.</para>
    ///
    /// <para>Pure read. Wrapped in try/catch end-to-end so a malformed JSON,
    /// permission error, or unexpected schema shift can never break the
    /// working-tree refresh that calls us. Returns empty on any failure.
    /// Bounded by <see cref="MaxFileBytes"/> and <see cref="MaxChangelogChars"/>
    /// to prevent a hostile repo from triggering unbounded allocation or
    /// CPU burn on every working-tree refresh.</para>
    /// </summary>
    public sealed class ProjectJsonChangelogParser : IChangelogParser
    {
        /// <inheritdoc/>
        public string Name => "ProjectJsonChangelogParser";

        /// <summary>
        /// Strict trailer form ONLY: <c>[Task #abcd1234]</c> at end-of-line
        /// (optionally followed by trailing whitespace). Case-insensitive on
        /// the hex digits to tolerate uppercase variants. Permissive forms
        /// (bare 8-hex tokens, parenthesized refs, mid-line brackets) are
        /// deliberately rejected — those appear in prose and are NOT
        /// authoritative ownership tags.
        ///
        /// <para>The <c>\s*$</c> anchor with <see cref="RegexOptions.Multiline"/>
        /// requires the bracket to be the LAST token on its line. Without the
        /// anchor, a hostile changelog line like
        /// <c>"follow-up to [Task #deadbeef] before landing [Task #feedc0de]"</c>
        /// could mint the first inline mention as the primary attribution
        /// even though the trailer (<c>feedc0de</c>) is the authoritative
        /// owner. With the anchor, only the trailer matches; inline mentions
        /// are silently dropped. The current parser splits on <c>\n</c> and
        /// matches per line, so <see cref="RegexOptions.Multiline"/> is
        /// belt-and-suspenders for any future refactor that runs the regex
        /// against the whole string.</para>
        /// </summary>
        private static readonly Regex TaskIdPattern = new Regex(
            @"\[Task #(?<id>[0-9a-fA-F]{8})\]\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

        // DoS guard: project.json is normally a few KB. Anything beyond 1MB
        // is almost certainly hostile or corrupted — refuse to read.
        private const int MaxFileBytes = 1_000_000;

        // DoS guard: even with a valid JSON shell, an enormous changeLog
        // string would make regex evaluation linear in length per refresh.
        // 100K chars is ~2000 typical entries — well above any realistic
        // project's history.
        private const int MaxChangelogChars = 100_000;

        // Cap the per-line snippet captured in the Reason string so the
        // link description stays readable in the task panel. 160 chars
        // leaves room for the "project-json: matched '...'" wrapper.
        private const int MaxSnippetLength = 160;

        /// <inheritdoc/>
        public IEnumerable<ChangelogAttribution> Parse(string repoRoot, string filePath)
        {
            // Defensive: parser must not throw on bad input. Any exception
            // becomes an empty result so the caller's working-tree refresh
            // keeps rendering.
            try
            {
                if (string.IsNullOrEmpty(repoRoot) || string.IsNullOrEmpty(filePath))
                {
                    return Array.Empty<ChangelogAttribution>();
                }

                // Only parse .claude/project.json — every other file is a fast no-op.
                // Resolve via Path.Combine so we compare canonical absolute paths
                // (handles trailing-slash drift and mixed separators on Windows).
                string expected;
                try
                {
                    expected = Path.GetFullPath(Path.Combine(repoRoot, ".claude", "project.json"));
                }
                catch
                {
                    return Array.Empty<ChangelogAttribution>();
                }

                string actual;
                try
                {
                    actual = Path.GetFullPath(filePath);
                }
                catch
                {
                    return Array.Empty<ChangelogAttribution>();
                }

                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return Array.Empty<ChangelogAttribution>();
                }

                if (!File.Exists(actual))
                {
                    return Array.Empty<ChangelogAttribution>();
                }

                // DoS guard: bail before File.ReadAllText if the file is
                // larger than any realistic .claude/project.json. A hostile
                // repo could otherwise force the parser to allocate
                // megabytes/gigabytes on every working-tree refresh.
                try
                {
                    var fi = new FileInfo(actual);
                    if (fi.Length > MaxFileBytes)
                    {
                        System.Diagnostics.Trace.WriteLine(
                            $"[ProjectJsonChangelogParser] skipping '{actual}' — file size {fi.Length} exceeds MaxFileBytes {MaxFileBytes}");
                        return Array.Empty<ChangelogAttribution>();
                    }
                }
                catch
                {
                    return Array.Empty<ChangelogAttribution>();
                }

                string json;
                try
                {
                    json = File.ReadAllText(actual);
                }
                catch
                {
                    return Array.Empty<ChangelogAttribution>();
                }

                if (string.IsNullOrWhiteSpace(json))
                {
                    return Array.Empty<ChangelogAttribution>();
                }

                string changeLog = null;
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        return Array.Empty<ChangelogAttribution>();
                    }

                    // The shipped schema uses a camelCase `changeLog` field
                    // (single markdown string). Fall back to `changelog` for
                    // forward compatibility with any future rename.
                    if (doc.RootElement.TryGetProperty("changeLog", out var cl) && cl.ValueKind == JsonValueKind.String)
                    {
                        changeLog = cl.GetString();
                    }
                    else if (doc.RootElement.TryGetProperty("changelog", out var cl2) && cl2.ValueKind == JsonValueKind.String)
                    {
                        changeLog = cl2.GetString();
                    }
                }
                catch
                {
                    return Array.Empty<ChangelogAttribution>();
                }

                if (string.IsNullOrEmpty(changeLog))
                {
                    return Array.Empty<ChangelogAttribution>();
                }

                // DoS guard: cap the changelog text we process. Even with
                // valid JSON, an unbounded changelog would make regex
                // evaluation O(n) per refresh.
                if (changeLog.Length > MaxChangelogChars)
                {
                    System.Diagnostics.Trace.WriteLine(
                        $"[ProjectJsonChangelogParser] skipping '{actual}' — changeLog length {changeLog.Length} exceeds MaxChangelogChars {MaxChangelogChars}");
                    return Array.Empty<ChangelogAttribution>();
                }

                // Walk the changelog line-by-line so the Reason string can
                // quote the matched entry verbatim — reviewers tracing an
                // auto-link want to see WHICH line produced it, not just
                // "matched somewhere in changeLog".
                //
                // Iteration order: the shipped convention is newest-on-top.
                // Splitting on '\n' and iterating yields entries in
                // newest-first order, which is the IChangelogParser ordering
                // contract callers rely on (first item = most-recent task).
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var results = new List<ChangelogAttribution>();
                foreach (var rawLine in changeLog.Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(rawLine)) continue;
                    var line = rawLine.TrimEnd('\r');
                    var matches = TaskIdPattern.Matches(line);
                    if (matches.Count == 0) continue;

                    foreach (Match m in matches)
                    {
                        var idGroup = m.Groups["id"];
                        if (idGroup == null || !idGroup.Success) continue;
                        // Normalize to lower-case so dedup matches the 8-char
                        // id-key convention used by MessageBroker._tasks.
                        var id = idGroup.Value.ToLowerInvariant();
                        if (!seen.Add(id)) continue;

                        // Trim long lines for the Reason string so the
                        // link description stays readable in the task panel.
                        var snippet = line.Trim();
                        if (snippet.Length > MaxSnippetLength)
                        {
                            snippet = snippet.Substring(0, MaxSnippetLength - 3) + "...";
                        }

                        results.Add(new ChangelogAttribution
                        {
                            TaskId = id,
                            Reason = $"project-json: matched '{snippet}'",
                            // ParserName left null — ChangelogAttributionService
                            // stamps it from IChangelogParser.Name on the way out.
                        });
                    }
                }

                if (results.Count == 0) return Array.Empty<ChangelogAttribution>();
                return results;
            }
            catch
            {
                // Total belt-and-suspenders: the inner branches all return
                // empty on failure, but a defensive outer catch guarantees
                // the contract even if a new code path is added later.
                return Array.Empty<ChangelogAttribution>();
            }
        }
    }
}
