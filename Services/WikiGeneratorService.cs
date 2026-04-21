using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MultiTerminal.MCPServer.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Generates per-subsystem markdown wiki articles from the code graph + code digests + controller routes.
    /// Articles are written to .claude/wiki/ and meant to give agents targeted session-start context
    /// (~200 token index + ~500 tokens per subsystem article).
    /// </summary>
    public class WikiGeneratorService
    {
        private readonly CodeGraphQuery _codeGraph;
        private readonly KnowledgeDatabase _knowledgeDb;

        public WikiGeneratorService(CodeGraphQuery codeGraph, KnowledgeDatabase knowledgeDb)
        {
            _codeGraph = codeGraph;
            _knowledgeDb = knowledgeDb;
        }

        /// <summary>
        /// Regenerate all wiki articles for a project. Writes to {projectRoot}/.claude/wiki/.
        /// Returns the list of articles written.
        /// </summary>
        public List<WikiArticle> GenerateAll(string projectRoot, string projectId)
        {
            var manifest = LoadManifest(projectRoot);
            if (manifest == null || manifest.Subsystems == null || manifest.Subsystems.Count == 0)
                throw new InvalidOperationException($"No wiki-manifest.json found at {Path.Combine(projectRoot, ".claude", "wiki", "wiki-manifest.json")}");

            var articles = new List<WikiArticle>();
            var fileToSubsystem = BuildFileToSubsystemMap(manifest, projectRoot);

            foreach (var subsystem in manifest.Subsystems)
            {
                var article = BuildArticle(subsystem, projectRoot, projectId, fileToSubsystem);
                articles.Add(article);
            }

            WriteArticles(projectRoot, articles, manifest);
            return articles;
        }

        /// <summary>
        /// Regenerate a single article by subsystem ID.
        /// </summary>
        public WikiArticle GenerateOne(string projectRoot, string projectId, string subsystemId)
        {
            var manifest = LoadManifest(projectRoot);
            var subsystem = manifest.Subsystems.FirstOrDefault(s =>
                string.Equals(s.Id, subsystemId, StringComparison.OrdinalIgnoreCase));
            if (subsystem == null)
                throw new ArgumentException($"Subsystem '{subsystemId}' not found in manifest");

            var fileToSubsystem = BuildFileToSubsystemMap(manifest, projectRoot);
            var article = BuildArticle(subsystem, projectRoot, projectId, fileToSubsystem);

            var wikiDir = Path.Combine(projectRoot, ".claude", "wiki");
            Directory.CreateDirectory(wikiDir);
            File.WriteAllText(Path.Combine(wikiDir, subsystem.Id + ".md"), article.Markdown);
            return article;
        }

        /// <summary>List articles currently present in .claude/wiki/ without regenerating.</summary>
        public List<WikiArticle> ListArticles(string projectRoot)
        {
            var result = new List<WikiArticle>();
            var manifest = LoadManifest(projectRoot);
            if (manifest?.Subsystems == null) return result;

            var wikiDir = Path.Combine(projectRoot, ".claude", "wiki");
            foreach (var sub in manifest.Subsystems)
            {
                var path = Path.Combine(wikiDir, sub.Id + ".md");
                var exists = File.Exists(path);
                result.Add(new WikiArticle
                {
                    Id = sub.Id,
                    Name = sub.Name,
                    Description = sub.Description,
                    Tags = sub.Tags ?? new List<string>(),
                    GeneratedAt = exists ? File.GetLastWriteTimeUtc(path).ToString("o") : null,
                    Markdown = exists ? File.ReadAllText(path) : null
                });
            }
            return result;
        }

        /// <summary>Read a single article's markdown from disk.</summary>
        public string GetArticleMarkdown(string projectRoot, string subsystemId)
        {
            var path = Path.Combine(projectRoot, ".claude", "wiki", subsystemId + ".md");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        private WikiManifest LoadManifest(string projectRoot)
        {
            var path = Path.Combine(projectRoot, ".claude", "wiki", "wiki-manifest.json");
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            };
            return JsonSerializer.Deserialize<WikiManifest>(json, options);
        }

        private Dictionary<string, string> BuildFileToSubsystemMap(WikiManifest manifest, string projectRoot)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sub in manifest.Subsystems)
            {
                foreach (var file in ExpandRootFiles(sub, projectRoot))
                {
                    if (!map.ContainsKey(file)) map[file] = sub.Id;
                }
            }
            return map;
        }

        private List<string> ExpandRootFiles(SubsystemDefinition sub, string projectRoot)
        {
            var files = new List<string>(sub.RootFiles ?? new List<string>());
            if (!string.IsNullOrEmpty(sub.ControllerGlob))
            {
                var globDir = Path.Combine(projectRoot, Path.GetDirectoryName(sub.ControllerGlob.Replace('/', Path.DirectorySeparatorChar)) ?? "");
                var pattern = Path.GetFileName(sub.ControllerGlob);
                if (Directory.Exists(globDir))
                {
                    foreach (var f in Directory.GetFiles(globDir, pattern))
                    {
                        var rel = Path.GetRelativePath(projectRoot, f).Replace('\\', '/');
                        if (!files.Contains(rel, StringComparer.OrdinalIgnoreCase))
                            files.Add(rel);
                    }
                }
            }
            return files;
        }

        private WikiArticle BuildArticle(
            SubsystemDefinition sub,
            string projectRoot,
            string projectId,
            Dictionary<string, string> fileToSubsystem)
        {
            var article = new WikiArticle
            {
                Id = sub.Id,
                Name = sub.Name,
                Description = sub.Description,
                Tags = sub.Tags ?? new List<string>(),
                GeneratedAt = DateTime.UtcNow.ToString("o")
            };

            var rootFiles = ExpandRootFiles(sub, projectRoot);
            var dependsOn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var gotchasSet = new HashSet<string>();

            foreach (var relFile in rootFiles)
            {
                var absFile = Path.Combine(projectRoot, relFile.Replace('/', Path.DirectorySeparatorChar));
                var absFileForward = absFile.Replace('\\', '/');
                var lineCount = File.Exists(absFile) ? File.ReadAllLines(absFile).Length : 0;

                // Enrich with code_digest if available (code_digests uses backslash paths)
                var digest = SafeGetDigest(projectId, absFile)
                    ?? SafeGetDigest(projectId, absFileForward)
                    ?? SafeGetDigest(projectId, relFile);
                var purpose = digest?.Purpose;
                if (!string.IsNullOrEmpty(digest?.Gotchas))
                {
                    foreach (var line in digest.Gotchas.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var trimmed = line.TrimStart('-', ' ', '*').Trim();
                        if (trimmed.Length > 0) gotchasSet.Add(trimmed);
                    }
                }

                article.Files.Add(new WikiFileEntry
                {
                    FilePath = relFile,
                    LineCount = lineCount,
                    Purpose = purpose
                });

                // Query symbols for this file from code graph (cg_symbols uses forward-slash paths)
                // CA2000: `??` short-circuit guarantees at most one non-null DataTable is created; `using var` disposes it. Analyzer can't track short-circuit semantics across chained SafeGetFileSymbols calls.
#pragma warning disable CA2000
                using var symbols = SafeGetFileSymbols(absFileForward)
                    ?? SafeGetFileSymbols(absFile)
                    ?? SafeGetFileSymbols(relFile);
#pragma warning restore CA2000
                if (symbols != null)
                {
                    CollectSymbols(article, symbols, relFile);
                }

                // Parse routes if this looks like a controller
                if (relFile.Contains("Controllers", StringComparison.OrdinalIgnoreCase) && File.Exists(absFile))
                {
                    CollectRoutes(article, absFile, relFile);
                }
            }

            // External callers: for each key class/method, find callers outside this subsystem
            CollectExternalCallersAndDeps(article, rootFiles, fileToSubsystem, dependsOn);
            article.DependsOn = dependsOn.OrderBy(x => x).ToList();
            article.Gotchas = gotchasSet.Take(10).ToList();

            article.Markdown = RenderMarkdown(article);
            return article;
        }

        private CodeDigest SafeGetDigest(string projectId, string filePath)
        {
            try { return _knowledgeDb?.GetCodeDigest(projectId, filePath); }
            catch { return null; }
        }

        private DataTable SafeGetFileSymbols(string filePath)
        {
            try { return _codeGraph?.GetFileSymbols(filePath); }
            catch { return null; }
        }

        private void CollectSymbols(WikiArticle article, DataTable symbols, string relFile)
        {
            foreach (DataRow row in symbols.Rows)
            {
                var type = row["type"]?.ToString() ?? "";
                var name = row["name"]?.ToString() ?? "";
                var line = row["line_number"] is long l ? (int)l : Convert.ToInt32(row["line_number"] ?? 0);
                var entry = new WikiSymbolEntry
                {
                    Name = name,
                    FilePath = relFile,
                    LineNumber = line,
                    SymbolType = type
                };

                if (type == "class" || type == "interface" || type == "struct" || type == "enum")
                {
                    article.KeyClasses.Add(entry);
                }
                else if (type == "method" && IsInterestingMethod(row))
                {
                    // Qualify method name with class
                    var memberOf = row.Table.Columns.Contains("member_of") ? row["member_of"]?.ToString() : "";
                    if (!string.IsNullOrEmpty(memberOf))
                        entry.Name = memberOf + "." + name;
                    article.KeyMethods.Add(entry);
                }
            }
        }

        private bool IsInterestingMethod(DataRow row)
        {
            var accessibility = row.Table.Columns.Contains("accessibility") ? row["accessibility"]?.ToString() : "";
            if (accessibility != "public" && accessibility != "internal") return false;
            var name = row["name"]?.ToString() ?? "";
            if (name.StartsWith("get_") || name.StartsWith("set_")) return false;
            if (name == "ToString" || name == "Equals" || name == "GetHashCode" || name == "Dispose") return false;
            return true;
        }

        private static readonly Regex RouteAttributeRegex = new Regex(
            @"\[Http(Get|Post|Put|Delete|Patch)(?:\(""([^""]*)""\))?\]",
            RegexOptions.Compiled);

        private static readonly Regex RouteBaseRegex = new Regex(
            @"\[Route\(""([^""]*)""\)\]",
            RegexOptions.Compiled);

        private void CollectRoutes(WikiArticle article, string absFile, string relFile)
        {
            var lines = File.ReadAllLines(absFile);
            string baseRoute = "";
            var baseMatch = RouteBaseRegex.Match(string.Join("\n", lines));
            if (baseMatch.Success) baseRoute = baseMatch.Groups[1].Value.Replace("[controller]", GetControllerName(relFile));

            for (int i = 0; i < lines.Length; i++)
            {
                var match = RouteAttributeRegex.Match(lines[i]);
                if (!match.Success) continue;
                var method = match.Groups[1].Value.ToUpperInvariant();
                var sub = match.Groups[2].Success ? match.Groups[2].Value : "";
                var path = CombineRoute(baseRoute, sub);
                article.Routes.Add(new WikiRouteEntry
                {
                    HttpMethod = method,
                    Path = path,
                    FilePath = relFile,
                    LineNumber = i + 1
                });
            }
        }

        private string GetControllerName(string filePath)
        {
            var name = Path.GetFileNameWithoutExtension(filePath);
            if (name.EndsWith("Controller")) name = name.Substring(0, name.Length - "Controller".Length);
            return name.ToLowerInvariant();
        }

        private string CombineRoute(string baseRoute, string sub)
        {
            if (string.IsNullOrEmpty(sub)) return "/" + baseRoute.TrimStart('/');
            if (string.IsNullOrEmpty(baseRoute)) return "/" + sub.TrimStart('/');
            return "/" + baseRoute.TrimStart('/').TrimEnd('/') + "/" + sub.TrimStart('/');
        }

        private void CollectExternalCallersAndDeps(
            WikiArticle article,
            List<string> rootFiles,
            Dictionary<string, string> fileToSubsystem,
            HashSet<string> dependsOn)
        {
            var rootFileSet = new HashSet<string>(rootFiles, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>();

            // Sample: use the first 5 key classes/methods for caller lookups (perf bound)
            var sampleSymbols = article.KeyClasses.Take(5)
                .Concat(article.KeyMethods.Take(10))
                .ToList();

            foreach (var sym in sampleSymbols)
            {
                var symbolIds = SafeFindSymbolIds(sym.Name);
                foreach (var id in symbolIds)
                {
                    // CA2000: analyzer false-positive on private method returning IDisposable via try/catch; `using var` disposes on all paths.
#pragma warning disable CA2000
                    using var callers = SafeGetCallers(id);
#pragma warning restore CA2000
                    if (callers == null) continue;

                    foreach (DataRow row in callers.Rows)
                    {
                        var callFile = row.Table.Columns.Contains("call_file") ? row["call_file"]?.ToString() : "";
                        if (string.IsNullOrEmpty(callFile)) continue;

                        // Is this caller inside our subsystem or outside?
                        var callRel = MakeRelative(callFile);
                        if (rootFileSet.Contains(callRel)) continue;

                        var callerName = row["name"]?.ToString() ?? "";
                        var callerMember = row.Table.Columns.Contains("member_of") ? row["member_of"]?.ToString() : "";
                        var display = string.IsNullOrEmpty(callerMember) ? callerName : callerMember + "." + callerName;
                        var key = display + "|" + callRel;
                        if (seen.Add(key) && article.ExternalCallers.Count < 20)
                        {
                            var callLine = row["call_line"] is long l ? (int)l : Convert.ToInt32(row["call_line"] ?? 0);
                            article.ExternalCallers.Add(new WikiSymbolEntry
                            {
                                Name = display,
                                FilePath = callRel,
                                LineNumber = callLine,
                                SymbolType = row["type"]?.ToString() ?? "method"
                            });
                        }

                        // Depends-on tracking: if caller belongs to another subsystem, they depend on us
                        if (fileToSubsystem.TryGetValue(callRel, out var callerSubId) && callerSubId != article.Id)
                        {
                            // Reverse: callerSubId depends on article.Id. We track the dependency direction here:
                            // article.UsedBy gets callerSubId. But we'll compute UsedBy via the "dependsOn" pass
                            // after all articles are generated. For now, just note that callerSubId uses us.
                        }
                    }
                }
            }
        }

        private List<long> SafeFindSymbolIds(string name)
        {
            var ids = new List<long>();
            try
            {
                var justName = name.Contains('.') ? name.Substring(name.LastIndexOf('.') + 1) : name;
                using var result = _codeGraph?.FindSymbol(justName);
                if (result == null) return ids;
                foreach (DataRow row in result.Rows)
                {
                    if (row["id"] is long id) ids.Add(id);
                    else ids.Add(Convert.ToInt64(row["id"]));
                }
            }
            catch { }
            return ids;
        }

        private DataTable SafeGetCallers(long id)
        {
            try { return _codeGraph?.GetCallers(id); }
            catch { return null; }
        }

        private string MakeRelative(string absPath)
        {
            // Normalize to forward slashes; try to strip common project root prefixes
            var normalized = absPath.Replace('\\', '/');
            var marker = "/MultiTerminal/";
            var idx = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return normalized.Substring(idx + marker.Length);
            return normalized;
        }

        private string RenderMarkdown(WikiArticle article)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# {article.Name}");
            sb.AppendLine();
            sb.AppendLine($"> {article.Description}");
            sb.AppendLine();
            if (article.Tags.Count > 0)
            {
                sb.AppendLine("**Tags:** " + string.Join(", ", article.Tags.Select(t => "`" + t + "`")));
                sb.AppendLine();
            }

            sb.AppendLine("## Key Files");
            sb.AppendLine();
            foreach (var f in article.Files.OrderByDescending(x => x.LineCount))
            {
                var lineTag = f.LineCount > 0 ? $" ({f.LineCount} LOC)" : "";
                sb.AppendLine($"- `{f.FilePath}`{lineTag}");
                if (!string.IsNullOrEmpty(f.Purpose))
                    sb.AppendLine($"  - {f.Purpose}");
            }
            sb.AppendLine();

            if (article.KeyClasses.Count > 0)
            {
                sb.AppendLine("## Key Classes");
                sb.AppendLine();
                foreach (var c in article.KeyClasses.Take(20))
                    sb.AppendLine($"- **{c.Name}** ({c.SymbolType}) — `{c.FilePath}:{c.LineNumber}`");
                sb.AppendLine();
            }

            if (article.KeyMethods.Count > 0)
            {
                sb.AppendLine("## Key Methods");
                sb.AppendLine();
                foreach (var m in article.KeyMethods.Take(25))
                    sb.AppendLine($"- `{m.Name}` — `{m.FilePath}:{m.LineNumber}`");
                sb.AppendLine();
            }

            if (article.Routes.Count > 0)
            {
                sb.AppendLine("## Routes");
                sb.AppendLine();
                foreach (var r in article.Routes.Take(40))
                    sb.AppendLine($"- `{r.HttpMethod}` `{r.Path}` — `{r.FilePath}:{r.LineNumber}`");
                if (article.Routes.Count > 40)
                    sb.AppendLine($"- _...and {article.Routes.Count - 40} more_");
                sb.AppendLine();
            }

            if (article.ExternalCallers.Count > 0)
            {
                sb.AppendLine("## External Callers");
                sb.AppendLine();
                sb.AppendLine("> Code outside this subsystem that calls into it.");
                sb.AppendLine();
                foreach (var c in article.ExternalCallers.Take(15))
                    sb.AppendLine($"- `{c.Name}` — `{c.FilePath}:{c.LineNumber}`");
                sb.AppendLine();
            }

            if (article.Gotchas.Count > 0)
            {
                sb.AppendLine("## Gotchas");
                sb.AppendLine();
                foreach (var g in article.Gotchas)
                    sb.AppendLine($"- {g}");
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine($"_Generated {article.GeneratedAt} · [Back to index](./index.md)_");
            return sb.ToString();
        }

        private void WriteArticles(string projectRoot, List<WikiArticle> articles, WikiManifest manifest)
        {
            var wikiDir = Path.Combine(projectRoot, ".claude", "wiki");
            Directory.CreateDirectory(wikiDir);

            foreach (var article in articles)
            {
                var path = Path.Combine(wikiDir, article.Id + ".md");
                File.WriteAllText(path, article.Markdown);
            }

            // Write index.md
            var index = new StringBuilder();
            index.AppendLine("# MultiTerminal Wiki");
            index.AppendLine();
            index.AppendLine("> Per-subsystem reference articles generated from the code graph + code digests.");
            index.AppendLine("> Load this index at session start (~200 tokens) and fetch specific articles on demand.");
            index.AppendLine();
            index.AppendLine("## Subsystems");
            index.AppendLine();
            foreach (var article in articles)
            {
                var tagStr = article.Tags.Count > 0 ? " _(" + string.Join(", ", article.Tags) + ")_" : "";
                index.AppendLine($"- **[{article.Name}](./{article.Id}.md)**{tagStr} — {article.Description}");
            }
            index.AppendLine();
            index.AppendLine($"_Generated {DateTime.UtcNow:o} · {articles.Count} articles_");
            File.WriteAllText(Path.Combine(wikiDir, "index.md"), index.ToString());
        }
    }
}
