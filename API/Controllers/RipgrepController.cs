using Microsoft.AspNetCore.Mvc;
using MultiTerminal.Services;

namespace MultiTerminal.API.Controllers
{
    [ApiController]
    [Route("api/search")]
    public class RipgrepController : ControllerBase
    {
        private readonly RipgrepService _ripgrep;

        public RipgrepController(RipgrepService ripgrep)
        {
            _ripgrep = ripgrep;
        }

        /// <summary>
        /// Search file contents using ripgrep.
        /// </summary>
        [HttpPost("content")]
        public IActionResult SearchContent([FromBody] SearchContentRequest request)
        {
            if (string.IsNullOrEmpty(request.Pattern))
                return BadRequest(new { error = "pattern is required" });
            if (string.IsNullOrEmpty(request.Path))
                return BadRequest(new { error = "path is required" });

            if (!_ripgrep.IsAvailable)
                return StatusCode(503, new { error = "rg.exe not available" });

            var options = new RipgrepOptions
            {
                CaseInsensitive = request.CaseInsensitive,
                Multiline = request.Multiline,
                FixedStrings = request.FixedStrings,
                Glob = request.Glob,
                FileType = request.FileType,
                MaxCount = request.MaxCount,
                Context = request.Context,
                Before = request.Before,
                After = request.After,
                FilesWithMatches = request.FilesWithMatches,
                Count = request.Count
            };

            var result = _ripgrep.Search(request.Pattern, request.Path, options);

            if (!result.Success)
                return BadRequest(new { error = result.Error });

            return Ok(new
            {
                success = true,
                matchCount = result.MatchCount,
                matches = result.Matches,
                stats = result.Stats
            });
        }

        /// <summary>
        /// Find files matching a glob pattern.
        /// </summary>
        [HttpPost("files")]
        public IActionResult FindFiles([FromBody] FindFilesRequest request)
        {
            if (string.IsNullOrEmpty(request.Path))
                return BadRequest(new { error = "path is required" });

            if (!_ripgrep.IsAvailable)
                return StatusCode(503, new { error = "rg.exe not available" });

            var result = _ripgrep.FindFiles(request.Path, request.Glob, request.FileType);

            if (!result.Success)
                return BadRequest(new { error = result.Error });

            return Ok(new
            {
                success = true,
                fileCount = result.Files.Count,
                files = result.Files
            });
        }

        /// <summary>
        /// Check if ripgrep is available.
        /// </summary>
        [HttpGet("status")]
        public IActionResult Status()
        {
            return Ok(new
            {
                available = _ripgrep.IsAvailable
            });
        }
    }

    public class SearchContentRequest
    {
        public string Pattern { get; set; }
        public string Path { get; set; }
        public bool CaseInsensitive { get; set; }
        public bool Multiline { get; set; }
        public bool FixedStrings { get; set; }
        public string Glob { get; set; }
        public string FileType { get; set; }
        public int MaxCount { get; set; }
        public int Context { get; set; }
        public int Before { get; set; }
        public int After { get; set; }
        public bool FilesWithMatches { get; set; }
        public bool Count { get; set; }
    }

    public class FindFilesRequest
    {
        public string Path { get; set; }
        public string Glob { get; set; }
        public string FileType { get; set; }
    }
}
