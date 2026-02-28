using System;
using System.Collections.Generic;
using System.Text.Json;
using MultiTerminal.MCPServer.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Seeds the initial plan data for the Task-Centric Memory System.
    /// Call SeedInitialPlan() once to create the first plan.
    /// </summary>
    public static class PlanSeeder
    {
        /// <summary>
        /// Seed the "Task-Centric Memory System" plan as the first active plan.
        /// </summary>
        public static void SeedInitialPlan()
        {
            using var db = new PlanDatabase();

            // Check if plan already exists
            var existing = db.GetActivePlan();
            if (existing != null)
            {
                System.Diagnostics.Debug.WriteLine($"[PlanSeeder] Active plan already exists: {existing.Title}");
                return;
            }

            // Create the plan
            var plan = new Plan
            {
                Id = "plan0001",
                Title = "Task-Centric Memory System",
                Description = "Implement a task/plan-centric workflow system that persists across sessions. When terminals start, they should know what plan is active, what phase it's in, and what their assignment is.",
                Content = @"## Goal
Replace session-based context with task/plan-centric workflow. Terminals wake up knowing their mission.

## Schema (5 tables)
- Plan: id, title, description, content, current_phase, status, leader_id
- PlanPhase: id, plan_id, phase_name, phase_order, checklist_json
- PlanAssignment: id, plan_id, terminal_name, role, assigned_task_summary, status
- PlanDecision: id, plan_id, phase, decision_text, rationale, decided_by
- KanbanTask: (existing) + plan_id FK

## Work Packages
1. Schema/Models (Bob) - C# models and SQLite tables
2. Startup Hook (Alice) - Query active plan, inject context
3. MCP Tools (Charlie) - create_plan, assign_task, update_phase, etc.
4. Testing/Validation (Diana) - Tests, phase transitions, edge cases

## Success Criteria
- On startup, each terminal sees: Active Plan, Phase, Their Assignment, Checklist
- Plans persist across sessions
- Leader can manage assignments and phase transitions",
                CurrentPhase = "coding",
                Status = "active",
                LeaderId = "Bob",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            db.SavePlan(plan);

            // Create phases with checklists
            var designChecklist = JsonSerializer.Serialize(new List<ChecklistItem>
            {
                new() { Item = "Define schema tables", Done = true },
                new() { Item = "Define C# models", Done = true },
                new() { Item = "Team alignment on design", Done = true },
                new() { Item = "User approval", Done = true }
            });

            var codingChecklist = JsonSerializer.Serialize(new List<ChecklistItem>
            {
                new() { Item = "Create Plan.cs models", Done = true },
                new() { Item = "Create PlanDatabase.cs service", Done = true },
                new() { Item = "Seed initial plan data", Done = false },
                new() { Item = "Update startup hook (Alice)", Done = false },
                new() { Item = "Create MCP tools (Charlie)", Done = false },
                new() { Item = "Integrate with MessageBroker", Done = false }
            });

            var testingChecklist = JsonSerializer.Serialize(new List<ChecklistItem>
            {
                new() { Item = "Test startup context injection", Done = false },
                new() { Item = "Test phase transitions", Done = false },
                new() { Item = "Test assignment updates", Done = false },
                new() { Item = "Test edge cases (no plan, blocked)", Done = false }
            });

            var completedChecklist = JsonSerializer.Serialize(new List<ChecklistItem>
            {
                new() { Item = "Leader sign-off", Done = false },
                new() { Item = "Documentation updated", Done = false }
            });

            var phases = new[]
            {
                new PlanPhase { Id = "phase001", PlanId = plan.Id, PhaseName = "design", PhaseOrder = 1, ChecklistJson = designChecklist, StartedAt = DateTime.UtcNow.AddHours(-1), CompletedAt = DateTime.UtcNow },
                new PlanPhase { Id = "phase002", PlanId = plan.Id, PhaseName = "coding", PhaseOrder = 2, ChecklistJson = codingChecklist, StartedAt = DateTime.UtcNow },
                new PlanPhase { Id = "phase003", PlanId = plan.Id, PhaseName = "testing", PhaseOrder = 3, ChecklistJson = testingChecklist },
                new PlanPhase { Id = "phase004", PlanId = plan.Id, PhaseName = "completed", PhaseOrder = 4, ChecklistJson = completedChecklist }
            };

            foreach (var phase in phases)
            {
                db.SavePhase(phase);
            }

            // Create assignments
            var assignments = new[]
            {
                new PlanAssignment { Id = "asgn001", PlanId = plan.Id, TerminalName = "Bob", Role = "leader", AssignedTaskSummary = "Schema/Models - Create C# models and SQLite tables, coordinate team", Status = "in_progress" },
                new PlanAssignment { Id = "asgn002", PlanId = plan.Id, TerminalName = "Alice", Role = "member", AssignedTaskSummary = "Startup Hook - Modify hook to query active plan and inject context", Status = "assigned" },
                new PlanAssignment { Id = "asgn003", PlanId = plan.Id, TerminalName = "Charlie", Role = "member", AssignedTaskSummary = "MCP Tools - Create create_plan, assign_task, update_phase, get_my_assignment tools", Status = "assigned" },
                new PlanAssignment { Id = "asgn004", PlanId = plan.Id, TerminalName = "Diana", Role = "member", AssignedTaskSummary = "Testing/Validation - Write tests, validate phase transitions, edge cases", Status = "assigned" }
            };

            foreach (var assignment in assignments)
            {
                db.SaveAssignment(assignment);
            }

            // Record the design decisions
            var decisions = new[]
            {
                new PlanDecision { Id = "dec001", PlanId = plan.Id, Phase = "design", DecisionText = "Separate Plan table from KanbanTask", Rationale = "A Plan can spawn multiple tasks. Clean separation of concerns.", DecidedBy = "Team" },
                new PlanDecision { Id = "dec002", PlanId = plan.Id, Phase = "design", DecisionText = "Global active plan (not per-terminal)", Rationale = "Team works on one mission together. Leader coordinates assignments.", DecidedBy = "Team" },
                new PlanDecision { Id = "dec003", PlanId = plan.Id, Phase = "design", DecisionText = "Fixed phase enum (design→coding→testing→completed)", Rationale = "Keep it simple. Can extend later if needed.", DecidedBy = "Alice" },
                new PlanDecision { Id = "dec004", PlanId = plan.Id, Phase = "design", DecisionText = "PlanDecision table for tracking rationale", Rationale = "Context is gold when resuming later. 'Why did we choose X?'", DecidedBy = "Diana" }
            };

            foreach (var decision in decisions)
            {
                db.SaveDecision(decision);
            }

            System.Diagnostics.Debug.WriteLine($"[PlanSeeder] Seeded plan: {plan.Title}");
        }

        /// <summary>
        /// Check if seeding is needed and seed if so.
        /// Safe to call multiple times.
        /// </summary>
        public static void EnsureSeeded()
        {
            try
            {
                using var db = new PlanDatabase();
                var plans = db.GetAllPlans();
                if (plans.Count == 0)
                {
                    SeedInitialPlan();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PlanSeeder] Error: {ex.Message}");
            }
        }
    }
}
