using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;

namespace MultiTerminal.API.Controllers
{
    /// <summary>
    /// REST endpoints for vector-embedded session memory.
    /// Provides hybrid search (FTS5 + vector similarity) over session transcript chunks.
    /// </summary>
    [ApiController]
    [Route("api/session-memory")]
    public class SessionMemoryController : ControllerBase
    {
        private readonly MessageBroker _broker;

        public SessionMemoryController(MessageBroker broker)
        {
            _broker = broker;
        }

        private SessionMemoryDatabase GetDb() => _broker.SessionMemoryDb;

        /// <summary>
        /// Hybrid search over session memory chunks.
        /// GET /api/session-memory/search?query=&projectPath=&topK=10
        /// </summary>
        [HttpGet("search")]
        public IActionResult Search(
            [FromQuery] string query,
            [FromQuery] string projectPath = null,
            [FromQuery] int topK = 10,
            [FromQuery] string agentName = null)
        {
            var db = GetDb();
            if (db == null)
                return StatusCode(503, new { error = "SessionMemoryDatabase is not available" });

            if (string.IsNullOrWhiteSpace(query))
                return BadRequest(new { error = "query parameter is required" });

            if (topK <= 0 || topK > 100) topK = 10;

            var results = db.Search(query, projectPath, topK, agentName);

            return Ok(new
            {
                success = true,
                query,
                count = results.Count,
                results = results.ConvertAll(r => new
                {
                    r.SessionId,
                    r.TerminalName,
                    r.ProjectPath,
                    r.ChunkIndex,
                    r.ChunkText,
                    r.Score,
                    r.Timestamp,
                    r.Metadata
                })
            });
        }

        /// <summary>
        /// Index a specific session file.
        /// POST /api/session-memory/index { sessionFilePath, terminalName, projectPath }
        /// </summary>
        [HttpPost("index")]
        public IActionResult IndexSession([FromBody] IndexSessionRequest request)
        {
            var db = GetDb();
            if (db == null)
                return StatusCode(503, new { error = "SessionMemoryDatabase is not available" });

            if (string.IsNullOrEmpty(request?.SessionFilePath))
                return BadRequest(new { error = "sessionFilePath is required" });

            // Path traversal protection: only allow indexing files under known Claude project directories
            string fullPath = System.IO.Path.GetFullPath(request.SessionFilePath);
            string claudeDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
                + System.IO.Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(claudeDir, StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = "sessionFilePath must be within the Claude projects directory" });

            int chunks = db.IndexSessionFile(fullPath, request.TerminalName, request.ProjectPath);

            return Ok(new { success = true, chunksCreated = chunks });
        }

        /// <summary>
        /// Index all unindexed sessions for a project.
        /// POST /api/session-memory/index-project { projectPath, terminalName }
        /// </summary>
        [HttpPost("index-project")]
        public IActionResult IndexProject([FromBody] IndexProjectRequest request)
        {
            var db = GetDb();
            if (db == null)
                return StatusCode(503, new { error = "SessionMemoryDatabase is not available" });

            if (string.IsNullOrEmpty(request?.ProjectPath))
                return BadRequest(new { error = "projectPath is required" });

            int chunks = 0;
            try
            {
                chunks = db.IndexProjectSessions(request.ProjectPath, request.TerminalName);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SessionMemory] Index-project failed: {ex.Message}");
                return StatusCode(500, new { error = "Indexing failed" });
            }

            return Ok(new { success = true, chunksCreated = chunks });
        }

        /// <summary>
        /// Get indexing statistics.
        /// GET /api/session-memory/stats?projectPath=
        /// </summary>
        [HttpGet("stats")]
        public IActionResult GetStats([FromQuery] string projectPath = null)
        {
            var db = GetDb();
            if (db == null)
                return StatusCode(503, new { error = "SessionMemoryDatabase is not available" });

            var stats = db.GetStats(projectPath);
            return Ok(new
            {
                success = true,
                stats.TotalChunks,
                stats.IndexedSessions
            });
        }

        /// <summary>
        /// Find unindexed sessions for crash recovery.
        /// GET /api/session-memory/unindexed?projectPath=
        /// </summary>
        [HttpGet("unindexed")]
        public IActionResult GetUnindexed([FromQuery] string projectPath)
        {
            var db = GetDb();
            if (db == null)
                return StatusCode(503, new { error = "SessionMemoryDatabase is not available" });

            if (string.IsNullOrEmpty(projectPath))
                return BadRequest(new { error = "projectPath is required" });

            var files = db.FindUnindexedSessions(projectPath);
            return Ok(new { success = true, count = files.Count, files });
        }
    }

    public class IndexSessionRequest
    {
        public string SessionFilePath { get; set; }
        public string TerminalName { get; set; }
        public string ProjectPath { get; set; }
    }

    public class IndexProjectRequest
    {
        public string ProjectPath { get; set; }
        public string TerminalName { get; set; }
    }
}
