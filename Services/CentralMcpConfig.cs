#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Owns <c>%APPDATA%\multiterminal\.mcp.json</c>, the file every MT launch passes to Claude Code via
    /// <c>--mcp-config</c> so a terminal gets the core <c>multiterminal</c> and <c>mcp-gateway</c> servers
    /// (task cb4883b6, GitHub #8/#25).
    ///
    /// <para><b>Why this exists.</b> Nothing wrote the file on an installed machine: the writer had no
    /// callers, <see cref="LaunchCommandBuilder.GetMcpConfigPath"/> returned null, and the flag was
    /// silently omitted from every launch. Dev boxes never noticed because each carried a hand-made copy.</para>
    ///
    /// <para><b>Heal, do not rewrite.</b> The file is written ONLY when it is missing, unparseable, lacks
    /// a core server that could be generated now, or names a path that no longer exists. A working file
    /// is never touched. Rewriting on every startup was the first version of the fix, and on a dev box
    /// it would have replaced a hand-set gateway build with whichever build the resolver found first —
    /// silently, on every launch. A config nobody asked to change should not change.</para>
    ///
    /// <para>Only the two core entries are inspected. Any other server a person added is left alone,
    /// and survives a heal only if the heal is needed at all.</para>
    /// </summary>
    internal static class CentralMcpConfig
    {
        internal const string GatewayServerName = "mcp-gateway";
        internal const string MultiTerminalServerName = "multiterminal";

        /// <summary>What can be generated right now. A null member means that server cannot be.</summary>
        /// <param name="GatewayExe">A built McpGateway.exe (installed or dev build).</param>
        /// <param name="GatewayProjectDir">The McpGateway project, for a dev box with no build (<c>dotnet run</c>).</param>
        /// <param name="McpIndexJs">The multiterminal MCP server's <c>index.js</c>.</param>
        internal sealed record Sources(string? GatewayExe, string? GatewayProjectDir, string? McpIndexJs);

        internal enum Outcome
        {
            /// <summary>The existing file is healthy and was left untouched.</summary>
            Healthy,
            /// <summary>The file was missing or broken and has been written.</summary>
            Written,
            /// <summary>The file needs healing but no core server can be generated, so nothing was written.</summary>
            NothingToWrite,
            /// <summary>Running under a test host; the production file is never touched.</summary>
            SkippedUnderTestHost,
            /// <summary>An I/O error; see the log.</summary>
            Failed,
        }

        internal static string DefaultPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "multiterminal", ".mcp.json");

        /// <summary>
        /// Builds the file's JSON, or null when neither core server can be generated. The gateway prefers a
        /// built exe and falls back to <c>dotnet run --project</c> only when no build exists.
        /// </summary>
        internal static string? Generate(Sources sources)
        {
            var servers = new JsonObject();

            if (sources.GatewayExe != null)
            {
                servers[GatewayServerName] = new JsonObject
                {
                    ["type"] = "stdio",
                    ["command"] = sources.GatewayExe,
                    ["args"] = new JsonArray(),
                };
            }
            else if (sources.GatewayProjectDir != null)
            {
                servers[GatewayServerName] = new JsonObject
                {
                    ["type"] = "stdio",
                    ["command"] = "dotnet",
                    ["args"] = new JsonArray("run", "--project", sources.GatewayProjectDir),
                };
            }

            if (sources.McpIndexJs != null)
            {
                servers[MultiTerminalServerName] = new JsonObject
                {
                    ["type"] = "stdio",
                    ["command"] = "node",
                    ["args"] = new JsonArray(sources.McpIndexJs),
                };
            }

            if (servers.Count == 0)
                return null;

            var root = new JsonObject { ["mcpServers"] = servers };
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>
        /// Null when <paramref name="existingJson"/> is healthy; otherwise the reason it needs healing.
        /// Pure: the filesystem is reached only through <paramref name="pathExists"/>.
        /// </summary>
        /// <param name="existingJson">The file's content, or null when the file does not exist.</param>
        /// <param name="sources">What could be generated now. A core server that can be generated must be present.</param>
        /// <param name="pathExists">True when a file OR directory exists at the path.</param>
        internal static string? WhyUnhealthy(string? existingJson, Sources sources, Func<string, bool> pathExists)
        {
            if (existingJson == null)
                return "missing";

            // The whole inspection sits inside the try, not just the parse. JsonNode.Parse ACCEPTS a
            // repeated key; the JsonObject then throws ArgumentException (not JsonException) on first
            // access, which can be the root lookup or any per-server lookup below. Claude Code's
            // JSON.parse accepts the same file, so it is a realistic hand edit — and before this, it
            // escaped the MainForm constructor and stopped MultiTerminal opening (pipeline Run 1).
            try
            {
                // Root checked as an object first: indexing a JsonArray by name throws.
                var servers = (JsonNode.Parse(existingJson) as JsonObject)?["mcpServers"] as JsonObject;
                if (servers == null)
                    return "no mcpServers object";

                bool canGenerateGateway = sources.GatewayExe != null || sources.GatewayProjectDir != null;
                bool canGenerateMultiTerminal = sources.McpIndexJs != null;

                foreach (var (name, canGenerate) in new[]
                         {
                             (GatewayServerName, canGenerateGateway),
                             (MultiTerminalServerName, canGenerateMultiTerminal),
                         })
                {
                    if (!servers.TryGetPropertyValue(name, out JsonNode? entry) || entry is not JsonObject entryObject)
                    {
                        if (canGenerate)
                            return $"no '{name}' entry";
                        continue;
                    }

                    foreach (string path in RootedPaths(entryObject))
                    {
                        if (!pathExists(path))
                            return $"'{name}' points at a path that does not exist: {path}";
                    }
                }

                return null;
            }
            catch (JsonException ex)
            {
                return $"unparseable ({ex.Message})";
            }
            catch (ArgumentException ex)
            {
                return $"unparseable, repeated key ({ex.Message})";
            }
        }

        /// <summary>
        /// Heals the file at <paramref name="configPath"/> if it needs it. Does not throw for any file
        /// CONTENT (<see cref="WhyUnhealthy"/> turns every parse failure into a reason) and catches the
        /// I/O and access errors a read or write can raise. It does not catch everything: the startup
        /// caller wraps it in a catch-all, and so does <see cref="PathForLaunch"/>.
        /// </summary>
        internal static Outcome Ensure(
            string configPath,
            Sources sources,
            Func<string, bool> pathExists,
            Action<string>? log)
        {
            try
            {
                string? existing = File.Exists(configPath) ? File.ReadAllText(configPath) : null;
                string? reason = WhyUnhealthy(existing, sources, pathExists);
                if (reason == null)
                    return Outcome.Healthy;

                string? json = Generate(sources);
                if (json == null)
                {
                    log?.Invoke($"{configPath} needs healing ({reason}), but no core MCP server can be generated; left as is");
                    return Outcome.NothingToWrite;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                File.WriteAllText(configPath, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                log?.Invoke($"Wrote {configPath} ({reason})");
                return Outcome.Written;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log?.Invoke($"Could not heal {configPath}: {ex.Message}");
                return Outcome.Failed;
            }
        }

        /// <summary>
        /// Heals the file, then returns its path for <c>--mcp-config</c>, or null when there is still no
        /// file to pass. Runs before EVERY launch, not only when the file is absent (task a796e5f9): a file
        /// that broke after startup — an index.js or gateway that moved, a bad hand edit — was otherwise
        /// handed to every terminal until MultiTerminal restarted. Safe to run each time because
        /// <see cref="Ensure"/> leaves a healthy file byte-identical.
        /// <para>Never throws: a launch must not fail because a heal did. On an unexpected error the
        /// existing file, if any, is passed as it is.</para>
        /// </summary>
        internal static string? PathForLaunch(string configPath, Func<Outcome> ensure, Action<string>? log)
        {
            try
            {
                ensure();
            }
            catch (Exception ex)
            {
                log?.Invoke($"Could not check {configPath} before launch: {ex.Message}");
            }

            return File.Exists(configPath) ? configPath : null;
        }

        /// <summary>
        /// Heals the production file from the production sources. A no-op under a test host, because
        /// <see cref="LaunchCommandBuilder"/> calls this and tests exercise the launch builder.
        /// </summary>
        internal static Outcome EnsureDefault(Action<string>? log)
        {
            if (ProductionDataGuard.IsUnderTestHost())
                return Outcome.SkippedUnderTestHost;

            return Ensure(DefaultPath, ResolveDefaultSources(), p => File.Exists(p) || Directory.Exists(p), log);
        }

        /// <summary>Where the multiterminal MCP server must be for it to be generated, whether or not it is there.</summary>
        internal static string DefaultMcpIndexJs =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "multiterminal", "mcp", "index.js");

        internal static Sources ResolveDefaultSources()
        {
            string indexJs = DefaultMcpIndexJs;
            string projectDir = GatewayIntegrationService.GatewayProjectPath;

            return new Sources(
                GatewayExe: GatewayIntegrationService.ResolveGatewayExePath(),
                GatewayProjectDir: Directory.Exists(projectDir) ? projectDir : null,
                McpIndexJs: File.Exists(indexJs) ? indexJs : null);
        }

        /// <summary>
        /// The absolute paths an entry depends on: its command and any rooted argument. A bare
        /// <c>node</c>/<c>dotnet</c> command is resolved through PATH and is not checked.
        /// </summary>
        private static IEnumerable<string> RootedPaths(JsonObject entry)
        {
            if (entry["command"] is JsonValue command && command.TryGetValue(out string? commandText) && IsRooted(commandText))
                yield return commandText!;

            if (entry["args"] is JsonArray args)
            {
                foreach (JsonNode? arg in args)
                {
                    if (arg is JsonValue value && value.TryGetValue(out string? argText) && IsRooted(argText))
                        yield return argText!;
                }
            }
        }

        private static bool IsRooted(string? path) =>
            !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);
    }
}
