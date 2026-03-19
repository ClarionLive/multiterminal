# Test Isolation for Database Tests

This document describes the test isolation mechanism for database-dependent tests in MultiTerminal.

## The Problem

Database tests must be isolated from the production database to:
- Avoid corrupting live data during test runs
- Ensure repeatable, deterministic test results
- Allow parallel test execution without conflicts

## Solution: MULTITERMINAL_TEST_DB Environment Variable

The `TaskDatabase.GetDatabasePath()` method checks for a test override:

```csharp
public static string GetDatabasePath()
{
    // Check for test database override
    var testDb = Environment.GetEnvironmentVariable("MULTITERMINAL_TEST_DB");
    if (!string.IsNullOrEmpty(testDb)) return testDb;

    // Production path
    string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    string folder = Path.Combine(appData, "multiterminal");
    return Path.Combine(folder, "multiterminal.db");
}
```

## Test Setup Pattern

Tests should follow this pattern for proper isolation:

```csharp
public class PlanDatabaseTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly PlanDatabase _db;

    public PlanDatabaseTests()
    {
        // Create unique test database path
        _testDbPath = Path.Combine(
            Path.GetTempPath(),
            $"multiterminal_test_{Guid.NewGuid():N}.db"
        );

        // Set environment variable BEFORE creating database instance
        Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _testDbPath);

        _db = new PlanDatabase();
    }

    public void Dispose()
    {
        _db?.Dispose();

        // IMPORTANT: Clear connection pool before file deletion
        SQLiteConnection.ClearAllPools();

        if (File.Exists(_testDbPath))
        {
            File.Delete(_testDbPath);
        }

        // Clean up environment
        Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
    }
}
```

## SQLite Connection Pool Cleanup

SQLite maintains a connection pool that can keep file handles open even after `Dispose()` is called. To reliably delete test databases:

```csharp
// Must call BEFORE File.Delete()
SQLiteConnection.ClearAllPools();
```

Without this, you may encounter:
```
System.IO.IOException: The process cannot access the file '...' because it is being used by another process.
```

## Bug Fix History

**Issue**: Tests were hitting the production database instead of isolated test databases.

**Root Cause**: `TaskDatabase.GetDatabasePath()` was not checking the `MULTITERMINAL_TEST_DB` environment variable.

**Fix**: Added environment variable check at the start of `GetDatabasePath()`.

**Date**: Testing phase of Task-Centric Memory System implementation.

## Best Practices

1. **Always use unique paths** - Include a GUID in the test database filename
2. **Set env var before instantiation** - The database path is resolved in the constructor
3. **Clear connection pools** - Call `SQLiteConnection.ClearAllPools()` before file deletion
4. **Clean up environment** - Set the env var to null in `Dispose()`

## Required Using Statements

```csharp
using System;
using System.Data.SQLite;  // For ClearAllPools()
using System.IO;
```
