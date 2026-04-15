using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;

namespace MultiTerminal.API.Controllers
{
    [ApiController]
    [Route("api/code-graph")]
    public class CodeGraphController : ControllerBase
    {
        private readonly MessageBroker _broker;

        public CodeGraphController(MessageBroker broker)
        {
            _broker = broker;
        }

        private CodeGraphDatabase GetDb() => _broker.CodeGraphDb;
        private CodeGraphQuery GetQuery() => _broker.CodeGraphQuery;

        /// <summary>
        /// Search symbols by name with optional type filter.
        /// GET /api/code-graph/search?query=MessageBroker&type=class
        /// </summary>
        [HttpGet("search")]
        public IActionResult Search([FromQuery] string query, [FromQuery] string type = null)
        {
            var q = GetQuery();
            if (q == null) return StatusCode(503, new { error = "CodeGraph not available" });
            if (string.IsNullOrWhiteSpace(query)) return BadRequest(new { error = "query is required" });

            var dt = q.FindSymbol(query, type);
            return Ok(new { success = true, count = dt.Rows.Count, results = DataTableToList(dt) });
        }

        /// <summary>
        /// Get direct callers of a symbol.
        /// GET /api/code-graph/callers?symbolId=42
        /// </summary>
        [HttpGet("callers")]
        public IActionResult GetCallers([FromQuery] long symbolId, [FromQuery] string symbolName = null)
        {
            var q = GetQuery();
            if (q == null) return StatusCode(503, new { error = "CodeGraph not available" });

            long id = ResolveSymbolId(symbolId, symbolName);
            if (id < 0) return NotFound(new { error = "Symbol not found" });

            var dt = q.GetCallers(id);
            return Ok(new { success = true, symbolId = id, count = dt.Rows.Count, results = DataTableToList(dt) });
        }

        /// <summary>
        /// Get direct callees of a symbol.
        /// GET /api/code-graph/callees?symbolId=42
        /// </summary>
        [HttpGet("callees")]
        public IActionResult GetCallees([FromQuery] long symbolId, [FromQuery] string symbolName = null)
        {
            var q = GetQuery();
            if (q == null) return StatusCode(503, new { error = "CodeGraph not available" });

            long id = ResolveSymbolId(symbolId, symbolName);
            if (id < 0) return NotFound(new { error = "Symbol not found" });

            var dt = q.GetCallees(id);
            return Ok(new { success = true, symbolId = id, count = dt.Rows.Count, results = DataTableToList(dt) });
        }

        /// <summary>
        /// Transitive impact analysis (blast radius).
        /// GET /api/code-graph/impact?symbolId=42&maxDepth=10
        /// </summary>
        [HttpGet("impact")]
        public IActionResult GetImpact([FromQuery] long symbolId, [FromQuery] string symbolName = null, [FromQuery] int maxDepth = 10)
        {
            var q = GetQuery();
            if (q == null) return StatusCode(503, new { error = "CodeGraph not available" });

            long id = ResolveSymbolId(symbolId, symbolName);
            if (id < 0) return NotFound(new { error = "Symbol not found" });

            var dt = q.GetImpact(id, maxDepth);
            return Ok(new { success = true, symbolId = id, count = dt.Rows.Count, results = DataTableToList(dt) });
        }

        /// <summary>
        /// Inheritance/implementation tree.
        /// GET /api/code-graph/inheritance?symbolId=42
        /// </summary>
        [HttpGet("inheritance")]
        public IActionResult GetInheritance([FromQuery] long symbolId, [FromQuery] string symbolName = null)
        {
            var q = GetQuery();
            if (q == null) return StatusCode(503, new { error = "CodeGraph not available" });

            long id = ResolveSymbolId(symbolId, symbolName);
            if (id < 0) return NotFound(new { error = "Symbol not found" });

            var dt = q.GetInheritanceTree(id);
            return Ok(new { success = true, symbolId = id, count = dt.Rows.Count, results = DataTableToList(dt) });
        }

