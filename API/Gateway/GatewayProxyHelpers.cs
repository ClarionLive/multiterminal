using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace MultiTerminal.API.Gateway
{
    /// <summary>
    /// Small proxy helpers for the gateway's one remaining outbound hop — the Cloudflare
    /// Worker permission relay (task ca6c5344, item [5]). Ported from the standalone
    /// MultiRemote's ProxyHelpers. Everything else is a direct in-process service call;
    /// permissions stay an HTTP forward because the relay lives off-box (workers.dev).
    /// </summary>
    internal static class GatewayProxyHelpers
    {
        private static readonly Regex IdPattern = new Regex(@"^[a-zA-Z0-9_\-]+$", RegexOptions.Compiled);

        /// <summary>Forward an upstream response body + status verbatim as application/json.</summary>
        internal static async Task<IResult> ProxyResponse(HttpResponseMessage response)
        {
            var json = await response.Content.ReadAsStringAsync();
            return Results.Content(json, "application/json", statusCode: (int)response.StatusCode);
        }

        /// <summary>Validate a route ID is a safe alphanumeric/underscore/dash string (≤64 chars).</summary>
        internal static bool IsValidId(string id)
        {
            return !string.IsNullOrEmpty(id) && id.Length <= 64 && IdPattern.IsMatch(id);
        }
    }
}
