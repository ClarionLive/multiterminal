# Testing and Validation Documentation

## Overview

The Task-Centric Memory System uses xUnit for automated testing with a focus on database layer validation and startup context generation.

## Test Project Structure

```
MultiTerminal.Tests/
├── PlanDatabaseTests.cs    # Comprehensive database and context tests
└── UnitTest1.cs            # Placeholder for additional tests
```

## Test Isolation Strategy

### Environment-Based Database Isolation

Tests use the `MULTITERMINAL_TEST_DB` environment variable to create isolated SQLite databases:

```csharp
public PlanDatabaseTests()
{
    _testDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_test_{Guid.NewGuid():N}.db");
    Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _testDbPath);
    _db = new PlanDatabase();
}
```

**Key benefits:**
- Each test run gets a unique database file
- No interference between parallel test executions
- Automatic cleanup via `IDisposable`

### Cleanup Protocol

```csharp
public void Dispose()
{
    _db?.Dispose();
    SQLiteConnection.ClearAllPools(); // Release file locks
    if (File.Exists(_testDbPath))
    {
        File.Delete(_testDbPath);
    }
    Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
}
```

## Test Categories

### 1. Plan CRUD Tests

| Test | Purpose |
|------|---------|
| `SavePlan_NewPlan_CanBeRetrieved` | Verify basic plan creation and retrieval |
| `SavePlan_ActiveStatus_DeactivatesOtherPlans` | Ensure only one active plan at a time |
| `GetActivePlan_NoActivePlan_ReturnsNull` | Handle no active plan gracefully |
| `GetActivePlan_WithActivePlan_ReturnsPlan` | Retrieve active plan correctly |
| `SetActivePlan_ActivatesDraftPlan` | Status transitions work correctly |
| `GetAllPlans_ReturnsAllPlans` | List operations return complete results |

### 2. Phase Tests

| Test | Purpose |
|------|---------|
| `CreateDefaultPhases_CreatesAllFourPhases` | Validates 4-phase workflow (design → coding → testing → completed) |
| `SavePhase_UpdatesChecklist` | Checklist items persist correctly |

### 3. Assignment Tests

| Test | Purpose |
|------|---------|
| `SaveAssignment_NewAssignment_CanBeRetrieved` | Basic assignment CRUD |
| `GetAssignmentForTerminal_NotFound_ReturnsNull` | Graceful handling of missing assignments |
| `SaveAssignment_BlockedStatus_SavesBlockedBy` | Blocked status with reason persists |

### 4. GenerateStartupContext Tests

| Test | Purpose |
|------|---------|
| `GenerateStartupContext_NoActivePlan_ReturnsNoActivePlanMessage` | Fallback message when no plan active |
| `GenerateStartupContext_WithActivePlan_ReturnsFormattedContext` | Correct plan summary format |
| `GenerateStartupContext_AssignedTerminal_ShowsAssignment` | Terminal-specific context |
| `GenerateStartupContext_UnassignedTerminal_ShowsNotAssigned` | Handle unassigned terminals |
| `GenerateStartupContext_BlockedAssignment_ShowsBlockedBy` | Display blocked status with reason |
| `GenerateStartupContext_WithChecklist_ShowsChecklistItems` | Checklist rendering (done/pending) |
| `GenerateStartupContext_EmptyChecklist_NoChecklistSection` | Omit empty checklist sections |

### 5. Decision Tests

| Test | Purpose |
|------|---------|
| `SaveDecision_NewDecision_CanBeRetrieved` | Plan decisions persist with rationale |

## Edge Cases Covered

### No Active Plan
When no plan is active, `GenerateStartupContext` returns a helpful message suggesting the use of standalone KanbanTask tools instead.

### Blocked Status
Assignments can have a `blocked` status with a `BlockedBy` field explaining the blocker. This appears in startup context so terminals know they're waiting on dependencies.

### Terminal Not Assigned
Terminals not assigned to the active plan see "Not assigned to this plan" rather than an error.

### Single Active Plan Constraint
When a new plan is set to `active`, all other plans are automatically set to `paused` to maintain the single-active-plan invariant.

### Empty Checklist
Phases with no checklist items don't show an empty "Checklist:" section - the section is omitted entirely.

## Running Tests

```powershell
# From the MultiTerminal directory
dotnet test MultiTerminal.Tests

# Run with detailed output
dotnet test MultiTerminal.Tests --logger "console;verbosity=detailed"
```

## Live Integration Testing

Beyond automated tests, the system was validated through live multi-terminal testing:

1. **Startup Context Injection** - Verified context appears at session start
2. **Phase Transitions** - Tested transitions: testing → completed → testing (revert)
3. **Assignment Updates** - Verified status changes: assigned → blocked → assigned
4. **Real-time Notifications** - Confirmed plan updates push to terminals

## Test Checklist

- [x] Test startup context injection
- [x] Test phase transitions
- [x] Test assignment updates
- [x] Test edge cases (no plan, blocked status)
