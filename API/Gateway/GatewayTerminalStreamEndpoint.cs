using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using MultiTerminal.Services;

namespace MultiTerminal.API.Gateway
{
    /// <summary>
    /// Interactive terminal console WebSocket for the phone (task ca6c5344, item [6]).
    /// The standalone MultiRemote PROXIED this to ws://localhost:5050/api/terminal/{id}/stream;
    /// here we bind the upgraded socket DIRECTLY to MT's in-process
    /// <see cref="TerminalStreamService"/> (the exact instance MainForm wired the terminal
    /// resolver onto), eliminating the second WebSocket hop entirely. Auth is re-checked in
    /// the handler because the session middleware doesn't fully cover WS upgrades; the
    /// SessionAuthMiddleware gate also 401s unauthenticated /api/* upgrades before we get here.
    /// </summary>
    public static class GatewayTerminalStreamEndpoint
    {
        public static void MapMultiRemoteTerminalStream(this WebApplication app)
        {
            app.Map("/api/terminal/{id}/ws", async (HttpContext context, string id, TerminalStreamService streamService) =>
            {
                var isAuthenticated = context.Session.GetString("authenticated");
                if (isAuthenticated != "true")
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsJsonAsync(new { error = "Not authenticated" });
                    return;
                }

                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new { error = "WebSocket connection required" });
                    return;
                }

                using var ws = await context.WebSockets.AcceptWebSocketAsync();

                // Hand the live socket to MT's stream service — it owns the bidirectional
                // relay (binary = raw VT/ANSI terminal I/O, text = JSON control frames).
                await streamService.HandleConnectionAsync(id, ws, context.RequestAborted);
            });
        }
    }
}
