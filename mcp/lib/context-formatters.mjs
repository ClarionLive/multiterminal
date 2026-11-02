function usageWindow(label, percent, resetIn) {
  let text = `${label}: ${percent == null ? "unknown" : percent + "%"}`;
  if (resetIn) text += ` (resets in ${resetIn})`;
  return text;
}

export function formatClaudeUsage(stats) {
  return `Claude usage — ${usageWindow("5h", stats?.fiveHourPercent, stats?.fiveHourResetIn)}`
    + `  ${usageWindow("7d", stats?.sevenDayPercent, stats?.sevenDayResetIn)}`;
}
