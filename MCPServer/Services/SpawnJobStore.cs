using System;
using System.Collections.Concurrent;
using System.Threading;

namespace MultiTerminal.MCPServer.Services
{
    /// <summary>What <see cref="SpawnJobStore.TryCollect"/> found for a docId.</summary>
    public enum SpawnJobCollectOutcome
    {
        /// <summary>This call took the job. It is the only call that ever will; this is the receipt.</summary>
        Collected,

        /// <summary>
        /// An earlier call took the job, and this call is inside the re-fetch window, so it gets the job
        /// again. Not a second receipt: the collection was already logged once.
        /// </summary>
        Refetched,

        /// <summary>A job existed, an earlier call took it, and the re-fetch window has closed.</summary>
        AlreadyCollected,

        /// <summary>No job was ever stored for this docId.</summary>
        NotFound,
    }

    /// <summary>
    /// One spawned helper's job, held until the helper asks for it.
    /// </summary>
    public sealed class PendingSpawnJob
    {
        // The collection time IS the collected flag: 0 means not collected. One compare-and-swap sets both,
        // so a racing caller that sees the job as collected can never read a null collection time.
        private long _collectedTicks;
        private int _giveUpReported;
        private string _job;

        internal PendingSpawnJob(string docId, string agentName, string spawnerName, string job, DateTime createdUtc)
        {
            DocId = docId;
            AgentName = agentName;
            SpawnerName = spawnerName;
            _job = job;
            JobLength = job?.Length ?? 0;
            CreatedUtc = createdUtc;
        }

        public string DocId { get; }

        public string AgentName { get; }

        public string SpawnerName { get; }

        /// <summary>
        /// The job text. Becomes null LAZILY after the re-fetch window closes: the next <c>TryAdd</c>, or a
        /// <c>TryCollect</c> answering AlreadyCollected, releases it. Until one of those runs it stays in
        /// memory. The point is to stop a long session accumulating every job ever spawned (up to 16,000
        /// characters each), NOT to scrub secrets on a deadline. Callers get the text from
        /// <c>TryCollect</c>'s <c>job</c> out-parameter, never from here.
        /// </summary>
        public string Job => Volatile.Read(ref _job);

        /// <summary>The job's length, kept after the text is released so a receipt can still report it.</summary>
        public int JobLength { get; }

        public DateTime CreatedUtc { get; }

        /// <summary>Set by the one call that collected the job; null until then.</summary>
        public DateTime? CollectedUtc
        {
            get
            {
                long ticks = Interlocked.Read(ref _collectedTicks);
                return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
            }
        }

        public bool IsCollected => Interlocked.Read(ref _collectedTicks) != 0;

        /// <summary>True once the uncollected-job report has been sent, so a later collection can be logged as late.</summary>
        public bool GiveUpReported => Volatile.Read(ref _giveUpReported) != 0;

        internal bool TryMarkCollected(DateTime nowUtc)
        {
            // Ticks of a real DateTime are never 0, so 0 is free to mean "not collected".
            return Interlocked.CompareExchange(ref _collectedTicks, nowUtc.Ticks, 0) == 0;
        }

        internal void ReleaseJobText() => Volatile.Write(ref _job, null);

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
    /// <para><b>Collected once, re-fetchable briefly.</b> The first collect is the receipt. For
    /// <see cref="RefetchWindow"/> after it, a repeat gets the job again as
    /// <see cref="SpawnJobCollectOutcome.Refetched"/>: that covers a response lost in transit, whose retry
    /// comes seconds later (pipeline Run 1, Owner decision 2026-09-16). After the window a repeat gets
    /// <see cref="SpawnJobCollectOutcome.AlreadyCollected"/>, never the job. A helper that re-runs its
    /// startup instruction after compaction does so minutes later, and must not do the job twice.</para>
    ///
    /// <para><b>The store does not check who is asking.</b> The window makes a re-fetch cheap, which is only
    /// safe if the caller is the pane the job was stored for. <c>SpawnController.CollectJob</c> proves that
    /// with the pane's launch nonce BEFORE calling here. A new caller of <see cref="TryCollect"/> must do
    /// the same, or any local process holding a docId can take the job and fake the receipt.</para>
    ///
    /// <para><b>Keys compare ordinally, by decision.</b> A docId is minted by MT, never typed by a person,
    /// so there is nothing to normalise. An ignore-case or trimming comparer here would let one pane
    /// collect a job addressed to a different pane whose id differs only in that way. See
    /// <see cref="KeyComparer"/>.</para>
    /// </summary>
    public sealed class SpawnJobStore
    {
        /// <summary>The comparer the job table actually uses. Exposed so a test can ask it directly.</summary>
        public static readonly StringComparer KeyComparer = StringComparer.Ordinal;

