using System.Collections.Generic;

namespace MultiTerminal.Models
{
    /// <summary>
    /// Aggregated analytics payload for a single project. Pushed from
    /// HudDashboardRenderer into hud-dashboard.html as a separate
    /// <c>"analytics"</c> WebView2 message — sibling to the existing
    /// <c>project_info</c> / <c>task_stats</c> / <c>activity</c> messages.
    /// (Bundling all four into one payload is a noted follow-up if flicker
    /// becomes observable in real use.)
    /// </summary>
    /// <remarks>
    /// <see cref="Degraded"/> is set when ProjectAnalyticsService dropped
    /// data during aggregation — a corrupted <c>checklist_json</c>, a
    /// <c>SQLiteException</c> on the per-task <c>task_reports</c> read,
    /// etc. The renderer surfaces this as an "incomplete" badge so the
    /// user knows the green/yellow/red Health colors are computed from
    /// partial evidence and shouldn't be trusted as authoritative. Without
    /// the flag, fail-soft logging would hide read failures behind
    /// plausible-looking analytics in Release builds.
    /// </remarks>
    public sealed record ProjectAnalytics(
        KpiStripData Kpis,
        IReadOnlyList<HealthDay> Health,
        IReadOnlyList<ProgressPoint> Progress,
        bool Degraded);

    /// <summary>
    /// The six KPI cards rendered across the top of the Dashboard tab.
    /// Order is fixed and matches the visual layout left-to-right.
    /// </summary>
    public sealed record KpiStripData(
        KpiCard Open,
        KpiCard InProgress,
        KpiCard Blocked,
        KpiCard Completion,
        KpiCard Health,
        KpiCard LastActivity);

    /// <summary>
    /// One KPI card. Tone is the only color-bearing field per the
    /// "greyscale + status-only color" rule (good/warn/bad/neutral).
    /// </summary>
    public sealed record KpiCard(string Value, string Sublabel, string Tone)
    {
        public const string ToneNeutral = "neutral";
        public const string ToneGood = "good";
        public const string ToneWarn = "warn";
        public const string ToneBad = "bad";
    }

    /// <summary>
    /// One day in the 30-day Health Timeline band. Reason is shown in the
    /// hover tooltip on the rendered band.
    /// </summary>
    public sealed record HealthDay(string Date, string Status, string Reason)
    {
        public const string StatusGreen = "green";
        public const string StatusYellow = "yellow";
        public const string StatusRed = "red";
        public const string StatusUnknown = "unknown";
    }

    /// <summary>
    /// One day in the 30-day Progress Over Time series. Counts are end-of-day
    /// snapshots that the JS polyline plots as cumulative Done vs cumulative
    /// Total. The gap (<see cref="Open"/> = <see cref="Total"/> - <see cref="Done"/>)
    /// is the outstanding work line.
    /// </summary>
    /// <remarks>
    /// We don't have task-status history, so "in_progress at past EoD" can't be
    /// reconstructed reliably. <see cref="Done"/> + <see cref="Total"/> are
    /// computed from verifiable timestamps (CreatedAt and the latest
    /// "→ done" checklist-note transition), which keeps the chart honest.
    /// </remarks>
    public sealed record ProgressPoint(string Date, int Done, int Open, int Total);
}
