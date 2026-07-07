using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Background service that keeps the Code Graph index continuously fresh. Watches every
    /// registered C# project root for <c>.cs</c> changes and runs a <b>debounced full reindex</b>
    /// (~12 s quiet window) in-process, so the graph never goes stale waiting for someone to run
    /// <c>index_code_graph</c> manually.
    ///
    /// <para>The indexer (<see cref="CSharpCodeGraphIndexer.IndexDirectory"/>) is full-reindex-only
    /// and non-atomic, so this watcher MUST debounce/coalesce rather than reindex per edit, and it
    /// routes every reindex through <see cref="CodeGraphIndexCoordinator"/> so it can never overlap
    /// the manual REST trigger on the shared SQLite connection.</para>
    ///
    /// <para>Lifecycle mirrors <see cref="GitRepoWatcher"/>: construct-disabled / enable-last
    /// FileSystemWatchers, one shared <see cref="Timer"/> armed Infinite and reset on each event so
    /// it fires only after quiet, snapshot+clear pending state under lock, raise work outside the
    /// lock in try/catch, <c>volatile</c> started/disposed flags, idempotent Start, and Stop that
    /// disposes watchers + timer each in its own try/catch.</para>
    ///
    /// <para>Config (env vars, MULTITERMINAL_* convention):
    /// <list type="bullet">
    ///   <item><c>MULTITERMINAL_CODEGRAPH_WATCH</c> — 0/false/off/no/disabled disable (case-insensitive); default ON.</item>
    ///   <item><c>MULTITERMINAL_CODEGRAPH_DEBOUNCE_MS</c> — debounce window, default 12000, floor 3000.</item>
    ///   <item><c>MULTITERMINAL_CODEGRAPH_MIN_INTERVAL_MS</c> — freshness floor, default 300000; skip
    ///   reindex if <c>last_indexed</c> is within this window.</item>
    /// </list></para>
    /// </summary>
    public sealed class CodeGraphWatcher : IDisposable
    {
        private readonly ProjectService _projects;
        private readonly CodeGraphDatabase _db;
        private readonly CodeGraphIndexCoordinator _coordinator;

        private readonly object _lock = new object();

        // root path -> {installed watcher, registry project name for the reindex call}. Mutated only
        // under _lock by ReconcileWatchedRoots.
        private readonly Dictionary<string, WatchedRoot> _watched =
            new Dictionary<string, WatchedRoot>(StringComparer.OrdinalIgnoreCase);

        // Roots with pending work since the last debounce fire, each carrying whether it's a
        // bootstrap/sweep (skip-if-fresh) vs a real FS edit (defer-if-fresh, never drop) and its
        // consecutive transient-failure count. A root is "dirty" iff it's a key here. Mutated only
        // under _lock; snapshot+cleared in OnDebounceFired.
        private readonly Dictionary<string, PendingWork> _pending =
            new Dictionary<string, PendingWork>(StringComparer.OrdinalIgnoreCase);

        private Timer _debounceTimer;

        // Delay before the startup staleness sweep (and a newly-registered root's bootstrap) fires —
        // short, so we heal a stale graph promptly on launch / onboarding, but not on the UI thread.
        private const int StartupSweepDelayMs = 2000;

        // G2/G6 bounded retry: a transient RunIndex exception re-queues the root with backoff, but
        // only up to a bounded attempt count so a poison root can't spin forever (it then waits for
        // the next real FS edit, which resets the budget).
        // A real EDIT keeps the tight budget (a future edit can always recover it). A BOOTSTRAP root
        // (startup sweep / onboarding) has NO future edit to fall back on, so it gets a larger budget
        // AND an escalating backoff that exceeds _debounceMs — enough to outlast a tens-of-seconds
        // startup DB lock (e.g. the migration) — still bounded so a permanently-broken root gives up.
        private const int MaxEditAttempts = 5;
        private const int MaxBootstrapAttempts = 10;
        private const int RetryBackoffMs = 5000;          // edit floor + bootstrap per-attempt escalation step
        private const long BootstrapBackoffCapMs = 120_000;

        // Upper bounds for the numeric env knobs. Debounce max keeps the value well inside int range
        // so the (int) cast can't overflow; freshness-floor max caps at 1 day.
        private const long DebounceMaxMs = 600_000;
        private const long MinIntervalMaxMs = 86_400_000;

        private readonly bool _watchEnabled;
        private readonly int _debounceMs;
        private readonly long _minIntervalMs;

        // Config-parse warnings collected during construction (when DebugLogService isn't wired yet)
        // and flushed in Start() so a typo'd / clamped env var surfaces in the MCP-visible log.
        private readonly List<string> _configWarnings = new List<string>();

        // Volatile so the early-return _disposed/_started checks at the top of public entry points
        // and FS handlers are observed promptly across cores even outside the lock.
        private volatile bool _started;
        private volatile bool _disposed;

        /// <summary>
        /// Optional debug log service for MCP-visible logging.
        /// </summary>
        public DebugLogService DebugLogService { get; set; }

        /// <param name="projects">Registry source for the C# project roots to watch.</param>
        /// <param name="db">Code graph DB — used for the <c>last_indexed</c> freshness floor.</param>
        /// <param name="query">Query layer. Accepted for wiring symmetry with the indexer/coordinator
        /// and to keep the MainForm construction stable if incremental indexing is added later; the
        /// watcher reindexes via <paramref name="coordinator"/>, which already owns the query layer.</param>
        /// <param name="coordinator">Gate that serializes all reindexes against the manual REST trigger.</param>
        public CodeGraphWatcher(
            ProjectService projects,
            CodeGraphDatabase db,
            CodeGraphQuery query,
            CodeGraphIndexCoordinator coordinator)
        {
            _projects = projects ?? throw new ArgumentNullException(nameof(projects));
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            _ = query; // see param doc — not stored; reindex goes through the coordinator.

            _watchEnabled = ReadWatchEnabled();
            // Debounce ms: floor 3000, clamped to DebounceMaxMs BEFORE the int cast so the cast can't overflow.
            _debounceMs = (int)ReadLongEnv("MULTITERMINAL_CODEGRAPH_DEBOUNCE_MS", 12000, 3000, DebounceMaxMs);
            // Freshness floor ms: floor 0 (0 disables the floor), max 1 day.
            _minIntervalMs = ReadLongEnv("MULTITERMINAL_CODEGRAPH_MIN_INTERVAL_MS", 300000, 0, MinIntervalMaxMs);
        }

        // ─── Logging (mirrors TeamWatcherService:92-108) ─────────────────────

        private void Log(string msg)
        {            DebugLogService?.Trace("CodeGraphWatcher", msg);
        }

        private void LogInfo(string msg)
        {            DebugLogService?.Info("CodeGraphWatcher", msg);
        }

        private void LogError(string msg)
        {            DebugLogService?.Error("CodeGraphWatcher", msg);
        }

        // ─── Lifecycle ───────────────────────────────────────────────────────

        /// <summary>
        /// Begin watching. Idempotent — calling multiple times is a no-op after the first.
        /// If disabled via <c>MULTITERMINAL_CODEGRAPH_WATCH</c>, logs and returns without installing
        /// any watchers (leaving <c>_started</c> false).
        /// </summary>
        public void Start()
        {
            if (_disposed) return;
            lock (_lock)
            {
                if (_started) return;

                // Surface any config-parse warnings now that DebugLogService is wired (it's null
                // during construction). Done before the disabled-check so a bad WATCH value still logs.
                if (_configWarnings.Count > 0)
                {
                    foreach (var w in _configWarnings)
                        LogError(w);
                    _configWarnings.Clear();
                }

                if (!_watchEnabled)
                {
                    LogInfo("Disabled via MULTITERMINAL_CODEGRAPH_WATCH — no watchers installed.");
                    return;
                }

                _started = true;

                // One shared debounce timer, armed Infinite; each FS event resets it so it only
                // fires after the quiet window (OnEvent -> Change(_debounceMs, Infinite)).
                _debounceTimer = new Timer(OnDebounceFired, null, Timeout.Infinite, Timeout.Infinite);

                // React to registry changes at runtime so registering/unregistering a C# project
                // starts/stops watching it with no app restart. Subscribe before the initial
                // reconcile so a registration racing startup isn't missed.
                // G5: ProjectUpdated is the ONLY registry event the new-project wizard, MCP
                // create_project, and REST POST /api/projects raise (all route through
                // MessageBroker.CreateProject -> ProjectService.SaveProject -> ProjectUpdated, none of
                // which fire ProjectRegistered). Subscribing it here installs a watcher + bootstraps
                // those projects this session. The net-new gate makes the noisier ProjectUpdated fires
                // (edits/prompts on already-watched roots) harmless — they install/bootstrap nothing.
                // ProjectRegistered is now technically redundant (RegisterProject calls SaveProject ->
                // ProjectUpdated before raising it) but kept as defensive belt-and-suspenders; the
                // net-new gate collapses its double-fire to a single bootstrap. RegistryChangedExternally
                // likewise only co-fires with ProjectRemoved today, kept for the same reason.
                _projects.ProjectRegistered += OnProjectRegistryChanged;
                _projects.ProjectUpdated += OnProjectRegistryChanged;
                _projects.ProjectRemoved += OnProjectRegistryChanged;
                _projects.RegistryChangedExternally += OnRegistryChangedExternally;

                // F2 startup staleness sweep and G1 mid-session onboarding are the SAME mechanism:
                // ReconcileWatchedRoots bootstraps every NET-NEW root it installs (floor-gated,
                // skip-if-fresh — see OnDebounceFired). On this first reconcile every eligible root is
                // net-new, so all are queued for an initial index: a stale or never-indexed graph
                // (incl. the 83-day-old one) is healed, while an already-fresh graph is left alone.
                // The index runs on the timer's threadpool thread (off the UI thread), serialized +
                // skip-if-busy via the coordinator.
                ReconcileWatchedRoots(bootstrapNewRoots: true);

                LogInfo($"Started — debounce={_debounceMs}ms, minInterval={_minIntervalMs}ms, watching {_watched.Count} root(s).");
            }
        }

        /// <summary>
        /// Stop watching and dispose all FileSystemWatchers and the debounce timer. Idempotent.
        /// </summary>
        public void Stop()
        {
            lock (_lock)
            {
                if (!_started) return;
                _started = false;

                try
                {
                    _projects.ProjectRegistered -= OnProjectRegistryChanged;
                    _projects.ProjectUpdated -= OnProjectRegistryChanged;
                    _projects.ProjectRemoved -= OnProjectRegistryChanged;
                    _projects.RegistryChangedExternally -= OnRegistryChangedExternally;
                }
                catch { }

                foreach (var wr in _watched.Values)
                    DisposeWatcher(wr.Watcher);
                _watched.Clear();

                try { _debounceTimer?.Dispose(); } catch { }
                _debounceTimer = null;
                _pending.Clear();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }

        // ─── Root enumeration + watcher install ──────────────────────────────

        /// <summary>
        /// Reconcile installed watchers against the current set of eligible roots: a registered
        /// project whose <c>Path</c> exists on disk and contains a top-level <c>*.csproj</c>. Adds
        /// watchers for newly eligible roots, disposes+removes watchers for roots no longer eligible.
        /// Re-entrant under <see cref="_lock"/> (Monitor is recursive), so Start() may call it while
        /// holding the lock and the registry event handlers may call it independently.
        ///
        /// <para>When <paramref name="bootstrapNewRoots"/> is true, each NET-NEW eligible root is
        /// queued for a one-time floor-gated bootstrap index (G1) — so a project onboarded mid-session
        /// (new-project wizard / import-existing-folder / MCP / REST) is indexed without waiting for a
        /// <c>.cs</c> edit. Already-watched roots are NOT re-bootstrapped (no redundant reindex on
        /// every reconcile). Start's initial call passes true: on the first reconcile every eligible
        /// root is net-new, so all are bootstrapped — this IS the startup staleness sweep, unified into
        /// reconcile. Only the watcher-error recovery passes false (it already armed a real-edit
        /// recovery, which must not be downgraded to skip-if-fresh).</para>
        /// </summary>
        private void ReconcileWatchedRoots(bool bootstrapNewRoots = false)
        {
            if (_disposed) return;

            List<ProjectRegistryEntry> projects;
            try
            {
                projects = _projects.GetAllRegisteredProjects();
            }
            catch (Exception ex)
            {
                LogError($"Failed to enumerate registered projects: {ex.Message}");
                return;
            }

            // Build the eligible root -> project name map.
            var eligible = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in projects)
            {
                if (string.IsNullOrWhiteSpace(p?.Path)) continue;

                string root;
                try { root = Path.GetFullPath(p.Path); }
                catch { continue; }

                // Self-heal (task 19d0d867): a git-worktree path must never be treated as the
                // canonical root. Map it to its STABLE on-disk equivalent (repo root, with any
                // worktree-subdir suffix re-rooted under it — so a csproj-in-subfolder project still
                // lands on a watchable path) so the project stays watched even after its worktree is
                // pruned. NOTE: ProjectRegistryEntry carries no source_path, so unlike the write-guard
                // (which prefers source_path) the watcher uses path-derivation only — this is
                // defense-in-depth; the write-guard + startup migration are the durable repair.
                if (WorktreeLayout.TryMapWorktreePath(root, out _, out var stableRoot))
                {
                    LogInfo($"Project '{p.Name}' is registered at a worktree path '{root}'; watching stable path '{stableRoot}' instead.");
                    root = stableRoot;
                }

                // Previously skipped silently — the orphaned-worktree bug hid here (no error, no
                // log). Log so a missing root (pruned worktree whose repo root couldn't be derived,
                // or a moved folder) is diagnosable instead of invisible.
                if (!Directory.Exists(root))
                {
                    LogError($"Skipping project '{p.Name}': registered path '{root}' does not exist (pruned worktree or moved folder?).");
                    continue;
                }

                bool hasCsproj;
                try { hasCsproj = Directory.GetFiles(root, "*.csproj", SearchOption.TopDirectoryOnly).Length > 0; }
                catch { continue; }
                if (!hasCsproj) continue;

                eligible[root] = string.IsNullOrWhiteSpace(p.Name) ? Path.GetFileName(root) : p.Name;
            }

            lock (_lock)
            {
                if (_disposed || !_started) return;

                // Remove watchers for roots that are no longer eligible.
                var toRemove = _watched.Keys.Where(k => !eligible.ContainsKey(k)).ToList();
                foreach (var k in toRemove)
                {
                    DisposeWatcher(_watched[k].Watcher);
                    _watched.Remove(k);
                    _pending.Remove(k);
                    LogInfo($"Stopped watching {k}");
                }

                // Install watchers for newly eligible roots; refresh the name on already-watched ones.
                var newRoots = new List<string>();
                foreach (var kvp in eligible)
                {
                    if (_watched.TryGetValue(kvp.Key, out var existing))
                    {
                        existing.Name = kvp.Value;
                        continue;
                    }
                    try
                    {
                        _watched[kvp.Key] = new WatchedRoot { Watcher = MakeWatcher(kvp.Key), Name = kvp.Value };
                        newRoots.Add(kvp.Key);
                        LogInfo($"Watching {kvp.Key} (project '{kvp.Value}')");
                    }
                    catch (Exception ex)
                    {
                        LogError($"Failed to install watcher for {kvp.Key}: {ex.Message}");
                    }
                }

                // G1: bootstrap only the NET-NEW roots (floor-gated skip-if-fresh), and only when the
                // caller asked for it (mid-session registration) and the timer exists.
                if (bootstrapNewRoots && newRoots.Count > 0 && _started && _debounceTimer != null)
                {
                    MarkDirtyAndArm(newRoots, StartupSweepDelayMs, bootstrap: true);
                    LogInfo($"Bootstrapping {newRoots.Count} newly-eligible root(s) for an initial floor-gated index.");
                }
            }
        }

        // Registry event handlers — a project registered/updated/removed/externally-changed re-runs
        // the reconcile so the watched-root set tracks the registry without an app restart. Two
        // signatures (ProjectEventArgs vs EventArgs) adapt onto one body via ReconcileFromRegistry.
        private void OnProjectRegistryChanged(object sender, ProjectEventArgs e) => ReconcileFromRegistry("registry change");
        private void OnRegistryChangedExternally(object sender, EventArgs e) => ReconcileFromRegistry("external registry change");

        // Wrapped so a reconcile failure can't escape into the (often UI-thread) event raiser.
        private void ReconcileFromRegistry(string trigger)
        {
            if (_disposed || !_started) return;
            try { ReconcileWatchedRoots(bootstrapNewRoots: true); }
            catch (Exception ex) { LogError($"Reconcile after {trigger} failed: {ex.Message}"); }
        }

        // Construct disabled, wire handlers, then enable last — mirrors GitRepoWatcher / the
        // TeamWatcher hardening that closed the race where events fired between ctor and += were lost.
        private FileSystemWatcher MakeWatcher(string root)
        {
            var w = new FileSystemWatcher(root)
            {
                Filter = "*.cs",
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
                EnableRaisingEvents = false,
            };
            w.Changed += (s, e) => OnEvent(root, e.FullPath);
            w.Created += (s, e) => OnEvent(root, e.FullPath);
            w.Deleted += (s, e) => OnEvent(root, e.FullPath);
            w.Renamed += (s, e) => OnEvent(root, e.FullPath);
            w.Error += (s, e) => OnWatcherError(root, e);
            w.EnableRaisingEvents = true;
            return w;
        }

        // FileSystemWatcher raised Error — almost always InternalBufferOverflow (the OS dropped
        // events during a burst) or the watched root being removed. Either way some change events
        // were lost, so we force a recovery reindex (mark dirty + re-arm) and reinstall the watcher
        // for this root (overflow can leave the handle degraded), or drop it if the root is gone.
        private void OnWatcherError(string root, System.IO.ErrorEventArgs e)
        {
            if (_disposed) return;
            var ex = e?.GetException();
            LogError($"FileSystemWatcher error for {root}: {ex?.Message ?? "(unknown)"} — forcing recovery reindex + watcher repair.");

            lock (_lock)
            {
                if (!_started || _debounceTimer == null) return;

                // Recover events dropped during the error (e.g. InternalBufferOverflow): this root
                // needs a fresh full reindex.
                MarkDirtyAndArm(root, _debounceMs);

                // Drop the errored (possibly dead) handle. ReconcileWatchedRoots below is the SINGLE
                // owner of watcher eligibility — it reinstalls the root if it's still eligible
                // (exists + top-level *.csproj) or leaves it removed otherwise, so we don't duplicate
                // (and drift from) that predicate here.
                if (_watched.TryGetValue(root, out var wr))
                {
                    DisposeWatcher(wr.Watcher);
                    _watched.Remove(root);
                }
            }

            // Outside the lock: ReconcileWatchedRoots takes it itself. Reinstalls via the same path Start uses.
            try { ReconcileWatchedRoots(); }
            catch (Exception rex) { LogError($"Reconcile after watcher error failed: {rex.Message}"); }
        }

        // ─── Debounce + coalescing ───────────────────────────────────────────

        // Single authority for merging an incoming pending record for `root` into _pending. CALLER
        // MUST hold _lock. The "real edit beats bootstrap" rule lives here only: a real edit
        // (Bootstrap == false) on either side wins, so a bootstrap never downgrades a pending real
        // edit and a real edit during a bootstrap window flips it to defer-not-drop.
        // isRetry distinguishes a NEW signal (FS edit / bootstrap — a real edit resets the bounded
        // retry budget) from a deferred RETRY being re-queued (preserve its carried Attempts, unless
        // a concurrent new signal already replaced the entry, in which case that fresh budget wins).
        private void MergePending(string root, PendingWork incoming, bool isRetry)
        {
            if (_pending.TryGetValue(root, out var cur))
            {
                cur.Bootstrap &= incoming.Bootstrap;
                if (!isRetry && !incoming.Bootstrap)
                    cur.Attempts = 0;
            }
            else
            {
                _pending[root] = incoming;
            }
        }

        // Queue a root from a new signal (FS edit when bootstrap==false, sweep/onboarding when true).
        private void QueueRoot(string root, bool bootstrap)
            => MergePending(root, new PendingWork { Bootstrap = bootstrap }, isRetry: false);

        // Mark roots pending and (re-)arm the single shared debounce timer to fire after delayMs.
        // CALLER MUST hold _lock and have verified _started && _debounceTimer != null. Used by every
        // path that queues work (FS event, startup sweep, new-root bootstrap, watcher-error recovery).
        private void MarkDirtyAndArm(string root, long delayMs, bool bootstrap = false)
        {
            QueueRoot(root, bootstrap);
            _debounceTimer.Change(TimeSpan.FromMilliseconds(delayMs), Timeout.InfiniteTimeSpan);
        }

        private void MarkDirtyAndArm(IEnumerable<string> roots, long delayMs, bool bootstrap = false)
        {
            foreach (var r in roots) QueueRoot(r, bootstrap);
            _debounceTimer.Change(TimeSpan.FromMilliseconds(delayMs), Timeout.InfiniteTimeSpan);
        }

        // Records a dirty root and resets the shared debounce timer. Excluded paths (obj/, bin/,
        // generated files) are dropped so editor/build churn under those dirs doesn't trigger reindexes.
        private void OnEvent(string root, string fullPath)
        {
            if (_disposed) return;
            // Same exclusion filter the indexer applies, so we never trigger (or suppress) a reindex
            // on a file the indexer would treat differently.
            if (CSharpCodeGraphIndexer.IsExcludedPath(fullPath)) return;

            lock (_lock)
            {
                if (!_started || _debounceTimer == null) return;
                // Rapid edits keep pushing the single shared timer out (coalesce).
                MarkDirtyAndArm(root, _debounceMs);
            }
        }

        // Snapshot+clear the pending set under the lock, then (outside the lock) reindex each root
        // through the coordinator. Wrapped so a failure can never kill the timer.
        //
        // DEFER, NEVER DROP (real edits): a real FS edit skipped because it's within its per-project
        // freshness floor, because the indexer is busy, or because a transient index error occurred,
        // is re-queued and the timer re-armed so its pending edits are retried — never silently lost.
        // BOOTSTRAP roots (startup sweep / new-root onboarding) instead SKIP-IF-FRESH: a bootstrap
        // root the floor reports fresh is dropped (no reindex, no re-arm); only stale ones index.
        private void OnDebounceFired(object state)
        {
            // Phase 1 — snapshot+clear the pending set under the lock, capturing each root's project
            // name (resolved here so the per-root index work below runs outside the lock).
            List<(string Root, string Name, PendingWork Pw)> work;
            lock (_lock)
            {
                if (_disposed || !_started) { _pending.Clear(); return; }
                if (_pending.Count == 0) return;
                work = new List<(string, string, PendingWork)>(_pending.Count);
                foreach (var kv in _pending)
                {
                    _watched.TryGetValue(kv.Key, out var wr);
                    work.Add((kv.Key, wr?.Name, kv.Value));
                }
                _pending.Clear();
            }

            // Phase 2 — process each root outside the lock. Roots that defer are collected with the
            // earliest delay after which a retry is worth attempting (shared timer → re-arm once to min).
            var deferred = new List<(string Root, PendingWork Pw)>();
            long reArmDelayMs = long.MaxValue;
            foreach (var (root, name, pw) in work)
            {
                long? deferDelay = ProcessRoot(root, name, pw);
                if (deferDelay is long d)
                {
                    deferred.Add((root, pw));
                    reArmDelayMs = Math.Min(reArmDelayMs, d);
                }
            }

            // Phase 3 — re-queue the deferred roots and re-arm once, under the lock.
            if (deferred.Count > 0)
            {
                lock (_lock)
                {
                    if (_disposed || !_started || _debounceTimer == null) return;
                    // Anything already in _pending here was queued+armed by a CONCURRENT signal (an FS
                    // edit / watcher error / onboarding bootstrap) that landed while we indexed outside
                    // the lock. Our Change() below overrides that arm, so if such concurrent work
                    // exists, clamp to _debounceMs (G7b) — otherwise a floor-defer's multi-minute delay
                    // would over-delay that unrelated fresh edit's reindex.
                    bool hasConcurrentWork = _pending.Count > 0;
                    foreach (var (root, pw) in deferred)
                        MergePending(root, pw, isRetry: true);
                    // deferred.Count > 0 guarantees reArmDelayMs was lowered from long.MaxValue.
                    long delay = hasConcurrentWork ? Math.Min(reArmDelayMs, _debounceMs) : reArmDelayMs;
                    _debounceTimer.Change(TimeSpan.FromMilliseconds(delay), Timeout.InfiniteTimeSpan);
                }
            }
        }

        // Index one snapshotted root. Returns null if handled (indexed, bootstrap-skipped, or
        // given-up after the retry budget); otherwise the delay (ms) after which the caller should
        // re-queue+retry it. Never throws — a failure is logged and converted to a bounded retry.
        private long? ProcessRoot(string root, string projectName, PendingWork pw)
        {
            try
            {
                // Per-project freshness floor (F3): evaluated per root so a fresh project doesn't
                // suppress a stale one.
                if (IsWithinFreshnessFloor(projectName, out long remainingMs))
                {
                    if (pw.Bootstrap)
                    {
                        // G3: a bootstrap/sweep root that's already fresh is DROPPED — no reindex,
                        // no re-arm. (A real FS edit within the floor still defers, below.)
                        // ACCEPTED TRADEOFF (G7c, tracked in follow-up ticket 8633273d): the floor
                        // tests last_indexed RECENCY, not whether the graph still matches disk. A
                        // project changed OFFLINE (outside this process) within the floor window is
                        // skipped here until a live .cs edit or app restart re-triggers it. We do NOT
                        // defer-past-floor for bootstrap roots — that would reindex every fresh project
                        // on every launch, the exact waste G3 removed.
                        LogInfo($"Bootstrap of {root} skipped: already fresh (within {_minIntervalMs}ms floor).");
                        return null;
                    }
                    LogInfo($"Deferring reindex of {root}: within {_minIntervalMs}ms freshness floor, retry in ~{remainingMs}ms.");
                    return Math.Max(remainingMs, 1);
                }

                if (_coordinator.TryIndex(root, projectName, out var result) && result != null)
                {
                    LogInfo($"Reindexed '{result.ProjectName}': {result.FileCount} files, " +
                            $"{result.SymbolCount} symbols, {result.RelationshipCount} relationships in {result.DurationMs}ms.");
                    return null; // success — failure budget discarded with it.
                }

                // Indexer busy (manual REST index or another root mid-flight). Re-queue and retry
                // after the debounce window — coalescing, not dropping. Busy isn't a failure, so it
                // doesn't consume the bounded-retry budget.
                LogInfo($"Deferring reindex of {root}: indexer busy, retry in ~{_debounceMs}ms.");
                return _debounceMs;
            }
            catch (Exception ex)
            {
                // G2/G6: a transient index failure (SQLite 'database is locked' during a concurrent
                // manual index, DB still warming when the 2s startup sweep fires, etc.) must DEFER
                // with backoff, not drop — but bounded, so a poison root can't spin forever.
                pw.Attempts++;
                int maxAttempts = pw.Bootstrap ? MaxBootstrapAttempts : MaxEditAttempts;
                if (pw.Attempts < maxAttempts)
                {
                    long backoff = RetryBackoff(pw);
                    LogError($"Reindex of {root} failed (attempt {pw.Attempts}/{maxAttempts}), retrying in ~{backoff}ms: {ex.Message}");
                    return backoff;
                }
                LogError($"Reindex of {root} failed {pw.Attempts} times; giving up until the next change event: {ex.Message}");
                return null; // drop — a future FS edit re-queues it (QueueRoot resets Attempts to 0).
            }
        }

        // Backoff before the next retry of a failed index. A real edit uses a flat floor (a future
        // edit can always recover it). A bootstrap root — which has no future edit to fall back on —
        // escalates per attempt and exceeds _debounceMs, capped, so a sustained startup DB lock is
        // outlasted within its larger MaxBootstrapAttempts budget.
        private long RetryBackoff(PendingWork pw)
        {
            if (!pw.Bootstrap)
                return Math.Max(_debounceMs, RetryBackoffMs);
            long escalated = _debounceMs + (long)RetryBackoffMs * pw.Attempts;
            return Math.Min(escalated, BootstrapBackoffCapMs);
        }

        // Returns true if reindexing <paramref name="projectName"/> should be skipped because its
        // last index is too recent. Reads the PER-PROJECT last_indexed metadata (keyed by project
        // name) for the root under evaluation, so indexing one project never floor-suppresses another.
        // remainingMs is set to ~time until the floor expires (used to schedule a deferred retry).
        // A missing key (never indexed, or a pre-F3 graph that only wrote the global key) returns
        // false → the root is indexed, which is exactly how a stale graph heals.
        private bool IsWithinFreshnessFloor(string projectName, out long remainingMs)
        {
            remainingMs = 0;
            if (_minIntervalMs <= 0) return false;
            if (string.IsNullOrEmpty(projectName)) return false;

            string last = _db.GetProjectLastIndexed(projectName);
            if (string.IsNullOrEmpty(last)) return false;

            if (!DateTime.TryParse(last, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastUtc))
                return false;

            double ageMs = (DateTime.UtcNow - lastUtc.ToUniversalTime()).TotalMilliseconds;
            if (ageMs >= 0 && ageMs < _minIntervalMs)
            {
                remainingMs = (long)Math.Ceiling(_minIntervalMs - ageMs);
                return true;
            }
            return false;
        }

        // ─── Helpers ─────────────────────────────────────────────────────────

        private static void DisposeWatcher(FileSystemWatcher watcher)
        {
            if (watcher == null) return;
            try { watcher.EnableRaisingEvents = false; } catch { }
            try { watcher.Dispose(); } catch { }
        }

        private static readonly HashSet<string> WatchDisableTokens =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "0", "false", "off", "no", "disabled" };
        private static readonly HashSet<string> WatchEnableTokens =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "1", "true", "on", "yes", "enabled" };

        private bool ReadWatchEnabled()
        {
            var v = Environment.GetEnvironmentVariable("MULTITERMINAL_CODEGRAPH_WATCH");
            if (string.IsNullOrWhiteSpace(v)) return true;
            v = v.Trim();
            if (WatchDisableTokens.Contains(v)) return false;
            if (WatchEnableTokens.Contains(v)) return true;
            _configWarnings.Add($"MULTITERMINAL_CODEGRAPH_WATCH='{v}' is not a recognized on/off value; treating as enabled.");
            return true;
        }

        // Parses a long env var, defaulting on absent/unparseable and clamping to [floor, max].
        // Records a warning (flushed in Start) when a present value is unparseable or clamped, so a
        // typo is distinguishable from intent.
        private long ReadLongEnv(string name, long def, long floor, long max)
        {
            var raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw)) return def;

            var trimmed = raw.Trim();
            if (!long.TryParse(trimmed, out var parsed))
            {
                _configWarnings.Add($"{name}='{trimmed}' is not a valid integer; using default {def}.");
                return def;
            }
            if (parsed < floor)
            {
                _configWarnings.Add($"{name}={parsed} below floor {floor}; clamped to {floor}.");
                return floor;
            }
            if (parsed > max)
            {
                _configWarnings.Add($"{name}={parsed} above max {max}; clamped to {max}.");
                return max;
            }
            return parsed;
        }

        // One eligible root's installed watcher + its current registry project name (used as the
        // reindex's projectName). Name is refreshed in place on reconcile; Watcher is replaced only
        // by remove+add.
        private sealed class WatchedRoot
        {
            public FileSystemWatcher Watcher { get; set; }
            public string Name { get; set; }
        }

        // Per-root pending state in _pending (canonical semantics documented at _pending / OnDebounceFired).
        // NOTE: Bootstrap is a 2-kind policy bundle (skip-vs-defer-if-fresh, max attempts, backoff curve,
        // merge precedence). If a THIRD work-kind ever needs its own policy (the watcher-error recovery,
        // which currently rides the edit path, is the likely candidate), promote Bootstrap to a WorkKind
        // enum / RetryPolicy record rather than adding a second bool — don't create a flag cross-product.
        private sealed class PendingWork
        {
            public bool Bootstrap { get; set; }
            public int Attempts { get; set; }
        }
    }
}
