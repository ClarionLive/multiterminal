using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace MultiTerminal.API.Gateway
{
    /// <summary>
    /// Login/logout/status endpoints for the MultiRemote phone gateway (task ca6c5344,
    /// item [2]). Ported verbatim from the standalone MultiRemote (Auth/AuthEndpoints.cs):
    /// per-IP rate limiting (5 attempts / 5 min lockout) and a fixed 1s delay on failure to
    /// slow brute force. Credentials come from MultiRemote:Auth:Username / :Password — the real
    /// secret lives in the gitignored appsettings.Local.json override. Login FAILS CLOSED while
    /// those are unset or still at the committed "changeme" placeholders (pipeline Run-2 security
    /// HIGH): the gateway must not be reachable behind Tailscale with guessable defaults.
    /// </summary>
    public static class AuthEndpoints
    {
        // Rate limiting: track failed attempts per IP.
        private static readonly ConcurrentDictionary<string, (int count, DateTime lastAttempt)> _failedAttempts = new ConcurrentDictionary<string, (int count, DateTime lastAttempt)>();
        private const int MaxAttempts = 5;
        private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

        public static void MapAuthEndpoints(this WebApplication app, IConfigurationSection config)
        {
            app.MapPost("/api/auth/login", async (HttpContext context) =>
            {
                var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                // Check rate limit.
                if (_failedAttempts.TryGetValue(clientIp, out var record))
                {
                    if (record.count >= MaxAttempts && DateTime.UtcNow - record.lastAttempt < LockoutDuration)
                    {
                        var remaining = LockoutDuration - (DateTime.UtcNow - record.lastAttempt);
                        context.Response.StatusCode = 429;
                        return Results.Json(new { error = "Too many failed attempts. Try again later.", retryAfterSeconds = (int)remaining.TotalSeconds }, statusCode: 429);
                    }

                    // Reset if lockout has expired.
                    if (DateTime.UtcNow - record.lastAttempt >= LockoutDuration)
                    {
                        _failedAttempts.TryRemove(clientIp, out _);
                    }
                }

                var form = await context.Request.ReadFromJsonAsync<LoginRequest>();
                if (form is null)
                {
                    return Results.BadRequest(new { error = "Invalid request" });
                }

                var expectedUser = config.GetValue<string>("Auth:Username");
                var expectedPass = config.GetValue<string>("Auth:Password");

                // Fail closed on missing/default credentials (pipeline Run-2 security HIGH). The
                // committed appsettings.json ships changeme/changeme and appsettings.Local.json is
                // only an OPTIONAL override, so an install that forgets it must NOT be reachable
                // behind Tailscale with guessable creds. Disable login entirely until BOTH the
                // username and password are configured to non-default values.
                if (string.IsNullOrEmpty(expectedUser) || string.IsNullOrEmpty(expectedPass) ||
                    string.Equals(expectedUser, "changeme", StringComparison.Ordinal) ||
                    string.Equals(expectedPass, "changeme", StringComparison.Ordinal))
                {
                    return Results.Json(
                        new { error = "Login is disabled until non-default MultiRemote:Auth credentials are set in appsettings.Local.json." },
                        statusCode: 503);
                }

                if (form.Username == expectedUser && form.Password == expectedPass)
                {
                    // Clear failed attempts on success.
                    _failedAttempts.TryRemove(clientIp, out _);
                    context.Session.SetString("authenticated", "true");
                    context.Session.SetString("username", form.Username);
                    return Results.Ok(new { success = true, message = "Logged in" });
                }

                // Track failed attempt.
                _failedAttempts.AddOrUpdate(
                    clientIp,
                    _ => (1, DateTime.UtcNow),
                    (_, existing) => (existing.count + 1, DateTime.UtcNow));

                // Fixed delay to slow brute-force.
                await Task.Delay(1000);
                return Results.Json(new { error = "Invalid credentials" }, statusCode: 401);
            });

            app.MapPost("/api/auth/logout", async (HttpContext context) =>
            {
                context.Session.Clear();
                await Task.CompletedTask;
                return Results.Ok(new { success = true, message = "Logged out" });
            });

            app.MapGet("/api/auth/status", (HttpContext context) =>
            {
                var isAuth = context.Session.GetString("authenticated") == "true";
                var username = context.Session.GetString("username") ?? "";
                return Results.Ok(new { authenticated = isAuth, username });
            });
        }
    }

    public record LoginRequest(string Username, string Password);
}
