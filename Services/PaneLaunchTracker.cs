using System;
using System.Collections.Generic;

namespace MultiTerminal.Services
{
    /// <summary>
    /// The writes <see cref="PaneLaunchTracker"/> makes. <see cref="PaneLaunchRecorder"/> is the real one;
    /// tests supply a fake so the tracker's decisions can be checked without a database.
    /// </summary>
    public interface IPaneLaunchSink
    {
        /// <summary>Writes an open launch row; returns its id, or null when it could not be written.</summary>
        string BeginLaunch(string docId, string name, string projectId, string cwd, string kind, string resumedFromSessionId);

        /// <summary>
        /// Records the session a launch runs. <paramref name="sessionChangedInLaunch"/>: the pane moved from
        /// one session to another inside this launch, which may continue the launch on a new row.
        /// </summary>
        /// <returns>The launch id now carrying the session (a continuation's id when one was made), or null.</returns>
        string AdoptSession(string launchId, string sessionId, bool sessionChangedInLaunch);

        void LinkSpawner(string childLaunchId, string spawnerLaunchId);

        /// <summary>Ends a launch; an already-ended launch keeps its first reason.</summary>
        void End(string launchId, string reason);

        void EndAllForShutdown();
    }

    /// <summary>
    /// MainForm's side of the launch record (task 7d20e94c item 2): which launch each pane is running,
    /// which end reason a close writes, the spawner link, and the shutdown guard. Owns no UI and no
    /// database, so its decisions are unit-tested against a fake <see cref="IPaneLaunchSink"/>.
    ///
    /// <para><b>Threading.</b> Every current caller is on the UI thread (LaunchBegan is raised there,
    /// ClaudeSessionAdopted and TerminalExited are posted there, the spawn and close-helper callbacks
    /// Invoke onto it, FormClosing runs there). That is an accident of the callers, not a property of
    /// this class, so the maps are still guarded by a lock. Sink calls are made OUTSIDE the lock: they
    /// write SQLite, and the lock only protects the two dictionaries.</para>
    ///
    /// <para><b>Spawner link, either order.</b> A helper's LaunchBegan fires inside
    /// <c>AddNewTerminal</c>, before the spawn path knows the helper's DocId, so the link is normally
    /// made after the launch row exists. If it does not exist yet, the spawner's launch is held as
    /// pending for that DocId and applied when its LaunchBegan arrives. The spawner's launch is resolved
    /// when <see cref="LinkSpawner"/> is called, not when the pending link is applied.</para>
    ///
    /// <para><b>Shutdown guard.</b> Once <see cref="BeginShutdown"/> ran, every open launch of this MT run
    /// is <c>mt_exit</c>, and the close handlers write nothing: MT stopping its panes is not the Owner
    /// closing them. Without the guard every clean exit would mark every helper closed and nothing could
    /// ever be resumed. A crash runs none of this, so its rows stay open, which also reads as open.</para>
    /// </summary>
    public sealed class PaneLaunchTracker
    {
        private readonly IPaneLaunchSink _sink;
        private readonly object _lock = new object();

        // DocIds and launch ids are GUID strings MT mints: exact by decision.
        private readonly Dictionary<string, CurrentLaunch> _currentLaunchByDoc = new Dictionary<string, CurrentLaunch>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _pendingSpawnerByDoc = new Dictionary<string, string>(StringComparer.Ordinal);

        // Item 8: child launch id -> the spawner launch it was linked to (only launches still current), and
        // the launch of a pane whose process exited while the pane stayed open. Both exist only so a PM's
        // tab close can tell which open helper panes it spawned; the database stays the record.
        private readonly Dictionary<string, string> _spawnerByLaunch = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, CurrentLaunch> _exitedLaunchByDoc = new Dictionary<string, CurrentLaunch>(StringComparer.Ordinal);
        private bool _shuttingDown;

        public PaneLaunchTracker(IPaneLaunchSink sink)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        }

        /// <summary>True once <see cref="BeginShutdown"/> ran.</summary>
        public bool IsShuttingDown
        {
            get { lock (_lock) return _shuttingDown; }
        }

        /// <summary>The launch the pane is running, or null when none is open.</summary>
        public string GetCurrentLaunchId(string docId)
        {
            if (string.IsNullOrEmpty(docId)) return null;
            lock (_lock)
            {
                return _currentLaunchByDoc.TryGetValue(docId, out var current) ? current.LaunchId : null;
            }
        }

