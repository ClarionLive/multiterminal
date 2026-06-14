using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace MultiTerminal.API.Gateway
{
    /// <summary>
    /// Cookie-session gate for the MultiRemote phone gateway (task ca6c5344, item [2]).
    /// Ported verbatim from the standalone MultiRemote (Auth/SessionAuthMiddleware.cs):
    /// public paths (login page, login/logout API, push key, health, service worker)
    /// pass through; everything else requires an authenticated session. API calls get a
    /// 401, page requests redirect to /login.html. Runs in the gateway's OWN pipeline on
    /// :5100 only — it never touches MultiTerminal's unauthenticated :5050 API (D1).
    /// </summary>
    public class SessionAuthMiddleware
    {
        private readonly RequestDelegate _next;

        private static readonly HashSet<string> PublicPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "/login.html",
            "/api/auth/login",
            "/api/push/key",
            "/health",
            "/api/notifications/runtime",
            "/sw.js",
        };

        public SessionAuthMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var path = context.Request.Path.Value ?? "";

            // Allow public paths.
            if (IsPublicPath(path))
            {
                await _next(context);
                return;
            }

            // Allow static assets for the login page (css/js/img needed before auth).
            if (path.StartsWith("/css/") || path.StartsWith("/js/") || path.StartsWith("/img/"))
            {
                await _next(context);
                return;
            }

            // Check session.
            var isAuthenticated = context.Session.GetString("authenticated");
            if (isAuthenticated != "true")
            {
                // API calls get 401, page requests get redirected.
                if (path.StartsWith("/api/"))
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsJsonAsync(new { error = "Not authenticated" });
                    return;
                }

                context.Response.Redirect("/login.html");
                return;
            }

            await _next(context);
        }

        private static bool IsPublicPath(string path)
        {
            return PublicPaths.Contains(path) || path == "/login";
        }
    }
}
