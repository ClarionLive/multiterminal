# Git Visibility Tab — Smoke Test Plan

> Walk this checklist end-to-end. Each step has an **EXPECT** line stating what
> should be true. Mark ✅ pass or ❌ fail with a one-line note. Tests cover the
> new HUD Git tab plus the two refactored primitives (dashboard widget,
> TerminalDocument session-diff popup) — both of which were exercised by gate
> agents but not live-verified.
>
> Task: `fb718102` — Git Visibility Tab.

---

## Pre-flight

- [ ] MT built clean from current branch (`dotnet build` → 0 warnings, 0 errors).
- [ ] MT launched and at least one MT-tracked project pinned.
- [ ] **Project A — no remote (the MT repo itself):** `git remote -v` returns empty.
- [ ] **Project B — with remote (any Clarion project that pushes to GitHub):** `git remote -v` returns at least one entry.
- [ ] **Project C — no .git directory:** any random folder that hasn't been `git init`'d. Easiest: `mkdir %TEMP%\mt-nogit-fixture`.
- [ ] (Optional) **Project D — `.git` is a file (worktree gitlink):** `git worktree add` against any repo. Skip if not practical.

---

## Section 1 — Dashboard widget regression

The dashboard widget previously used inline `RunGitCommand` shell-outs. Item [4]
refactored it to read through `GitRepoService` (shared with the new Git tab).
Goal: confirm no visual or data regression.

- [ ] Open Project A, switch to the **📊 Dashboard** tab in the HUD.
- [ ] **EXPECT:** Branch field shows the current branch (`master` for MT).
- [ ] **EXPECT:** "X changes" badge displays the uncommitted-file count and matches
      `git status --porcelain | wc -l` run from the terminal.
- [ ] **EXPECT:** Last-commit row shows the latest commit's short hash + subject +
      relative time. Cross-check with `git log -1 --format="%h %s %ar"`.
- [ ] Make a small uncommitted edit to any file (touch / append a comment).
- [ ] **EXPECT:** "X changes" updates within ~1s (file-watcher refresh).
- [ ] Revert the edit. **EXPECT:** count returns to baseline.

---

## Section 2 — TerminalDocument session-diff popup regression

Item [6] extracted `GenerateDiffHtml` to `Controls/Shared/DiffRenderer.cs`.
TerminalDocument's session diff popup now delegates to
`DiffRenderer.RenderUnifiedDiff`. Goal: confirm visual output is identical.

- [ ] In Project A, with at least one uncommitted file edit present, trigger the
      session diff popup the way you normally would (right-click in the
      activity feed, or whatever path opens it).
- [ ] **EXPECT:** Diff popup opens. Background dark, monospaced font.
- [ ] **EXPECT:** Header band shows the file name + path.
- [ ] **EXPECT:** Added lines are green-highlighted, deleted lines are
      red-highlighted, hunk headers (`@@`) are blue, file headers (`+++`/`---`)
      are dim. Same color palette as before the refactor.
- [ ] **EXPECT:** No console errors in the WebView2 dev-tools (if accessible).

---

## Section 3 — Git tab header — no-remote variant (Project A: MT itself)

- [ ] Switch to the **🔀 Git** tab in the HUD strip (between Tasks and Notes).
- [ ] **EXPECT:** Header reads `master · N uncommitted changes · no remote configured`
      (where N matches `git status --porcelain` output count).
- [ ] **EXPECT:** No ⚠ icon, no warning colour. Wording is neutral.
- [ ] **EXPECT:** Refresh button (↻) visible on the right.
- [ ] **EXPECT:** No Fetch button (⤓) — there's no remote to fetch from.
- [ ] Click ↻ Refresh. **EXPECT:** values re-fetch (no visible regression; subtle).

---

## Section 4 — Git tab header — has-remote variant (Project B: Clarion repo)

- [ ] Switch to Project B in MT. Open the 🔀 Git tab.
- [ ] **EXPECT:** Header reads `<branch> · N uncommitted · ↑X ↓Y · fetched <relative time> ago`.
      X/Y are ahead/behind counts vs the upstream branch; cross-check with
      `git status -sb` (`[ahead X, behind Y]`).
- [ ] **EXPECT:** Fetch button (⤓ Fetch) is visible.
- [ ] **EXPECT:** Refresh button (↻) is visible.
- [ ] Click ⤓ Fetch. **EXPECT:** No-op for v1 (button is wired but stubbed —
      full network-fetch deferred to a follow-up). Document any unexpected
      error popup.

---

## Section 5 — Working Changes panel + click-to-diff round trip

- [ ] In Project A on the Git tab, ensure there is at least one uncommitted
      file. **EXPECT:** the "Working Changes (N)" section in the left column
      lists each uncommitted file as a row with: status marker (`M`/`A`/`D`/`U`/`R`),
      relative file path, +/- line stats.
- [ ] **EXPECT:** Status markers match: M for modified, A for added (staged), D
      for deleted, U for untracked, R for renamed.
- [ ] Click a modified file row. **EXPECT:** Diff pane (right column) populates
      within ~1s. Side-by-side default — left column shows deletions in red,
      right column shows additions in green, context lines span both columns.
- [ ] Click the **≡ Unified** toggle. **EXPECT:** Diff re-renders as single-column
      unified format with the same colours. Active button highlighted.
