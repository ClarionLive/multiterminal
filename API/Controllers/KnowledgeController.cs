using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;

namespace MultiTerminal.API.Controllers
{
    /// <summary>
    /// REST endpoints for the Institutional Memory system — knowledge entries and code digests.
    /// Knowledge entries capture decisions, patterns, gotchas, and insights across sessions.
    /// Code digests provide structured summaries of source files for fast agent orientation.
    /// </summary>
    [ApiController]
    [Route("api/knowledge")]
    public class KnowledgeController : ControllerBase
    {
        private readonly MessageBroker _broker;

        public KnowledgeController(MessageBroker broker)
        {
            _broker = broker;
        }

        private KnowledgeDatabase GetService() => _broker.KnowledgeDb;

        private IActionResult ServiceUnavailable()
            => Problem(detail: "KnowledgeDatabase is not available", statusCode: 503);

        // ── Knowledge entries ──────────────────────────────────────────────────

        /// <summary>
        /// Search knowledge entries by text query, category, project, and tags.
        /// GET /api/knowledge/search?query=&category=&projectId=&tags=&limit=20
        /// </summary>
        [HttpGet("search")]
        public IActionResult SearchKnowledge(
            [FromQuery] string query,
            [FromQuery] string category,
            [FromQuery] string projectId,
            [FromQuery] string tags,
            [FromQuery] int limit = 20)
        {
            if (limit <= 0 || limit > 500) limit = 20;

            var service = GetService();
            if (service == null) return ServiceUnavailable();

            var results = service.SearchKnowledge(query, category, projectId, tags, limit: limit);

            // Bump attention decay counters for returned entries
            if (results.Count > 0)
            {
                try { service.BumpReferences(results.ConvertAll(r => r.Id)); }
                catch (Exception ex) { _broker?.DebugLogService?.Error("KnowledgeController", $"BumpReferences failed: {ex.Message}"); }
            }

            return Ok(new { results, totalCount = results.Count });
        }

        /// <summary>
        /// Get decay-ranked knowledge entries for context injection.
        /// Returns pre-formatted markdown with tiered injection: top 10 get full content preview,
        /// next 5 get title-only. Bumps reference counts for all returned entries.
        /// GET /api/knowledge/inject?limit=15
        /// </summary>
        [HttpGet("inject")]
        public IActionResult GetDecayRankedInjection([FromQuery] int limit = 15)
        {
            var service = GetService();
            if (service == null) return ServiceUnavailable();

            try
            {
                var entries = service.GetDecayRanked(limit);
                if (entries.Count == 0)
                    return Ok(new { markdown = "", entryCount = 0 });

                // Bump references for all returned entries
                try { service.BumpReferences(entries.ConvertAll(e => e.Id)); }
                catch { /* non-fatal */ }

                // Build tiered markdown: top 10 full, next 5 title-only
                var sb = new StringBuilder();
                sb.AppendLine("# Project Knowledge (auto-injected)");
                sb.AppendLine();

                int fullCount = Math.Min(10, entries.Count);
                for (int i = 0; i < fullCount; i++)
                {
                    var e = entries[i];
                    string preview = e.Content != null && e.Content.Length > 200
                        ? e.Content.Substring(0, 200) + "..."
                        : e.Content ?? "";
                    sb.AppendLine($"- **[{e.Category}] {e.Title}**: {preview}");
                }

                if (entries.Count > fullCount)
                {
                    sb.AppendLine();
                    sb.AppendLine("_Also available (use query_knowledge for details):_");
                    for (int i = fullCount; i < entries.Count; i++)
                    {
                        sb.AppendLine($"- [{entries[i].Category}] {entries[i].Title}");
                    }
                }

                return Ok(new { markdown = sb.ToString(), entryCount = entries.Count });
            }
            catch (Exception ex)
            {
                return Problem(detail: ex.Message, statusCode: 500);
            }
        }

