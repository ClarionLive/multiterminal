# MCP tool: `get_my_terminal_stats` (task e855c051)

Gives an agent programmatic access to its own terminal's usage stats — the same
numbers the HUD shows — so it can self-check whether it's a good moment to wrap up
and recommend a `/clear`. **Context %** (window fill) is the clear/handoff signal;
**5h / 7d** are the rate-cap (quota) signal — kept separate on purpose.

## Why this snippet lives in `docs/`

The Node MCP tool definitions live in `%APPDATA%/multiterminal/mcp/index.js`, which
is **not** in this git repo (the installer treats `%APPDATA%` as the source). So the
worktree auto-merge can't carry an `index.js` edit. This file is the
version-controlled, reviewable source of truth for the tool; apply it to the live
`index.js` as a deploy step (see below). The C# side it calls
(`StatusLineStatsReader` + `GET /api/terminals/{name}/stats`) **is** in the repo and
unit-tested.

## Backing REST endpoint (already in repo)

`GET /api/terminals/{name}/stats[?docId=...]` → `TerminalUsageStats` JSON (camelCase):

```
{ "available": true, "terminalName": "Alice", "model": "opus-4-8",
  "contextPercent": 43, "fiveHourPercent": 55, "sevenDayPercent": 66,
  "fiveHourPace": 3, "sevenDayPace": -2, "fiveHourResetIn": "2h 15m",
  "isOffPeak": true, "quotaSource": "shared",
  "quotaSourceTimestampMs": 1700000000000, "quotaAgeSeconds": 4.0, "quotaStale": false,
  "sourceTimestampMs": 1700000000000, "ageSeconds": 5.0, "stale": false }
```

`available: false` ⇒ terminal hasn't reported yet (newly opened / non-Claude / statusline not fired).

## Apply to `%APPDATA%/multiterminal/mcp/index.js`

### 1. Add to the tools list (near the other `get_my_*` tool defs, ~line 841)

```js
{
  name: "get_my_terminal_stats",
  description: "Get YOUR terminal's live usage stats: context-window % (the signal for when to wrap up / recommend a /clear) and 5h/7d account quota (rate-cap signal). Reads the same numbers the MultiTerminal HUD shows. agentName/docId default from MULTITERMINAL_NAME / MULTITERMINAL_DOC_ID env, so normally call with no args.",
  inputSchema: {
    type: "object",
    properties: {
      agentName: { type: "string", description: "Your terminal/agent name (defaults to MULTITERMINAL_NAME env var)" },
      docId: { type: "string", description: "Your terminal doc ID (defaults to MULTITERMINAL_DOC_ID env var; if omitted the newest stats file for the name is used)" }
    }
  }
},
```

### 2. Add the handler case (in the tool `switch`, near `case "get_my_active_task"`, ~line 3118)

```js
case "get_my_terminal_stats": {
  const name = args.agentName || process.env.MULTITERMINAL_NAME;
  const docId = args.docId || process.env.MULTITERMINAL_DOC_ID;
  if (!name) {
    return { content: [{ type: "text", text: "No terminal name available (set MULTITERMINAL_NAME or pass agentName)." }] };
  }
  const qs = docId ? `?docId=${encodeURIComponent(docId)}` : "";
  const s = await apiCall(`/api/terminals/${encodeURIComponent(name)}/stats${qs}`);
  if (!s || !s.available) {
    return { content: [{ type: "text", text: `No usage stats reported yet for "${name}" (newly opened, non-Claude, or the statusline hasn't fired). Try again shortly.` }] };
  }

  const ctx = s.contextPercent;
  let hint;
  // A stale reading must NOT produce an actionable clear/handoff recommendation:
  // an abandoned/zombie session resolved by name (docId omitted) could otherwise
  // report an hours-old 95% and tell the agent to "/clear now" off dead data.
  // Stale short-circuits to a refresh-first message until a fresh sample lands.
  if (s.stale) hint = `Reading is stale (last update ${Math.round(s.ageSeconds)}s ago) — refresh before acting on a clear/handoff.`;
  else if (ctx == null) hint = "Context % not reported (non-Claude terminal?).";
  else if (ctx >= 85) hint = "⚠️ Context high — write continuation notes / commit WIP and recommend a /clear now.";
  else if (ctx >= 70) hint = "Context getting full — plan to wrap at the next task boundary.";
  else hint = "Context healthy — fine to keep going.";

  let text = `📊 Terminal usage — ${name}${s.model ? ` (${s.model})` : ""}\n`;
  text += `\nCONTEXT (window fill → clear/handoff signal):\n`;
  text += `  ${ctx == null ? "--" : ctx + "%"} used${s.stale ? `  ⏳ (stale — last update ${Math.round(s.ageSeconds)}s ago)` : ""}\n`;
  text += `  → ${hint}\n`;
  // quotaStale is tracked separately from context staleness: the shared account
  // file can be stale (writer stopped / rate-limit data dropped) while the
  // per-terminal context fill is current. Flag it so a cached rate-cap isn't read as live.
  text += `\nQUOTA (rate-cap signal, account-level)${s.quotaStale ? "  ⏳ stale" : ""}:\n`;
  text += `  5h:  ${s.fiveHourPercent == null ? "--" : s.fiveHourPercent + "%"}${s.fiveHourResetIn ? ` (resets in ${s.fiveHourResetIn})` : ""}\n`;
  text += `  7d:  ${s.sevenDayPercent == null ? "--" : s.sevenDayPercent + "%"}\n`;
  if (s.isOffPeak === true) text += `  (off-peak window)\n`;
  return { content: [{ type: "text", text }] };
}
```

### 3. Restart MultiTerminal (or relaunch the MCP server) so the new tool registers.

## Hint thresholds

`>=85` act (handoff + recommend clear), `>=70` plan to wrap, else healthy. The owner
prefers clearing around 30–40% or at task boundaries, so treat these as upper bounds,
not targets. Agents can't `/clear` themselves — the value is doing the *handoff prep*
(continuation notes, commit WIP) at the right moment and telling the user.

## Phase 2 (separate task)

A background monitor that watches `contextPercent` and pushes a channel message at the
thresholds above (with hysteresis — fire once per crossing). It reuses
`StatusLineStatsReader`, so the C# primitive built here is the foundation.