- [ ] Click **⫶⫶ Side-by-side** toggle. **EXPECT:** Returns to two-column.
- [ ] Click an untracked file (`U` marker). **EXPECT:** Diff pane shows
      `(no changes found)` or empty — untracked files don't have a diff against
      HEAD in v1.
- [ ] Click another file row. **EXPECT:** Selection highlights move; diff
      pane updates to the new file.

---

## Section 6 — Recent commits log + click-to-diff against parent

- [ ] On the Git tab, the "Recent Commits (N)" section is below Working Changes.
      **EXPECT:** Last 30 commits listed newest-first, each row showing short
      SHA + subject + author + (any co-authors) + relative time.
- [ ] Cross-check the top entry against `git log -1 --format="%h %s"`.
- [ ] Click a commit row. **EXPECT:** Diff pane shows the diff between that commit
      and its first parent. Selection moves from the file list (if any) to the
      commit row.
- [ ] Click another commit. **EXPECT:** Diff updates.
- [ ] Click an uncommitted file row again. **EXPECT:** Diff returns to the
      working-tree view; commit highlight clears.

---

## Section 7 — Branches panel

- [ ] On the Git tab, the "Branches (N)" section is below Recent Commits.
      **EXPECT:** Local + remote branches both listed.
- [ ] **EXPECT:** Current branch row at the top, marked with a filled circle (●),
      bold name, accent-colour highlight.
- [ ] **EXPECT:** Other branches use an open circle (○).
- [ ] **EXPECT:** Branches with no commit in 30+ days appear dimmed (~50% opacity).
      The current branch is never dimmed even if old.
- [ ] **EXPECT:** Remote-tracking branches show a small `remote` pill tag inline.
- [ ] **EXPECT:** Last-commit relative time on the right of each row.

---

## Section 8 — Phase 2 overlays (chips + cross-task contamination banner)

Requires at least one active kanban task with `link_task_file` calls covering
some uncommitted files. Easiest fixture: this very task `fb718102` has many
files linked.

- [ ] Switch back to a project where you have an active task with linked files
      (the MT repo + this task is the canonical fixture).
- [ ] **EXPECT:** Working-changes file rows now show chips on the right:
      `📋 <agent>` + `<task-id-short>` + pipeline-status glyph + verdict.
- [ ] **EXPECT:** Hovering the `📋` chip shows tooltip: *"Task assignee — may not
      be the file's actual editor. Per-line attribution from activity_feed coming
      in a follow-up item."*
- [ ] **EXPECT:** Hovering the task-id chip shows the full task id + title.
- [ ] **EXPECT:** Files with no active-task linkage have no chips (CSS collapses
      empty chips).
- [ ] **Cross-task contamination test:** if you have uncommitted files spanning
      ≥2 active tasks (Henry's `cf4575e7` + Alice's `fb718102` qualifies if
      both still in_progress at test time), the banner at the top of Working
      Changes should read: `N uncommitted files span M active tasks — commit-bundling risk.`
- [ ] **EXPECT:** Banner has red/⚠ iconography. This should be the **only**
      red/warning element in the panel.
- [ ] **EXPECT:** When all uncommitted files belong to a single active task (or
      no active task), banner is hidden.

---

## Section 9 — Empty states

### 9a. NotARepo — easy fixture

- [ ] Register Project C (a folder with no `.git` directory) in MT.
- [ ] Open the 🔀 Git tab on that project.
- [ ] **EXPECT:** Empty-state text reads: *"No git repository — This project has no
      git repository. Initialize one with `git init` to use the Git tab."*
- [ ] **EXPECT:** No header strip, no body sections rendered.
- [ ] **EXPECT:** No error iconography — neutral tone.

### 9b. LinkedGitDir — manual verification

If Project D (worktree gitlink) is set up:

- [ ] Open the 🔀 Git tab on the worktree project.
- [ ] **EXPECT:** Empty-state text reads: *"Linked git directory — This project's
      .git is a submodule or worktree gitlink. The Git tab can only inspect
      standard repositories. Open the parent repository to view git state."*

If Project D is not practical:

- [ ] Document this case as "manually verified by inspection of code path —
      `GitRepoManager.DetectLayout` returns `LinkedGitDir` when `.git` is a file."

---

## Section 10 — Dashboard dirty-click deep-link (item [12])

- [ ] In Project A on the **📊 Dashboard** tab, with the dirty-state badge
      visible (`X changes`), hover the badge.
- [ ] **EXPECT:** Cursor is `pointer`. Badge has a dotted underline. Tooltip
      reads: *"Click to open the Git tab and view the uncommitted changes."*
- [ ] Click the badge. **EXPECT:** HUD tab strip switches to **🔀 Git**, same
      project's working-changes panel visible.
- [ ] Stage all uncommitted edits + commit OR `git stash` to clear the working
      tree, then return to the Dashboard tab.
- [ ] **EXPECT:** With zero changes, the badge is empty/hidden, no underline,
      no cursor: pointer, click does nothing (inert).

---

## Sign-off

- [ ] All sections passed. Item [13] → done.

If any **EXPECT** failed, capture the specific step + the observed-vs-expected
delta in the testing notes for fb718102 item [13]. Failure transitions item
back to coding for me (Alice) to investigate.