        /// <summary>
        /// Create a new knowledge entry.
        /// POST /api/knowledge
        /// </summary>
        [HttpPost]
        public IActionResult AddKnowledgeEntry([FromBody] KnowledgeEntry entry)
        {
            if (entry == null)
                return Problem(detail: "Request body is required", statusCode: 400);

            if (string.IsNullOrWhiteSpace(entry.Title))
                return Problem(detail: "title is required", statusCode: 400);

            if (string.IsNullOrWhiteSpace(entry.Content))
                return Problem(detail: "content is required", statusCode: 400);

            var service = GetService();
            if (service == null) return ServiceUnavailable();

            var id = service.AddKnowledgeEntry(entry);
            return Ok(new { id });
        }

        /// <summary>
        /// Update an existing knowledge entry by ID.
        /// Accepted field keys: category, title, content, tags, confidence, superseded_by.
        /// PUT /api/knowledge/{id}
        /// Body: { "category": "...", "confidence": "confirmed", ... }
        /// </summary>
        [HttpPut("{id:int}")]
        public IActionResult UpdateKnowledgeEntry(int id, [FromBody] Dictionary<string, string> fields)
        {
            if (fields == null || fields.Count == 0)
                return Problem(detail: "At least one field is required", statusCode: 400);

            var service = GetService();
            if (service == null) return ServiceUnavailable();

            int rows = service.UpdateKnowledgeEntry(id, fields);
            if (rows == 0)
                return Problem(detail: $"Knowledge entry {id} not found or no valid fields provided", statusCode: 404);

            return Ok();
        }

        /// <summary>
        /// Bump attention decay counters for a batch of knowledge entry IDs.
        /// Called by session-status-hook after injecting entries into agent context.
        /// POST /api/knowledge/bump
        /// Body: { "ids": [1, 2, 3] }
        /// </summary>
        [HttpPost("bump")]
        public IActionResult BumpReferences([FromBody] BumpReferencesRequest request)
        {
            if (request?.Ids == null || request.Ids.Count == 0)
                return Problem(detail: "ids array is required", statusCode: 400);

            if (request.Ids.Count > 100)
                return Problem(detail: "ids array exceeds maximum of 100 entries", statusCode: 400);

            var service = GetService();
            if (service == null) return ServiceUnavailable();

            service.BumpReferences(request.Ids);
            return Ok(new { bumped = request.Ids.Count });
        }

        // ── Research cache ────────────────────────────────────────────────────

        /// <summary>
        /// Look up cached web research by query text. Returns the cached entry if found.
        /// GET /api/knowledge/research-cache?query=...
        /// </summary>
        [HttpGet("research-cache")]
        public IActionResult LookupResearchCache([FromQuery] string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return Problem(detail: "query is required", statusCode: 400);

            var service = GetService();
            if (service == null) return ServiceUnavailable();

            string hash = ComputeQueryHash(query);
            var entry = service.LookupResearchCache(hash);

            if (entry == null)
            {
                // Also try FTS5 search with web_research category for fuzzy matches
                var ftsResults = service.SearchKnowledge(query, category: "web_research", limit: 3);
                if (ftsResults.Count > 0)
                    return Ok(new { hit = true, source = "fts", results = ftsResults });

                return Ok(new { hit = false });
            }

            return Ok(new { hit = true, source = "exact", result = entry });
        }

