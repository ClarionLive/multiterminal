using System;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.Services;

namespace MultiTerminal.API.Controllers
{
    /// <summary>
    /// WebSocket endpoint for streaming terminal I/O.
    /// Upgrades HTTP connections to WebSocket for real-time terminal access.
    ///
    /// Protocol:
    /// - Binary frames: raw terminal I/O (VT/ANSI escape sequences)
    /// - Text frames: JSON control messages (resize, disconnect, status)
    /// </summary>
    [ApiController]
    public class TerminalStreamController : ControllerBase
    {
        private readonly TerminalStreamService _streamService;

        public TerminalStreamController(TerminalStreamService streamService)
        {
            _streamService = streamService;
        }

        /// <summary>
        /// WebSocket endpoint for streaming terminal I/O.
        /// GET /api/terminal/{id}/stream
        /// </summary>
        [HttpGet("api/terminal/{id}/stream")]
        public async Task Get(string id)
        {
            if (!HttpContext.WebSockets.IsWebSocketRequest)
            {
                HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await HttpContext.Response.WriteAsync("WebSocket connection required");
                return;
            }

            var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();

            try
            {
                await _streamService.HandleConnectionAsync(id, webSocket, HttpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TerminalStreamController] Error: {ex.Message}");

                if (webSocket.State == WebSocketState.Open)
                {
                    try
                    {
                        await webSocket.CloseAsync(
                            WebSocketCloseStatus.InternalServerError,
                            "Server error",
                            default);
                    }
                    catch { }
                }
            }
        }

        /// <summary>
        /// REST endpoint to list active terminal streams.
        /// GET /api/terminal/streams
        /// </summary>
        [HttpGet("api/terminal/streams")]
        public IActionResult GetActiveStreams()
        {
            var streams = _streamService.GetActiveStreams();
            var result = new System.Collections.Generic.List<object>();

            foreach (var terminalId in streams)
            {
                result.Add(new
                {
                    terminalId,
                    subscriberCount = _streamService.GetSubscriberCount(terminalId)
                });
            }

            return Ok(new { streams = result });
        }
    }
}
