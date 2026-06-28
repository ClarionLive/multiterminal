using System;
using System.Threading;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Serializes all Code Graph (re)index operations through a single global permit so the
    /// non-atomic <see cref="CSharpCodeGraphIndexer.IndexDirectory"/> rebuild
    /// (ClearProject → Pass 1 symbols → ClearProjectRelationships + global LoadSymbolLookup → Pass 2)
    /// can never run concurrently with another index on the shared SQLite connection.
    ///
    /// Both entry points — the manual REST trigger (<c>CodeGraphController.Index</c>, via
    /// <see cref="Index"/>) and the background <see cref="CodeGraphWatcher"/> (via
    /// <see cref="TryIndex"/>) — route through here. One <see cref="SemaphoreSlim"/>(1,1) is the
    /// correct granularity: the indexer's connection and its in-memory <c>LoadSymbolLookup</c> are
    /// process-global, so per-project locking would not be safe.
    /// </summary>
    public sealed class CodeGraphIndexCoordinator : IDisposable
    {
        private readonly CodeGraphDatabase _db;
        private readonly CodeGraphQuery _query;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private bool _disposed;

        public CodeGraphIndexCoordinator(CodeGraphDatabase db, CodeGraphQuery query)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _query = query ?? throw new ArgumentNullException(nameof(query));
        }

        /// <summary>
        /// True while an index is in progress (the permit is held). Best-effort snapshot for callers
        /// that want to surface status — not a substitute for the atomic <see cref="TryIndex"/> gate.
        /// </summary>
        public bool IsIndexing => _gate.CurrentCount == 0;

        /// <summary>
        /// Run a full reindex of <paramref name="directory"/>, blocking until any in-progress index
        /// completes. Used by the manual REST trigger, which must not be silently dropped.
        /// </summary>
        public CSharpCodeGraphIndexer.IndexResult Index(string directory, string projectName = null)
        {
            _gate.Wait();
            try
            {
                return RunIndex(directory, projectName);
            }
            finally
            {
                ReleaseGate();
            }
        }

        /// <summary>
        /// Attempt a full reindex of <paramref name="directory"/> only if no index is currently
        /// running. Returns false immediately (without blocking) when busy — the background watcher
        /// uses this so overlapping change bursts coalesce into the in-flight index rather than queue.
        /// </summary>
        public bool TryIndex(string directory, string projectName, out CSharpCodeGraphIndexer.IndexResult result)
        {
            result = null;
            if (!_gate.Wait(0))
                return false;
            try
            {
                result = RunIndex(directory, projectName);
                return true;
            }
            finally
            {
                ReleaseGate();
            }
        }

        // Runs a full reindex with the gate already held by the caller. Only the gate-acquisition
        // policy differs between Index (blocking) and TryIndex (skip-if-busy).
        private CSharpCodeGraphIndexer.IndexResult RunIndex(string directory, string projectName)
            => new CSharpCodeGraphIndexer(_db, _query).IndexDirectory(directory, projectName);

        // Release the gate, tolerating a shutdown that disposed it out from under a >10s index that
        // outran Dispose's drain bound — so the drain stays a pure backstop and never resurfaces the
        // ObjectDisposedException noise it exists to suppress.
        private void ReleaseGate()
        {
            try { _gate.Release(); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Drain any in-flight index before disposing the semaphore, so a RunIndex still holding
            // the permit isn't racing a Release() into a disposed semaphore (ObjectDisposedException
            // noise at shutdown). An index is ~4s; 10s is a generous bound — if it's exceeded we
            // dispose anyway rather than hang shutdown. We intentionally do not Release after a
            // successful Wait: disposal follows immediately.
            try { _gate.Wait(TimeSpan.FromSeconds(10)); } catch { }
            _gate.Dispose();
        }
    }
}
