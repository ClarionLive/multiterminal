#nullable enable
using System;
using System.Collections.Generic;

namespace MultiTerminal.MCPServer.Services
{
    /// <summary>
    /// Which pane spawned which helper pane, for <c>close_helper</c> (task 7f389704). A helper may be
    /// closed by an agent only when the request comes from the pane that spawned it.
    ///
    /// <para><b>Keyed on the spawner's proof, never on names.</b> Each entry holds the spawner pane's
    /// DocId AND the launch nonce it proved itself with, and a close must present both. A spawner NAME
    /// is whatever the caller typed (spawn_helper's spawnerName is not checked), so authorizing on it
    /// would let any agent close any helper by claiming its PM's name. The DocId alone is not enough
    /// either (pipeline Run 1, Codex security HIGH): DocIds are visible in terminal listings, and once
    /// the spawner's pane is gone a fresh registration can hold its old DocId with a nonce of its own.
    /// The nonce is a secret MT put only in that pane's environment, so it cannot be learned that way.
    /// A spawn with no resolvable nonce (the phone app, an MCP server predating this ticket) records
    /// nothing, and that pane can only be closed by the Owner.</para>
    ///
    /// <para>In memory only: after MultiTerminal restarts, no agent can close the panes it restores.
    /// That is deliberate; the Owner closes them, and nothing here outlives the panes it describes.</para>
    /// </summary>
    public sealed class SpawnedPaneRegistry
    {
        private readonly object _lock = new();

        // Keyed by the helper's DocId, which MT mints per pane: exact by decision, not by default.
        private readonly Dictionary<string, Entry> _byHelperDocId = new(StringComparer.Ordinal);

        /// <summary>One spawned helper pane and the proof of the pane that spawned it.</summary>
        public readonly record struct Entry(string HelperDocId, string HelperName, string SpawnerDocId, string SpawnerNonce);

        /// <summary>Number of helper panes currently recorded.</summary>
        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _byHelperDocId.Count;
                }
            }
        }

        /// <summary>
        /// Records that the pane <paramref name="spawnerDocId"/>, proven by
        /// <paramref name="spawnerNonce"/>, spawned the helper pane <paramref name="helperDocId"/>
        /// registered as <paramref name="helperName"/>. Does nothing when any value is missing: an
        /// unproven spawner is not recorded at all, rather than recorded as something that could later
        /// match. Returns whether an entry was stored, which is what the spawn response reports as
        /// closableBySpawner, so the two cannot disagree.
        /// </summary>
        public bool Record(string? helperDocId, string? helperName, string? spawnerDocId, string? spawnerNonce)
        {
            if (string.IsNullOrEmpty(helperDocId) || string.IsNullOrEmpty(helperName)
                || string.IsNullOrEmpty(spawnerDocId) || string.IsNullOrEmpty(spawnerNonce))
            {
                return false;
            }

            lock (_lock)
            {
                _byHelperDocId[helperDocId] = new Entry(helperDocId, helperName, spawnerDocId, spawnerNonce);
            }

            return true;
        }

        /// <summary>
        /// The recorded helpers named <paramref name="helperName"/> that the pane
        /// <paramref name="spawnerDocId"/> spawned while proving itself with
        /// <paramref name="spawnerNonce"/>. Both must match, exactly. Helpers other panes spawned are
        /// never returned, so a caller cannot learn whether a pane it did not spawn exists.
        ///
        /// <para>The name is compared ordinal-ignore-case, the comparison <c>MessageBroker.GetTerminal</c>
        /// uses for names, so "bob" finds the helper list_terminals shows as "Bob" exactly as it would
        /// for send_message. It is not trimmed here: SpawnController trims at the boundary, and a trim
        /// in this lookup would be a permissive comparison in front of an exact one.</para>
        /// </summary>
        public IReadOnlyList<Entry> FindForSpawner(string? spawnerDocId, string? spawnerNonce, string? helperName)
        {
            var found = new List<Entry>();
            if (string.IsNullOrEmpty(spawnerDocId) || string.IsNullOrEmpty(spawnerNonce) || string.IsNullOrEmpty(helperName))
            {
                return found;
            }

            lock (_lock)
            {
                foreach (var e in _byHelperDocId.Values)
                {
                    if (string.Equals(e.SpawnerDocId, spawnerDocId, StringComparison.Ordinal)
                        && string.Equals(e.SpawnerNonce, spawnerNonce, StringComparison.Ordinal)
                        && string.Equals(e.HelperName, helperName, StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add(e);
                    }
                }
            }

            return found;
        }

        /// <summary>
        /// Drops the helper pane's entry, whoever closed it. Called when the pane leaves the dock, so a
        /// closed pane's entry can never be matched again. Returns whether an entry was removed.
        /// </summary>
        public bool Forget(string? helperDocId)
        {
            if (string.IsNullOrEmpty(helperDocId))
            {
                return false;
            }

            lock (_lock)
            {
                return _byHelperDocId.Remove(helperDocId);
            }
        }

        /// <summary>
        /// Drops every entry the pane <paramref name="spawnerDocId"/> spawned. Called when that pane
        /// leaves the dock: a spawner that is gone keeps no power to close anything (pipeline Run 1,
        /// Codex security), and its helpers can then only be closed by the Owner. Returns how many
        /// entries were removed.
        /// </summary>
        public int ForgetSpawner(string? spawnerDocId)
        {
            if (string.IsNullOrEmpty(spawnerDocId))
            {
                return 0;
            }

            lock (_lock)
            {
                var doomed = new List<string>();
                foreach (var e in _byHelperDocId.Values)
                {
                    if (string.Equals(e.SpawnerDocId, spawnerDocId, StringComparison.Ordinal))
                    {
                        doomed.Add(e.HelperDocId);
                    }
                }

                foreach (var id in doomed)
                {
                    _byHelperDocId.Remove(id);
                }

                return doomed.Count;
            }
        }
    }
}
