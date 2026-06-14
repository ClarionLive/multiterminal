using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MultiTerminal.API.Gateway
{
    /// <summary>
    /// Web Push subscription endpoints for the phone (task ca6c5344, item [7]). Ported
    /// verbatim from the standalone MultiRemote's PushEndpoints. /api/push/key is public
    /// (it's in <see cref="SessionAuthMiddleware"/>'s allowlist — the client needs the VAPID
    /// public key before it can subscribe); subscribe/test require a session.
    /// </summary>
    public static class GatewayPushEndpoints
    {
        public static void MapMultiRemotePushEndpoints(this WebApplication app)
        {
            app.MapGet("/api/push/key", (PushNotificationService push) =>
                Results.Ok(new { publicKey = push.PublicKey }));

            app.MapPost("/api/push/subscribe", async (HttpContext context, PushNotificationService push) =>
            {
                var sub = await context.Request.ReadFromJsonAsync<SubscribeRequest>();
                if (sub == null || string.IsNullOrEmpty(sub.Endpoint))
                    return Results.BadRequest(new { error = "Invalid subscription" });

                // Validate/bound the client-supplied fields before persisting them to push-config.json
                // (pipeline Run-5 security MEDIUM: untrusted input persisted unbounded). A Web Push
                // endpoint is always an absolute https URL; cap field + id sizes to prevent file bloat.
                if (!Uri.TryCreate(sub.Endpoint, UriKind.Absolute, out var ep) || ep.Scheme != Uri.UriSchemeHttps)
                    return Results.BadRequest(new { error = "endpoint must be an absolute https URL" });
                if (sub.Endpoint.Length > 2048
                    || (sub.P256dh?.Length ?? 0) > 512
                    || (sub.Auth?.Length ?? 0) > 512
                    || (sub.DeviceId?.Length ?? 0) > 128)
                    return Results.BadRequest(new { error = "subscription field exceeds maximum length" });

                push.AddSubscription(sub.Endpoint, sub.P256dh, sub.Auth, sub.DeviceId);
                return Results.Ok(new { success = true });
            });

            app.MapPost("/api/push/test", async (PushNotificationService push) =>
            {
                var result = await push.SendToAllWithResult("MultiRemote", "Test notification!");
                return Results.Ok(result);
            });
        }
    }

    // DeviceId is an optional stable per-install id (PWA localStorage) used to dedup ghost
    // subscriptions by device (item [11], Finding 3); older clients omit it (null → no dedup).
    public record SubscribeRequest(string Endpoint, string P256dh, string Auth, string DeviceId = null);
}
