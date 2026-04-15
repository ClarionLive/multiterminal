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
| `tasks` | id, title, status, assignee, checklist_json, plan, continuation_notes | Kanban tasks |
| `task_helpers` | task_id, helper_name, added_by | Helper assignments |
| `task_relationships` | task_id, related_task_id, relationship_type | Task dependencies/links |
| `task_file_links` | task_id, file_path, added_by, description | Files associated with tasks |
| `task_reports` | id, task_id, invocation_id, agent_name, report_type, report_content, verdict, score, created_at, created_by | Persisted agent reports |
| `task_summaries` | task_id, summary_at, previous_status, new_status | Progress snapshots |
| `task_attachments` | id, task_id, checklist_item_index, file_name, content_type, image_data | Images/files on checklist items |
| `team_member_profiles` | display_name, avatar_url, specialties, availability | Team profiles |
| `owner_profile` | key, value | Owner identity (git config, GitHub token) |
| `activity_feed` | activity_type, event_data, timestamp | Activity events |
| `user_inbox` | user_id, task_id, message_type | Notifications |
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
| `complexity_decisions` | id, task_id, complexity, reasoning | Task complexity assessments |

## ProjectDatabase.cs Tables (same multiterminal.db file)

| Table | Key Columns | Purpose |
|-------|-------------|---------|
| `projects` | id, name, description, path, source_path, deploy_path, build_command, git_repo_url, git_default_branch, git_auto_commit, is_pinned | Project core record |
| `project_agents` | project_id, agent_name, role, preferred_model | Agents assigned to project |
| `project_mcp_servers` | project_id, server_name, is_enabled | MCP servers per project |
| `project_specialist_agents` | project_id, agent_type, is_enabled, custom_prompt | Specialist agents |
| `project_paths` | project_id, path_type, path_value, description | Named filesystem paths |
| `project_prompts` | project_id, prompt_type, prompt_text, display_order | Stored prompts |
| `project_skills` | project_id, skill_name, is_enabled | Skills enabled per project |

## Data Storage Patterns

- Checklists: JSON array in `checklist_json` -> `[{"item":"...","status":"pending|coding|testing|done","notes":[...]}]`
- Plans: Markdown in `plan` field
- Continuation notes: Free text in `continuation_notes` (session handoff context)
