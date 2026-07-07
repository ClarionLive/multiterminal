# Startup Resilience — Owner Manual Test Script (task 4fec40e2)

The single-instance mutex and port-5050 contention paths **cannot be exercised by an agent
inside MultiTerminal** — triggering them means launching a second `MultiTerminal.exe`, which
agents never do, and binding :5050, which the running app already holds. So the decision logic
is covered by unit tests (`MultiTerminal.Tests`), and these end-to-end paths are verified here
by the Owner during a deploy/restart window.

**Prerequisite:** exit MultiTerminal → run `deploy.ps1` → relaunch from `Deploy\`, so the new
`Program.cs` mutex, `HealthController`, and `MainForm` bind-failure path are live.

---

## What the unit tests already cover (no manual action)

`dotnet test MultiTerminal.Tests` (30 startup-resilience tests) proves, headlessly:

- `IsAddressInUse` recognizes the Kestrel "address already in use" exception shape (direct
  `SocketException`, `IOException`-wrapped, deeply nested, and message-only fallback) and
  rejects unrelated errors.
- `Classify` maps a probe result to the right verdict (MT marker → *already running*; reached-
  but-not-MT / timeout / null → *foreign holder*).
- `BuildMessage` produces the correct wording for each verdict (PID, port, process name, the
  "does not fall back to another port" note).
- `FindHolderPid` selects the listening PID from a TCP table (LISTEN preferred, non-LISTEN
  fallback, invalid PIDs skipped).
- `Parse` fingerprints MultiTerminal by the `service` marker and round-trips a `HealthIdentity`
  serialized exactly as ASP.NET Core serializes it (camelCase) — locking the `/api/health` ↔
  self-probe contract.

---

## Test 1 — Health endpoint identity (`/api/health`)

1. With MultiTerminal running, from any shell:
   ```
   curl http://127.0.0.1:5050/api/health
   ```
2. **Expect** a JSON body carrying the identity, e.g.:
   ```json
   {"service":"multiterminal-rest-api","app":"MultiTerminal","version":"...","pid":12345,
    "machine":"...","user":"...","sessionId":1,"port":5050,"startedUtc":"..."}
   ```
3. **Pass:** `service` is exactly `multiterminal-rest-api` and `pid` is MultiTerminal's PID
   (check Task Manager). This marker is what the self-probe uses to tell "another MT" from a
   foreign holder.

## Test 2 — Single-instance mutex (same session)

1. With MultiTerminal running, double-click `Deploy\MultiTerminal.exe` again (or run it from a
   shell) **in the same Windows session**.
2. **Expect:**
   - The **existing** MultiTerminal window comes to the foreground (restored if minimized).
   - An information dialog: *"MultiTerminal is already running in this session. The existing
     window has been brought to the front."*
   - The second process **exits cleanly** — no splash, no second window, no second REST host.
3. **Pass:** exactly one MultiTerminal process remains (verify in Task Manager); the first
   window is focused.

> Note: the mutex is `Local\` (per-session) by design. A second copy launched under a
> *different* Windows session (fast user switching / RDP) is NOT stopped by the mutex — it is
> caught instead by Test 3's port path (it collides on the machine-wide :5050 bind). That is
> why both guards exist.

## Test 3 — Foreign process holding port 5050

1. **Exit** MultiTerminal completely (so :5050 is free).
2. Start a dummy listener on 5050 that is *not* MultiTerminal. Easiest option, in a PowerShell
   window that you leave open:
   ```powershell
   $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 5050)
   $listener.Start()
   "holding 5050 as PID $PID — leave this window open"
   # ...run the test, then Ctrl+C / close this window to release the port.
   ```
   Note the PID printed.
3. Launch `Deploy\MultiTerminal.exe`.
4. **Expect** a warning dialog (**not** the old "MCP Server Startup Error" stack trace):
   - Title: *"MultiTerminal — port in use"*.
   - Body names port 5050 and the holder **process name + PID** (the PID from step 2), and
     states MultiTerminal does not fall back to another port.
   - Buttons: **Retry** / **Cancel**.
5. **Verify the PID** in the dialog matches the PowerShell PID from step 2.
6. **Retry path:** close the PowerShell listener (releases 5050), click **Retry** → MultiTerminal
   should bind and start normally.
7. **Exit path (re-run):** with the listener held again, click **Cancel** → MultiTerminal exits
   (no dead-API window left running).

## Test 4 — Stale/other MultiTerminal holding the port (cross-session, optional)

Only reproducible with two Windows sessions. If available:

1. In session A, run MultiTerminal (holds :5050).
2. In session B (different user), launch MultiTerminal.
3. **Expect** the dialog titled *"MultiTerminal already running"* — the self-probe reached
   session A's `/api/health`, so it reports **another MultiTerminal** (with its PID/user/session)
   rather than a foreign process, and offers Retry/Cancel.

---

## Result recording

For each test, record Pass/Fail + notes. Test 1 is quick and low-risk; Tests 2–4 require the
deploy/restart window. A failure in Tests 2–4 routes the relevant checklist item back to coding
with the observed vs expected behavior.
