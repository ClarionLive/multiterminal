using System;

namespace MultiTerminal.DashboardHeader
{
    /// <summary>
    /// One project as the header's Launch Project picker shows it (task 4cac608c). MainForm builds
    /// the list; the picker sorts pinned first, then most recently opened.
    /// </summary>
    public sealed record HeaderProjectItem(
        string Id,
        string Name,
        string Path,
        string Icon,
        string IconColor,
        bool IsPinned,
        DateTime LastOpenedAt,
        string Status);
}
