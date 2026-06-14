using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using MultiTerminal.API.Controllers;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;

namespace MultiTerminal.API.Gateway
{
    /// <summary>
    /// In-process replacements for the standalone MultiRemote's Terminals/Messages/Inbox
    /// proxy hops (task ca6c5344, item [4]). Each handler calls MT's <see cref="MessageBroker"/>
    /// DIRECTLY — no HTTP hop to :5050 — and reproduces the exact response shape of the
    /// corresponding MT controller action so the PWA sees identical payloads. The PWA's
    /// friendlier route names (/api/terminals, /api/messages/*, /api/inbox/*) are mapped
    /// straight here rather than rewritten onto MT's native routes (/api/messaging/*,
    /// /api/tasks/inbox/*), avoiding any routing-order subtlety. The request DTOs are
    /// reused from MT's controllers so binding stays identical.
    /// </summary>
    public static class GatewayMessagingEndpoints
    {
        public static void MapMultiRemoteMessagingEndpoints(this WebApplication app)
        {
            // ---- Terminals (MR /api/terminals → MT MessagingController) ----------------
            app.MapGet("/api/terminals", (MessageBroker broker) =>
                Results.Ok(broker.GetTerminals()));

            app.MapPost("/api/terminals/register", (RegisterTerminalRequest request, MessageBroker broker) =>
            {
                var result = broker.RegisterTerminal(request.Name, request.DocId, channelPort: request.ChannelPort);
                if (!result.Success)
                    return Results.BadRequest(new { error = result.Error });
                return Results.Ok(new { terminalId = result.TerminalId });
            });

            // ---- Messages (MR /api/messages/* → MT MessagingController/Elicitations) ----
            app.MapPost("/api/messages/send", async (SendMessageRequest request, MessageBroker broker, HttpContext ctx) =>
            {
                MarkPhoneSource(broker, ctx);
                var result = await broker.SendMessage(request.FromTerminalId, request.To, request.Message, request.Priority);
                return Results.Ok(result);
            });

            app.MapPost("/api/messages/broadcast", async (BroadcastRequest request, MessageBroker broker, HttpContext ctx) =>
            {
                MarkPhoneSource(broker, ctx);
                var result = await broker.Broadcast(request.FromTerminalId, request.Message);
                return Results.Ok(result);
            });

            // Image upload (mirrors MessagingController.UploadMessageImages validation).
            app.MapPost("/api/messages/images", (UploadMessageImagesRequest request, MessageBroker broker) =>
            {
                if (request?.Images == null || request.Images.Count == 0)
                    return Results.BadRequest(new { error = "At least one image is required." });
                if (request.Images.Count > 10)
                    return Results.BadRequest(new { error = "Maximum 10 images per batch." });

                var inputs = new List<MessageImageInput>();
                foreach (var img in request.Images)
                {
                    if (string.IsNullOrEmpty(img.Base64Data))
                        return Results.BadRequest(new { error = $"Image '{img.FileName}' has no data." });
                    if (string.IsNullOrEmpty(img.MimeType) || !img.MimeType.StartsWith("image/"))
                        return Results.BadRequest(new { error = $"Invalid MIME type for '{img.FileName}': {img.MimeType}" });

                    inputs.Add(new MessageImageInput
                    {
                        FileName = img.FileName ?? "image",
                        MimeType = img.MimeType,
                        Base64Data = img.Base64Data,
                        FileSizeBytes = img.Base64Data.Length * 3 / 4,
                    });
                }

                var batchId = broker.SaveMessageImages(inputs);
                return Results.Ok(new { batchId, imageCount = inputs.Count });
            });

            app.MapGet("/api/messages/images/{batchId}", (string batchId, MessageBroker broker) =>
            {
                var images = broker.GetMessageImages(batchId);
                if (images == null || images.Count == 0)
                    return Results.NotFound(new { error = $"No images found for batch '{batchId}'." });

                return Results.Ok(images.Select(i => new
                {
                    i.Id,
                    i.BatchId,
                    i.FileName,
                    i.MimeType,
                    i.Base64Data,
                    i.FileSizeBytes,
                    i.CreatedAt,
                }));
            });

            // Elicitation respond (MR /api/messages/elicitations/{id}/respond → MT ElicitationsController).
            app.MapPost("/api/messages/elicitations/{id}/respond",
                (string id, ElicitationRespondRequest request, MessageBroker broker, HttpContext ctx, PermissionRelayService permissionRelay) =>
            {
                if (string.IsNullOrWhiteSpace(request.Action))
                    return Results.BadRequest(new { error = "action is required" });
                var validActions = new[] { "accept", "decline", "cancel" };
                if (!validActions.Contains(request.Action))
                    return Results.BadRequest(new { error = "action must be accept, decline, or cancel" });

                MarkPhoneSource(broker, ctx);

                var response = new ElicitationResponse
                {
                    Action = request.Action,
                    ContentJson = request.ContentJson ?? "{}",
                };

                var success = broker.SubmitElicitationResponse(id, response);
                if (!success)
                    return Results.NotFound(new { error = "Elicitation not found or expired" });

                // Cancel any in-flight Worker poll for this elicitation — we have the answer.
                permissionRelay?.Cancel(id);
                return Results.Ok(new { success = true });
            });

            // Pending messages for a terminal — keep AFTER the more specific /api/messages/*
            // literals above (distinct segment counts, but registered last for clarity).
            app.MapGet("/api/messages/{terminalId}", (string terminalId, MessageBroker broker) =>
                Results.Ok(broker.GetMessages(terminalId)));

            // ---- Inbox (MR /api/inbox/* → MT TasksController inbox actions) -------------
            app.MapGet("/api/inbox/{userId}", (string userId, MessageBroker broker, bool unreadOnly = false, int limit = 50) =>
                Results.Ok(broker.GetInbox(userId, unreadOnly, limit)));

            app.MapPost("/api/inbox/{messageId}/read", (string messageId, MessageBroker broker) =>
            {
                broker.MarkInboxRead(messageId);
                return Results.Ok(new { success = true });
            });

            app.MapPost("/api/inbox/{userId}/read-all", (string userId, MessageBroker broker) =>
            {
                broker.MarkAllInboxRead(userId);
                return Results.Ok(new { success = true });
            });

            app.MapPost("/api/inbox/{messageId}/reply", (string messageId, ReplyToInboxRequest request, MessageBroker broker) =>
            {
                broker.ReplyToInbox(messageId, request.ReplyText);
                return Results.Ok(new { success = true });
            });

            app.MapGet("/api/inbox/{userId}/unread-count", (string userId, MessageBroker broker) =>
            {
                var result = broker.GetInbox(userId, unreadOnly: true);
                return Results.Ok(new { count = result.UnreadCount });
            });
        }

        // The gateway IS the phone's origin, so writes through it carry the same intent
        // as the standalone MultiRemote's injected "X-Source: phone" header — flip MT into
        // remote mode. Mirrors MessagingController.InferRemoteModeFromSource (phone branch):
        // short-circuit when already remote so settings.txt isn't rewritten on every hit.
        private static void MarkPhoneSource(MessageBroker broker, HttpContext ctx)
        {
            if (broker == null || broker.IsRemoteMode)
                return;
            var callerIp = ctx?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
            broker.DebugLogService?.Info("RemoteMode", $"desktop→phone (X-Source=phone, caller={callerIp})");
            broker.SetRemoteMode(true);
        }
    }
}
