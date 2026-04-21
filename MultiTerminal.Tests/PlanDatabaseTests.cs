using System;
using System.Data.SQLite;
using System.IO;
using MultiTerminal.Services;
using MultiTerminal.MCPServer.Models;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Tests for PlanDatabase - database layer and GenerateStartupContext.
    /// </summary>
    public sealed class PlanDatabaseTests : IDisposable
    {
        private readonly string _testDbPath;
        private readonly PlanDatabase _db;

        public PlanDatabaseTests()
        {
            // Use isolated test database
            _testDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_test_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _testDbPath);
            _db = new PlanDatabase();
        }

        public void Dispose()
        {
            _db?.Dispose();
            SQLiteConnection.ClearAllPools(); // Release file locks before deletion
            if (File.Exists(_testDbPath))
            {
                File.Delete(_testDbPath);
            }
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
            GC.SuppressFinalize(this);
        }

        #region Plan CRUD Tests

        [Fact]
        public void SavePlan_NewPlan_CanBeRetrieved()
        {
            // Arrange
            var plan = new Plan
            {
                Title = "Test Plan",
                Description = "Test Description",
                Status = "draft"
            };

            // Act
            _db.SavePlan(plan);
            var retrieved = _db.GetPlan(plan.Id);

            // Assert
            Assert.NotNull(retrieved);
            Assert.Equal("Test Plan", retrieved.Title);
            Assert.Equal("Test Description", retrieved.Description);
            Assert.Equal("draft", retrieved.Status);
            Assert.Equal("design", retrieved.CurrentPhase);
        }

        [Fact]
        public void SavePlan_ActiveStatus_DeactivatesOtherPlans()
        {
            // Arrange
            var plan1 = new Plan { Title = "Plan 1", Status = "active" };
            var plan2 = new Plan { Title = "Plan 2", Status = "active" };

            // Act
            _db.SavePlan(plan1);
            _db.SavePlan(plan2);

            // Assert - only plan2 should be active
            var retrieved1 = _db.GetPlan(plan1.Id);
            var retrieved2 = _db.GetPlan(plan2.Id);
            Assert.Equal("paused", retrieved1.Status);
            Assert.Equal("active", retrieved2.Status);
        }

        [Fact]
        public void GetActivePlan_NoActivePlan_ReturnsNull()
        {
            // Arrange - no plans created

            // Act
            var result = _db.GetActivePlan();

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void GetActivePlan_WithActivePlan_ReturnsPlan()
        {
            // Arrange
            var plan = new Plan { Title = "Active Plan", Status = "active" };
            _db.SavePlan(plan);

            // Act
            var result = _db.GetActivePlan();

            // Assert
            Assert.NotNull(result);
            Assert.Equal("Active Plan", result.Title);
        }

        [Fact]
        public void SetActivePlan_ActivatesDraftPlan()
        {
            // Arrange
            var plan = new Plan { Title = "Draft Plan", Status = "draft" };
            _db.SavePlan(plan);

            // Act
            _db.SetActivePlan(plan.Id);
            var result = _db.GetPlan(plan.Id);

            // Assert
            Assert.Equal("active", result.Status);
        }

        [Fact]
        public void GetAllPlans_ReturnsAllPlans()
        {
            // Arrange
            _db.SavePlan(new Plan { Title = "Plan 1", Status = "draft" });
            _db.SavePlan(new Plan { Title = "Plan 2", Status = "draft" });
            _db.SavePlan(new Plan { Title = "Plan 3", Status = "draft" });

            // Act
            var plans = _db.GetAllPlans();

            // Assert
            Assert.Equal(3, plans.Count);
        }

        #endregion

        #region Phase Tests

        [Fact]
        public void CreateDefaultPhases_CreatesAllFourPhases()
        {
            // Arrange
            var plan = new Plan { Title = "Test Plan" };
            _db.SavePlan(plan);

            // Act
            _db.CreateDefaultPhases(plan.Id);
            var phases = _db.GetPlanPhases(plan.Id);

            // Assert
            Assert.Equal(4, phases.Count);
            Assert.Equal("design", phases[0].PhaseName);
            Assert.Equal("coding", phases[1].PhaseName);
            Assert.Equal("testing", phases[2].PhaseName);
            Assert.Equal("completed", phases[3].PhaseName);
        }

        [Fact]
        public void SavePhase_UpdatesChecklist()
        {
            // Arrange
            var plan = new Plan { Title = "Test Plan" };
            _db.SavePlan(plan);
            _db.CreateDefaultPhases(plan.Id);
            var phases = _db.GetPlanPhases(plan.Id);
            var designPhase = phases[0];

            // Act
            var checklist = designPhase.GetChecklist();
            checklist.Add(new ChecklistItem { Item = "Test Item", Done = false });
            designPhase.SetChecklist(checklist);
            _db.SavePhase(designPhase);

            // Assert
            var updatedPhases = _db.GetPlanPhases(plan.Id);
            var updatedChecklist = updatedPhases[0].GetChecklist();
            Assert.Single(updatedChecklist);
            Assert.Equal("Test Item", updatedChecklist[0].Item);
        }

        #endregion

        #region Assignment Tests

        [Fact]
        public void SaveAssignment_NewAssignment_CanBeRetrieved()
        {
            // Arrange
            var plan = new Plan { Title = "Test Plan" };
            _db.SavePlan(plan);
            var assignment = new PlanAssignment
            {
                PlanId = plan.Id,
                TerminalName = "TestTerminal",
                Role = "member",
                AssignedTaskSummary = "Test task"
            };

            // Act
            _db.SaveAssignment(assignment);
            var retrieved = _db.GetAssignmentForTerminal(plan.Id, "TestTerminal");

            // Assert
            Assert.NotNull(retrieved);
            Assert.Equal("TestTerminal", retrieved.TerminalName);
            Assert.Equal("member", retrieved.Role);
            Assert.Equal("Test task", retrieved.AssignedTaskSummary);
        }

        [Fact]
        public void GetAssignmentForTerminal_NotFound_ReturnsNull()
        {
            // Arrange
            var plan = new Plan { Title = "Test Plan" };
            _db.SavePlan(plan);

            // Act
            var result = _db.GetAssignmentForTerminal(plan.Id, "NonExistentTerminal");

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void SaveAssignment_BlockedStatus_SavesBlockedBy()
        {
            // Arrange
            var plan = new Plan { Title = "Test Plan" };
            _db.SavePlan(plan);
            var assignment = new PlanAssignment
            {
                PlanId = plan.Id,
                TerminalName = "TestTerminal",
                Status = "blocked",
                BlockedBy = "Waiting for API"
            };

            // Act
            _db.SaveAssignment(assignment);
            var retrieved = _db.GetAssignmentForTerminal(plan.Id, "TestTerminal");

            // Assert
            Assert.Equal("blocked", retrieved.Status);
            Assert.Equal("Waiting for API", retrieved.BlockedBy);
        }

        #endregion

        #region GenerateStartupContext Tests

        [Fact]
        public void GenerateStartupContext_NoActivePlan_ReturnsNoActivePlanMessage()
        {
            // Arrange - no plans

            // Act
            var context = _db.GenerateStartupContext("TestTerminal");

            // Assert
            Assert.Contains("No Active Plan", context);
            Assert.Contains("No plan is currently active", context);
            Assert.Contains("KanbanTask", context); // Verify fallback suggestion included
        }

        [Fact]
        public void GenerateStartupContext_WithActivePlan_ReturnsFormattedContext()
        {
            // Arrange
            var plan = new Plan
            {
                Title = "Test Plan",
                Status = "active",
                CurrentPhase = "coding",
                LeaderId = "Bob"
            };
            _db.SavePlan(plan);
            _db.CreateDefaultPhases(plan.Id);

            // Act
            var context = _db.GenerateStartupContext("TestTerminal");

            // Assert
            Assert.Contains("Active Plan: Test Plan", context);
            Assert.Contains("Phase: coding", context);
            Assert.Contains("Leader: Bob", context);
        }

        [Fact]
        public void GenerateStartupContext_AssignedTerminal_ShowsAssignment()
        {
            // Arrange
            var plan = new Plan { Title = "Test Plan", Status = "active" };
            _db.SavePlan(plan);
            _db.CreateDefaultPhases(plan.Id);
            var assignment = new PlanAssignment
            {
                PlanId = plan.Id,
                TerminalName = "Diana",
                Role = "member",
                AssignedTaskSummary = "Testing/Validation",
                Status = "in_progress"
            };
            _db.SaveAssignment(assignment);

            // Act
            var context = _db.GenerateStartupContext("Diana");

            // Assert
            Assert.Contains("Your Role: member", context);
            Assert.Contains("Your Task: Testing/Validation", context);
            Assert.Contains("Status: in_progress", context);
        }

        [Fact]
        public void GenerateStartupContext_UnassignedTerminal_ShowsNotAssigned()
        {
            // Arrange
            var plan = new Plan { Title = "Test Plan", Status = "active" };
            _db.SavePlan(plan);
            _db.CreateDefaultPhases(plan.Id);

            // Act
            var context = _db.GenerateStartupContext("UnknownTerminal");

            // Assert
            Assert.Contains("Not assigned to this plan", context);
        }

        [Fact]
        public void GenerateStartupContext_BlockedAssignment_ShowsBlockedBy()
        {
            // Arrange
            var plan = new Plan { Title = "Test Plan", Status = "active" };
            _db.SavePlan(plan);
            _db.CreateDefaultPhases(plan.Id);
            var assignment = new PlanAssignment
            {
                PlanId = plan.Id,
                TerminalName = "Diana",
                Status = "blocked",
                BlockedBy = "Waiting for API review"
            };
            _db.SaveAssignment(assignment);

            // Act
            var context = _db.GenerateStartupContext("Diana");

            // Assert
            Assert.Contains("Blocked By: Waiting for API review", context);
        }

        [Fact]
        public void GenerateStartupContext_WithChecklist_ShowsChecklistItems()
        {
            // Arrange
            var plan = new Plan { Title = "Test Plan", Status = "active", CurrentPhase = "design" };
            _db.SavePlan(plan);
            _db.CreateDefaultPhases(plan.Id);

            var phases = _db.GetPlanPhases(plan.Id);
            var designPhase = phases[0];
            designPhase.SetChecklist(new System.Collections.Generic.List<ChecklistItem>
            {
                new ChecklistItem { Item = "Define schema", Done = true },
                new ChecklistItem { Item = "Create models", Done = false }
            });
            _db.SavePhase(designPhase);

            // Act
            var context = _db.GenerateStartupContext("TestTerminal");

            // Assert
            Assert.Contains("[x] Define schema", context);
            Assert.Contains("[ ] Create models", context);
        }

        [Fact]
        public void GenerateStartupContext_EmptyChecklist_NoChecklistSection()
        {
            // Arrange
            var plan = new Plan { Title = "Test Plan", Status = "active", CurrentPhase = "design" };
            _db.SavePlan(plan);
            _db.CreateDefaultPhases(plan.Id);
            // Default phases have empty checklists

            // Act
            var context = _db.GenerateStartupContext("TestTerminal");

            // Assert
            Assert.DoesNotContain("Checklist:", context);
        }

        #endregion

        #region Decision Tests

        [Fact]
        public void SaveDecision_NewDecision_CanBeRetrieved()
        {
            // Arrange
            var plan = new Plan { Title = "Test Plan" };
            _db.SavePlan(plan);
            var decision = new PlanDecision
            {
                PlanId = plan.Id,
                Phase = "design",
                DecisionText = "Use SQLite for storage",
                Rationale = "Simple, file-based, no server needed",
                DecidedBy = "Team"
            };

            // Act
            _db.SaveDecision(decision);
            var decisions = _db.GetPlanDecisions(plan.Id);

            // Assert
            Assert.Single(decisions);
            Assert.Equal("Use SQLite for storage", decisions[0].DecisionText);
            Assert.Equal("Simple, file-based, no server needed", decisions[0].Rationale);
        }

        #endregion
    }
}
