# Knowledge Database

> Stores knowledge entries (patterns, decisions, gotchas), code digests, and research cache. FTS5 search across knowledge content.

**Tags:** `knowledge`, `persistence`, `search`

## Key Files

- `Services/KnowledgeDatabase.cs` (643 LOC)
  - CRUD and FTS5 search for knowledge entries and code digests in shared tasks.db
- `API/Controllers/KnowledgeController.cs` (359 LOC)
  - REST endpoints for institutional memory system: knowledge entries and code digests with search, CRUD, and stale detection.
- `MCPServer/Models/KnowledgeEntry.cs` (53 LOC)
  - Unit of institutional memory capturing decisions, patterns, gotchas, anti-patterns, debug insights, preferences with category, confidence, and supersession.
- `MCPServer/Models/CodeDigest.cs` (46 LOC)
  - Structured summary of a source file for agent orientation: purpose, classes, methods, patterns, gotchas, dependencies, with SHA256 stale detection.

## Key Classes

- **KnowledgeDatabase** (class) — `Services/KnowledgeDatabase.cs:13`
- **KnowledgeEntry** (class) — `MCPServer/Models/KnowledgeEntry.cs:8`
- **CodeDigest** (class) — `MCPServer/Models/CodeDigest.cs:7`
- **KnowledgeController** (class) — `API/Controllers/KnowledgeController.cs:17`
- **StaleDigestRequest** (class) — `API/Controllers/KnowledgeController.cs:339`
- **BumpReferencesRequest** (class) — `API/Controllers/KnowledgeController.cs:345`
- **ResearchCacheRequest** (class) — `API/Controllers/KnowledgeController.cs:350`

## Key Methods

- `KnowledgeDatabase.AddKnowledgeEntry` — `Services/KnowledgeDatabase.cs:34`
- `KnowledgeDatabase.UpdateKnowledgeEntry` — `Services/KnowledgeDatabase.cs:68`
- `KnowledgeDatabase.DeprecateKnowledgeEntry` — `Services/KnowledgeDatabase.cs:114`
- `KnowledgeDatabase.GetKnowledgeEntry` — `Services/KnowledgeDatabase.cs:132`
- `KnowledgeDatabase.GetKnowledgeBySource` — `Services/KnowledgeDatabase.cs:152`
- `KnowledgeDatabase.SearchKnowledge` — `Services/KnowledgeDatabase.cs:179`
- `KnowledgeDatabase.BumpReference` — `Services/KnowledgeDatabase.cs:282`
- `KnowledgeDatabase.BumpReferences` — `Services/KnowledgeDatabase.cs:299`
- `KnowledgeDatabase.LookupResearchCache` — `Services/KnowledgeDatabase.cs:321`
- `KnowledgeDatabase.ResearchCacheExists` — `Services/KnowledgeDatabase.cs:345`
- `KnowledgeDatabase.SaveCodeDigest` — `Services/KnowledgeDatabase.cs:370`
- `KnowledgeDatabase.GetCodeDigest` — `Services/KnowledgeDatabase.cs:412`
- `KnowledgeDatabase.GetStaleDigests` — `Services/KnowledgeDatabase.cs:450`
- `KnowledgeDatabase.DeleteCodeDigest` — `Services/KnowledgeDatabase.cs:483`
- `KnowledgeDatabase.GetDecayRanked` — `Services/KnowledgeDatabase.cs:572`
- `KnowledgeController.SearchKnowledge` — `API/Controllers/KnowledgeController.cs:39`
- `KnowledgeController.GetDecayRankedInjection` — `API/Controllers/KnowledgeController.cs:70`
- `KnowledgeController.AddKnowledgeEntry` — `API/Controllers/KnowledgeController.cs:123`
- `KnowledgeController.UpdateKnowledgeEntry` — `API/Controllers/KnowledgeController.cs:148`
- `KnowledgeController.BumpReferences` — `API/Controllers/KnowledgeController.cs:170`
- `KnowledgeController.LookupResearchCache` — `API/Controllers/KnowledgeController.cs:192`
- `KnowledgeController.SaveResearchCache` — `API/Controllers/KnowledgeController.cs:222`
- `KnowledgeController.GetCodeDigest` — `API/Controllers/KnowledgeController.cs:280`
- `KnowledgeController.SaveCodeDigest` — `API/Controllers/KnowledgeController.cs:302`
- `KnowledgeController.GetStaleDigests` — `API/Controllers/KnowledgeController.cs:324`

## Routes

- `GET` `/api/knowledge/search` — `API/Controllers/KnowledgeController.cs:39`
- `GET` `/api/knowledge/inject` — `API/Controllers/KnowledgeController.cs:70`
- `POST` `/api/knowledge` — `API/Controllers/KnowledgeController.cs:123`
- `PUT` `/api/knowledge/{id:int}` — `API/Controllers/KnowledgeController.cs:148`
- `POST` `/api/knowledge/bump` — `API/Controllers/KnowledgeController.cs:170`
- `GET` `/api/knowledge/research-cache` — `API/Controllers/KnowledgeController.cs:192`
- `POST` `/api/knowledge/research-cache` — `API/Controllers/KnowledgeController.cs:222`
- `GET` `/api/knowledge/digest` — `API/Controllers/KnowledgeController.cs:280`
- `POST` `/api/knowledge/digest` — `API/Controllers/KnowledgeController.cs:302`
- `POST` `/api/knowledge/digest/stale` — `API/Controllers/KnowledgeController.cs:324`

## External Callers

> Code outside this subsystem that calls into it.

- `HudKnowledgeRenderer.RefreshKnowledge` — `Controls/HudKnowledgePanel/HudKnowledgeRenderer.cs:123`

## Gotchas

- FTS5 query clamped to 500 chars to prevent oversized expressions; code_digests uses UPSERT by (project_id, file_path)
- Category: decision/pattern/gotcha/anti_pattern/debug_insight/preference, Confidence: observed/confirmed/deprecated, SupersededBy points to newer entry ID
- FileHash used for stale detection, DigestModel stores which Claude model generated it (e.g. claude-sonnet-4-6)
- KnowledgeDatabase accessed via MessageBroker, must validate query/field parameters, stale detection uses SHA256 hashes

---
_Generated 2026-06-10T17:01:21.3096741Z · [Back to index](./index.md)_
