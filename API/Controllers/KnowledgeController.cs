using System.Collections.Generic;
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
            => StatusCode(503, new { error = "KnowledgeDatabase is not available" });

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
            return Ok(new { results, totalCount = results.Count });
        }

        /// <summary>
        /// Create a new knowledge entry.
        /// POST /api/knowledge
        /// </summary>
        [HttpPost]
        public IActionResult AddKnowledgeEntry([FromBody] KnowledgeEntry entry)
        {
            if (entry == null)
                return BadRequest(new { error = "Request body is required" });

            if (string.IsNullOrWhiteSpace(entry.Title))
                return BadRequest(new { error = "title is required" });

            if (string.IsNullOrWhiteSpace(entry.Content))
                return BadRequest(new { error = "content is required" });

            var service = GetService();
            if (service == null) return ServiceUnavailable();

            var id = service.AddKnowledgeEntry(entry);
            return Ok(new { success = true, id });
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
                return BadRequest(new { error = "At least one field is required" });

            var service = GetService();
            if (service == null) return ServiceUnavailable();

            int rows = service.UpdateKnowledgeEntry(id, fields);
            if (rows == 0)
                return NotFound(new { error = $"Knowledge entry {id} not found or no valid fields provided" });

            return Ok(new { success = true });
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
                return BadRequest(new { error = "filePath is required" });

            var service = GetService();
            if (service == null) return ServiceUnavailable();

            var digest = service.GetCodeDigest(projectId, filePath);
            if (digest == null)
                return NotFound(new { error = "No digest found for the given file" });

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
                return BadRequest(new { error = "Request body is required" });

            if (string.IsNullOrWhiteSpace(digest.FilePath))
                return BadRequest(new { error = "filePath is required" });

            var service = GetService();
            if (service == null) return ServiceUnavailable();

            var id = service.SaveCodeDigest(digest);
            return Ok(new { success = true, id });
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
}