        /// <summary>
        /// Dead code detection — unreferenced public/internal methods.
        /// GET /api/code-graph/dead-code?projectId=1
        /// </summary>
        [HttpGet("dead-code")]
        public IActionResult GetDeadCode([FromQuery] int? projectId = null)
        {
            var q = GetQuery();
            if (q == null) return StatusCode(503, new { error = "CodeGraph not available" });

            var dt = q.GetDeadCode(projectId);
            return Ok(new { success = true, count = dt.Rows.Count, results = DataTableToList(dt) });
        }

        /// <summary>
        /// All symbols in a file.
        /// GET /api/code-graph/file-symbols?filePath=Services/TaskDatabase.cs
        /// </summary>
        [HttpGet("file-symbols")]
        public IActionResult GetFileSymbols([FromQuery] string filePath)
        {
            var q = GetQuery();
            if (q == null) return StatusCode(503, new { error = "CodeGraph not available" });
            if (string.IsNullOrWhiteSpace(filePath)) return BadRequest(new { error = "filePath is required" });

            var dt = q.GetFileSymbols(filePath);
            return Ok(new { success = true, filePath, count = dt.Rows.Count, results = DataTableToList(dt) });
        }

        /// <summary>
        /// Trigger re-index of a directory.
        /// POST /api/code-graph/index { directory, projectName }
        /// </summary>
        [HttpPost("index")]
        public IActionResult Index([FromBody] IndexRequest request)
        {
            var db = GetDb();
            var q = GetQuery();
            if (db == null || q == null) return StatusCode(503, new { error = "CodeGraph not available" });
            if (string.IsNullOrEmpty(request?.Directory)) return BadRequest(new { error = "directory is required" });

            try
            {
                var indexer = new CSharpCodeGraphIndexer(db, q);
                var result = indexer.IndexDirectory(request.Directory, request.ProjectName);
                return Ok(new
                {
                    success = true,
                    result.ProjectName,
                    result.FileCount,
                    result.SymbolCount,
                    result.RelationshipCount,
                    result.DurationMs
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = $"Indexing failed: {ex.Message}" });
            }
        }

        /// <summary>
        /// Index statistics.
        /// GET /api/code-graph/stats
        /// </summary>
        [HttpGet("stats")]
        public IActionResult GetStats()
        {
            var q = GetQuery();
            if (q == null) return StatusCode(503, new { error = "CodeGraph not available" });

            var dt = q.GetStats();
            if (dt.Rows.Count == 0) return Ok(new { success = true, indexed = false });

            var row = dt.Rows[0];
            return Ok(new
            {
                success = true,
                indexed = true,
                projectCount = Convert.ToInt32(row["project_count"]),
                fileCount = Convert.ToInt32(row["file_count"]),
                classCount = Convert.ToInt32(row["class_count"]),
                interfaceCount = Convert.ToInt32(row["interface_count"]),
                methodCount = Convert.ToInt32(row["method_count"]),
                propertyCount = Convert.ToInt32(row["property_count"]),
                constructorCount = Convert.ToInt32(row["constructor_count"]),
                otherTypeCount = Convert.ToInt32(row["other_type_count"]),
                totalSymbols = Convert.ToInt32(row["total_symbols"]),
                relationshipCount = Convert.ToInt32(row["relationship_count"]),
                lastIndexed = row["last_indexed"]?.ToString(),
                durationMs = row["duration_ms"]?.ToString()
            });
        }

        // --- Helpers ---

        private long ResolveSymbolId(long symbolId, string symbolName)
        {
            if (symbolId > 0) return symbolId;
            if (!string.IsNullOrEmpty(symbolName))
                return GetDb()?.FindSymbolIdByName(symbolName) ?? -1;
            return -1;
        }

        private static List<Dictionary<string, object>> DataTableToList(DataTable dt)
        {
            return dt.Rows.Cast<DataRow>().Select(row =>
                dt.Columns.Cast<DataColumn>().ToDictionary(
                    col => col.ColumnName,
                    col => row[col] == DBNull.Value ? null : row[col]
                )
            ).ToList();
        }
    }

    public class IndexRequest
    {
        public string Directory { get; set; }
        public string ProjectName { get; set; }
    }
}
