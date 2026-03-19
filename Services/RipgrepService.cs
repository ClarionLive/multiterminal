using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Wraps the ripgrep (rg.exe) binary for fast code/content search.
    /// Used by REST API, MCP tools, and internal services.
    /// </summary>
    public class RipgrepService
    {
        private readonly string _rgExePath;

        public RipgrepService(string rgExePath = null)
        {
            _rgExePath = rgExePath ?? ResolveRgExePath();
            if (!File.Exists(_rgExePath))
                Trace.WriteLine($"[RipgrepService] WARNING: rg.exe not found at {_rgExePath}");
            else
                Trace.WriteLine($"[RipgrepService] Using rg.exe at {_rgExePath}");
        }

        public bool IsAvailable => File.Exists(_rgExePath);

        /// <summary>
        /// Search for a pattern in a path. Returns structured JSON results from rg --json.
        /// </summary>
        public RipgrepResult Search(string pattern, string searchPath, RipgrepOptions options = null)
        {
            if (!IsAvailable)
                return new RipgrepResult { Success = false, Error = "rg.exe not found" };

            options = options ?? new RipgrepOptions();
            var args = BuildArgs(pattern, searchPath, options);

            try
            {
                var (exitCode, stdout, stderr) = RunProcess(args, options.TimeoutMs);

                // rg exit codes: 0=matches found, 1=no matches, 2=error
                if (exitCode == 2)
                    return new RipgrepResult { Success = false, Error = stderr };

                if (options.JsonOutput)
                    return ParseJsonOutput(stdout);

                return ParseTextOutput(stdout, exitCode);
            }
            catch (Exception ex)
            {
                return new RipgrepResult { Success = false, Error = ex.Message };
            }
        }

        /// <summary>
        /// List files matching a glob pattern (rg --files -g "pattern").
        /// </summary>
        public RipgrepResult FindFiles(string searchPath, string glob = null, string type = null)
        {
            if (!IsAvailable)
                return new RipgrepResult { Success = false, Error = "rg.exe not found" };

            var argList = new List<string> { "--files" };

            if (!string.IsNullOrEmpty(glob))
            {
                argList.Add("-g");
                argList.Add(QuoteArg(glob));
            }
            if (!string.IsNullOrEmpty(type))
            {
                argList.Add("-t");
                argList.Add(type);
            }

            argList.Add(QuoteArg(searchPath));

            try
            {
                var (exitCode, stdout, stderr) = RunProcess(string.Join(" ", argList), 30000);

                if (exitCode == 2)
                    return new RipgrepResult { Success = false, Error = stderr };

                var files = new List<string>();
                foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    files.Add(line.Trim());

                return new RipgrepResult
                {
                    Success = true,
                    Files = files,
                    MatchCount = files.Count
                };
            }
            catch (Exception ex)
            {
                return new RipgrepResult { Success = false, Error = ex.Message };
            }
        }

        private List<string> BuildArgList(string pattern, string searchPath, RipgrepOptions options)
        {
            var args = new List<string>();

            if (options.JsonOutput)
                args.Add("--json");

            if (options.CaseInsensitive)
                args.Add("-i");

            if (options.Multiline)
            {
                args.Add("-U");
                args.Add("--multiline-dotall");
            }

            if (options.FixedStrings)
                args.Add("-F");

            if (!string.IsNullOrEmpty(options.Glob))
            {
                args.Add("-g");
                args.Add(QuoteArg(options.Glob));
            }

            if (!string.IsNullOrEmpty(options.FileType))
            {
                args.Add("-t");
                args.Add(options.FileType);
            }

            if (options.MaxCount > 0)
            {
                args.Add("-m");
                args.Add(options.MaxCount.ToString());
            }

            if (options.Context > 0)
            {
                args.Add("-C");
                args.Add(options.Context.ToString());
            }
            else
            {
                if (options.Before > 0)
                {
                    args.Add("-B");
                    args.Add(options.Before.ToString());
                }
                if (options.After > 0)
                {
                    args.Add("-A");
                    args.Add(options.After.ToString());
                }
            }

            if (options.FilesWithMatches)
                args.Add("-l");

            if (options.Count)
                args.Add("-c");

            if (options.LineNumber)
                args.Add("-n");

            args.Add("--");
            args.Add(QuoteArg(pattern));
            args.Add(QuoteArg(searchPath));

            return args;
        }

        private string BuildArgs(string pattern, string searchPath, RipgrepOptions options)
        {
            return string.Join(" ", BuildArgList(pattern, searchPath, options));
        }

        private (int exitCode, string stdout, string stderr) RunProcess(string arguments, int timeoutMs)
        {
            var psi = new ProcessStartInfo
            {
                FileName = _rgExePath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

            using (var process = Process.Start(psi))
            {
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();

                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(); } catch { }
                    return (2, "", "Search timed out");
                }

                return (process.ExitCode, stdout, stderr);
            }
        }

        private RipgrepResult ParseTextOutput(string stdout, int exitCode)
        {
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var matches = new List<RipgrepMatch>();

            foreach (var line in lines)
            {
                // rg outputs: filepath:line:content  or  filepath:count
                var trimmed = line.TrimEnd('\r');
                matches.Add(new RipgrepMatch { Line = trimmed });
            }

            return new RipgrepResult
            {
                Success = true,
                Matches = matches,
                MatchCount = matches.Count,
                RawOutput = stdout
            };
        }

        private RipgrepResult ParseJsonOutput(string stdout)
        {
            var matches = new List<RipgrepMatch>();
            var stats = new RipgrepStats();

            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var type = root.GetProperty("type").GetString();

                    if (type == "match")
                    {
                        var data = root.GetProperty("data");
                        var path = data.GetProperty("path").GetProperty("text").GetString();
                        var lineNum = data.GetProperty("line_number").GetInt64();
                        var text = data.GetProperty("lines").GetProperty("text").GetString();

                        matches.Add(new RipgrepMatch
                        {
                            FilePath = path,
                            LineNumber = (int)lineNum,
                            Text = text?.TrimEnd('\r', '\n'),
                            Line = $"{path}:{lineNum}:{text?.TrimEnd('\r', '\n')}"
                        });
                    }
                    else if (type == "summary")
                    {
                        var data = root.GetProperty("data");
                        var s = data.GetProperty("stats");
                        stats.MatchedLines = s.GetProperty("matched_lines").GetInt64();
                        stats.Matches = s.GetProperty("matches").GetInt64();
                        stats.SearchedFiles = s.GetProperty("searches").GetInt64();
                        stats.ElapsedMs = data.GetProperty("elapsed_total").GetProperty("secs").GetDouble() * 1000
                                        + data.GetProperty("elapsed_total").GetProperty("nanos").GetDouble() / 1_000_000;
                    }
                }
                catch { }
            }

            return new RipgrepResult
            {
                Success = true,
                Matches = matches,
                MatchCount = matches.Count,
                Stats = stats
            };
        }

        private static string ResolveRgExePath()
        {
            // Try: Assembly location + tools/rg.exe
            try
            {
                var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var toolPath = Path.Combine(assemblyDir, "tools", "rg.exe");
                if (File.Exists(toolPath))
                    return toolPath;
            }
            catch { }

            // Try: source tree tools/rg.exe (dev mode)
            try
            {
                var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                // Walk up from bin/Release/net8.0-windows/win-x64 to project root
                var dir = new DirectoryInfo(assemblyDir);
                for (int i = 0; i < 6 && dir != null; i++)
                {
                    var candidate = Path.Combine(dir.FullName, "tools", "rg.exe");
                    if (File.Exists(candidate))
                        return candidate;
                    dir = dir.Parent;
                }
            }
            catch { }

            // Fall back to PATH
            return "rg.exe";
        }

        private static string QuoteArg(string arg)
        {
            if (arg.Contains(' ') || arg.Contains('"'))
                return "\"" + arg.Replace("\"", "\\\"") + "\"";
            return arg;
        }
    }

    public class RipgrepOptions
    {
        public bool JsonOutput { get; set; } = true;
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
        public bool LineNumber { get; set; } = true;
        public int TimeoutMs { get; set; } = 30000;
    }

    public class RipgrepResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public List<RipgrepMatch> Matches { get; set; } = new List<RipgrepMatch>();
        public List<string> Files { get; set; } = new List<string>();
        public int MatchCount { get; set; }
        public RipgrepStats Stats { get; set; }
        public string RawOutput { get; set; }
    }

    public class RipgrepMatch
    {
        public string FilePath { get; set; }
        public int LineNumber { get; set; }
        public string Text { get; set; }
        public string Line { get; set; }
    }

    public class RipgrepStats
    {
        public long MatchedLines { get; set; }
        public long Matches { get; set; }
        public long SearchedFiles { get; set; }
        public double ElapsedMs { get; set; }
    }
}
