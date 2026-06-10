# Code Graph

> Roslyn-based C# code indexer and query layer. Extracts symbols (classes, methods, properties) and relationships (calls, inherits, references) into cg_* SQLite tables.

**Tags:** `indexing`, `roslyn`, `analysis`

## Key Files

- `Services/CSharpCodeGraphIndexer.cs` (806 LOC)
- `Services/CodeGraphDatabase.cs` (350 LOC)
- `API/Controllers/CodeGraphController.cs` (253 LOC)
- `Services/CodeGraphQuery.cs` (219 LOC)
- `MCPServer/Models/CodeSymbol.cs` (23 LOC)
- `MCPServer/Models/CodeRelationship.cs` (12 LOC)

## Key Classes

- **CSharpCodeGraphIndexer** (class) — `Services/CSharpCodeGraphIndexer.cs:22`
- **SymbolExtractor** (class) — `Services/CSharpCodeGraphIndexer.cs:218`
- **RelationshipExtractor** (class) — `Services/CSharpCodeGraphIndexer.cs:471`
- **IndexResult** (class) — `Services/CSharpCodeGraphIndexer.cs:765`
- **CodeGraphDatabase** (class) — `Services/CodeGraphDatabase.cs:15`
- **CodeGraphQuery** (class) — `Services/CodeGraphQuery.cs:11`
- **CodeGraphController** (class) — `API/Controllers/CodeGraphController.cs:11`
- **IndexRequest** (class) — `API/Controllers/CodeGraphController.cs:223`
- **CodeSymbol** (class) — `MCPServer/Models/CodeSymbol.cs:3`
- **CodeRelationship** (class) — `MCPServer/Models/CodeRelationship.cs:3`

## Key Methods

- `CSharpCodeGraphIndexer.IndexDirectory` — `Services/CSharpCodeGraphIndexer.cs:38`
- `SymbolExtractor.VisitClassDeclaration` — `Services/CSharpCodeGraphIndexer.cs:234`
- `SymbolExtractor.VisitInterfaceDeclaration` — `Services/CSharpCodeGraphIndexer.cs:237`
- `SymbolExtractor.VisitStructDeclaration` — `Services/CSharpCodeGraphIndexer.cs:240`
- `SymbolExtractor.VisitEnumDeclaration` — `Services/CSharpCodeGraphIndexer.cs:243`
- `SymbolExtractor.VisitDelegateDeclaration` — `Services/CSharpCodeGraphIndexer.cs:246`
- `SymbolExtractor.VisitMethodDeclaration` — `Services/CSharpCodeGraphIndexer.cs:266`
- `SymbolExtractor.VisitConstructorDeclaration` — `Services/CSharpCodeGraphIndexer.cs:292`
- `SymbolExtractor.VisitPropertyDeclaration` — `Services/CSharpCodeGraphIndexer.cs:312`
- `SymbolExtractor.VisitEventFieldDeclaration` — `Services/CSharpCodeGraphIndexer.cs:333`
- `SymbolExtractor.VisitEventDeclaration` — `Services/CSharpCodeGraphIndexer.cs:357`
- `SymbolExtractor.VisitFieldDeclaration` — `Services/CSharpCodeGraphIndexer.cs:377`
- `RelationshipExtractor.VisitClassDeclaration` — `Services/CSharpCodeGraphIndexer.cs:492`
- `RelationshipExtractor.VisitStructDeclaration` — `Services/CSharpCodeGraphIndexer.cs:495`
- `RelationshipExtractor.VisitInvocationExpression` — `Services/CSharpCodeGraphIndexer.cs:525`
- `RelationshipExtractor.VisitObjectCreationExpression` — `Services/CSharpCodeGraphIndexer.cs:557`
- `RelationshipExtractor.VisitAssignmentExpression` — `Services/CSharpCodeGraphIndexer.cs:588`
- `RelationshipExtractor.VisitFieldDeclaration` — `Services/CSharpCodeGraphIndexer.cs:652`
- `RelationshipExtractor.VisitPropertyDeclaration` — `Services/CSharpCodeGraphIndexer.cs:673`
- `RelationshipExtractor.VisitMethodDeclaration` — `Services/CSharpCodeGraphIndexer.cs:687`
- `CodeGraphDatabase.InsertProject` — `Services/CodeGraphDatabase.cs:94`
- `CodeGraphDatabase.FindProjectIdByName` — `Services/CodeGraphDatabase.cs:107`
- `CodeGraphDatabase.InsertProjectDependency` — `Services/CodeGraphDatabase.cs:116`
- `CodeGraphDatabase.InsertSymbol` — `Services/CodeGraphDatabase.cs:127`
- `CodeGraphDatabase.FindSymbolId` — `Services/CodeGraphDatabase.cs:159`

## Routes

- `GET` `/api/code-graph/search` — `API/Controllers/CodeGraphController.cs:51`
- `GET` `/api/code-graph/callers` — `API/Controllers/CodeGraphController.cs:66`
- `GET` `/api/code-graph/callees` — `API/Controllers/CodeGraphController.cs:83`
- `GET` `/api/code-graph/impact` — `API/Controllers/CodeGraphController.cs:100`
- `GET` `/api/code-graph/inheritance` — `API/Controllers/CodeGraphController.cs:117`
- `GET` `/api/code-graph/dead-code` — `API/Controllers/CodeGraphController.cs:134`
- `GET` `/api/code-graph/file-symbols` — `API/Controllers/CodeGraphController.cs:148`
- `POST` `/api/code-graph/index` — `API/Controllers/CodeGraphController.cs:163`
- `GET` `/api/code-graph/stats` — `API/Controllers/CodeGraphController.cs:198`

---
_Generated 2026-06-10T17:01:21.2733863Z · [Back to index](./index.md)_
