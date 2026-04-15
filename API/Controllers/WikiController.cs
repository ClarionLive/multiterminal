using System;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;

namespace MultiTerminal.API.Controllers
{
    /// <summary>
    /// REST endpoints for the wiki generator. Generates per-subsystem markdown articles
    /// from the code graph + code digests and writes them to .claude/wiki/.
    /// </summary>
    [ApiController]
    [Route("api/wiki")]
    public class WikiController : ControllerBase
    {
        private readonly MessageBroker _broker;

        public WikiController(MessageBroker broker)
        {
            _broker = broker;
        }

        private WikiGeneratorService GetService() => _broker.WikiGenerator;

        private IActionResult ServiceUnavailable()
            => StatusCode(503, new { error = "WikiGeneratorService is not available" });

        /// <summary>
        /// Regenerate all wiki articles for a project.
        /// POST /api/wiki/generate
        /// Body: { projectRoot, projectId, subsystemId? }
        /// If subsystemId is provided, only that one is regenerated.
        /// </summary>
        [HttpPost("generate")]
        public IActionResult Generate([FromBody] GenerateRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ProjectRoot))
                return BadRequest(new { error = "projectRoot is required" });

            var service = GetService();
            if (service == null) return ServiceUnavailable();

            try
            {
                if (!string.IsNullOrWhiteSpace(request.SubsystemId))
                {
                    var article = service.GenerateOne(request.ProjectRoot, request.ProjectId, request.SubsystemId);
                    return Ok(new
                    {
                        success = true,
                        article = new
                        {
                            id = article.Id,
                            name = article.Name,
                            classCount = article.KeyClasses.Count,
                            methodCount = article.KeyMethods.Count,
                            routeCount = article.Routes.Count,
                            fileCount = article.Files.Count
                        }
                    });
                }

                var articles = service.GenerateAll(request.ProjectRoot, request.ProjectId);
                return Ok(new
                {
                    success = true,
                    count = articles.Count,
                    articles = articles.ConvertAll(a => new
                    {
                        id = a.Id,
                        name = a.Name,
                        classCount = a.KeyClasses.Count,
                        methodCount = a.KeyMethods.Count,
                        routeCount = a.Routes.Count,
                        fileCount = a.Files.Count,
                        markdownBytes = a.Markdown?.Length ?? 0
                    })
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// List all wiki articles currently on disk for a project.
        /// GET /api/wiki/articles?projectRoot=...
        /// </summary>
        [HttpGet("articles")]
        public IActionResult ListArticles([FromQuery] string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
                return BadRequest(new { error = "projectRoot query parameter is required" });

            var service = GetService();
            if (service == null) return ServiceUnavailable();

            try
            {
                var articles = service.ListArticles(projectRoot);
                return Ok(new
                {
                    count = articles.Count,
                    articles = articles.ConvertAll(a => new
                    {
                        id = a.Id,
                        name = a.Name,
                        description = a.Description,
                        tags = a.Tags,
                        generatedAt = a.GeneratedAt,
                        hasContent = !string.IsNullOrEmpty(a.Markdown)
                    })
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Fetch the markdown for a single wiki article.
        /// GET /api/wiki/articles/{id}?projectRoot=...
        /// </summary>
        [HttpGet("articles/{id}")]
        public IActionResult GetArticle(string id, [FromQuery] string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
                return BadRequest(new { error = "projectRoot query parameter is required" });

            var service = GetService();
            if (service == null) return ServiceUnavailable();

            var markdown = service.GetArticleMarkdown(projectRoot, id);
            if (markdown == null)
                return NotFound(new { error = $"Article '{id}' not found. Run POST /api/wiki/generate first." });

            return Ok(new { id, markdown });
        }

        public class GenerateRequest
        {
            public string ProjectRoot { get; set; }
            public string ProjectId { get; set; }
            public string SubsystemId { get; set; }
        }
    }
}