        /// <summary>
        /// A pane started a launch. A previous launch of the same pane that is still open ended without
        /// anyone saying so ("Launch as" stops the process directly), so it is ended <c>process_exit</c>
        /// first. A pending spawner link for this pane is applied to the new launch.
        /// </summary>
        /// <param name="launchGeneration">The pane's Claude-session launch generation for this launch
        /// (<c>ClaudeSessionTracker.Generation</c> right after <c>BeginLaunch</c>); an adoption carrying
        /// another generation belongs to another launch and is ignored. Null = not known.</param>
        /// <returns>The new launch id, or null when the row could not be written.</returns>
        public string OnLaunchBegan(string docId, string name, string projectId, string cwd, string kind, string resumedFromSessionId, long? launchGeneration = null)
        {
            if (string.IsNullOrEmpty(docId)) return null;

            CurrentLaunch previous;
            bool shuttingDown;
            lock (_lock)
            {
                _currentLaunchByDoc.TryGetValue(docId, out previous);
                _currentLaunchByDoc.Remove(docId);
                _exitedLaunchByDoc.Remove(docId);
                if (previous != null) _spawnerByLaunch.Remove(previous.LaunchId);
                shuttingDown = _shuttingDown;
            }

            if (previous != null && !shuttingDown)
                _sink.End(previous.LaunchId, PaneLaunchEndReason.ProcessExit);

            string launchId = _sink.BeginLaunch(docId, name, projectId, cwd, kind, resumedFromSessionId);
            if (launchId == null) return null;

            string pendingSpawner;
            lock (_lock)
            {
                _currentLaunchByDoc[docId] = new CurrentLaunch(launchId, launchGeneration);
                if (_pendingSpawnerByDoc.TryGetValue(docId, out pendingSpawner))
                {
                    _pendingSpawnerByDoc.Remove(docId);
                    _spawnerByLaunch[launchId] = pendingSpawner;
                }
            }

            if (pendingSpawner != null)
                _sink.LinkSpawner(launchId, pendingSpawner);
            return launchId;
        }

        /// <summary>
        /// The pane's current launch learned (or changed) its Claude session id. The event is posted, so
        /// it can arrive after the launch it belongs to was replaced (pipeline Run 1): it is ignored when
        /// <paramref name="launchGeneration"/> and the current launch's generation are both known and
        /// differ. A non-null <paramref name="previousSessionId"/> means the pane moved session within the
        /// launch (<c>/clear</c>, a fork): the sink may then continue the launch on a new row, which
        /// becomes the pane's current launch, so later spawns link to it. Writes nothing once MT is
        /// shutting down: a continuation opened after the shutdown pass would stay open and read as a
        /// crash.
        /// </summary>
        public void OnSessionAdopted(string docId, string sessionId, string previousSessionId = null, long? launchGeneration = null)
        {
            if (string.IsNullOrEmpty(docId) || string.IsNullOrEmpty(sessionId)) return;

            CurrentLaunch current;
            lock (_lock)
            {
                if (_shuttingDown) return;
                if (!_currentLaunchByDoc.TryGetValue(docId, out current)) return;
            }

            if (launchGeneration.HasValue && current.Generation.HasValue && launchGeneration.Value != current.Generation.Value)
                return;

            string carrying = _sink.AdoptSession(current.LaunchId, sessionId, !string.IsNullOrEmpty(previousSessionId));
            if (carrying == null || string.Equals(carrying, current.LaunchId, StringComparison.Ordinal))
                return;

            lock (_lock)
            {
                // Only while the pane is still on the launch that was continued.
                if (_currentLaunchByDoc.TryGetValue(docId, out var now) && ReferenceEquals(now, current))
                {
                    _currentLaunchByDoc[docId] = new CurrentLaunch(carrying, current.Generation);

                    // The continuation row carries the same spawner link (AdoptPaneLaunchSession copies it).
                    if (_spawnerByLaunch.TryGetValue(current.LaunchId, out var spawnerOfContinued))
                    {
                        _spawnerByLaunch.Remove(current.LaunchId);
                        _spawnerByLaunch[carrying] = spawnerOfContinued;
                    }
                }
            }
        }

        /// <summary>
        /// Links the helper pane <paramref name="childDocId"/> to the launch <paramref name="spawnerDocId"/>
        /// is running now. Linked at once when the helper's launch row exists, else held until it does.
        /// Does nothing when the spawner has no open launch.
        /// </summary>
        public void LinkSpawner(string childDocId, string spawnerDocId)
        {
            if (string.IsNullOrEmpty(childDocId) || string.IsNullOrEmpty(spawnerDocId)) return;

            string spawnerLaunch;
            string childLaunch;
            lock (_lock)
            {
                if (!_currentLaunchByDoc.TryGetValue(spawnerDocId, out var spawner)) return;
                spawnerLaunch = spawner.LaunchId;
                if (!_currentLaunchByDoc.TryGetValue(childDocId, out var child))
                {
                    _pendingSpawnerByDoc[childDocId] = spawnerLaunch;
                    return;
                }

                childLaunch = child.LaunchId;
                _spawnerByLaunch[childLaunch] = spawnerLaunch;
            }

            _sink.LinkSpawner(childLaunch, spawnerLaunch);
        }

