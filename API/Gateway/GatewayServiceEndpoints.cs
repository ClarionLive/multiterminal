using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using MultiTerminal.API.Controllers;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;

namespace MultiTerminal.API.Gateway
{
    /// <summary>
    /// Item [5] in-process/self-contained endpoints for the MultiRemote gateway
    /// (task ca6c5344): permission relay (the one outbound hop — to the Cloudflare
    /// Worker), terminal spawn (direct <see cref="SpawnService"/> call), and URL unfurl
    /// (self-contained Open Graph fetch). Digest + remote-mode — also item [5] — are
    /// served by the mounted MT controllers, so they aren't here.
    /// </summary>
    public static class GatewayServiceEndpoints
    {
        // ===================== Permissions (Cloudflare Worker relay) =====================
        // The phone fetches/answers/dismisses pending permission requests via the off-box
        // Worker. The shared X-API-Key is read from config and forwarded server-side; the
        // client never sees it. Uses a per-request HttpRequestMessage (not the shared
        // client's default headers), matching MT's PermissionRelayService pattern.
        public static void MapMultiRemotePermissionsEndpoints(this WebApplication app)
        {
            var group = app.MapGroup("/api/permissions");

            group.MapGet("", async (System.Net.Http.IHttpClientFactory factory, IConfiguration config) =>
            {
                if (!TryGetApiKey(config, out var apiKey, out var authError))
                    return Results.Json(new { error = authError }, statusCode: 503);
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, "/permissions/pending");
                    request.Headers.TryAddWithoutValidation("X-API-Key", apiKey);
                    var response = await factory.CreateClient("PermissionRelay").SendAsync(request);
                    return await GatewayProxyHelpers.ProxyResponse(response);
                }
                catch
                {
                    return Results.Json(new { error = "Permission relay unavailable" }, statusCode: 502);
                }
            });

