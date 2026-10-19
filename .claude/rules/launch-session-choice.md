# Project launch: new session, continue last, or resume a past one

Task **ec2ed636** (2026-10-17). Read this before changing `MainForm.OnStartScreenProjectLaunched`,
`SessionCatalog`, `LaunchSessionChoiceModel`, `ClaudeSessionTracker` or the `--resume` argument in
`LaunchCommandBuilder`. Task **7d20e94c** added helpers resuming with their PM (last section); read
that before changing a pane close path, `MainForm.OnFormClosing`, `AddNewTerminal`'s resume/pin
parameters or `pane_launches`.

## What the Owner sees

Launching a Claude project from Home, Browse All, the launch caret, the header's Launch Project or
New Project on an existing folder goes through one hook, `OnStartScreenProjectLaunched`. Before the
claude command is built, MT decides:

| Condition | Result |
|---|---|
| Shift held on the click | New session, no prompt |
| Not a Claude kind (Codex, PowerShell) | New session, no prompt |
| `projects.launch_mode` = `new` | New session |
| `launch_mode` = `continue` | Resume the newest resumable **non-helper** session (newest helper only if every candidate is a helper); new if none; the dialog if that one may be open elsewhere |
| `launch_mode` = `ask` (default, NULL, unknown) | `LaunchSessionDialog` if at least one session is resumable, else new |
| Catalog load failed | A message: start new or cancel. Never silently treated as "no history" |

The dialog offers New session (default), Continue last (context used, agent, age, summary), and the last ~15
sessions, helper and worktree sessions included. There is an editable name, prefilled with the
session's original MT name. "Don't ask again" writes `launch_mode` via `ProjectDatabase.SetLaunchMode`
(New maps to `new`, Continue to `continue`; picking a specific session leaves `ask`). The way back to
`ask` is the **Launch mode** field in the project settings form's Launch section (the gear on a terminal's
status bar, a Home card or a Browse All row): Ask each time / Always start a new session / Always continue
the last session. It saves through `ProjectSettingsService`, which writes the column with
`SetLaunchMode` inside the save's transaction, so `SetLaunchMode` stays its only writer; it applies at the
next launch.

### The past-session list (item 7)

An owner-drawn details `ListView` (the dialog is resizable): **Agent** (`[Helper]` marker, `⚠` when the
session may be open elsewhere), **Where** (project folder / worktree `<id>`), **Age**, **Context**,
**Summary** (fills the remaining width). Font is the dialog's +2pt, rows ~1.6x the text height (a
1px-wide `SmallImageList` sets the row height). Double-click or Enter on a row resumes it; while the
list has focus Enter is "Resume selected", not the dialog default. Header, rows and selection are
drawn from the `TerminalTheme`, so a new column is an owner-draw change, not just a `Columns.Add`.

**Context** is the context in use at the session's last main-thread assistant turn, read in
`SessionCatalog` from the transcript tail (`TryReadContextUsage`): `message.usage.input_tokens +
cache_read_input_tokens + cache_creation_input_tokens` (output tokens excluded). Sidechain turns,
`message.model == "<synthetic>"` and all-zero usage are skipped. If the 64KB tail holds no such turn,
one 512KB tail read is tried. The window comes from `message.model` and the line's `requestedModel`
(`ContextWindowFor`): a `[1m]` suffix, Fable/Mythos, any generation-5 model and Opus/Sonnet 4.6+ are
1M; older ids are 200k; anything unrecognised is unknown (tokens only). A window the tokens exceed is
dropped rather than shown as >100%. Text and colour are `LaunchSessionChoiceModel.FormatContext` /
`ContextLevelOf`: `142k / 200k (71%)`, warning at 80%, critical at 90%, `—` with no usage. The same
text leads the Continue-last preview.

