using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using MultiTerminal.MCPServer.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// SQLite database service for persisting Plans and related entities.
    /// Uses the same database as TaskDatabase at %APPDATA%\multiterminal\multiterminal.db
    /// </summary>
    public class PlanDatabase : IDisposable
    {
        private readonly string _databasePath;
        private SQLiteConnection _connection;
        private bool _isDisposed;

        /// <summary>
        /// Creates a new PlanDatabase instance.
        /// </summary>
        public PlanDatabase()
        {
            _databasePath = TaskDatabase.GetDatabasePath();
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            string folder = Path.GetDirectoryName(_databasePath);
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            var connectionString = new SQLiteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Version = 3,
                JournalMode = SQLiteJournalModeEnum.Wal,
                Pooling = true
            }.ToString();

            _connection = new SQLiteConnection(connectionString);
            _connection.Open();

            CreateSchema();
        }

        private void CreateSchema()
        {
            const string schema = @"
                -- Plans table: the central organizing concept
                CREATE TABLE IF NOT EXISTS plans (
                    id TEXT PRIMARY KEY,
                    title TEXT NOT NULL,
                    description TEXT,
                    content TEXT,
                    current_phase TEXT NOT NULL DEFAULT 'design',
                    status TEXT NOT NULL DEFAULT 'draft',
                    leader_id TEXT,
                    created_at DATETIME NOT NULL,
                    updated_at DATETIME NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_plans_status ON plans(status);
                CREATE INDEX IF NOT EXISTS idx_plans_leader ON plans(leader_id);

                -- Plan phases with checklists
                CREATE TABLE IF NOT EXISTS plan_phases (
                    id TEXT PRIMARY KEY,
                    plan_id TEXT NOT NULL,
                    phase_name TEXT NOT NULL,
                    phase_order INTEGER NOT NULL,
                    checklist_json TEXT DEFAULT '[]',
                    started_at DATETIME,
                    completed_at DATETIME,
                    FOREIGN KEY (plan_id) REFERENCES plans(id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS idx_plan_phases_plan ON plan_phases(plan_id);

                -- Plan assignments: who is working on what
                CREATE TABLE IF NOT EXISTS plan_assignments (
                    id TEXT PRIMARY KEY,
                    plan_id TEXT NOT NULL,
                    terminal_name TEXT NOT NULL,
                    role TEXT NOT NULL DEFAULT 'member',
                    assigned_task_summary TEXT,
                    status TEXT NOT NULL DEFAULT 'assigned',
                    blocked_by TEXT,
                    created_at DATETIME NOT NULL,
                    FOREIGN KEY (plan_id) REFERENCES plans(id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS idx_plan_assignments_plan ON plan_assignments(plan_id);
                CREATE INDEX IF NOT EXISTS idx_plan_assignments_terminal ON plan_assignments(terminal_name);

                -- Plan decisions: recorded decisions for context
                CREATE TABLE IF NOT EXISTS plan_decisions (
                    id TEXT PRIMARY KEY,
                    plan_id TEXT NOT NULL,
                    phase TEXT NOT NULL,
                    decision_text TEXT NOT NULL,
                    rationale TEXT,
                    decided_by TEXT,
                    created_at DATETIME NOT NULL,
                    FOREIGN KEY (plan_id) REFERENCES plans(id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS idx_plan_decisions_plan ON plan_decisions(plan_id);
            ";

            using var command = new SQLiteCommand(schema, _connection);
            command.ExecuteNonQuery();
        }

        #region Plan Operations

        /// <summary>
        /// Get the currently active plan (status = 'active').
        /// Returns null if no plan is active.
        /// </summary>
        public Plan GetActivePlan()
        {
            const string sql = @"
                SELECT id, title, description, content, current_phase, status, leader_id, created_at, updated_at
                FROM plans
                WHERE status = 'active'
                LIMIT 1
            ";

            using var command = new SQLiteCommand(sql, _connection);
            using var reader = command.ExecuteReader();

            if (reader.Read())
            {
                return ReadPlan(reader);
            }

            return null;
        }

        /// <summary>
        /// Get a plan by ID.
        /// </summary>
        public Plan GetPlan(string planId)
        {
            const string sql = @"
                SELECT id, title, description, content, current_phase, status, leader_id, created_at, updated_at
                FROM plans
                WHERE id = @id
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", planId);
            using var reader = command.ExecuteReader();

            if (reader.Read())
            {
                return ReadPlan(reader);
            }

            return null;
        }

        /// <summary>
        /// Get all plans.
        /// </summary>
        public List<Plan> GetAllPlans()
        {
            var plans = new List<Plan>();

            const string sql = @"
                SELECT id, title, description, content, current_phase, status, leader_id, created_at, updated_at
                FROM plans
                ORDER BY created_at DESC
            ";

            using var command = new SQLiteCommand(sql, _connection);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                plans.Add(ReadPlan(reader));
            }

            return plans;
        }

        /// <summary>
        /// Save a plan (insert or update).
        /// </summary>
        public void SavePlan(Plan plan)
        {
            // If setting to active, ensure no other plan is active
            if (plan.Status == "active")
            {
                DeactivateAllPlans();
            }

            const string sql = @"
                INSERT INTO plans (id, title, description, content, current_phase, status, leader_id, created_at, updated_at)
                VALUES (@id, @title, @description, @content, @currentPhase, @status, @leaderId, @createdAt, @updatedAt)
                ON CONFLICT(id) DO UPDATE SET
                    title = @title,
                    description = @description,
                    content = @content,
                    current_phase = @currentPhase,
                    status = @status,
                    leader_id = @leaderId,
                    updated_at = @updatedAt
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", plan.Id);
            command.Parameters.AddWithValue("@title", plan.Title);
            command.Parameters.AddWithValue("@description", (object)plan.Description ?? DBNull.Value);
            command.Parameters.AddWithValue("@content", (object)plan.Content ?? DBNull.Value);
            command.Parameters.AddWithValue("@currentPhase", plan.CurrentPhase);
            command.Parameters.AddWithValue("@status", plan.Status);
            command.Parameters.AddWithValue("@leaderId", (object)plan.LeaderId ?? DBNull.Value);
            command.Parameters.AddWithValue("@createdAt", plan.CreatedAt);
            command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow);

            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Set a plan as active (deactivates all others).
        /// </summary>
        public void SetActivePlan(string planId)
        {
            DeactivateAllPlans();

            const string sql = "UPDATE plans SET status = 'active', updated_at = @now WHERE id = @id";
            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", planId);
            command.Parameters.AddWithValue("@now", DateTime.UtcNow);
            command.ExecuteNonQuery();
        }

        private void DeactivateAllPlans()
        {
            const string sql = "UPDATE plans SET status = 'paused', updated_at = @now WHERE status = 'active'";
            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@now", DateTime.UtcNow);
            command.ExecuteNonQuery();
        }

        private Plan ReadPlan(SQLiteDataReader reader)
        {
            return new Plan
            {
                Id = reader.GetString(0),
                Title = reader.GetString(1),
                Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                Content = reader.IsDBNull(3) ? null : reader.GetString(3),
                CurrentPhase = reader.GetString(4),
                Status = reader.GetString(5),
                LeaderId = reader.IsDBNull(6) ? null : reader.GetString(6),
                CreatedAt = reader.GetDateTime(7),
                UpdatedAt = reader.GetDateTime(8)
            };
        }

        #endregion

        #region Phase Operations

        /// <summary>
        /// Get all phases for a plan.
        /// </summary>
        public List<PlanPhase> GetPlanPhases(string planId)
        {
            var phases = new List<PlanPhase>();

            const string sql = @"
                SELECT id, plan_id, phase_name, phase_order, checklist_json, started_at, completed_at
                FROM plan_phases
                WHERE plan_id = @planId
                ORDER BY phase_order
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@planId", planId);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                phases.Add(new PlanPhase
                {
                    Id = reader.GetString(0),
                    PlanId = reader.GetString(1),
                    PhaseName = reader.GetString(2),
                    PhaseOrder = reader.GetInt32(3),
                    ChecklistJson = reader.IsDBNull(4) ? "[]" : reader.GetString(4),
                    StartedAt = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                    CompletedAt = reader.IsDBNull(6) ? null : reader.GetDateTime(6)
                });
            }

            return phases;
        }

        /// <summary>
        /// Save a phase (insert or update).
        /// </summary>
        public void SavePhase(PlanPhase phase)
        {
            const string sql = @"
                INSERT INTO plan_phases (id, plan_id, phase_name, phase_order, checklist_json, started_at, completed_at)
                VALUES (@id, @planId, @phaseName, @phaseOrder, @checklistJson, @startedAt, @completedAt)
                ON CONFLICT(id) DO UPDATE SET
                    phase_name = @phaseName,
                    phase_order = @phaseOrder,
                    checklist_json = @checklistJson,
                    started_at = @startedAt,
                    completed_at = @completedAt
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", phase.Id);
            command.Parameters.AddWithValue("@planId", phase.PlanId);
            command.Parameters.AddWithValue("@phaseName", phase.PhaseName);
            command.Parameters.AddWithValue("@phaseOrder", phase.PhaseOrder);
            command.Parameters.AddWithValue("@checklistJson", phase.ChecklistJson ?? "[]");
            command.Parameters.AddWithValue("@startedAt", (object)phase.StartedAt ?? DBNull.Value);
            command.Parameters.AddWithValue("@completedAt", (object)phase.CompletedAt ?? DBNull.Value);

            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Create default phases for a new plan.
        /// </summary>
        public void CreateDefaultPhases(string planId)
        {
            var phases = new[]
            {
                ("design", 1),
                ("coding", 2),
                ("testing", 3),
                ("completed", 4)
            };

            foreach (var (name, order) in phases)
            {
                var phase = new PlanPhase
                {
                    PlanId = planId,
                    PhaseName = name,
                    PhaseOrder = order,
                    StartedAt = order == 1 ? DateTime.UtcNow : null
                };
                SavePhase(phase);
            }
        }

        #endregion

        #region Assignment Operations

        /// <summary>
        /// Get all assignments for a plan.
        /// </summary>
        public List<PlanAssignment> GetPlanAssignments(string planId)
        {
            var assignments = new List<PlanAssignment>();

            const string sql = @"
                SELECT id, plan_id, terminal_name, role, assigned_task_summary, status, blocked_by, created_at
                FROM plan_assignments
                WHERE plan_id = @planId
                ORDER BY created_at
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@planId", planId);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                assignments.Add(ReadAssignment(reader));
            }

            return assignments;
        }

        /// <summary>
        /// Get assignment for a specific terminal in a plan.
        /// </summary>
        public PlanAssignment GetAssignmentForTerminal(string planId, string terminalName)
        {
            const string sql = @"
                SELECT id, plan_id, terminal_name, role, assigned_task_summary, status, blocked_by, created_at
                FROM plan_assignments
                WHERE plan_id = @planId AND terminal_name = @terminalName
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@planId", planId);
            command.Parameters.AddWithValue("@terminalName", terminalName);
            using var reader = command.ExecuteReader();

            if (reader.Read())
            {
                return ReadAssignment(reader);
            }

            return null;
        }

        /// <summary>
        /// Save an assignment (insert or update).
        /// </summary>
        public void SaveAssignment(PlanAssignment assignment)
        {
            const string sql = @"
                INSERT INTO plan_assignments (id, plan_id, terminal_name, role, assigned_task_summary, status, blocked_by, created_at)
                VALUES (@id, @planId, @terminalName, @role, @assignedTaskSummary, @status, @blockedBy, @createdAt)
                ON CONFLICT(id) DO UPDATE SET
                    terminal_name = @terminalName,
                    role = @role,
                    assigned_task_summary = @assignedTaskSummary,
                    status = @status,
                    blocked_by = @blockedBy
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", assignment.Id);
            command.Parameters.AddWithValue("@planId", assignment.PlanId);
            command.Parameters.AddWithValue("@terminalName", assignment.TerminalName);
            command.Parameters.AddWithValue("@role", assignment.Role);
            command.Parameters.AddWithValue("@assignedTaskSummary", (object)assignment.AssignedTaskSummary ?? DBNull.Value);
            command.Parameters.AddWithValue("@status", assignment.Status);
            command.Parameters.AddWithValue("@blockedBy", (object)assignment.BlockedBy ?? DBNull.Value);
            command.Parameters.AddWithValue("@createdAt", assignment.CreatedAt);

            command.ExecuteNonQuery();
        }

        private PlanAssignment ReadAssignment(SQLiteDataReader reader)
        {
            return new PlanAssignment
            {
                Id = reader.GetString(0),
                PlanId = reader.GetString(1),
                TerminalName = reader.GetString(2),
                Role = reader.GetString(3),
                AssignedTaskSummary = reader.IsDBNull(4) ? null : reader.GetString(4),
                Status = reader.GetString(5),
                BlockedBy = reader.IsDBNull(6) ? null : reader.GetString(6),
                CreatedAt = reader.GetDateTime(7)
            };
        }

        #endregion

        #region Decision Operations

        /// <summary>
        /// Get all decisions for a plan.
        /// </summary>
        public List<PlanDecision> GetPlanDecisions(string planId)
        {
            var decisions = new List<PlanDecision>();

            const string sql = @"
                SELECT id, plan_id, phase, decision_text, rationale, decided_by, created_at
                FROM plan_decisions
                WHERE plan_id = @planId
                ORDER BY created_at
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@planId", planId);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                decisions.Add(new PlanDecision
                {
                    Id = reader.GetString(0),
                    PlanId = reader.GetString(1),
                    Phase = reader.GetString(2),
                    DecisionText = reader.GetString(3),
                    Rationale = reader.IsDBNull(4) ? null : reader.GetString(4),
                    DecidedBy = reader.IsDBNull(5) ? null : reader.GetString(5),
                    CreatedAt = reader.GetDateTime(6)
                });
            }

            return decisions;
        }

        /// <summary>
        /// Save a decision.
        /// </summary>
        public void SaveDecision(PlanDecision decision)
        {
            const string sql = @"
                INSERT INTO plan_decisions (id, plan_id, phase, decision_text, rationale, decided_by, created_at)
                VALUES (@id, @planId, @phase, @decisionText, @rationale, @decidedBy, @createdAt)
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", decision.Id);
            command.Parameters.AddWithValue("@planId", decision.PlanId);
            command.Parameters.AddWithValue("@phase", decision.Phase);
            command.Parameters.AddWithValue("@decisionText", decision.DecisionText);
            command.Parameters.AddWithValue("@rationale", (object)decision.Rationale ?? DBNull.Value);
            command.Parameters.AddWithValue("@decidedBy", (object)decision.DecidedBy ?? DBNull.Value);
            command.Parameters.AddWithValue("@createdAt", decision.CreatedAt);

            command.ExecuteNonQuery();
        }

        #endregion

        #region Context Generation

        /// <summary>
        /// Generate startup context for a terminal based on active plan and assignment.
        /// Returns formatted string for injection into session context.
        /// </summary>
        public string GenerateStartupContext(string terminalName)
        {
            return GenerateStartupContext(terminalName, null);
        }

        /// <summary>
        /// Generate startup context for a terminal based on active plan and assignment.
        /// Optionally includes team activity status.
        /// </summary>
        /// <param name="terminalName">The terminal requesting context</param>
        /// <param name="taskDb">Optional TaskDatabase for team activity lookup</param>
        public string GenerateStartupContext(string terminalName, TaskDatabase taskDb)
        {
            var lines = new List<string>();

            // Add task stack section first (most important for the terminal)
            if (taskDb != null)
            {
                var taskStackSection = GenerateTaskStackSection(taskDb, terminalName);
                if (!string.IsNullOrEmpty(taskStackSection))
                {
                    lines.Add(taskStackSection);
                    lines.Add("");
                }
            }

            var plan = GetActivePlan();
            if (plan != null)
            {
                var phases = GetPlanPhases(plan.Id);
                var assignment = GetAssignmentForTerminal(plan.Id, terminalName);
                var currentPhase = phases.Find(p => p.PhaseName == plan.CurrentPhase);

                lines.Add($"## Active Plan: {plan.Title}");
                lines.Add($"Phase: {plan.CurrentPhase} ({phases.FindIndex(p => p.PhaseName == plan.CurrentPhase) + 1}/{phases.Count}) | Leader: {plan.LeaderId ?? "Unassigned"}");

                if (assignment != null)
                {
                    lines.Add($"Your Role: {assignment.Role}");
                    lines.Add($"Your Task: {assignment.AssignedTaskSummary ?? "Not specified"}");
                    lines.Add($"Status: {assignment.Status}");
                    if (!string.IsNullOrEmpty(assignment.BlockedBy))
                    {
                        lines.Add($"Blocked By: {assignment.BlockedBy}");
                    }
                }
                else
                {
                    lines.Add("Your Role: Not assigned to this plan");
                }

                // Add current phase checklist
                if (currentPhase != null)
                {
                    var checklist = currentPhase.GetChecklist();
                    if (checklist.Count > 0)
                    {
                        lines.Add("");
                        lines.Add("Checklist:");
                        foreach (var item in checklist)
                        {
                            var mark = item.Done ? "x" : " ";
                            lines.Add($"  [{mark}] {item.Item}");
                        }
                    }
                }
            }
            else if (taskDb == null || string.IsNullOrEmpty(lines.FirstOrDefault()))
            {
                // Only show "No Active Plan" if there's no task stack either
                lines.Add("## No Active Plan");
                lines.Add("No plan is currently active. Create a new plan or pick up a KanbanTask.");
            }

            // Add team status section if TaskDatabase is available
            if (taskDb != null)
            {
                var teamStatus = GenerateTeamStatusSection(taskDb, terminalName);
                if (!string.IsNullOrEmpty(teamStatus))
                {
                    lines.Add("");
                    lines.Add(teamStatus);
                }

                // Add recent progress section
                var recentProgress = GenerateRecentProgressSection(taskDb);
                if (!string.IsNullOrEmpty(recentProgress))
                {
                    lines.Add("");
                    lines.Add(recentProgress);
                }
            }

            return string.Join("\n", lines);
        }

        /// <summary>
        /// Generate the task stack section for a terminal's startup context.
        /// Shows active task, paused tasks, stale warnings, and helper assignments.
        /// </summary>
        private string GenerateTaskStackSection(TaskDatabase taskDb, string terminalName)
        {
            var lines = new List<string>();

            // Get all tasks assigned to this terminal
            var allTasks = taskDb.LoadAllTasks();
            var myTasks = allTasks.FindAll(t =>
                t.Assignee != null &&
                t.Assignee.Equals(terminalName, StringComparison.OrdinalIgnoreCase) &&
                t.Status == "in_progress");

            // Get active task (SubStatus = "active")
            var activeTask = myTasks.Find(t => t.SubStatus == "active");

            // Get paused tasks (SubStatus = "paused"), ordered by PausedAt DESC (LIFO)
            var pausedTasks = myTasks.FindAll(t => t.SubStatus == "paused");
            pausedTasks.Sort((a, b) =>
            {
                var aPaused = a.PausedAt ?? DateTime.MinValue;
                var bPaused = b.PausedAt ?? DateTime.MinValue;
                return bPaused.CompareTo(aPaused); // DESC order
            });

            // Get stale tasks for this terminal
            var staleTasks = taskDb.GetStaleTasksForTerminal(terminalName);

            // Get tasks where this terminal is a helper
            var helperTaskIds = taskDb.GetTasksWhereHelper(terminalName);
            var helperTasks = new List<KanbanTask>();
            foreach (var taskId in helperTaskIds)
            {
                var task = taskDb.GetTask(taskId);
                if (task != null && task.Status != "done")
                {
                    helperTasks.Add(task);
                }
            }

            // If no tasks in any category, return empty
            if (activeTask == null && pausedTasks.Count == 0 && staleTasks.Count == 0 && helperTasks.Count == 0)
            {
                return null;
            }

            lines.Add($"## Your Current Task ({terminalName})");

            // Active task section
            if (activeTask != null)
            {
                var latestSummary = taskDb.GetLatestSummary(activeTask.Id);
                var shortId = activeTask.Id.Length > 6 ? activeTask.Id.Substring(0, 6) : activeTask.Id;

                // Get time since last update
                var lastUpdateTime = latestSummary?.SummaryAt ?? activeTask.CreatedAt;
                var timeAgo = FormatTimeAgo(lastUpdateTime);

                lines.Add($"  ACTIVE: [{shortId}] {activeTask.Title}");
                lines.Add($"   Last update: {timeAgo}");

                if (latestSummary != null)
                {
                    if (!string.IsNullOrEmpty(latestSummary.WorkCompleted))
                    {
                        var workText = latestSummary.WorkCompleted.Length > 60
                            ? latestSummary.WorkCompleted.Substring(0, 57) + "..."
                            : latestSummary.WorkCompleted;
                        lines.Add($"   Progress: {workText}");
                    }
                    if (!string.IsNullOrEmpty(latestSummary.NextSteps))
                    {
                        var nextText = latestSummary.NextSteps.Length > 60
                            ? latestSummary.NextSteps.Substring(0, 57) + "..."
                            : latestSummary.NextSteps;
                        lines.Add($"   Next: {nextText}");
                    }
                    if (!string.IsNullOrEmpty(latestSummary.Blockers))
                    {
                        lines.Add($"   Blockers: {latestSummary.Blockers}");
                    }
                }
            }
            else
            {
                lines.Add("  ACTIVE: (none)");
            }

            // Paused tasks section (excluding stale ones which are shown separately)
            var nonStalePaused = pausedTasks.FindAll(t => t.StaleLevel == 0);
            if (nonStalePaused.Count > 0)
            {
                lines.Add("");
                lines.Add($"  PAUSED ({nonStalePaused.Count}):");
                foreach (var task in nonStalePaused)
                {
                    var shortId = task.Id.Length > 6 ? task.Id.Substring(0, 6) : task.Id;
                    var pausedAgo = task.PausedAt.HasValue ? FormatTimeAgo(task.PausedAt.Value) : "unknown";
                    lines.Add($"   [{shortId}] {task.Title} (paused {pausedAgo})");
                }
            }

            // Stale tasks warning section
            if (staleTasks.Count > 0)
            {
                lines.Add("");
                lines.Add("  STALE TASKS:");
                foreach (var task in staleTasks)
                {
                    var shortId = task.Id.Length > 6 ? task.Id.Substring(0, 6) : task.Id;
                    var pausedDays = task.PausedAt.HasValue
                        ? (int)(DateTime.UtcNow - task.PausedAt.Value).TotalDays
                        : 0;
                    var staleNote = task.StaleLevel >= 2
                        ? "(close or reprioritize?)"
                        : "(still relevant?)";
                    lines.Add($"   [{shortId}] {task.Title} - paused {pausedDays} days {staleNote}");
                }
            }

            // Helper assignments section
            if (helperTasks.Count > 0)
            {
                lines.Add("");
                lines.Add("  HELPER ON:");
                foreach (var task in helperTasks)
                {
                    var shortId = task.Id.Length > 6 ? task.Id.Substring(0, 6) : task.Id;
                    var owner = task.Assignee ?? "unassigned";
                    lines.Add($"   [{shortId}] {owner}'s task: {task.Title}");
                }
            }

            return string.Join("\n", lines);
        }

        /// <summary>
        /// Generate the team status section for startup context.
        /// Shows all registered terminals with their current activity and staleness info.
        /// </summary>
        private string GenerateTeamStatusSection(TaskDatabase taskDb, string currentTerminal)
        {
            var activities = taskDb.GetTeamActivityWithStaleness();
            if (activities.Count == 0)
            {
                return null;
            }

            var lines = new List<string> { "Team Status:" };

            foreach (var activity in activities)
            {
                var prefix = activity.Terminal == currentTerminal ? "- (you) " : "- ";
                var formatted = TaskDatabase.FormatTerminalActivity(activity);
                lines.Add($"{prefix}{formatted}");
            }

            return string.Join("\n", lines);
        }

        /// <summary>
        /// Generate the recent progress section for startup context.
        /// Shows recent task summaries to give context on what has been happening.
        /// </summary>
        private string GenerateRecentProgressSection(TaskDatabase taskDb, int limit = 5)
        {
            var summaries = taskDb.GetAllRecentSummaries(limit);
            if (summaries.Count == 0)
            {
                return null;
            }

            var lines = new List<string> { "Recent Progress:" };

            foreach (var summary in summaries)
            {
                var task = taskDb.GetTask(summary.TaskId);
                var taskTitle = task?.Title ?? summary.TaskId;
                var timeAgo = FormatTimeAgo(summary.SummaryAt);
                var autoPrefix = summary.IsAutoGenerated ? "[Auto] " : "";
                var authorPart = !string.IsNullOrEmpty(summary.Author) ? $" - {summary.Author}" : "";

                // Build a concise description of what happened
                var actionText = BuildSummaryActionText(summary, taskTitle);

                lines.Add($"- {autoPrefix}{actionText} ({timeAgo}){authorPart}");
            }

            return string.Join("\n", lines);
        }

        /// <summary>
        /// Build a concise action text for a summary based on status changes.
        /// </summary>
        private static string BuildSummaryActionText(TaskSummary summary, string taskTitle)
        {
            // If there's a work completed description, use it
            if (!string.IsNullOrEmpty(summary.WorkCompleted))
            {
                // Truncate long descriptions
                var workText = summary.WorkCompleted.Length > 60
                    ? summary.WorkCompleted.Substring(0, 57) + "..."
                    : summary.WorkCompleted;
                return $"{workText} on '{taskTitle}'";
            }

            // Otherwise, describe based on status change
            if (summary.NewStatus == "completed" || summary.NewStatus == "done")
            {
                return $"Completed work on '{taskTitle}'";
            }
            if (summary.NewStatus == "in_progress" || summary.NewStatus == "in-progress")
            {
                return $"Started work on '{taskTitle}'";
            }
            if (summary.NewStatus == "blocked")
            {
                return $"Blocked on '{taskTitle}'";
            }
            if (summary.NewStatus == "paused")
            {
                return $"Paused work on '{taskTitle}'";
            }

            // Generic fallback
            return $"Updated '{taskTitle}'";
        }

        /// <summary>
        /// Format a DateTime as a human-readable relative time string.
        /// </summary>
        private static string FormatTimeAgo(DateTime utcTime)
        {
            var elapsed = DateTime.UtcNow - utcTime;

            if (elapsed.TotalMinutes < 1)
            {
                return "just now";
            }
            if (elapsed.TotalMinutes < 60)
            {
                var mins = (int)elapsed.TotalMinutes;
                return $"{mins} min ago";
            }
            if (elapsed.TotalHours < 24)
            {
                var hours = (int)elapsed.TotalHours;
                return hours == 1 ? "1 hour ago" : $"{hours} hours ago";
            }
            if (elapsed.TotalDays < 7)
            {
                var days = (int)elapsed.TotalDays;
                return days == 1 ? "1 day ago" : $"{days} days ago";
            }

            return utcTime.ToString("MMM d");
        }

        #endregion

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed) return;
            if (disposing)
            {
                _connection?.Close();
                _connection?.Dispose();
            }
            _isDisposed = true;
        }
    }
}
