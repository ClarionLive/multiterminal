# Git Worktrees for Multi-Agent Programming

**Research Date:** 2026-02-03
**Researcher:** Diana
**Status:** Initial Research Complete

---

## Executive Summary

Git worktrees provide a mechanism for checking out multiple branches of a repository into separate directories simultaneously. This approach has gained significant traction in the AI-assisted development community as a way to run multiple AI coding agents in parallel without filesystem conflicts.

This document evaluates whether git worktrees could benefit our MultiTerminal setup and proposes a hybrid integration model.

---

## What Are Git Worktrees?

Git worktrees allow you to have multiple working directories for the same repository, each with its own branch checked out. Unlike cloning a repository multiple times (which duplicates the entire `.git` directory), worktrees share a single repository database.

### Basic Commands

```bash
# Create a new worktree with a new branch
git worktree add ../feature-auth -b feature-auth

# Create a worktree for an existing branch
git worktree add ../hotfix hotfix-branch

# List all worktrees
git worktree list

# Remove a worktree
git worktree remove ../feature-auth

# Prune stale worktree references
git worktree prune
```

### Key Constraint

Git enforces that each branch can only be checked out in one worktree at a time. This prevents conflicts but requires coordination when multiple agents might want the same branch.

---

## Benefits for Multi-Agent Development

### 1. Filesystem Isolation

Each agent operates in its own directory with its own file state. This eliminates the core problem of multiple agents trying to edit the same files simultaneously.

> "Parallel agents only feel parallel when the filesystem is parallel."
> — Pochi Engineering Team

### 2. True Parallelization

Teams report running 4-10+ AI agents simultaneously, each working on different features:
- One agent refactoring authentication
- Another building UI components
- A third writing tests
- A fourth updating documentation

### 3. Shared Repository Benefits

| Feature | Worktrees | Multiple Clones |
|---------|-----------|-----------------|
| Disk space | Single `.git` | Duplicated per clone |
| Fetch once | All worktrees updated | Must fetch each clone |
| Branch visibility | Full access | Full access |
| Cherry-picking | Immediate | Requires remote |

### 4. Clean Merge Workflow

Each worktree produces a focused branch with related commits. Merging back to main is cleaner than untangling interleaved changes from a shared directory.

---

## Challenges and Considerations

### Learning Curve

Git worktrees add complexity. Team members need to understand:
- Worktree lifecycle management
- Branch-worktree relationship
- When to prune vs. remove

### Directory Confusion

With multiple active worktrees, it's easy to make changes in the wrong directory. Clear naming conventions help:
```
~/projects/
  myapp/              # Main worktree (main branch)
  myapp.feature-auth/ # Feature worktree
  myapp.feature-ui/   # Feature worktree
```

### Environment Setup

Each worktree needs its own environment setup:
```bash
cd ../myapp.feature-auth
npm install  # Required for each worktree
```

### Staleness Risk

Worktrees not regularly updated from main can drift, leading to larger merge conflicts later. Regular rebasing is recommended.

---

## Comparison: Current MultiTerminal vs. Worktree Approach

### Current MultiTerminal Setup

**Architecture:** Multiple Claude instances share the same working directory, coordinating via MCP messaging.

| Aspect | Evaluation |
|--------|------------|
| Real-time coordination | Excellent - shared context, immediate visibility |
| File conflicts | Risk - two agents editing same file |
| Task handoff | Easy - no branch switching needed |
| Merge complexity | Low - all changes in one place |

**Best for:** Tightly coupled tasks, code review, debugging, coordination-heavy work.

### Worktree Approach

**Architecture:** Each Claude instance operates in an isolated directory with its own branch.

| Aspect | Evaluation |
|--------|------------|
| Real-time coordination | Requires explicit messaging |
| File conflicts | None - complete isolation |
| Task handoff | Requires merge or cherry-pick |
| Merge complexity | Higher - must integrate branches |

**Best for:** Independent features, longer-running tasks, parallel sprints.

---

## Proposed Hybrid Model

Combine the strengths of both approaches:

### Phase 1: Planning & Coordination (Shared)

