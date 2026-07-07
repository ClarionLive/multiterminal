using System;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;

namespace MultiTerminal.API.Controllers
{
    [ApiController]
    [Route("api/debug")]
    public class DebugController : ControllerBase
    {
        private readonly MessageBroker _broker;

        public DebugController(MessageBroker broker)
        {
            _broker = broker;
        }

        private DebugLogService DebugLog => _broker.DebugLogService;

        /// <summary>
        /// Get debug log entries with filtering and pagination.
        /// Query params: ?count=50&offset=0&source=MessageBroker&level=Info&search=delivery
        /// </summary>
        [HttpGet("logs")]
        public IActionResult GetLogs(
            [FromQuery] int count = 50,
            [FromQuery] int offset = 0,
            [FromQuery] string source = null,
            [FromQuery] string level = null,
            [FromQuery] string search = null)
        {
            if (DebugLog == null)
                return Problem(detail: "DebugLogService not initialized", statusCode: 503);

            var messages = DebugLog.GetMessages();

            if (!string.IsNullOrEmpty(source))
            {
                messages = messages
                    .Where(m => m.Source.Contains(source, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (!string.IsNullOrEmpty(level) && Enum.TryParse<Models.DebugLogLevel>(level, true, out var parsedLevel))
            {
                messages = messages
                    .Where(m => m.Level == parsedLevel)
                    .ToList();
            }

            if (!string.IsNullOrEmpty(search))
            {
                messages = messages
                    .Where(m => m.Message.Contains(search, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            // Order by most recent, apply offset and count
            var result = messages
                .OrderByDescending(m => m.Timestamp)
                .Skip(offset)
                .Take(count)
                .Select(m => new
                {
                    timestamp = m.Timestamp.ToString("HH:mm:ss.fff"),
                    source = m.Source,
                    level = m.Level.ToString(),
                    message = m.Message
                })
                .ToList();

            return Ok(new { total = messages.Count, offset, count = result.Count, entries = result });
        }

        /// <summary>
        /// Clear all debug log entries.
        /// </summary>
        [HttpDelete("logs")]
        public IActionResult ClearLogs()
        {
            if (DebugLog == null)
                return Problem(detail: "DebugLogService not initialized", statusCode: 503);

            var previousCount = DebugLog.Count;
            DebugLog.Clear();
            return Ok(new { message = "Debug logs cleared", previousCount });
        }

        /// <summary>
        /// Pause debug logging (new entries silently discarded).
        /// </summary>
        [HttpPost("pause")]
        public IActionResult Pause()
        {
            if (DebugLog == null)
                return Problem(detail: "DebugLogService not initialized", statusCode: 503);

            DebugLog.Pause();
            return Ok(new { isPaused = true });
        }

        /// <summary>
        /// Resume debug logging.
        /// </summary>
        [HttpPost("resume")]
        public IActionResult Resume()
        {
            if (DebugLog == null)
                return Problem(detail: "DebugLogService not initialized", statusCode: 503);

            DebugLog.Resume();
            return Ok(new { isPaused = false });
        }

        /// <summary>
        /// Get debug log status (count, paused state, capacity, current log file).
        /// </summary>
        [HttpGet("status")]
        public IActionResult GetStatus()
        {
            if (DebugLog == null)
                return Problem(detail: "DebugLogService not initialized", statusCode: 503);

            return Ok(new
            {
                count = DebugLog.Count,
                isPaused = DebugLog.IsPaused,
                maxCapacity = 10000,
                logFile = DebugLog.LogFilePath,
                logDirectory = DebugLogService.LogDirectory
            });
        }

        /// <summary>
        /// Cache-coherency check (P5 / 1df2a534): sample cached tasks and compare their persisted fields
        /// against the DB rows. Returns coherent=true with an empty divergences list under the single
        /// write path. Optional ?sampleSize=N. The HTTP surface CLAMPS the sample to [1, 500] (a request
        /// for 0/negative defaults to 50) so a loopback web page can't force an unbounded per-task DB scan
        /// (Codex security review). Internal/test callers use the broker method directly with sampleSize=0
        /// for a full scan.
        /// </summary>
        [HttpGet("cache-coherency")]
        public IActionResult GetCacheCoherency([FromQuery] int sampleSize = 50)
        {
            int clampedSampleSize = sampleSize <= 0 ? 50 : System.Math.Min(sampleSize, 500);
            var report = _broker.VerifyCacheCoherency(clampedSampleSize);
            return Ok(new
            {
                coherent = report.Coherent,
                cachedCount = report.CachedCount,
                checkedCount = report.Checked,
                divergences = report.Divergences
            });
        }

        /// <summary>
        /// List all available log files, most recent first.
        /// </summary>
        [HttpGet("files")]
        public IActionResult ListLogFiles()
        {
            var files = DebugLogService.ListLogFiles();
            return Ok(new
            {
                directory = DebugLogService.LogDirectory,
                currentFile = DebugLog?.LogFilePath,
                files = files.Select(f => new
                {
                    path = f,
                    name = Path.GetFileName(f),
                    size = new FileInfo(f).Length,
                    isCurrent = string.Equals(f, DebugLog?.LogFilePath, StringComparison.OrdinalIgnoreCase)
                }).ToList()
            });
        }

        /// <summary>
        /// Read a previous log file by filename. Defaults to previous session (index 1).
        /// Query params: ?file=debug-2026-03-08_17-01-42.log&lines=200&search=AgentPanel
        /// </summary>
        [HttpGet("files/{fileName}")]
        public IActionResult ReadLogFile(
            string fileName,
            [FromQuery] int lines = 200,
            [FromQuery] string search = null)
        {
            string filePath = Path.Combine(DebugLogService.LogDirectory, fileName);

            // Prevent path traversal
            if (!Path.GetFullPath(filePath).StartsWith(DebugLogService.LogDirectory, StringComparison.OrdinalIgnoreCase))
                return Problem(detail: "Invalid file name", statusCode: 400);

            // CA3003: filePath is already canonicalized by Path.GetFullPath above and confirmed to
            // be rooted in DebugLogService.LogDirectory; any traversal attempt was rejected with
            // BadRequest before reaching here.
#pragma warning disable CA3003
            if (!System.IO.File.Exists(filePath))
                return Problem(detail: $"Log file not found: {fileName}", statusCode: 404);
#pragma warning restore CA3003

            var logLines = DebugLogService.ReadLogFile(filePath, lines > 0 ? lines : 0);

            if (!string.IsNullOrEmpty(search))
            {
                logLines = logLines
                    .Where(l => l.Contains(search, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            return Ok(new
            {
                file = fileName,
                totalLines = logLines.Count,
                entries = logLines
            });
        }
    }
}
