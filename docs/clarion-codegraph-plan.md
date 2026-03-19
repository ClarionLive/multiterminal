# ClarionCodeGraph — IDE Addin Plan

## Overview

A Clarion IDE addin (SharpDevelop dockable pad) that indexes Clarion solutions into a searchable code graph stored in SQLite. Provides structured code navigation queries: find procedures, callers, callees, dead code, impact analysis, inheritance trees, and file dependencies.

**Inspired by:** [CodeGraphContext](https://github.com/CodeGraphContext/CodeGraphContext) — adapted for Clarion's regular syntax using regex instead of tree-sitter, SQLite instead of Neo4j.

**Kanban Task ID:** 0ed9e33b

---

## Why This Matters

Clarion developers currently have zero navigation tooling:
- No "Find All References"
- No call hierarchy
- No dead code detection
- No impact analysis ("if I change X, what breaks?")
- Only flat text search (grep/find)

This addin brings Visual Studio-level code navigation to the Clarion IDE.

---

## Architecture

```
User opens .sln in Clarion IDE
         |
         v
  SolutionParser.cs          -- Parse .sln for project list + dependency GUIDs
         |
         v
  ProjectParser.cs           -- Parse each .cwproj for <Compile Include="*.clw">
         |
         v
  SourceResolver.cs          -- Find actual source files (default: .\source\)
         |
         v
  ClarionParser.cs           -- Regex-based, two-pass extraction
    Pass 1: MAP/MODULE blocks -> procedure declarations + which file they're in
    Pass 2: CODE sections -> routine defs, procedure calls, DO calls
         |
         v
  CodeGraphDatabase.cs       -- SQLite storage (nodes + edges tables)
         |
         v
  CodeGraphQuery.cs          -- Query methods: callers, callees, dead code, etc.
         |
         v
  CodeGraphControl.cs        -- WinForms UI (search pad)
         |
         v
  EditorService.cs           -- Navigate to file:line in Clarion editor
```

---

## Project Structure

```
ClarionCodeGraph/
├── ClarionCodeGraph.sln
├── ClarionCodeGraph/
│   ├── ClarionCodeGraph.csproj          (.NET Framework 4.0, x86)
│   ├── ClarionCodeGraph.addin           (SharpDevelop manifest)
│   ├── Properties/AssemblyInfo.cs
│   │
│   ├── CodeGraphPad.cs                  (dockable pad shell - AbstractPadContent)
│   ├── ShowCodeGraphCommand.cs          (menu/shortcut command)
│   │
│   ├── Controls/
│   │   ├── CodeGraphControl.cs          (main UI - WinForms UserControl)
│   │   └── CodeGraphControl.Designer.cs
│   │
│   ├── Parsing/
│   │   ├── SolutionParser.cs            (parse .sln -> project list + dependencies)
│   │   ├── ProjectParser.cs             (parse .cwproj -> source file list)
│   │   ├── SourceResolver.cs            (locate actual .clw/.inc files)
│   │   ├── ClarionParser.cs             (regex extraction of symbols + relationships)
│   │   └── Models/
│   │       ├── ClarionSymbol.cs         (name, type, file, line, params, return type)
│   │       ├── ClarionRelationship.cs   (from_id, to_id, type, file, line)
│   │       ├── SolutionProject.cs       (name, guid, cwproj path, dependency GUIDs)
│   │       └── ParseResult.cs           (symbols + relationships from one file)
│   │
│   ├── Graph/
│   │   ├── CodeGraphDatabase.cs         (SQLite - create tables, CRUD, queries)
│   │   ├── CodeGraphIndexer.cs          (orchestrates: solution -> projects -> parse -> store)
│   │   └── CodeGraphQuery.cs            (structured queries: callers, callees, dead code, etc.)
│   │
│   └── Services/
│       ├── EditorService.cs             (navigate to file:line in Clarion editor)
│       └── SettingsService.cs           (persist user preferences in AppData)
```

---

## Target Environment

- **.NET Framework 4.0** (Clarion IDE constraint)
- **x86 platform** (Clarion is 32-bit)
- **SharpDevelop addin format** (ICSharpCode.Core.dll, ICSharpCode.SharpDevelop.dll from C:\Clarion12\bin\)
- **SQLite** via System.Data.SQLite or manual P/Invoke (check what's available in .NET 4.0 without NuGet)
- **No external dependencies** — everything must ship as a single DLL + .addin file

---

## Parsing Details

### What We Parse and Where

| Source | What We Extract |
|--------|----------------|
| `.sln` file | Project names, GUIDs, .cwproj paths, `ProjectDependencies` (inter-project dependency graph) |
| `.cwproj` files | `<Compile Include="xxx.clw">` entries (source file manifest), `<OutputType>` (Library/Exe), `<AssemblyName>` |
| Main `.clw` (has `PROGRAM`) | `MAP...END` block containing `MODULE('file.clw')` → procedure declarations |
| Member `.clw` files | `MEMBER('parent.clw')`, `PROCEDURE`/`FUNCTION` definitions, `ROUTINE` definitions, procedure calls in CODE sections |
| `.inc` files | `CLASS` definitions, `INTERFACE` definitions, method declarations |

### Regex Patterns (Validated Against Real aPOSitive Code)

```
PROGRAM marker:        ^\s*PROGRAM\s*$
MAP start:             ^\s*MAP\s*$
MODULE declaration:    MODULE\s*\(\s*'([^']+)'\s*\)
MAP procedure decl:    ^\s{2,}(\w+)\s*(\([^)]*\))?\s*(,.*)?$     (inside MODULE block)
MEMBER declaration:    MEMBER\s*\(\s*'([^']+)'\s*\)
PROCEDURE definition:  ^(\w+)\s+PROCEDURE\s*(\([^)]*\))?
FUNCTION definition:   ^(\w+)\s+FUNCTION\s*(\([^)]*\))?
ROUTINE definition:    ^(\w+)\s+ROUTINE\s*$
CLASS definition:      ^(\w+)\s+CLASS\s*(\([^)]*\))?
INTERFACE definition:  ^(\w+)\s+INTERFACE\s*(\([^)]*\))?
INCLUDE statement:     INCLUDE\s*\(\s*'([^']+)'\s*\)
DO (routine call):     \bDO\s+(\w+)
```

### Two-Pass Indexing Strategy

**Pass 1 — Build symbol table from MAP blocks:**
1. Find the main `.clw` file (the one with `PROGRAM` keyword)
2. Parse its `MAP...END` section
3. Extract every `MODULE('file.clw')` → procedure/function names inside it
4. This gives the authoritative list of what procedures exist and which source file defines them
5. Also parse .inc files for CLASS/INTERFACE declarations

**Pass 2 — Parse MEMBER files for relationships:**
1. Each numbered `.clw` starts with `MEMBER('parent.clw')` — confirms module membership
2. Find ROUTINE definitions (local scope, called via `DO RoutineName`)
3. Scan CODE sections for procedure calls (match known procedure names from Pass 1)
4. Build CALLS edges, DO edges, INCLUDE edges

### Call Resolution Rules (Clarion Scoping)

When a procedure name appears in code:
1. Check local routines first (same file, via `DO`)
2. Check procedures in same project's MAP
3. Check procedures in dependency projects (from .sln ProjectDependencies)
4. If ambiguous, record all candidates (let user disambiguate in UI)

---

## SQLite Schema

```sql
-- Projects (from .sln)
CREATE TABLE projects (
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL,
    guid TEXT,
    cwproj_path TEXT,
    output_type TEXT,        -- Library, Exe
    sln_path TEXT             -- which solution this belongs to
);

-- Project dependencies (from .sln ProjectDependencies)
CREATE TABLE project_dependencies (
    project_id INTEGER REFERENCES projects(id),
    depends_on_id INTEGER REFERENCES projects(id),
    PRIMARY KEY (project_id, depends_on_id)
);

-- Symbols (procedures, functions, classes, routines, etc.)
CREATE TABLE symbols (
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL,
    type TEXT NOT NULL,        -- procedure, function, routine, class, interface, module, file
    file_path TEXT NOT NULL,
    line_number INTEGER,
    project_id INTEGER REFERENCES projects(id),
    params TEXT,               -- parameter list text
    return_type TEXT,           -- return type if function
    parent_name TEXT,           -- base class if CLASS(BaseClass)
    member_of TEXT,             -- MEMBER('parent.clw') value
    scope TEXT,                -- global (in MAP), local (routine), module
    source_preview TEXT         -- first few lines of the body (optional)
);

-- Relationships (edges)
CREATE TABLE relationships (
    id INTEGER PRIMARY KEY,
    from_id INTEGER REFERENCES symbols(id),
    to_id INTEGER REFERENCES symbols(id),
    type TEXT NOT NULL,         -- calls, do, inherits, implements, includes, contains, member_of, depends_on
    file_path TEXT,             -- where the reference occurs
    line_number INTEGER
);

-- Index metadata
CREATE TABLE index_metadata (
    key TEXT PRIMARY KEY,
    value TEXT
);
-- Keys: sln_path, last_indexed, file_count, symbol_count, index_duration_ms

-- Indexes
CREATE INDEX idx_sym_name ON symbols(name);
CREATE INDEX idx_sym_type ON symbols(type);
CREATE INDEX idx_sym_file ON symbols(file_path);
CREATE INDEX idx_sym_project ON symbols(project_id);
CREATE INDEX idx_rel_from ON relationships(from_id);
CREATE INDEX idx_rel_to ON relationships(to_id);
CREATE INDEX idx_rel_type ON relationships(type);
```

---

## Query Types (UI)

| Query | SQL Pattern | Description |
|-------|------------|-------------|
| **Find Procedure** | `WHERE name LIKE ?` | Type-ahead search, jump to definition |
| **Who Calls This?** | `JOIN relationships WHERE to_id = ? AND type = 'calls'` | Direct callers of a procedure |
| **What Does This Call?** | `JOIN relationships WHERE from_id = ? AND type = 'calls'` | What this procedure calls |
| **Impact Analysis** | Recursive CTE on callers | Transitive callers — "if I change X, what's affected?" |
| **Dead Code** | `LEFT JOIN relationships ... WHERE to_id IS NULL AND type = 'procedure'` | Procedures never called from anywhere |
| **Inheritance Tree** | Recursive CTE on `inherits` edges | Class hierarchy |
| **File Dependencies** | `WHERE type = 'includes'` | INCLUDE graph |
| **Project Dependencies** | `project_dependencies` table | Inter-project dependency graph |
| **Find by File** | `WHERE file_path = ?` | All symbols defined in a file |
| **Find by Project** | `WHERE project_id = ?` | All symbols in a project |

---

## UI Design

```
+-- ClarionCodeGraph ------------------------------------------------+
| Solution: [v61positive.sln              ] [Re-index] [Settings]    |
|                                                                     |
| Query: [Find Procedure    v] [________________search___] [Go]      |
|                                                                     |
| Results: (247 procedures, 1,623 files, indexed in 2.3s)            |
| +----------------------------------------------------------------+ |
| | Name              | Type      | File              | Line | Proj| |
| |----------------------------------------------------------------| |
| | DateSelect        | function  | PRMBase001.clw    |    9 | Base| |
| |   Called by: MainMenu (PRMBase020.clw:45)          [Go To]     | |
| |   Called by: QuickDate (PRM001_005.clw:112)        [Go To]     | |
| |   Calls: TSCalendar, OpenAllTables                             | |
| |----------------------------------------------------------------| |
| | DateRanger        | procedure | PRMBase002.clw    |   12 | Base| |
| |   Called by: ReportSetup (PRMBase035.clw:78)       [Go To]     | |
| +----------------------------------------------------------------+ |
+---------------------------------------------------------------------+
```

**Double-click** any result row or [Go To] link → EditorService opens the file at that line.

**UI Controls (WinForms):**
- ComboBox for query type
- TextBox for search input (with type-ahead/autocomplete)
- ListView or DataGridView for results (with columns: Name, Type, File, Line, Project)
- TreeView for hierarchical results (callers, inheritance)
- StatusBar showing index stats
- Button: Re-index, Settings

---

## Source Path Resolution

**Default behavior:** Look for source files in `.\source\` subfolder relative to each .cwproj.

**Settings dialog options (future):**
- Source path pattern (default: `.\source\`)
- Custom path per project
- .red file path (for full redirection parsing later)

**For v1:** Hardcode `.\source\` pattern. The .red file shows `*.clw = .\Source;` as the first search path for both Debug32 and Release32, so this covers the standard setup.

---

## .red File Reference (For Future Use)

The redirection file at `C:\Clarion10v8\bin\Clarion10v61.red` shows:

```
[Debug32]
*.clw = .\source;          <- source files
*.inc = .\source;          <- include files

[Common]
*.clw = .\Source;           <- primary
*.clw = .\;                 <- fallback to project root
*.clw = h:\dev\Source\SharedLibsrc;   <- shared libraries
*.clw = %ROOT%\Libsrc\win;           <- Clarion runtime libs
*.inc = .\Source;
*.inc = %ROOT%\Libsrc\win;
*.inc = h:\dev\Source\Classes;
*.inc = h:\dev\Source\SharedClasses;
```

The `.red` file defines search paths per file extension per build configuration. For v1 we skip this complexity and use `.\source\`. For v2, we could parse the .red to resolve INCLUDEs to their actual file locations across shared library paths.

---

## Real Code Examples (From aPOSitive v61)

### .sln structure (v61positive.sln — 642 lines)
```
Microsoft Visual Studio Solution File, Format Version 12.00
# Clarion 2.1.0.2447
Project("{12B76EC0-...}") = "PRMBase", "v61PRMBase\PRMBase.cwproj", "{08B059B1-...}"
    ProjectSection(ProjectDependencies) = postProject
        {CD74DA65-...} = {CD74DA65-...}          <- depends on another project
    EndProjectSection
EndProject
...  (27 projects total)
```

### .cwproj structure (PRMBase.cwproj)
```xml
<ItemGroup>
    <Compile Include="PRMBase.clw"><Generated>true</Generated></Compile>
    <Compile Include="PRMBase001.clw"><Generated>true</Generated></Compile>
    ...  (60+ .clw files in PRMBase alone)
</ItemGroup>
```

### Main .clw MAP section (PRMBase.clw, line 748)
```clarion
   MAP
     MODULE('PRMBase001.clw')
       DateSelect(LONG),LONG
     END
     MODULE('PRMBase004.clw')
       DemoPlayWav
       GetFileSystemTimeUTC(STRING FileName, BYTE which, *SYSTEMTIME systime), Byte
       GetFileDateUTC( STRING iFile, BYTE bType=0 ), LONG
       OpenAllTables(<BYTE>)
     END
     ...
   END
```

### Member .clw file (PRMBase001.clw)
```clarion
                     MEMBER('PRMBase.clw')                 ! This is a MEMBER module

DateSelect FUNCTION(WorkDate)
   ...
  CODE
  ...
  DO PrepareProcedure          <- routine call
  TSCalendar                   <- procedure call (defined in PRMBase016.clw per MAP)
  ...

PrepareProcedure ROUTINE       <- local routine definition
  ...

BindFields ROUTINE
UnBindFields ROUTINE
ProcedureReturn ROUTINE
```

### CLASS definition (in .clw, with inheritance)
```clarion
NYS:DockingPane_WndExt        CLASS(DockingPaneEventMgrClass)
                              END
```

---

## Scale Estimates (aPOSitive v61)

- **27 projects** in the solution
- **~60 .clw files** per project (PRMBase has 60, others vary)
- **~1,600 total source files** estimated
- **~2,000-5,000 procedure/function symbols** estimated
- **Thousands of CALLS edges**
- **SQLite can handle millions of rows** — no performance concern
- **Index time target:** < 5 seconds for full solution

---

## Checklist Items (For Kanban)

### Phase 1: Project Scaffold
1. Create ClarionCodeGraph addin project structure (sln, csproj, addin manifest, AssemblyInfo)
2. Create CodeGraphPad.cs and ShowCodeGraphCommand.cs (dockable pad shell)
3. Verify it loads in Clarion IDE (empty pad)

### Phase 2: Parsing Layer
4. Create models: ClarionSymbol, ClarionRelationship, SolutionProject, ParseResult
5. Implement SolutionParser.cs — parse .sln for projects + dependencies
6. Implement ProjectParser.cs — parse .cwproj for source file lists
7. Implement SourceResolver.cs — locate .clw/.inc files in .\source\ subfolder
8. Implement ClarionParser.cs — regex extraction (two-pass: MAP declarations + CODE calls)

### Phase 3: Database & Indexing
9. Implement CodeGraphDatabase.cs — SQLite schema creation + CRUD operations
10. Implement CodeGraphIndexer.cs — orchestrate: solution -> projects -> parse -> store
11. Implement CodeGraphQuery.cs — all query methods (callers, callees, dead code, impact, etc.)

### Phase 4: UI & IDE Integration
12. Implement CodeGraphControl.cs — WinForms UI (query type dropdown, search box, results grid)
13. Implement EditorService.cs — navigate to file:line in Clarion editor
14. Implement SettingsService.cs — persist solution path, preferences
15. Wire everything together: pad loads → user selects .sln → index → query → navigate

### Phase 5: Testing & Polish
16. Test against aPOSitive v61 solution (27 projects, ~1,600 files)
17. Performance tuning (index time, query time, UI responsiveness)
18. Build and deploy to Clarion IDE AddIns folder

---

## Dependencies & References

- **Clarion IDE addin skill:** `~/.claude/skills/clarion-ide-addin/skill.md` — has all SharpDevelop templates
- **Clarion language skill:** `~/.claude/skills/clarion/skill.md` — syntax reference
- **Test solution:** `H:\Dev\aPOSitive\v61positive.sln` (27 projects, production codebase)
- **Test .cwproj:** `H:\Dev\aPOSitive\v61PRMBase\PRMBase.cwproj` (60+ .clw files)
- **Test source:** `H:\Dev\aPOSitive\v61PRMBase\source\` (actual .clw files)
- **Red file:** `C:\Clarion10v8\bin\Clarion10v61.red` (redirection paths — for future .red parsing)
- **Clarion IDE bin:** `C:\Clarion12\bin\` (ICSharpCode.Core.dll, ICSharpCode.SharpDevelop.dll)
- **CodeGraphContext repo:** github.com/CodeGraphContext/CodeGraphContext (design inspiration)

---

## Open Questions

1. **SQLite library for .NET 4.0:** System.Data.SQLite NuGet? Or is there a lighter option that doesn't require NuGet for a single-DLL deployment? Could bundle sqlite3.dll and P/Invoke.
2. **Where to create the project:** Separate repo? Under H:\Dev? Under H:\DevLaptop? Need user input on folder location.
3. **Keyboard shortcut:** What shortcut for the pad? Ctrl+Alt+G? Ctrl+Shift+F12?
4. **Index storage location:** Per-solution SQLite file (next to .sln)? Or in AppData?