Use MultiTerminal chat for:
- Task breakdown and assignment
- Architecture decisions
- Code review discussions
- Bug triage

### Phase 2: Implementation (Isolated)

Spin up worktrees for:
- Independent feature development
- Long-running refactoring
- Tasks touching different parts of codebase

### Phase 3: Integration (Shared)

Return to shared context for:
- Merge conflict resolution
- Integration testing
- Final review

### Implementation Sketch

```
/worktree create feature-auth
  → Creates worktree at ../MultiTerminal.feature-auth
  → Assigns current agent to that worktree
  → Updates MultiTerminal registry with worktree path

/worktree status
  → Lists all active worktrees and assigned agents

/worktree merge feature-auth
  → Merges branch back to main
  → Cleans up worktree
  → Notifies team via MultiTerminal chat
```

---

## Tooling: Worktrunk

[Worktrunk](https://github.com/max-sixty/worktrunk) is a new CLI (released February 2026) designed specifically for AI agent worktree workflows.

### Key Features

- **Branch-based addressing:** `wt switch feature-auth` instead of paths
- **Automatic directory creation:** Configurable path templates
- **Status indicators:** Shows uncommitted changes, unpushed commits
- **One-command merge:** Squash, rebase, merge, and cleanup
- **Hooks:** Trigger actions on create, pre-merge, post-merge
- **LLM integration:** Auto-generate commit messages from diffs
- **Claude Code support:** Launch agents with custom instructions

### Installation

```bash
# macOS
brew install max-sixty/tap/worktrunk

# Cargo
cargo install worktrunk
```

---

## Recommendations

### Short Term

1. **Monitor current pain points:** Track instances where agents conflict on files
2. **Experiment manually:** Try worktrees for the next independent feature set
3. **Evaluate Worktrunk:** Test if it simplifies our workflow

### Medium Term

4. **Build integration:** Add `/worktree` commands to MultiTerminal
5. **Define guidelines:** When to use shared vs. isolated mode
6. **Automate setup:** Script environment initialization for new worktrees

### Long Term

7. **Smart assignment:** MultiTerminal could auto-detect when tasks are independent and suggest worktree isolation
8. **Conflict prevention:** Analyze task dependencies before assignment

---

## Conclusion

Git worktrees offer a compelling solution for running multiple AI agents in parallel without filesystem conflicts. For our MultiTerminal setup, a **hybrid model** combining shared coordination with isolated implementation appears most promising.

The key insight is that worktrees and shared-directory approaches solve different problems:
- **Worktrees:** Maximize parallelism for independent work
- **Shared directory:** Maximize coordination for interdependent work

Implementing worktree support as an *option* within MultiTerminal would give us flexibility to choose the right approach for each task.

---

## References

1. [Git Worktrees: The Secret Weapon for AI Agents](https://medium.com/@mabd.dev/git-worktrees-the-secret-weapon-for-running-multiple-ai-coding-agents-in-parallel-e9046451eb96)
2. [How Git Worktrees Changed My AI Agent Workflow | Nx Blog](https://nx.dev/blog/git-worktrees-ai-agents)
3. [Running Multiple AI Agents Using Git Worktrees | Medium](https://medium.com/design-bootcamp/running-multiple-ai-agents-at-once-using-git-worktrees-57759e001d7a)
4. [How We Built True Parallel Agents With Git Worktrees | DEV](https://dev.to/getpochi/how-we-built-true-parallel-agents-with-git-worktrees-2580)
5. [Shipping Faster with Claude Code and Git Worktrees | incident.io](https://incident.io/blog/shipping-faster-with-claude-code-and-git-worktrees)
6. [Worktrunk - Git Worktree CLI for AI Agents](https://github.com/max-sixty/worktrunk)
7. [Git Worktree Documentation](https://git-scm.com/docs/git-worktree)
8. [Using Git Worktrees for Concurrent Development | Ken Muse](https://www.kenmuse.com/blog/using-git-worktrees-for-concurrent-development/)
9. [Mastering Git Worktrees with Claude Code | Medium](https://medium.com/@dtunai/mastering-git-worktrees-with-claude-code-for-parallel-development-workflow-41dc91e645fe)