**Helper summaries.** A helper's first prompt is only "collect your job", so for a helper the
summary is the first line of its `get_my_spawn_job` result body (after the `YOUR JOB from` header),
matched by `tool_use_id`. In real helper transcripts that result is ~650KB in, past the 256KB head,
so helpers alone get a bounded second read (to 1.5MB / 400 lines) that continues from the end of the
head bytes already in memory (the head is never read twice; a job inside the head costs no read).
Every summary goes through `ToSummaryLine`, and every other displayed transcript- or DB-derived
string (agent name, spawner, the tooltip's cwd, the worktree id) through `ToDisplayText`, which
strips control and format characters (bidi overrides, zero-width): it is untrusted text. The cwd a
resume launches from is never altered; only the tooltip's copy is.

**Read budget.** The catalog runs on the UI thread. The 512KB usage tail and the helper job scan are
OPTIONAL reads, made only for an entry that passed every rejection check and only while the
per-`List` byte budget (`DefaultReadByteBudget`, 24MB) has room; otherwise the entry is still listed
with `—` context or its fallback summary. The 256KB head and 64KB tail are always read.

## Why transcripts, not MT's session tables

`session_lineage` has no rows for spawned helpers and its `session_file_path` goes stale when Claude
Code moves a transcript into a worktree's folder. `SessionCatalog` therefore reads Claude Code's own
folders: `~/.claude/projects/<encoded root>` and every `<encoded root>--claude-worktrees-*`. Encoding
is `[^A-Za-z0-9] -> '-'`. It reads only a bounded head and tail of each transcript and caps the number
of transcripts read. Agent names and summaries come from injected DB lookups, with the transcript as
fallback.

## The rules that keep a resume safe

- **The resume cwd is the transcript's own folder.** Claude finds a session only from the directory
  it was started in. `ResolveCwd` returns only a recorded cwd that encodes to the transcript's folder
  (or the project root for the root's own folder); anything else drops the session rather than
  offering one that fails with "No conversation found".
- **Transcript content is untrusted.** `ValidateResumeCwd` checks the cwd *as a string* before any
  filesystem call: a local drive path, canonicalised, equal to the project root or under
  `<root>\.claude\worktrees\<id>`. A UNC path would otherwise make Windows authenticate to a remote
  host just by opening the chooser. It is re-validated immediately before `BuildCommand`. Junctions
  are not resolved (local-trust model).
- **Never two processes on one transcript.** The live set is every MT pane's
  `CurrentClaudeSessionId`, every pane's `PendingResumeSessionId` (reserved the moment a resume
  launches, before the statusline reports anything) and every headless helper's
  `AgentProcess.SessionId`. A pane or helper with no known id yet marks every session written since
  its launch as "may be open elsewhere", which needs a confirmation. Just before registration, the
  live set is rebuilt and the transcript re-checked; a change aborts back to the start screen.
- **Outside MT it is advisory only.** CA tabs and plain terminals are invisible to the live set; the
  only signal is a transcript written in the last ~2 minutes. There is no cross-process lock.
- **Name and launch path.** The resumed pane goes through `LaunchCommandBuilder` like any launch; only
  `--resume <id>` is added (GUID-validated, Claude only). A chosen name equal to the project-derived
  identity goes through `TryRegisterProjectIdentity` (fail closed); any other name is registered with
  `PreRegisterTerminalWithName` and the pane's launch nonce, which must not rotate before
  `StartTerminal`. A held name comes back suffixed, and the pane shows the actual name.
- **`--resume`, never `--continue`.** `--continue` picks the newest session in the cwd, which may be
  live, and cannot reach worktree sessions.

## Helpers resume with their PM (task 7d20e94c)

Resuming a PM session also reopens the helper panes it had open when it last ended. Owner decisions:
Continue mode (no dialog) resumes all of them and reports what it skipped; a helper whose Claude
process exited while its pane stayed open counts as CLOSED; one level only (no helpers of helpers),
at most 6. A new session never resumes helpers.

### The launch record

Nothing durable linked a PM session to its helpers: `SpawnedPaneRegistry` and `SpawnJobStore` are in
memory, `MULTITERMINAL_SPAWNER` is a name, and names repeat across days. So every pane launch is a row
in `pane_launches` (`Services/PaneLaunchRecorder`; schema in `database-tables.md`), keyed by launch
and session ids. MainForm writes it only through `Services/PaneLaunchTracker`:

| Event | Where | Write |
|---|---|---|
| `TerminalDocument.LaunchBegan` (every launch path, after `Start` returns) | `WirePaneLaunchRecording`, called at every `new TerminalDocument()` site next to `TerminalExited` | new row; a previous launch of the same pane still open is ended `process_exit` first ("Launch as" stops the process directly) |
| `ClaudeSessionAdopted` | same | `session_id` of the pane's current launch |
| spawn | `OnSpawnRequested`, inside the Invoke after `AddNewTerminal` | `spawner_launch_id` = the PROVEN spawner pane's (nonce, never the typed name) current launch |
| close_helper | `OnSpawnedPaneCloseRequested`, BEFORE `doc.Close()` | `close_helper`; the `pane_closed` that Close raises then finds no open launch |
| pane removed from the dock | `OnDockContentRemoved` | `pane_closed` |
| process exit, pane left open | `OnTerminalExited` | `process_exit` |
| MT exits | `OnFormClosing`, right after the exit confirmation | every open row of this run `mt_exit`; see the guard below |
| MT crashes | nothing | rows stay open, which also reads as open |

**Spawner link in either order.** The helper's `LaunchBegan` fires inside `AddNewTerminal`, before
the caller knows the helper's DocId, so the link is normally made after the row exists. If it does
not exist yet, the spawner's launch (resolved at link time) is held per DocId and applied once when
the helper's launch begins; closing the pane first drops it.

**Threading.** Every caller is on the UI thread today (LaunchBegan is raised there; the adopted and
exited events are posted there; the spawn and close callbacks Invoke onto it). That is the callers'
accident, not the class's property, so the tracker's two maps are under a lock; the SQLite writes
happen outside it.

### The shutdown guard, and why it is first

`OnFormClosing` stops every pane, and a stopped pane looks exactly like the Owner closing it (exit
and removal events). Without a guard every clean exit would mark every helper closed and nothing
could ever be resumed. So `PaneLaunchTracker.BeginShutdown` runs at the start of the REAL close path:
after the exit confirmation (a declined one returns before it and changes nothing) and before the
session save and the pane teardown. It marks every open launch of this run `mt_exit`
(`EndAllForShutdown`), and from then on the three close handlers write nothing. It is idempotent, so
the session-save pass's re-triggered `Close()` does nothing twice. A second close that is cancelled
after the confirmation (nothing does that today) would leave the tracker silent for the rest of the
run.

### Choosing and resuming

- **Dialog.** "Also resume helpers" lists `GetOpenHelpers` for the selected row, else the Continue
  last candidate (name, where, started), all ticked; click or Space toggles. An untick is remembered
  per (session, helper) in `HelperTickState`, so a session the Owner never looked at resumes all its
  helpers. Helpers whose session is live now are not listed. The lookup is
  `LaunchSessionChoiceModel.HelpersFor`, cached per session and called on selection change, never
  while painting. No helpers: one dimmed "—" row.
- **Wiring.** After the PM's `StartTerminal` on a resume, `QueueHelperResume` posts
  `ResumeHelpersFor`, which runs only while the PM pane is still running the launch that resumed that
  session (a refused or replaced PM takes no helpers).
- **Per helper**, the PM resume's own rules (`HelperResume.Check`): rebuild the live set (it includes
  the PM and the helpers already reopened), `SessionCatalog.TryGetEntry` by id, the model's "may be
  open elsewhere" rule (SKIPPED, not confirmed: there is no one to confirm; so a helper written in the
  last ~2 minutes is skipped), `RecheckResume`, `ValidateResumeCwd`, and the folder still existing.
  Then `BuildCommand(ClaudeCode, project, cwd, sessionId)` as for the PM, and `AddNewTerminal` with
  the original name (atomic: a held name comes back suffixed), the PM as spawner, the PM's profile
  inherited, unattended (a profile refusal is a skip), `resumeSessionId` (reserves
  `PendingResumeSessionId`) and `pinWorkingDirectory` (no `ResolveSpawnDir`: the repo-root move and the
  task-worktree cd would both cause "No conversation found").
- **Re-link.** `SpawnService.Panes.Record(helper, name, pm DocId, pm nonce)`, so `close_helper` works
  from the resumed PM, and the helper's launch row is linked to the PM's current launch.
- **One summary**, and only if something was skipped or renamed ("Notes came back as Notes-2"; the PM's
  conversation still uses the old name).

### Known gaps

- Only sessions launched after this shipped have rows; older helpers are never offered.
- A pane whose session id MT never learns (a project statusLine that bypasses MT's) has no
  `session_id`, so it is never offered as a helper.
- A resumed helper is a spawned pane with no job: its SessionStart briefing asks it to collect one,
  and the collect answers `no_job`.
- The resume happens on the UI thread, one helper after another (each a catalog lookup and a pane).
