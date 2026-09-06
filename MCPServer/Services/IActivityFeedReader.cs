using System.Collections.Generic;

namespace MultiTerminal.MCPServer.Services
{
    /// <summary>
    /// The two reads <see cref="AgentActivityWatcher"/> needs from the activity feed (task edcdcdd5).
    /// </summary>
    /// <remarks>
    /// Narrow on purpose. <see cref="ActivityFeedService"/> is a concrete class over a live SQLite
    /// connection, which made the watcher's failure paths — a read that throws at startup, a read
    /// that throws mid-run — untestable without a corrupt database. The adversary gate found
    /// exactly such a path: a transient failure in the first watermark read left the watcher
    /// silently dead for the life of the process. This seam is what lets a test say "throw once,
    /// then work" and prove the watcher recovers.
    /// </remarks>
    public interface IActivityFeedReader
    {
        /// <summary>Rows with an id greater than <paramref name="afterId"/>, ascending by id.</summary>
        List<ActivityFeedEntry> GetActivitiesAfterId(long afterId, int limit = 200);

        /// <summary>The highest row id currently in the table, or 0 when it is empty.</summary>
        long GetMaxActivityId();
    }
}
