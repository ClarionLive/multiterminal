using System;
using System.IO;
using System.Text.RegularExpressions;
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
            => Problem(detail: "WikiGeneratorService is not available", statusCode: 503);

        // Resolve a caller-supplied projectRoot to a canonical absolute path that exists on disk.
        // Returns null on any failure; callers should respond 400. This collapses '..' segments and
        // normalizes separators, blocking the most basic path-traversal cases before the value
        // reaches File.* APIs downstream.
        private static string SafeProjectRoot(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                var full = Path.GetFullPath(raw);
                // CA3003: this IS the sanitizer — canonicalized path via GetFullPath, then existence check.
                // Returning null on miss prevents tainted values from reaching any File.* downstream.
#pragma warning disable CA3003
                return Directory.Exists(full) ? full : null;
#pragma warning restore CA3003
            }
            catch (ArgumentException) { return null; }
            catch (PathTooLongException) { return null; }
            catch (NotSupportedException) { return null; }
        }

        // Subsystem IDs are filename stems written by the generator; they must round-trip safely
        // through Path.Combine without enabling traversal. Restrict to a conservative alphabet so
        // backslashes / forward slashes / .. / colons / null bytes can never reach the File.* sink
        // in WikiGeneratorService.
        private static readonly Regex _safeSubsystemId = new Regex(@"^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);
        private static bool IsSafeSubsystemId(string id) => !string.IsNullOrEmpty(id) && _safeSubsystemId.IsMatch(id);

        /// <summary>
        /// Regenerate all wiki articles for a project.
        /// POST /api/wiki/generate
        /// Body: { projectRoot, projectId, subsystemId? }
        /// If subsystemId is provided, only that one is regenerated.
        /// </summary>
        [HttpPost("generate")]
        public IActionResult Generate([FromBody] GenerateRequest request)
        {
            if (request == null) return Problem(detail: "request body is required", statusCode: 400);
            var projectRoot = SafeProjectRoot(request.ProjectRoot);
            if (projectRoot == null)
                return Problem(detail: "projectRoot is required and must resolve to an existing directory", statusCode: 400);

            var service = GetService();
            if (service == null) return ServiceUnavailable();

            if (!string.IsNullOrWhiteSpace(request.SubsystemId) && !IsSafeSubsystemId(request.SubsystemId))
                return Problem(detail: "subsystemId must match ^[A-Za-z0-9_-]{1,64}$", statusCode: 400);

            try
            {
                if (!string.IsNullOrWhiteSpace(request.SubsystemId))
                {
                    var article = service.GenerateOne(projectRoot, request.ProjectId, request.SubsystemId);
                    return Ok(new
                    {
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

                var articles = service.GenerateAll(projectRoot, request.ProjectId);
                return Ok(new
                {
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
                return Problem(detail: ex.Message, statusCode: 500);
            }
        }

        /// <summary>
        /// List all wiki articles currently on disk for a project.
        /// GET /api/wiki/articles?projectRoot=...
        /// </summary>
        [HttpGet("articles")]
        public IActionResult ListArticles([FromQuery] string projectRoot)
        {
            var safeRoot = SafeProjectRoot(projectRoot);
            if (safeRoot == null)
                return Problem(detail: "projectRoot query parameter is required and must resolve to an existing directory", statusCode: 400);

            var service = GetService();
            if (service == null) return ServiceUnavailable();

            try
            {
                var articles = service.ListArticles(safeRoot);
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
                return Problem(detail: ex.Message, statusCode: 500);
            }
        }

        /// <summary>
        /// Fetch the markdown for a single wiki article.
        /// GET /api/wiki/articles/{id}?projectRoot=...
        /// </summary>
        [HttpGet("articles/{id}")]
        public IActionResult GetArticle(string id, [FromQuery] string projectRoot)
        {
            var safeRoot = SafeProjectRoot(projectRoot);
            if (safeRoot == null)
                return Problem(detail: "projectRoot query parameter is required and must resolve to an existing directory", statusCode: 400);
            if (!IsSafeSubsystemId(id))
                return Problem(detail: "id must match ^[A-Za-z0-9_-]{1,64}$", statusCode: 400);

            var service = GetService();
            if (service == null) return ServiceUnavailable();

            var markdown = service.GetArticleMarkdown(safeRoot, id);
            if (markdown == null)
                return Problem(detail: $"Article '{id}' not found. Run POST /api/wiki/generate first.", statusCode: 404);

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
