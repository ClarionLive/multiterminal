# Vendored MCP server (`multiterminal`)

This folder is a **reviewed source-of-truth copy** of the MultiTerminal MCP server that runs
as a Node.js process for each Claude Code session (the `multiterminal` MCP server registered in
`~/.claude.json`).

## Why it's here

The live/runtime copy lives at **`%APPDATA%\multiterminal\mcp\index.js`** and is **not otherwise
tracked in git**. The Inno Setup installer packages the MCP server *directly from that AppData
folder* (`installer/MultiTerminal.iss`):

```inno
#define McpServerDir GetEnv("APPDATA") + "\multiterminal\mcp"
Source: "{#McpServerDir}\*"; DestDir: "{userappdata}\multiterminal\mcp"; ...
```

So today the de-facto source-of-truth is a dev machine's `%APPDATA%`, which means MCP-server
changes are invisible to code review and absent from history. This vendored copy fixes the
**reviewability** gap: edits to the MCP server should be made to `%APPDATA%\multiterminal\mcp\index.js`
(required for runtime + packaging) **and** mirrored here so the diff is reviewable and tracked.

`node_modules/` is intentionally **not** vendored (large, restorable via `npm install` from
`package.json`).

## Follow-up (tracked, not done in task ca6c5344 item [11])

Make this folder the real source-of-truth and stop building from `%APPDATA%`:

1. Repoint `installer/MultiTerminal.iss` `#define McpServerDir` at this repo `mcp/` folder
   (plus a `node_modules` restore step, since deps aren't vendored).
2. Add a dev sync step (repo `mcp/` → `%APPDATA%\multiterminal\mcp`) so runtime always matches
   the reviewed copy, or have MT copy it on startup.

Until that rewire lands, **keep this copy in sync by hand** whenever `index.js` changes.

## Last sync

- `index.js`, `package.json` copied from `%APPDATA%\multiterminal\mcp` on 2026-06-13 as part of
  task `ca6c5344` item [11] (harden `send_push_notification`: route through MT :5050 with
  `forcePush=true`, report real delivery).
