using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace MultiTerminal.API.Controllers
{
    [ApiController]
    [Route("api/digest")]
    public class DigestController : ControllerBase
    {
        private static readonly string DigestRoot = @"H:\DevLaptop\Projects\DailyDigest\digests";
        private static readonly string ProjectRoot = @"H:\DevLaptop\Projects\DailyDigest";
        private static int _regenerating = 0; // concurrency guard

        /// <summary>
        /// GET /api/digest/latest — Get the most recent digest
        /// </summary>
        [HttpGet("latest")]
        public IActionResult GetLatest()
        {
            try
            {
                if (!System.IO.Directory.Exists(DigestRoot))
                    return NotFound(new { error = "No digests found" });

                // Find the most recent date folder
                var dirs = System.IO.Directory.GetDirectories(DigestRoot)
                    .Select(d => System.IO.Path.GetFileName(d))
                    .Where(d => d.Length == 10) // YYYY-MM-DD format
                    .OrderByDescending(d => d)
                    .ToList();

                if (dirs.Count == 0)
                    return NotFound(new { error = "No digests found" });

                return GetDigestForDate(dirs[0]);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// GET /api/digest/{date} — Get digest for a specific date (YYYY-MM-DD)
        /// </summary>
        [HttpGet("{date}")]
        public IActionResult GetByDate(string date)
        {
            if (!IsValidDateSegment(date))
                return BadRequest(new { error = "Date must be YYYY-MM-DD format" });

            return GetDigestForDate(date);
        }

        /// <summary>
        /// Strict YYYY-MM-DD validator — rejects anything that is not exactly 10 chars of
        /// digits in positions 0-3/5-6/8-9 with '-' at positions 4 and 7. Guarantees no path
        /// separators, drive letters, or traversal sequences can reach <see cref="GetDigestForDate"/>.
        /// </summary>
        private static bool IsValidDateSegment(string date)
        {
            if (string.IsNullOrEmpty(date) || date.Length != 10) return false;
            for (int i = 0; i < 10; i++)
            {
                char c = date[i];
                if (i == 4 || i == 7)
                {
                    if (c != '-') return false;
                }
                else if (c < '0' || c > '9')
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// GET /api/digest/dates — List available digest dates
        /// </summary>
        [HttpGet("dates")]
        public IActionResult GetDates()
        {
            try
            {
                if (!System.IO.Directory.Exists(DigestRoot))
                    return Ok(new { dates = Array.Empty<string>() });

                var dates = System.IO.Directory.GetDirectories(DigestRoot)
                    .Select(d => System.IO.Path.GetFileName(d))
                    .Where(d => d.Length == 10)
                    .OrderByDescending(d => d)
                    .Take(30)
                    .ToArray();

                return Ok(new { dates });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// POST /api/digest/regenerate — Fetch fresh data and regenerate today's digest
        /// </summary>
        [HttpPost("regenerate")]
        public async Task<IActionResult> Regenerate()
        {
            // Only one regeneration at a time
            if (Interlocked.CompareExchange(ref _regenerating, 1, 0) != 0)
                return Conflict(new { error = "Regeneration already in progress" });

            try
            {
                var scriptPath = System.IO.Path.Combine(ProjectRoot, "src", "regenerate.js");
                if (!System.IO.File.Exists(scriptPath))
                    return StatusCode(500, new { error = "Regenerate script not found" });

                var psi = new ProcessStartInfo
                {
                    FileName = "node",
                    Arguments = $"\"{scriptPath}\"",
                    WorkingDirectory = ProjectRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var process = Process.Start(psi);
                if (process == null)
                    return StatusCode(500, new { error = "Failed to start regeneration process" });

                var stdout = await process.StandardOutput.ReadToEndAsync();
                var stderr = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                    return StatusCode(500, new { error = "Regeneration failed", details = stderr, output = stdout });

                // Return the fresh digest
                return GetLatest();
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
            finally
            {
                Interlocked.Exchange(ref _regenerating, 0);
            }
        }

        private IActionResult GetDigestForDate(string date)
        {
            // Defense-in-depth: even though GetByDate validates, GetLatest feeds in directory
            // names read off disk. Re-validate so every path into this method is strict
            // YYYY-MM-DD with no separators / traversal.
            if (!IsValidDateSegment(date))
                return BadRequest(new { error = "Invalid date" });

            var dateDir = System.IO.Path.Combine(DigestRoot, date);
            // CA3003: `date` is guaranteed strict YYYY-MM-DD by IsValidDateSegment above; no path
            // separators or traversal sequences can appear in dateDir.
#pragma warning disable CA3003
            if (!System.IO.Directory.Exists(dateDir))
                return NotFound(new { error = $"No digest for {date}" });
#pragma warning restore CA3003

            var digestPath = System.IO.Path.Combine(dateDir, "digest.md");
            var rawPath = System.IO.Path.Combine(dateDir, "raw-data.json");

            string digestMd = null;
            object rawStats = null;

            // CA3003: digestPath = DigestRoot + strict-YYYY-MM-DD + constant "digest.md"; all
            // components are validated/constant, no user input can steer the path.
#pragma warning disable CA3003
            if (System.IO.File.Exists(digestPath))
                digestMd = System.IO.File.ReadAllText(digestPath);
#pragma warning restore CA3003

            object[] sources = null;

            // CA3003: rawPath = DigestRoot + strict-YYYY-MM-DD + constant "raw-data.json"; same
            // sanitization argument as digestPath above. Applies to both File.Exists and
            // File.ReadAllText below.
#pragma warning disable CA3003
            if (System.IO.File.Exists(rawPath))
            {
                try
                {
                    var rawJson = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(rawPath));
#pragma warning restore CA3003

                    var root = rawJson.RootElement;

                    // Extract stats without sending the full raw data
                    rawStats = new
                    {
                        github_repos = CountArray(root, "github"),
                        reddit_subs = CountArray(root, "reddit"),
                        hn_stories = CountArray(root, "hn"),
                        web_sources = CountArray(root, "web"),
                    };

                    // Flatten all source items for headline→source matching in the frontend
                    sources = FlattenSources(root);
                }
                catch { /* ignore parse errors */ }
            }

            if (digestMd == null)
                return NotFound(new { error = $"Digest not yet generated for {date}" });

            return Ok(new
            {
                date,
                digest = digestMd,
                stats = rawStats,
                sources,
                generated_at = System.IO.File.GetLastWriteTimeUtc(digestPath).ToString("o"),
            });
        }

        private static int CountArray(System.Text.Json.JsonElement root, string prop)
        {
            if (root.TryGetProperty(prop, out var el) && el.ValueKind == System.Text.Json.JsonValueKind.Array)
                return el.GetArrayLength();
            return 0;
        }

        /// <summary>
        /// Flatten all source items from raw-data.json into a unified list for headline→source matching.
        /// </summary>
        private static object[] FlattenSources(System.Text.Json.JsonElement root)
        {
            var sources = new System.Collections.Generic.List<object>();

            // GitHub: repos → issues + releases
            if (root.TryGetProperty("github", out var gh) && gh.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var repo in gh.EnumerateArray())
                {
                    var repoName = repo.TryGetProperty("repo", out var rn) ? rn.GetString() : "";
                    if (repo.TryGetProperty("issues", out var issues) && issues.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var issue in issues.EnumerateArray())
                        {
                            sources.Add(new
                            {
                                title = issue.TryGetProperty("title", out var t) ? t.GetString() : "",
                                url = issue.TryGetProperty("url", out var u) ? u.GetString() : "",
                                type = "github",
                                repo = repoName,
                                score = (issue.TryGetProperty("reactions", out var r) ? r.GetInt32() : 0)
                                      + (issue.TryGetProperty("comments", out var c) ? c.GetInt32() : 0),
                            });
                        }
                    }
                    if (repo.TryGetProperty("releases", out var releases) && releases.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var rel in releases.EnumerateArray())
                        {
                            sources.Add(new
                            {
                                title = (rel.TryGetProperty("name", out var n) ? n.GetString() : "")
                                     ?? (rel.TryGetProperty("tag_name", out var tag) ? tag.GetString() : "Release"),
                                url = rel.TryGetProperty("html_url", out var u) ? u.GetString() : "",
                                type = "github",
                                repo = repoName,
                                score = 0,
                            });
                        }
                    }
                }
            }

            // Reddit: subreddits → posts
            if (root.TryGetProperty("reddit", out var rd) && rd.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var sub in rd.EnumerateArray())
                {
                    var subName = sub.TryGetProperty("subreddit", out var sn) ? sn.GetString() : "";
                    if (sub.TryGetProperty("posts", out var posts) && posts.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var post in posts.EnumerateArray())
                        {
                            sources.Add(new
                            {
                                title = post.TryGetProperty("title", out var t) ? t.GetString() : "",
                                url = post.TryGetProperty("url", out var u) ? u.GetString() : "",
                                type = "reddit",
                                repo = subName != null ? "r/" + subName : "",
                                score = post.TryGetProperty("score", out var s) ? s.GetInt32() : 0,
                            });
                        }
                    }
                }
            }

            // HN: flat array of stories
            if (root.TryGetProperty("hn", out var hn) && hn.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var story in hn.EnumerateArray())
                {
                    sources.Add(new
                    {
                        title = story.TryGetProperty("title", out var t) ? t.GetString() : "",
                        url = story.TryGetProperty("url", out var u) ? u.GetString() : "",
                        type = "hn",
                        repo = "",
                        score = story.TryGetProperty("score", out var s) ? s.GetInt32() : 0,
                    });
                }
            }

            // Web: flat array of pages
            if (root.TryGetProperty("web", out var web) && web.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var page in web.EnumerateArray())
                {
                    sources.Add(new
                    {
                        title = page.TryGetProperty("title", out var t) ? t.GetString() : "",
                        url = page.TryGetProperty("url", out var u) ? u.GetString() : "",
                        type = "web",
                        repo = "",
                        score = 0,
                    });
                }
            }

            return sources.ToArray();
        }
    }
}
