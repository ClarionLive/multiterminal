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

        void AdoptSession(string launchId, string sessionId);

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
        private readonly Dictionary<string, string> _currentLaunchByDoc = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _pendingSpawnerByDoc = new Dictionary<string, string>(StringComparer.Ordinal);
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
                return _currentLaunchByDoc.TryGetValue(docId, out var id) ? id : null;
            }
        }

        /// <summary>
        /// A pane started a launch. A previous launch of the same pane that is still open ended without
        /// anyone saying so ("Launch as" stops the process directly), so it is ended <c>process_exit</c>
        /// first. A pending spawner link for this pane is applied to the new launch.
        /// </summary>
        /// <returns>The new launch id, or null when the row could not be written.</returns>
        public string OnLaunchBegan(string docId, string name, string projectId, string cwd, string kind, string resumedFromSessionId)
        {
            if (string.IsNullOrEmpty(docId)) return null;

            string previous;
            bool shuttingDown;
            lock (_lock)
            {
                _currentLaunchByDoc.TryGetValue(docId, out previous);
                _currentLaunchByDoc.Remove(docId);
                shuttingDown = _shuttingDown;
            }

            if (previous != null && !shuttingDown)
                _sink.End(previous, PaneLaunchEndReason.ProcessExit);

            string launchId = _sink.BeginLaunch(docId, name, projectId, cwd, kind, resumedFromSessionId);
            if (launchId == null) return null;

            string pendingSpawner;
            lock (_lock)
            {
                _currentLaunchByDoc[docId] = launchId;
                if (_pendingSpawnerByDoc.TryGetValue(docId, out pendingSpawner))
                    _pendingSpawnerByDoc.Remove(docId);
            }

            if (pendingSpawner != null)
                _sink.LinkSpawner(launchId, pendingSpawner);
            return launchId;
        }

        /// <summary>The pane's current launch learned (or changed) its Claude session id.</summary>
        public void OnSessionAdopted(string docId, string sessionId)
        {
            string launchId = GetCurrentLaunchId(docId);
            if (launchId == null || string.IsNullOrEmpty(sessionId)) return;
            _sink.AdoptSession(launchId, sessionId);
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
                if (!_currentLaunchByDoc.TryGetValue(spawnerDocId, out spawnerLaunch)) return;
                if (!_currentLaunchByDoc.TryGetValue(childDocId, out childLaunch))
                {
                    _pendingSpawnerByDoc[childDocId] = spawnerLaunch;
                    return;
                }
            }

            _sink.LinkSpawner(childLaunch, spawnerLaunch);
        }

        /// <summary>The pane left the dock (the Owner closed it): <c>pane_closed</c>.</summary>
        public void OnPaneClosed(string docId) => EndCurrent(docId, PaneLaunchEndReason.PaneClosed, forgetPending: true);

        /// <summary>
        /// The spawner closed the helper through close_helper: <c>close_helper</c>. Call it BEFORE closing
        /// the pane, so it is the first end and wins over the <c>pane_closed</c> the close then raises.
        /// </summary>
        public void OnHelperClosedBySpawner(string docId) => EndCurrent(docId, PaneLaunchEndReason.CloseHelper, forgetPending: false);

        /// <summary>The pane's process exited while the pane stays open: <c>process_exit</c>.</summary>
        public void OnProcessExited(string docId) => EndCurrent(docId, PaneLaunchEndReason.ProcessExit, forgetPending: false);

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

        private void EndCurrent(string docId, string reason, bool forgetPending)
        {
            if (string.IsNullOrEmpty(docId)) return;
            string launchId;
            lock (_lock)
            {
                if (forgetPending) _pendingSpawnerByDoc.Remove(docId);
                if (_shuttingDown) return;
                if (!_currentLaunchByDoc.TryGetValue(docId, out launchId)) return;
                _currentLaunchByDoc.Remove(docId);
            }

            _sink.End(launchId, reason);
        }
    }
}