        /// <summary>
        /// Save a web research result to the knowledge cache.
        /// POST /api/knowledge/research-cache
        /// Body: { "query": "...", "title": "...", "content": "...", "sourceAgent": "...", "sourceUrl": "..." }
        /// </summary>
        [HttpPost("research-cache")]
        public IActionResult SaveResearchCache([FromBody] ResearchCacheRequest request)
        {
            if (request == null)
                return Problem(detail: "Request body is required", statusCode: 400);
            if (string.IsNullOrWhiteSpace(request.Query))
                return Problem(detail: "query is required", statusCode: 400);
            if (string.IsNullOrWhiteSpace(request.Content))
                return Problem(detail: "content is required", statusCode: 400);

            // Server-side content size limit (defense-in-depth — hook truncates to 4000 but direct callers bypass)
            if (request.Content.Length > 10000)
                request.Content = request.Content.Substring(0, 10000);

            var service = GetService();
            if (service == null) return ServiceUnavailable();

            string hash = ComputeQueryHash(request.Query);

            // Dedup: skip if exact hash already exists
            if (service.ResearchCacheExists(hash))
                return Ok(new { deduplicated = true, message = "Research already cached" });

            var entry = new KnowledgeEntry
            {
                Category = "web_research",
                Title = request.Title ?? request.Query,
                Content = request.Content,
                SourceType = "auto_cache",
                SourceId = request.SourceUrl,
                SourceAgent = request.SourceAgent,
                Tags = request.Tags ?? "auto-cached",
                Confidence = "likely",
                QueryHash = hash
            };

            var id = service.AddKnowledgeEntry(entry);
            return Ok(new { id, queryHash = hash });
        }

        /// <summary>
        /// Compute a SHA256 hash of a normalized query string for deduplication.
        /// Normalizes: lowercase, trim, collapse whitespace.
        /// </summary>
        private static string ComputeQueryHash(string query)
        {
            var normalized = System.Text.RegularExpressions.Regex.Replace(
                query.Trim().ToLowerInvariant(), @"\s+", " ");
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        // ── Code digests ───────────────────────────────────────────────────────

        /// <summary>
        /// Get the code digest for a specific file.
        /// GET /api/knowledge/digest?filePath=&projectId=
        /// </summary>
        [HttpGet("digest")]
        public IActionResult GetCodeDigest(
            [FromQuery] string filePath,
            [FromQuery] string projectId)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return Problem(detail: "filePath is required", statusCode: 400);

            var service = GetService();
            if (service == null) return ServiceUnavailable();

            var digest = service.GetCodeDigest(projectId, filePath);
            if (digest == null)
                return Problem(detail: "No digest found for the given file", statusCode: 404);

            return Ok(digest);
        }

        /// <summary>
        /// Save or update the code digest for a file.
        /// POST /api/knowledge/digest
        /// </summary>
        [HttpPost("digest")]
        public IActionResult SaveCodeDigest([FromBody] CodeDigest digest)
        {
            if (digest == null)
                return Problem(detail: "Request body is required", statusCode: 400);

            if (string.IsNullOrWhiteSpace(digest.FilePath))
                return Problem(detail: "filePath is required", statusCode: 400);

            var service = GetService();
            if (service == null) return ServiceUnavailable();

            var id = service.SaveCodeDigest(digest);
            return Ok(new { id });
        }

        /// <summary>
        /// Return digests whose file hash has changed (stale digests).
        /// Caller provides a dictionary of filePath → currentHash in the request body.
        /// POST /api/knowledge/digest/stale?projectId=
        /// Body: { "fileHashes": { "path": "hash", ... } }
        /// </summary>
        [HttpPost("digest/stale")]
        public IActionResult GetStaleDigests(
            [FromQuery] string projectId,
            [FromBody] StaleDigestRequest request)
        {
            var service = GetService();
            if (service == null) return ServiceUnavailable();

            var stale = service.GetStaleDigests(projectId, request?.FileHashes ?? new Dictionary<string, string>());
            return Ok(new { stale, count = stale.Count });
        }
    }

    // Request/response models

    public class StaleDigestRequest
    {
        /// <summary>Map of filePath → currentSHA256 to check against stored digests.</summary>
        public Dictionary<string, string> FileHashes { get; set; }
    }

    public class BumpReferencesRequest
    {
        public List<int> Ids { get; set; }
    }

    public class ResearchCacheRequest
    {
        public string Query { get; set; }
        public string Title { get; set; }
        public string Content { get; set; }
        public string SourceAgent { get; set; }
        public string SourceUrl { get; set; }
        public string Tags { get; set; }
    }
}