        /// <summary>
        /// The panes whose CURRENT launch was spawned by <paramref name="pmDocId"/>'s launch (task 7d20e94c
        /// item 8), for "also close its helpers?" when the Owner closes the PM's tab. The PM's launch is
        /// its current one or, when its process exited and the pane stayed open (Home, claude exiting),
        /// the launch that just ended: those helpers are still running and still belong to that session.
        /// Helpers spawned before a <c>/clear</c> belong to the old session and are not listed; a helper
        /// whose own process exited has no current launch and is not listed (it counts as closed and
        /// would not be resumed anyway). Empty once MT is shutting down.
        /// </summary>
        public IReadOnlyList<string> GetOpenHelperDocIds(string pmDocId)
        {
            if (string.IsNullOrEmpty(pmDocId)) return Array.Empty<string>();
            lock (_lock)
            {
                if (_shuttingDown) return Array.Empty<string>();
                CurrentLaunch pm;
                if (!_currentLaunchByDoc.TryGetValue(pmDocId, out pm) && !_exitedLaunchByDoc.TryGetValue(pmDocId, out pm))
                    return Array.Empty<string>();

                var result = new List<string>();
                foreach (var pair in _currentLaunchByDoc)
                {
                    if (string.Equals(pair.Key, pmDocId, StringComparison.Ordinal)) continue;
                    if (_spawnerByLaunch.TryGetValue(pair.Value.LaunchId, out var spawner)
                        && string.Equals(spawner, pm.LaunchId, StringComparison.Ordinal))
                    {
                        result.Add(pair.Key);
                    }
                }

                return result;
            }
        }

        /// <summary>The pane left the dock (the Owner closed it): <c>pane_closed</c>.</summary>
        public void OnPaneClosed(string docId) => EndCurrent(docId, PaneLaunchEndReason.PaneClosed, forgetPending: true);

        /// <summary>
        /// The spawner closed the helper through close_helper: <c>close_helper</c>. Call it BEFORE closing
        /// the pane, so it is the first end and wins over the <c>pane_closed</c> the close then raises.
        /// </summary>
        public void OnHelperClosedBySpawner(string docId) => EndCurrent(docId, PaneLaunchEndReason.CloseHelper, forgetPending: false);

        /// <summary>
        /// The Owner closed this helper's PM and chose to close its helpers too (item 8):
        /// <c>closed_with_pm</c>, which stays resumable with the PM. Call it BEFORE closing the pane, as
        /// for <see cref="OnHelperClosedBySpawner"/>, so it wins over the <c>pane_closed</c> that follows.
        /// </summary>
        public void OnHelperClosedWithPm(string docId) => EndCurrent(docId, PaneLaunchEndReason.ClosedWithPm, forgetPending: false);

        /// <summary>
        /// The pane's process exited while the pane stays open: <c>process_exit</c>. The ended launch is
        /// remembered for the pane (item 8) so closing its tab can still offer to close its helpers.
        /// </summary>
        public void OnProcessExited(string docId)
        {
            var ended = EndCurrent(docId, PaneLaunchEndReason.ProcessExit, forgetPending: false);
            if (ended == null) return;
            lock (_lock)
            {
                if (!_shuttingDown && !_currentLaunchByDoc.ContainsKey(docId))
                    _exitedLaunchByDoc[docId] = ended;
            }
        }

        /// <summary>
        /// MT is really exiting (the close was not cancelled) and its panes are about to be stopped: every
        /// open launch of this run becomes <c>mt_exit</c>, and from now on the close handlers write
        /// nothing. Idempotent.
        /// </summary>
        public void BeginShutdown()
        {
            lock (_lock)
            {
                if (_shuttingDown) return;
                _shuttingDown = true;
            }

            _sink.EndAllForShutdown();
        }

        // Returns the launch it ended, or null when it ended none.
        private CurrentLaunch EndCurrent(string docId, string reason, bool forgetPending)
        {
            if (string.IsNullOrEmpty(docId)) return null;
            CurrentLaunch current;
            lock (_lock)
            {
                if (forgetPending)
                {
                    _pendingSpawnerByDoc.Remove(docId);
                    _exitedLaunchByDoc.Remove(docId);
                }

                if (_shuttingDown) return null;
                if (!_currentLaunchByDoc.TryGetValue(docId, out current)) return null;
                _currentLaunchByDoc.Remove(docId);
                _spawnerByLaunch.Remove(current.LaunchId);
            }

            _sink.End(current.LaunchId, reason);
            return current;
        }

        /// <summary>The launch a pane runs and its launch generation. Immutable: replaced, never mutated.</summary>
        private sealed class CurrentLaunch
        {
            public CurrentLaunch(string launchId, long? generation)
            {
                LaunchId = launchId;
                Generation = generation;
            }

            public string LaunchId { get; }

            public long? Generation { get; }
        }
    }
}
