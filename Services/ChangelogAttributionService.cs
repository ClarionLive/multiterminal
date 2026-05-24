using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Phase 4a skeleton (task d42423e3 D3). Coordinates a small list of
    /// <see cref="IChangelogParser"/> implementations and asks each one
    /// whether a working-tree file should be auto-linked to a kanban task.
    ///
    /// <para>The constructor accepts the parser list directly — per D3
    /// ("Don't build a parser registry on day one") there is intentionally
    /// no DI container hookup, no plugin discovery, no parser ordering
    /// policy. <see cref="MainForm"/> (or whoever wires the service in
    /// Phase 4b) constructs the list once with the parsers it wants and
    /// passes them in.</para>
    ///
    /// <para>Phase 4b's <c>ProjectJsonChangelogParser</c> is the v1 (and
    /// only) implementation. Adding <c>CHANGELOG.md</c> later means writing
    /// a new <see cref="IChangelogParser"/>, instantiating it, and pushing
    /// it into the list at construction. No service-side changes needed.</para>
    ///
    /// <para>Phase 4a ships the seam only — no parser implementations and
    /// no wiring into <c>BuildWorkingChanges</c>. The empty service is
    /// instantiable and answers "no attribution" for everything.</para>
    /// </summary>
    public sealed class ChangelogAttributionService
    {
        private readonly IReadOnlyList<IChangelogParser> _parsers;

        /// <summary>
        /// Construct with an explicit parser list. The list is snapshotted —
        /// later mutations of the source collection do not affect the
        /// service. Pass <c>null</c> or an empty list for the no-op service
        /// (every <see cref="AttributeFile"/> call returns an empty result).
        /// </summary>
        public ChangelogAttributionService(IList<IChangelogParser> parsers)
        {
            if (parsers == null || parsers.Count == 0)
            {
                _parsers = Array.Empty<IChangelogParser>();
                return;
            }
            // Snapshot — defending against the caller mutating the source
            // list after construction. ToArray-equivalent.
            var snap = new IChangelogParser[parsers.Count];
            int i = 0;
            foreach (var p in parsers)
            {
                if (p != null) snap[i++] = p;
            }
            if (i == parsers.Count)
            {
                _parsers = snap;
            }
            else
            {
                // Drop nulls — silently. Most likely scenario is a caller
                // composed the list with a conditional `cond ? parser : null`
                // pattern and forgot to filter.
                var trimmed = new IChangelogParser[i];
                Array.Copy(snap, trimmed, i);
                _parsers = trimmed;
            }
        }

        /// <summary>True when at least one parser is registered.</summary>
        public bool HasParsers => _parsers.Count > 0;

        /// <summary>
        /// Ask every registered parser whether <paramref name="filePath"/>
        /// should auto-attribute to one or more tasks. Returns the union of
        /// all parsers' results. Empty list = no parser matched (the caller
        /// should fall back to the "Needs a quick task" manual flow).
        ///
        /// <para>Per-parser exceptions are swallowed (logged to
        /// <see cref="Trace.WriteLine(string)"/>) so one buggy parser
        /// can't break working-tree-changes rendering for everyone else.
        /// Per-parser null returns are treated as empty.</para>
        /// </summary>
        public IList<ChangelogAttribution> AttributeFile(string repoRoot, string filePath)
        {
            // Fast path for the empty-service case (Phase 4a default state).
            if (_parsers.Count == 0) return Array.Empty<ChangelogAttribution>();
            if (string.IsNullOrEmpty(filePath)) return Array.Empty<ChangelogAttribution>();

            List<ChangelogAttribution> results = null;
            foreach (var parser in _parsers)
            {
                IEnumerable<ChangelogAttribution> parsed;
                try
                {
                    parsed = parser.Parse(repoRoot, filePath);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[ChangelogAttributionService] parser '{parser.Name}' threw on '{filePath}': {ex.Message}");
                    continue;
                }
                if (parsed == null) continue;

                foreach (var attr in parsed)
                {
                    if (attr == null || string.IsNullOrEmpty(attr.TaskId)) continue;
                    // Stamp the parser name on the way out so consumers don't
                    // have to know which parser produced which attribution.
                    // Respect an already-populated ParserName (defensive — a
                    // parser may legitimately want to claim a different name,
                    // e.g. when one parser delegates to a sub-parser).
                    if (string.IsNullOrEmpty(attr.ParserName))
                        attr.ParserName = parser.Name;
                    (results ??= new List<ChangelogAttribution>()).Add(attr);
                }
            }
            return (IList<ChangelogAttribution>)results ?? Array.Empty<ChangelogAttribution>();
        }
    }
}
