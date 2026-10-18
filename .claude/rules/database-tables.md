---
paths:
  - Services/TaskDatabase.cs
  - Services/ProjectDatabase.cs
  - MCPServer/Models/*.cs
  - Services/KnowledgeDatabase.cs
---

# Database Tables Reference

## CodeGraphDatabase.cs Tables (same multiterminal.db file)

| Table | Key Columns | Purpose |
|-------|-------------|---------|
| `cg_symbols` | id, name, type, file_path, line_number, project_id, params, return_type, parent_name, member_of, scope, accessibility, is_static, is_async, is_abstract, generic_params, source_preview | C# code symbols (classes, methods, properties, etc.) |
| `cg_relationships` | id, from_id, to_id, type, file_path, line_number | Symbol relationships (calls, inherits, implements, overrides, references, uses_type, imports) |
| `cg_projects` | id, name, csproj_path, output_type, sln_path | Indexed C# projects |
| `cg_project_dependencies` | project_id, depends_on_id | Project-to-project dependency edges |
| `cg_index_metadata` | key, value | Indexing state (last_indexed, duration_ms, per-project stats) |

## TaskDatabase.cs Tables (multiterminal.db)

| Table | Key Columns | Purpose |
|-------|-------------|---------|
| `tasks` | id, title, status, assignee, checklist_json, plan, continuation_notes, auto_status, review_notes | Kanban tasks. `review_notes` holds the structured human-code-review JSON array (cleared on Pass; snapshot saved to `task_reports` first). |
| `task_helpers` | task_id, helper_name, added_by | Helper assignments |
| `task_relationships` | task_id, related_task_id, relationship_type | Task dependencies/links |
| `task_file_links` | task_id, file_path, checklist_item_index (nullable), added_by, description | Files associated with tasks. `checklist_item_index` is NULL for task-scoped links (default) or a 0-based item index for item-scoped links (used by per-item review-note routing). |
| `task_worktrees` | task_id (PK), worktree_path, branch_name, created_at, status | Per-task git worktree records (Phase 1 worktree isolation, gated by MULTITERMINAL_WORKTREE_MODE) |
| `task_reports` | id, task_id, invocation_id, agent_name, report_type, report_content, verdict, score, created_at, created_by | Persisted agent reports |
| `task_summaries` | task_id, summary_at, previous_status, new_status | Progress snapshots |
| `task_attachments` | id, task_id, checklist_item_index, file_name, content_type, image_data | Images/files on checklist items |
| `team_member_profiles` | display_name, avatar_url, specialties, availability | Team profiles |
| `owner_profile` | key, value | Owner identity (git config, GitHub token) |
| `activity_feed` | activity_type, event_data, timestamp | Activity events |
| `user_inbox` | user_id, task_id (nullable since 77d1182f — taskless rows such as `spawn_failed`; `MigrateUserInboxTaskIdNullable` rebuilds the pre-existing NOT NULL table), type | Notifications; read by `get_inbox` / the Inbox panel |
| `notification_events` | id, source, event_type, title, body, timestamp | Push notification events |
| `helper_sessions` | task_id, prompt, status | Helper session tracking |
| `helper_messages` | task_id, helper_name, role, content | Helper conversation history |
| `terminal_activity` | terminal, status, activity | Terminal state |
| `agent_invocations` | id, agent_name, task_id, started_at, duration_ms | Agent performance tracking |
| `chat_messages` | id, from_terminal, to_terminal, message, timestamp | Persistent chat history |
| `session_lineage` | session_id, agent_name, session_type, summary, session_file_path | Session history & lineage |
| `session_messages` | session_id, role, content, tool_name, timestamp | Extracted messages from JSONL |
| `session_agent_map` | session_id, agent_name, is_active | Hook-written: which agent owns each session |
| `knowledge_entries` | id, topic, content, source, created_at | Institutional knowledge base |
| `code_digests` | id, file_path, digest, created_at | Code digest summaries |
| `project_note_tabs` | project_path (canonical key), tab_name, content, tab_order, is_default, created_by, updated_by | HUD Notes tabs. `created_by` NULL = the Owner wrote it (all pre-5d94ac91 rows); an agent name = written via `api/project-notes`. `updated_by` NULL = the Owner saved it last (the pane clears it). Agents never modify an Owner tab, and may `replace` their own only while `updated_by` is still theirs (task 5d94ac91). `agent_rev` (bumped by every agent write) / `owner_read_rev` (highest rev the Owner has seen): unread = `updated_by IS NOT NULL AND agent_rev > owner_read_rev` (`TaskDatabase.NoteTabUnreadSql`), driving the HUD Notes badge (task c133b27d). A counter, not `updated_at`, because a same-second append must stay unread; pre-existing rows migrate as 0/0, i.e. read. `pinned` (task 9a4a9d5e): the Owner pinned it to the top of the Notes rail; only pinned rows have a manual order (`tab_order`, `ReorderNoteTabs` moves pinned rows only), every other note is sorted unread-then-newest in the page. Only the Owner pins; the upgrade pinned every `created_by IS NULL` row in its existing order. `category_id`: NULL = General, else `note_categories.id`. |
| `note_categories` | id, name (UNIQUE COLLATE NOCASE), created_at | The Notes rail's categories (task 9a4a9d5e): ONE list shared by every project (Owner decision). General is built in and never stored. Owner-only writes (`AddNoteCategory` returns the existing row on a case-insensitive match, `RenameNoteCategory`, `DeleteNoteCategory` moves its notes to General in the same `SqliteWriteGate` transaction). Agents may file a NEW note under an existing category via `api/project-notes`/`add_project_note`; an unknown name lands in General and an existing note keeps its category. |
| `complexity_decisions` | id, task_id, complexity, reasoning | Task complexity assessments |

## ProjectDatabase.cs Tables (same multiterminal.db file)

| Table | Key Columns | Purpose |
|-------|-------------|---------|
| `projects` | id, name, description, path, source_path, deploy_path, build_command, git_repo_url, git_default_branch, git_auto_commit, is_pinned, default_terminal, quiet_start, claude_profile, launch_mode | Project core record. `quiet_start` (task e0fa9d90, GitHub #34) is SQLite-only and nullable: NULL reads as off, the SaveRichProject UPSERT COALESCEs it, and explicit writes go through `SetQuietStart` / `UpdateProjectField`. `claude_profile` (ticket c8ee0eec, GitHub #21) is the Claude Code config profile NAME, NULL = Default. project.json carries it too (`claudeProfile`, omitted for Default), so SaveRichProject writes it on INSERT only and never on update: a stale file must not move a project to another profile. An existing row changes only through `SetClaudeProfile` / `UpdateProjectField`. The row is the AUTHORITY at launch: every launch site asks `MainForm.DecideLaunchClaudeProfile` (rules in `Services/ClaudeProfileLaunchPolicy`), which reads the row; the nearest REGISTERED project at or above the launch folder owns the launch, and a project.json counts only in the launch folder itself when no row is registered there (never in a parent folder), and if that file's project id has a row registered at another folder under a different profile the launch is refused. A name this machine has no profile for, or a profile whose folder is missing, REFUSES the launch (Owner decision 2026-10-10); nothing falls back to Default. `launch_mode` (task ec2ed636) is SQLite-only TEXT `ask`/`new`/`continue`: NULL or unknown reads as `ProjectLaunchMode.Ask`, the SaveRichProject UPSERT does not name it, and the only writer is `SetLaunchMode` (Ask is written as `'ask'`, not NULL). |
| `project_agents` | project_id, agent_name, role, preferred_model | Agents assigned to project |
| `project_mcp_servers` | project_id, server_name, is_enabled | MCP servers per project |
| `project_specialist_agents` | project_id, agent_type, is_enabled, custom_prompt | Specialist agents |
| `project_paths` | project_id, path_type, path_value, description | Named filesystem paths |
| `project_prompts` | project_id, prompt_type, prompt_text, display_order | Stored prompts |
| `project_skills` | project_id, skill_name, is_enabled | Skills enabled per project |

## Data Storage Patterns

- Checklists: JSON array in `checklist_json` -> `[{"item":"...","status":"pending|coding|testing|done","notes":[...],"dependsOn":[0,1],"gloss":{"what":"...","why":"...","without":"..."}}]`
  - `dependsOn` / `gloss` were added by task 60665c6c for the 🔗 Plan graph tab. Because the checklist is JSON rather than relational columns, **no schema migration was needed** — absent fields deserialize to null and `ChecklistItem.NormalizeFromLegacy()` defaults `dependsOn` to an empty list. `gloss` is deliberately left null when unwritten (distinct from a blank one).
  - **`dependsOn` is sibling item indices, and an empty list means NO declared dependency — never "the previous item".** Stored array order is presentation order, not a constraint. See `.claude/rules/checklist-graph.md`.
- Plans: Markdown in `plan` field
- Continuation notes: Free text in `continuation_notes` (session handoff context)
