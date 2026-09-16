using System;
using System.Collections.Concurrent;
using System.Threading;

namespace MultiTerminal.MCPServer.Services
{
    /// <summary>What <see cref="SpawnJobStore.TryCollect"/> found for a docId.</summary>
    public enum SpawnJobCollectOutcome
    {
        /// <summary>This call took the job. It is the only call that ever will.</summary>
        Collected,

        /// <summary>A job existed and an earlier call already took it.</summary>
        AlreadyCollected,

        /// <summary>No job was ever stored for this docId.</summary>
        NotFound,
    }

    /// <summary>
    /// One spawned helper's job, held until the helper asks for it.
    /// </summary>
    public sealed class PendingSpawnJob
    {
        private int _collected;
        private int _giveUpReported;

        internal PendingSpawnJob(string docId, string agentName, string spawnerName, string job, DateTime createdUtc)
        {
            DocId = docId;
            AgentName = agentName;
            SpawnerName = spawnerName;
            Job = job;
            CreatedUtc = createdUtc;
        }

        public string DocId { get; }

        public string AgentName { get; }

        public string SpawnerName { get; }

        /// <summary>The job text. Kept after collection so the collecting caller can read it from the entry.</summary>
        public string Job { get; }

        public DateTime CreatedUtc { get; }

        /// <summary>Set by the one call that collected the job; null until then.</summary>
        public DateTime? CollectedUtc { get; private set; }

        public bool IsCollected => Volatile.Read(ref _collected) != 0;

        /// <summary>True once the uncollected-job report has been sent, so a later collection can be logged as late.</summary>
        public bool GiveUpReported => Volatile.Read(ref _giveUpReported) != 0;

        internal bool TryMarkCollected(DateTime nowUtc)
        {
            if (Interlocked.Exchange(ref _collected, 1) != 0)
                return false;

            CollectedUtc = nowUtc;
            return true;
        }

        /// <summary>
        /// Claims the right to send the uncollected-job report. Fails if the job was collected first,
        /// or if the report was already claimed.
        /// </summary>
        /// <remarks>
        /// This check does not atomically exclude a collection landing in the same instant: a helper can
        /// collect just after this returns true, and the report then describes a job that did arrive. The
        /// report's wording has to allow for that, and the collection is logged as late so the log shows
        /// both events.
        /// </remarks>
        internal bool TryMarkGiveUpReported()
        {
            if (IsCollected)
                return false;

            return Interlocked.Exchange(ref _giveUpReported, 1) == 0;
        }
    }

    /// <summary>
    /// Holds spawned helpers' jobs until each helper collects its own (task 8b270b37, Owner decision
    /// 2026-09-16).
    ///
    /// <para><b>Why pull instead of push.</b> Every push path lost jobs silently. Typing the job left it
    /// unsent in the composer. Sending it over the channel lost it whenever MT sent before Claude Code had
    /// started listening for channel messages. Live, Lv1x2's job went out 41ms before that moment and
    /// vanished, while the channel server answered 200 "delivered" (that 200 means only that the frame was
    /// written to stdout). A helper that asks for its job is by definition ready to receive it, and the
    /// request is the receipt.</para>
    ///
    /// <para><b>Collected at most once.</b> A second collect gets <see cref="SpawnJobCollectOutcome.AlreadyCollected"/>,
    /// never the job again. A helper that re-runs its startup instruction (after /clear or compaction)
    /// must not do the job twice. The cost is that a collect whose response is lost in transit loses the
    /// job; the caller is told when it was collected so it can say so rather than silently idle.</para>
    ///
    /// <para><b>Keys compare ordinally, by decision.</b> A docId is minted by MT, never typed by a person,
    /// so there is nothing to normalise. An ignore-case or trimming comparer here would let one pane
    /// collect a job addressed to a different pane whose id differs only in that way. See
    /// <see cref="KeyComparer"/>.</para>
    ///
    /// <para><b>Not an access control.</b> Anything that can reach MT's localhost API and knows a docId can
    /// collect that pane's job. That is the same trust boundary as the rest of the local API, and it is
    /// the transport, not this class, that bounds it.</para>
    /// </summary>
    public sealed class SpawnJobStore
    {
        /// <summary>The comparer the job table actually uses. Exposed so a test can ask it directly.</summary>
        public static readonly StringComparer KeyComparer = StringComparer.Ordinal;

        private readonly ConcurrentDictionary<string, PendingSpawnJob> _jobs = new(KeyComparer);
        private readonly Func<DateTime> _utcNow;

        public SpawnJobStore()
            : this(() => DateTime.UtcNow)
        {
        }

        /// <summary>Test seam for the clock.</summary>
        public SpawnJobStore(Func<DateTime> utcNow)
        {
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        }

        /// <summary>
        /// Raised after a successful collection, on the collecting thread. The subscriber logs the receipt.
        /// </summary>
        public event EventHandler<PendingSpawnJob> Collected;

        /// <summary>
        /// Stores a job for a pane. Returns false (and stores nothing) if the docId is blank, the job is
        /// blank, or the docId already has a job. A docId names exactly one pane, so a second job for it is
        /// a caller bug, and silently replacing the first would lose it.
        /// </summary>
        public bool TryAdd(string docId, string agentName, string spawnerName, string job)
        {
            if (string.IsNullOrEmpty(docId) || string.IsNullOrWhiteSpace(job))
                return false;

            return _jobs.TryAdd(docId, new PendingSpawnJob(docId, agentName, spawnerName, job, _utcNow()));
        }

        /// <summary>
        /// Takes the job for <paramref name="docId"/> if nobody has yet. <paramref name="entry"/> is set for
        /// both <see cref="SpawnJobCollectOutcome.Collected"/> and <see cref="SpawnJobCollectOutcome.AlreadyCollected"/>
        /// so the caller can report when an earlier collection happened; it is null only for NotFound.
        /// </summary>
        public SpawnJobCollectOutcome TryCollect(string docId, out PendingSpawnJob entry)
        {
            entry = null;
            if (string.IsNullOrEmpty(docId) || !_jobs.TryGetValue(docId, out var found))
                return SpawnJobCollectOutcome.NotFound;

            entry = found;
            if (!found.TryMarkCollected(_utcNow()))
                return SpawnJobCollectOutcome.AlreadyCollected;

            Collected?.Invoke(this, found);
            return SpawnJobCollectOutcome.Collected;
        }

        /// <summary>The entry for a docId, collected or not; null if none was stored.</summary>
        public PendingSpawnJob Get(string docId)
        {
            if (string.IsNullOrEmpty(docId))
                return null;

            return _jobs.TryGetValue(docId, out var entry) ? entry : null;
        }

        /// <summary>
        /// Claims the uncollected-job report for a docId. True means: a job exists, it has not been
        /// collected, and no report was claimed before. See <see cref="PendingSpawnJob.TryMarkGiveUpReported"/>
        /// for the race this does not close.
        /// </summary>
        public bool TryClaimGiveUpReport(string docId)
        {
            var entry = Get(docId);
            return entry != null && entry.TryMarkGiveUpReported();
        }
    }
}
