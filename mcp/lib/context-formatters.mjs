function usageWindow(label, percent, resetIn) {
  let text = `${label}: ${percent == null ? "unknown" : percent + "%"}`;
  if (resetIn) text += ` (resets in ${resetIn})`;
  return text;
}

// Plans without a 5-hour limit (Ultra) never report one, so its absence is not worth a line.
export function formatClaudeUsage(stats) {
  const sevenDay = usageWindow("7d", stats?.sevenDayPercent, stats?.sevenDayResetIn);
  if (stats?.fiveHourPercent == null) return `Claude usage — ${sevenDay}`;
  return `Claude usage — ${usageWindow("5h", stats.fiveHourPercent, stats.fiveHourResetIn)}  ${sevenDay}`;
}