        /// <summary>How long after the first collect the same pane can fetch its job again.</summary>
        public static readonly TimeSpan RefetchWindow = TimeSpan.FromSeconds(60);

        private readonly ConcurrentDictionary<string, PendingSpawnJob> _jobs = new(KeyComparer);
        private readonly Func<DateTime> _utcNow;
        private int _receiptSubscriberFailures;

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
        /// Raised once per job, after its FIRST collection, on the collecting thread. The subscriber logs the
        /// receipt. Each subscriber runs inside its own try/catch: the job is already marked collected when
        /// this fires, so a throwing subscriber must not turn the collect into an error, because the caller
        /// would then never see a job the store now considers delivered.
        /// </summary>
        public event EventHandler<PendingSpawnJob> Collected;

        /// <summary>
        /// Number of <see cref="Collected"/> subscriber calls that threw. A counter rather than a log because
        /// this class has no logger; it exists so a swallowed failure is observable rather than silent.
        /// </summary>
        public int ReceiptSubscriberFailures => Volatile.Read(ref _receiptSubscriberFailures);

        /// <summary>
        /// Stores a job for a pane. Returns false (and stores nothing) if the docId is blank, the job is
        /// blank, or the docId already has a job. A docId names exactly one pane, so a second job for it is
        /// a caller bug, and silently replacing the first would lose it.
        /// </summary>
        public bool TryAdd(string docId, string agentName, string spawnerName, string job)
        {
            ReleaseExpiredJobText();

            if (string.IsNullOrEmpty(docId) || string.IsNullOrWhiteSpace(job))
                return false;

            return _jobs.TryAdd(docId, new PendingSpawnJob(docId, agentName, spawnerName, job, _utcNow()));
        }

        /// <summary>
        /// Takes the job for <paramref name="docId"/>. See <see cref="SpawnJobCollectOutcome"/> for the four
        /// answers. <paramref name="job"/> is the text for Collected and Refetched, captured inside this call
        /// so a later release cannot race the caller; null otherwise. <paramref name="entry"/> is null only
        /// for NotFound.
        /// <para>⚠️ Does not verify the caller. See the class remarks.</para>
        /// </summary>
        public SpawnJobCollectOutcome TryCollect(string docId, out PendingSpawnJob entry, out string job)
        {
            entry = null;
            job = null;
            if (string.IsNullOrEmpty(docId) || !_jobs.TryGetValue(docId, out var found))
                return SpawnJobCollectOutcome.NotFound;

            entry = found;
            DateTime now = _utcNow();

            if (found.TryMarkCollected(now))
            {
                job = found.Job;
                RaiseCollected(found);
                return SpawnJobCollectOutcome.Collected;
            }

            // Collected before. Re-fetchable only inside the window, and only while the text is still held.
            DateTime collectedUtc = found.CollectedUtc ?? now;
            string text = found.Job;
            if (text != null && now - collectedUtc <= RefetchWindow)
            {
                job = text;
                return SpawnJobCollectOutcome.Refetched;
            }

            ReleaseExpiredJobText();
            return SpawnJobCollectOutcome.AlreadyCollected;
        }

        private void RaiseCollected(PendingSpawnJob entry)
        {
            var handlers = Collected;
            if (handlers == null)
                return;

            foreach (EventHandler<PendingSpawnJob> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, entry);
                }
#pragma warning disable CA1031 // A receipt logger's failure must not undo a delivery; counted in ReceiptSubscriberFailures.
                catch (Exception)
#pragma warning restore CA1031
                {
                    Interlocked.Increment(ref _receiptSubscriberFailures);
                }
            }
        }

        /// <summary>
        /// Releases the text of every job whose re-fetch window has closed. Called only from
        /// <see cref="TryAdd"/> and from <see cref="TryCollect"/>'s AlreadyCollected path, not from a timer,
        /// so an idle store keeps expired text until the next of those calls. That bounds growth, which is
        /// the purpose, and keeps the store free of threads and deterministic under the test clock. It is
        /// not a guarantee that text is gone by any particular time.
        /// </summary>
        private void ReleaseExpiredJobText()
        {
            DateTime now = _utcNow();
            foreach (var entry in _jobs.Values)
            {
                DateTime? collected = entry.CollectedUtc;
                if (collected.HasValue && now - collected.Value > RefetchWindow && entry.Job != null)
                    entry.ReleaseJobText();
            }
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
