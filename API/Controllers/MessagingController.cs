using System;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MultiTerminal.API.Controllers
{
    [ApiController]
    [Route("api/messaging")]
    public class MessagingController : ControllerBase
    {
        private readonly MessageBroker _broker;

        public MessagingController(MessageBroker broker)
        {
            _broker = broker;
        }

        /// <summary>
        /// Register a terminal with the messaging system
        /// </summary>
        [HttpPost("register")]
        public IActionResult RegisterTerminal([FromBody] RegisterTerminalRequest request)
        {
            var result = _broker.RegisterTerminal(request.Name, request.DocId, channelPort: request.ChannelPort, nonce: request.Nonce, ownerPid: request.OwnerPid);
            if (!result.Success)
                return Problem(detail: result.Error, statusCode: 400);

            return Ok(new { terminalId = result.TerminalId });
        }

        /// <summary>
        /// Resolve the agent name registered for a given Claude Code process id, so a channel server
        /// that started with no identity can discover the name its own session has since claimed
        /// (task c9285d2a). Returns 404 while nothing has claimed one, which is the normal state for
        /// a session that is simply not part of MultiTerminal.
        /// </summary>
        /// <remarks>
        /// Discloses only a NAME, which <c>GET /api/messaging/terminals</c> already lists in full, so
        /// this adds no new disclosure. It deliberately does NOT return the launch nonce: that is a
        /// secret, and handing it to anything that can guess a pid would turn proof-of-origin into a
        /// bearer token — the exact collapse TerminalInfo.LaunchNonce is JsonIgnore'd to prevent.
        /// </remarks>
        [HttpGet("channel-identity")]
        public IActionResult GetChannelIdentity([FromQuery] int ppid)
        {
            if (ppid <= 0)
                return BadRequest(new { error = "ppid must be a positive process id." });

            string name = _broker.GetTerminalNameByOwnerPid(ppid);
            if (string.IsNullOrEmpty(name))
                return NotFound();

            return Ok(new { name });
        }

        /// <summary>
        /// Send a message to another terminal
        /// </summary>
        [HttpPost("send")]
        public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest request)
        {
            InferRemoteModeFromSource();
            var result = await _broker.SendMessage(request.FromTerminalId, request.To, request.Message, request.Priority);
            return Ok(result);
        }

        /// <summary>
        /// Broadcast a message to all terminals
        /// </summary>
        [HttpPost("broadcast")]
        public async Task<IActionResult> Broadcast([FromBody] BroadcastRequest request)
        {
            InferRemoteModeFromSource();
            var result = await _broker.Broadcast(request.FromTerminalId, request.Message);
            return Ok(result);
        }

        // X-Source header presence-inference. The signal must be EXPLICIT so ambient
        // traffic (MCP tools, hook pings, cross-agent chatter) doesn't thrash the flag.
        //   X-Source: phone    → user is at phone → remote mode on
        //   X-Source: desktop  → user is at desk  → remote mode off
        //   absent / other     → no signal → leave state unchanged
        // Short-circuits when the inferred value already matches current state — avoids
        // rewriting settings.txt on every HTTP hit and keeps the audit log meaningful.
        private void InferRemoteModeFromSource()
        {
            if (!Request.Headers.TryGetValue("X-Source", out var v)) return;
            var src = v.ToString();

            bool intended;
            if (string.Equals(src, "phone", StringComparison.OrdinalIgnoreCase))
                intended = true;
            else if (string.Equals(src, "desktop", StringComparison.OrdinalIgnoreCase))
            {
                // fa1101db R2a — stamp desktop activity BEFORE the idempotent short-circuit so the
                // idle watcher's clock stays fresh even when remote mode is already off.
                _broker.RecordDesktopActivity();
                intended = false;
            }
            else
                return;

            if (intended == _broker.IsRemoteMode) return;

            var callerIp = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
            _broker.DebugLogService?.Info("RemoteMode",
                $"{(intended ? "desktop→phone" : "phone→desktop")} (X-Source={src}, caller={callerIp})");
            _broker.SetRemoteMode(intended);
        }

        /// <summary>
        /// Get pending messages for a terminal
        /// </summary>
        [HttpGet("messages/{terminalId}")]
        public IActionResult GetMessages(string terminalId)
        {
            var messages = _broker.GetMessages(terminalId);
            return Ok(messages);
        }

        /// <summary>
        /// List all registered terminals
        /// </summary>
        [HttpGet("terminals")]
        public IActionResult ListTerminals()
        {
            var terminals = _broker.GetTerminals();
            return Ok(terminals);
        }

        /// <summary>
        /// Disconnect a terminal by name (used by session-end hooks)
        /// </summary>
        [HttpPost("disconnect")]
        public IActionResult DisconnectTerminal([FromBody] DisconnectTerminalRequest request)
        {
            if (string.IsNullOrEmpty(request?.Name))
                return Problem(detail: "Name is required", statusCode: 400);

            _broker.DisconnectTerminalByName(request.Name);
            return Ok(new { name = request.Name });
        }

        /// <summary>
        /// Upload a batch of images for use in chat messages. Returns a batch ID
        /// that can be referenced in the message text as [ref:batchId].
        /// </summary>
        [HttpPost("images")]
        public IActionResult UploadMessageImages([FromBody] UploadMessageImagesRequest request)
        {
            if (request?.Images == null || request.Images.Count == 0)
                return Problem(detail: "At least one image is required.", statusCode: 400);

            if (request.Images.Count > 10)
                return Problem(detail: "Maximum 10 images per batch.", statusCode: 400);

            var inputs = new List<MessageImageInput>();
            foreach (var img in request.Images)
            {
                if (string.IsNullOrEmpty(img.Base64Data))
                    return Problem(detail: $"Image '{img.FileName}' has no data.", statusCode: 400);

                if (string.IsNullOrEmpty(img.MimeType) || !img.MimeType.StartsWith("image/"))
                    return Problem(detail: $"Invalid MIME type for '{img.FileName}': {img.MimeType}", statusCode: 400);

                inputs.Add(new MessageImageInput
                {
                    FileName = img.FileName ?? "image",
                    MimeType = img.MimeType,
                    Base64Data = img.Base64Data,
                    FileSizeBytes = img.Base64Data.Length * 3 / 4 // approximate decoded size
                });
            }

            var batchId = _broker.SaveMessageImages(inputs);
            return Ok(new { batchId, imageCount = inputs.Count });
        }

        /// <summary>
        /// Retrieve all images in a batch by batch ID.
        /// </summary>
        [HttpGet("images/{batchId}")]
        public IActionResult GetMessageImages(string batchId)
        {
            var images = _broker.GetMessageImages(batchId);
            if (images == null || images.Count == 0)
                return Problem(detail: $"No images found for batch '{batchId}'.", statusCode: 404);

            return Ok(images.Select(i => new
            {
                i.Id,
                i.BatchId,
                i.FileName,
                i.MimeType,
                i.Base64Data,
                i.FileSizeBytes,
                i.CreatedAt
            }));
        }
    }

    // Request models
    public class RegisterTerminalRequest
    {
        public string Name { get; set; }
        public string DocId { get; set; }
        /// <summary>
        /// HTTP port for this terminal's Claude Code Channel server.
        /// When provided, messages are delivered via channel instead of inbox files.
        /// </summary>
        public int? ChannelPort { get; set; }
        /// <summary>
        /// Per-launch proof-of-origin nonce (task fd3437e6), echoed by the child from its
        /// MULTITERMINAL_LAUNCH_NONCE env var. Required to adopt an "Unassigned" placeholder;
        /// null/absent for legacy clients (broker fails open on an unseeded placeholder).
        /// </summary>
        public string Nonce { get; set; }

        /// <summary>
        /// PID of the Claude Code process this registration came from (task c9285d2a), sent as
        /// process.ppid by the MCP server and the channel server — siblings under one claude.exe.
        /// It is what lets an ADOPTED terminal (one MT never launched, and which therefore has no
        /// launch nonce) prove that its two processes belong to the same session. Null/absent for
        /// every other client; the broker simply has no pid proof to check in that case.
        /// </summary>
        public int? OwnerPid { get; set; }
    }

    public class SendMessageRequest
    {
        public string FromTerminalId { get; set; }
        public string To { get; set; }
        public string Message { get; set; }
        /// <summary>
        /// Message priority: "low", "normal" (default), "high", "critical".
        /// </summary>
        public string Priority { get; set; }
    }

    public class BroadcastRequest
    {
        public string FromTerminalId { get; set; }
        public string Message { get; set; }
    }

    public class DisconnectTerminalRequest
    {
        public string Name { get; set; }
    }

    public class UploadMessageImagesRequest
    {
        public List<MessageImageUpload> Images { get; set; }
    }

    public class MessageImageUpload
    {
        public string FileName { get; set; }
        public string MimeType { get; set; }
        public string Base64Data { get; set; }
    }
}
