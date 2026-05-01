# Git Visibility Tab — Stress Test Plan

> Walk this checklist after running the fixture script. Tests confirm the HUD
> Git tab handles realistic-stress-scale data (100+ uncommitted files, 1000+
> commits in history, 100+ untracked files) without UI hangs or excessive
> memory growth.
>
> Task: `fb718102` — Git Visibility Tab. Item: `[14]`.
>
> Pair with: `docs/git-tab-smoke-test-plan.md` (functional), `scripts/stress-fixture-git-tab.ps1` (fixture generator).

---

## Step 1 — Generate the fixture

```powershell
cd H:\DevLaptop\ClarionPowerShell\MultiTerminal
.\scripts\stress-fixture-git-tab.ps1 -Force
```

Default settings: 1000 commits + 100 untracked files + 25 uncommitted
modifications, at `%TEMP%\mt-stress-fixture`. Adjust `-CommitCount` /
`-UntrackedFileCount` / `-Path` as needed.

- [ ] Script exits successfully. Final summary block prints non-zero counts:
      `Total commits: ≥1000`, `Working-tree: ≥125 entries`, `Branches: 1 local`.
- [ ] Note the printed `Path` — you'll register that directory as a project.

---

## Step 2 — Register the fixture as a project in MultiTerminal

- [ ] In MultiTerminal: New Project → point at the fixture path printed by the
      script (default `%TEMP%\mt-stress-fixture`).
- [ ] **EXPECT:** Project registers successfully. No errors during indexing.
- [ ] Open the project in MT.

---

## Step 3 — Initial Git tab load

- [ ] Switch to the **🔀 Git** tab.
- [ ] **EXPECT:** Tab populates in under 5 seconds. (LibGit2Sharp pulls the
      first GetWorkingTreeStatus + GetRecentCommits(30) + GetBranches inside
      one Task.Run; on a fresh tree of this size the budget is generous.)
- [ ] **EXPECT:** Header reads `master · 125 uncommitted changes · no remote configured`
      (count varies by the fixture parameters). No warning iconography.
- [ ] **EXPECT:** Working Changes section lists ≥125 file rows (25 modified +
      100 untracked). Scroll smoothly — the file tree should not freeze.
- [ ] **EXPECT:** Recent Commits section lists 30 rows (the cap). The most-recent
      entries match `git log -30 --format="%h %s"` from the terminal in the
      fixture directory.
- [ ] **EXPECT:** Branches section shows the single `master` branch as current.

---

## Step 4 — UI responsiveness under load

Goal: confirm no hangs when interacting with the tab while loaded with
stress-scale data.

- [ ] Click the first uncommitted file row.
- [ ] **EXPECT:** Diff pane populates in ≤2 seconds.
- [ ] Click rapidly through 10 different file rows in succession (faster than
      the diff queries can complete one-by-one).
- [ ] **EXPECT:** UI does not freeze. The final-clicked file's diff renders;
      stale-click guard drops earlier in-flight diffs (this is the guard from
      item [11] cleanup).
- [ ] Click rapidly through 10 different commit rows.
- [ ] **EXPECT:** Same — UI stays responsive, last-clicked wins, no stale
      content briefly flashing.
- [ ] Toggle Side-by-side ↔ Unified 5 times in a row.
- [ ] **EXPECT:** Each toggle re-renders ≤500 ms. No lag.

---

## Step 5 — Refresh under load

- [ ] In a separate terminal in the fixture directory, modify one of the
      tracked `src-N.txt` files (`echo extra-edit >> src-1.txt`).
- [ ] **EXPECT:** Within ~1.5 seconds (FileSystemWatcher 1 s debounce + render
      time), the working-changes count updates and the modified file's row
      reflects the new edit.
- [ ] In the same terminal, run `git commit -am "stress test commit"`.
- [ ] **EXPECT:** Within ~1.5 seconds, the commits log gains a new entry at the
      top, the uncommitted-changes count drops, and the dashboard widget (if
      visible) updates too.

---

## Step 6 — Memory stability

Goal: catch memory leaks introduced by the panel's refresh loop.

- [ ] In Task Manager / Resource Monitor, capture MultiTerminal's working set
      memory **before** opening the Git tab.
- [ ] Open the tab. Click 30+ different file rows + 30+ different commit rows
      over ~2 minutes.
- [ ] **EXPECT:** Working-set memory stabilises within ±50 MB of the baseline
      after activity ceases. No monotonic growth.
- [ ] (Optional) Leave the tab open for 10 minutes with no interaction.
- [ ] **EXPECT:** No background growth. File-watcher refresh on `.git/` paths
      should be infrequent on an idle fixture.

---

## Step 7 — Stress the empty-state path

- [ ] Close MT. Delete the fixture's `.git/` directory:
      `Remove-Item -Recurse -Force <fixture-path>\.git`
- [ ] Re-launch MT and re-open the project.
- [ ] **EXPECT:** Git tab renders the *NotARepo* empty-state ("No git repository
      — Initialize one with `git init` to use the Git tab"). No exception, no
      UI freeze, no crash.

---

## Step 8 — Cleanup

- [ ] Close MT.
- [ ] Optionally: `Remove-Item -Recurse -Force %TEMP%\mt-stress-fixture` to
      reclaim disk.
- [ ] Optionally: Unregister the stress-fixture project from MT's project
      registry.

---

## Sign-off

- [ ] All sections passed. Item [14] → done.

If any EXPECT failed, capture the specific step + observed-vs-expected delta
in the testing notes for fb718102 item [14]. Failures transition item back to
coding for me (Alice) to investigate. Particularly load-bearing:
- Step 3 — initial load >5s suggests LibGit2Sharp lock-held duration is too
  generous; fix is to chunk the work or move sub-queries out of the lock.
- Step 6 — monotonic memory growth suggests a leak in the refresh loop; first
  suspect is `attrByPath` retention or a missing `using` on a Patch.
- Step 4 — UI hang on rapid clicks suggests the stale-click guard isn't
  dropping in-flight tasks; verify the `selectionKey` echo is wired correctly.
