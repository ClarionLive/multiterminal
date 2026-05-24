using System.Collections.Generic;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Phase 4a seam for changelog-style auto-attribution (task d42423e3 D3).
    /// A parser inspects a single working-tree file and returns the task(s)
    /// it should be auto-linked to. Implementations are file-format-specific:
    /// the v1 implementation (Phase 4b) targets <c>.claude/project.json</c>;
    /// later additions (<c>CHANGELOG.md</c>, <c>RELEASES.md</c>) plug in
    /// without touching <see cref="ChangelogAttributionService"/>.
    ///
    /// Contract:
    ///   - Pure read. MUST NOT touch the database, network, or shell.
    ///   - Return empty (NOT null) when the parser doesn't recognize the
    ///     file or finds no matches. Returning null is a contract violation
    ///     but <see cref="ChangelogAttributionService"/> tolerates it
    ///     defensively.
    ///   - MUST NOT throw on malformed input — wrap parse failures into an
    ///     empty result. The service catches anyway, but a parser that
    ///     throws on every call is noisy in Debug logs.
    ///   - <paramref name="filePath"/> is absolute; resolve repo-relative
    ///     internally if needed via <paramref name="repoRoot"/>.
    /// </summary>
    public interface IChangelogParser
    {
        /// <summary>
        /// Short identifier (e.g. <c>"project-json"</c>) included in
        /// <see cref="ChangelogAttribution.Reason"/> so reviewers can tell
        /// which parser produced an attribution. Should be stable across
        /// versions — used in commit-message-style strings.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Inspect <paramref name="filePath"/> and return any attributions
        /// it implies. The same file may produce multiple attributions if
        /// it references multiple task IDs (e.g. a project.json changelog
        /// entry that says "consolidates 29ae1e99 + d29512ef").
        ///
        /// <para>Ordering contract: results MUST be returned in
        /// most-recent-first order. The HUD Git auto-link pass treats the
        /// first item in the IEnumerable as the primary attribution and
        /// renders the file under that task's group, so newer ownership
        /// must win over older mentions.</para>
        /// </summary>
        IEnumerable<ChangelogAttribution> Parse(string repoRoot, string filePath);
    }

    /// <summary>
    /// One auto-attribution result from a changelog parser. Consumed by
    /// <see cref="ChangelogAttributionService"/> and (in Phase 4b) by
    /// <c>BuildWorkingChanges</c> which writes the row into
    /// <c>task_file_links</c> and skips the "Needs a quick task" prompt.
    /// </summary>
    public sealed class ChangelogAttribution
    {
        /// <summary>Target task ID this file should link to.</summary>
        public string TaskId { get; set; }

        /// <summary>
        /// Human-readable explanation surfaced in agent logs and (eventually)
        /// in the link's <c>description</c> column so a future reviewer can
        /// tell why this link exists. Example:
        /// <c>"project-json: matched 'changelog catchup for 29ae1e99'"</c>.
        /// </summary>
        public string Reason { get; set; }

        /// <summary>
        /// Identifier of the parser that produced this attribution. Same
        /// value as <see cref="IChangelogParser.Name"/>. Useful when the
        /// service runs multiple parsers and a reviewer needs to debug
        /// which one fired.
        /// </summary>
        public string ParserName { get; set; }
    }
}