            group.MapPost("/{id}/respond", async (string id, HttpContext context, System.Net.Http.IHttpClientFactory factory, IConfiguration config) =>
            {
                if (!GatewayProxyHelpers.IsValidId(id))
                    return Results.BadRequest(new { error = "Invalid permission ID" });
                if (!TryGetApiKey(config, out var apiKey, out var authError))
                    return Results.Json(new { error = authError }, statusCode: 503);
                try
                {
                    var body = await ReadBody(context);
                    using var request = new HttpRequestMessage(HttpMethod.Post, $"/permissions/{id}/respond")
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    };
                    request.Headers.TryAddWithoutValidation("X-API-Key", apiKey);
                    var response = await factory.CreateClient("PermissionRelay").SendAsync(request);
                    return await GatewayProxyHelpers.ProxyResponse(response);
                }
                catch
                {
                    return Results.Json(new { error = "Permission relay unavailable" }, statusCode: 502);
                }
            });

            group.MapDelete("/{id}", async (string id, System.Net.Http.IHttpClientFactory factory, IConfiguration config) =>
            {
                if (!GatewayProxyHelpers.IsValidId(id))
                    return Results.BadRequest(new { error = "Invalid permission ID" });
                if (!TryGetApiKey(config, out var apiKey, out var authError))
                    return Results.Json(new { error = authError }, statusCode: 503);
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Delete, $"/permissions/{id}");
                    request.Headers.TryAddWithoutValidation("X-API-Key", apiKey);
                    var response = await factory.CreateClient("PermissionRelay").SendAsync(request);
                    return await GatewayProxyHelpers.ProxyResponse(response);
                }
                catch
                {
                    return Results.Json(new { error = "Permission relay unavailable" }, statusCode: 502);
                }
            });

            group.MapGet("/{id}/status", async (string id, System.Net.Http.IHttpClientFactory factory, IConfiguration config) =>
            {
                if (!GatewayProxyHelpers.IsValidId(id))
                    return Results.BadRequest(new { error = "Invalid permission ID" });
                if (!TryGetApiKey(config, out var apiKey, out var authError))
                    return Results.Json(new { error = authError }, statusCode: 503);
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, $"/permissions/{id}/status");
                    request.Headers.TryAddWithoutValidation("X-API-Key", apiKey);
                    var response = await factory.CreateClient("PermissionRelay").SendAsync(request);
                    return await GatewayProxyHelpers.ProxyResponse(response);
                }
                catch
                {
                    return Results.Json(new { error = "Permission relay unavailable" }, statusCode: 502);
                }
            });
        }

        // ============================ Spawn (in-process) =================================
        // MR /api/spawn → MT SpawnController.SpawnTerminal. Calls SpawnService directly
        // (the instance MainForm wired UI callbacks onto). Mirrors the controller's Oracle
        // guard + project-source-path resolution + response shape.
        public static void MapMultiRemoteSpawnEndpoints(this WebApplication app)
        {
            app.MapPost("/api/spawn", async (SpawnTerminalRequest request, SpawnService spawnService, ProjectDatabase projectDb) =>
            {
                if (string.IsNullOrWhiteSpace(request.AgentName))
                    return Results.BadRequest(new { success = false, error = "agentName is required" });

                if (request.AgentName.Equals(OracleService.OracleName, StringComparison.OrdinalIgnoreCase))
                    return Results.BadRequest(new { success = false, error = "Oracle is always-on and managed by OracleService. Send messages to Oracle directly." });

                string workingDir = request.WorkingDir;
                if (!string.IsNullOrWhiteSpace(request.ProjectId))
                {
                    var project = projectDb.GetRichProject(request.ProjectId);
                    if (project == null)
                        return Results.NotFound(new { success = false, error = $"Project '{request.ProjectId}' not found" });
                    if (string.IsNullOrWhiteSpace(project.SourcePath))
                        return Results.BadRequest(new { success = false, error = $"Project '{project.Name}' has no source path configured" });
                    workingDir = project.SourcePath;
                }

                var (success, docId, error) = await spawnService.SpawnTeammateAsync(
                    request.AgentName,
                    agentType: null,
                    workingDir,
                    initialPrompt: null,
                    spawnerName: "ClaudeRemote");

                if (!success)
                    return Results.BadRequest(new { success = false, error });

                return Results.Ok(new { success = true, terminalName = request.AgentName, docId });
            });
        }

        // ============================ Unfurl (self-contained) ============================
        private static readonly HttpClient _unfurlHttp = CreateUnfurlClient();
        private static readonly Regex _titleRegex =
            new Regex(@"<title[^>]*>([^<]+)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        public static void MapMultiRemoteUnfurlEndpoints(this WebApplication app)
        {
            app.MapGet("/api/unfurl", async (string url) =>
            {
                if (string.IsNullOrWhiteSpace(url))
                    return Results.BadRequest(new { error = "url parameter is required" });
                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    return Results.BadRequest(new { error = "Only http/https URLs are supported" });
                if (await IsBlockedUrlAsync(url))
                    return Results.BadRequest(new { error = "Private/internal URLs are not allowed" });

                try
                {
                    using var response = await _unfurlHttp.GetAsync(url);
                    if (!response.IsSuccessStatusCode)
                        return Results.Ok(new { url, error = $"HTTP {(int)response.StatusCode}" });

                    var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                    if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                        return Results.Ok(new { url, title = "", description = "Non-HTML content: " + contentType });

                    var html = await response.Content.ReadAsStringAsync();
                    if (html.Length > 50_000)
                        html = html.Substring(0, 50_000);

                    var result = new
                    {
                        url,
                        title = ExtractMeta(html, "og:title") ?? ExtractTitle(html) ?? "",
                        description = ExtractMeta(html, "og:description") ?? ExtractMetaName(html, "description") ?? "",
                        image = ExtractMeta(html, "og:image") ?? "",
                        siteName = ExtractMeta(html, "og:site_name") ?? ExtractDomain(url),
                    };
                    return Results.Ok(result);
                }
                catch (TaskCanceledException)
                {
                    return Results.Ok(new { url, error = "Request timed out" });
                }
                catch (Exception ex)
                {
                    return Results.Ok(new { url, error = ex.Message });
                }
            });
        }

        // ----------------------------- helpers -----------------------------
        private static bool TryGetApiKey(IConfiguration config, out string apiKey, out string error)
        {
            // Relay ApiKey resolves Multi-Connect settings-first → appsettings fallback (task 642c14e3).
            var configured = MultiConnectConfig.Resolve(
                SettingsService.Default.GetMultiConnectRelayApiKey(),
                config["MultiRemote:PermissionRelay:ApiKey"]);
            if (string.IsNullOrWhiteSpace(configured))
            {
                apiKey = string.Empty;
                error = "Permission relay not configured (missing API key)";
                return false;
            }
            apiKey = configured;
            error = string.Empty;
            return true;
        }

        private static async Task<string> ReadBody(HttpContext context)
        {
            using var reader = new System.IO.StreamReader(context.Request.Body);
            return await reader.ReadToEndAsync();
        }

        private static HttpClient CreateUnfurlClient()
        {
            // SSRF hardening (task ca6c5344, pipeline Run-1 security HIGH): do NOT auto-follow
            // redirects — a public URL could 30x into an internal target the input-host check
            // never sees. A redirect now surfaces as a non-success status and is reported, not
            // chased. The handler is owned by the long-lived HttpClient (disposeHandler=true).
            var handler = new HttpClientHandler { AllowAutoRedirect = false };
            try
            {
                var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (compatible; MultiRemote/1.0)");
                client.DefaultRequestHeaders.Add("Accept", "text/html");
                handler = null; // ownership transferred to the HttpClient (disposeHandler=true)
                return client;
            }
            finally
            {
                // Only disposes if the HttpClient ctor threw before taking ownership (CA2000).
                handler?.Dispose();
            }
        }

        // SSRF guard (task ca6c5344, pipeline Run-1 security HIGH). The old string-prefix check
        // missed DNS names that resolve to internal IPs, IPv6 ranges, CGNAT, link-local, 0.0.0.0,
        // and 172.x outside 16-31. Resolve the host and reject if ANY resolved address is non-
        // public (DNS-rebind defense). Residual: a rebind between this check and HttpClient's own
        // resolution is theoretically possible, accepted for an auth-gated convenience endpoint.
        private static async Task<bool> IsBlockedUrlAsync(string url)
        {
            Uri uri;
            try
            {
                uri = new Uri(url);
            }
            catch
            {
                return true;
            }

            var host = uri.Host;
            if (string.IsNullOrEmpty(host))
                return true;
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                return true;

            IPAddress[] addresses;
            if (IPAddress.TryParse(host, out var literal))
            {
                addresses = new[] { literal };
            }
            else
            {
                try
                {
                    addresses = await Dns.GetHostAddressesAsync(host);
                }
                catch
                {
                    return true; // unresolvable → fail closed
                }
            }

            if (addresses == null || addresses.Length == 0)
                return true;
            foreach (var addr in addresses)
            {
                if (IsBlockedAddress(addr))
                    return true;
            }
            return false;
        }

        private static bool IsBlockedAddress(IPAddress ip)
        {
            if (ip == null)
                return true;
            if (IPAddress.IsLoopback(ip))
                return true;
            if (ip.IsIPv4MappedToIPv6)
                ip = ip.MapToIPv4();

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                if (b[0] == 0) return true;                                  // 0.0.0.0/8
                if (b[0] == 10) return true;                                 // 10/8 private
                if (b[0] == 127) return true;                                // loopback
                if (b[0] == 169 && b[1] == 254) return true;                 // link-local
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;    // 172.16/12 private
                if (b[0] == 192 && b[1] == 168) return true;                 // 192.168/16 private
                if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;   // 100.64/10 CGNAT
                if (b[0] == 255 && b[1] == 255 && b[2] == 255 && b[3] == 255) return true; // broadcast
                return false;
            }

            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
                    return true;
                if (ip.Equals(IPAddress.IPv6Any))
                    return true; // ::
                return false;
            }

            return true; // unknown family → block
        }

        private static string ExtractMeta(string html, string property)
        {
            var match = OgMetaRegex(property).Match(html);
            return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : null;
        }

        private static string ExtractMetaName(string html, string name)
        {
            var match = NameMetaRegex(name).Match(html);
            return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : null;
        }

        private static string ExtractTitle(string html)
        {
            var match = _titleRegex.Match(html);
            return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value.Trim()) : null;
        }

        private static string ExtractDomain(string url)
        {
            try
            {
                return new Uri(url).Host.Replace("www.", "");
            }
            catch
            {
                return "";
            }
        }

        private static Regex OgMetaRegex(string property) =>
            new Regex($@"<meta[^>]+property\s*=\s*[""']{Regex.Escape(property)}[""'][^>]+content\s*=\s*[""']([^""']*)[""']",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static Regex NameMetaRegex(string name) =>
            new Regex($@"<meta[^>]+name\s*=\s*[""']{Regex.Escape(name)}[""'][^>]+content\s*=\s*[""']([^""']*)[""']",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }
}
