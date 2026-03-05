using Microsoft.AspNetCore.Mvc;
using MultiTerminal.MCPServer.Services;

namespace MultiTerminal.API.Controllers
{
    [ApiController]
    [Route("api/browser-tabs")]
    public class BrowserTabsController : ControllerBase
    {
        private readonly MessageBroker _broker;

        public BrowserTabsController(MessageBroker broker)
        {
            _broker = broker;
        }

        [HttpPost("open")]
        public IActionResult OpenTab([FromBody] OpenBrowserTabRequest request)
        {
            var (success, tabId, error) = _broker.OpenBrowserTab(
                request.TerminalId, request.Title, request.Url, request.Content);

            if (!success)
                return BadRequest(new { error });

            return Ok(new { success = true, tabId });
        }

        [HttpPost("update")]
        public IActionResult UpdateTab([FromBody] UpdateBrowserTabRequest request)
        {
            var (success, error) = _broker.SetBrowserContent(
                request.TerminalId, request.TabId, request.Title, request.Url, request.Content);

            if (!success)
                return BadRequest(new { error });

            return Ok(new { success = true });
        }

        [HttpPost("close")]
        public IActionResult CloseTab([FromBody] CloseBrowserTabRequest request)
        {
            var (success, error) = _broker.CloseBrowserTab(request.TerminalId, request.TabId);

            if (!success)
                return BadRequest(new { error });

            return Ok(new { success = true });
        }
    }

    public class OpenBrowserTabRequest
    {
        public string TerminalId { get; set; }
        public string Title { get; set; }
        public string Url { get; set; }
        public string Content { get; set; }
    }

    public class UpdateBrowserTabRequest
    {
        public string TerminalId { get; set; }
        public string TabId { get; set; }
        public string Title { get; set; }
        public string Url { get; set; }
        public string Content { get; set; }
    }

    public class CloseBrowserTabRequest
    {
        public string TerminalId { get; set; }
        public string TabId { get; set; }
    }
}
