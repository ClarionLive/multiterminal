using System.Threading.Tasks;
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
                return Problem(detail: error, statusCode: 400);

            return Ok(new { tabId });
        }

        [HttpPost("update")]
        public IActionResult UpdateTab([FromBody] UpdateBrowserTabRequest request)
        {
            var (success, error) = _broker.SetBrowserContent(
                request.TerminalId, request.TabId, request.Title, request.Url, request.Content);

            if (!success)
                return Problem(detail: error, statusCode: 400);

            return Ok();
        }

        [HttpPost("close")]
        public IActionResult CloseTab([FromBody] CloseBrowserTabRequest request)
        {
            var (success, error) = _broker.CloseBrowserTab(request.TerminalId, request.TabId);

            if (!success)
                return Problem(detail: error, statusCode: 400);

            return Ok();
        }

        [HttpPost("execute-script")]
        public async Task<IActionResult> ExecuteScript([FromBody] ExecuteScriptRequest request)
        {
            var (success, result, error) = await _broker.ExecuteBrowserScript(
                request.TerminalId, request.TabId, request.Script);

            if (!success)
                return Problem(detail: error, statusCode: 400);

            return Ok(new { result });
        }

        [HttpPost("console-logs")]
        public async Task<IActionResult> GetConsoleLogs([FromBody] GetConsoleLogsRequest request)
        {
            var (success, result, error) = await _broker.GetBrowserConsoleLogs(
                request.TerminalId, request.TabId, request.Limit);

            if (!success)
                return Problem(detail: error, statusCode: 400);

            return Ok(new { logs = result });
        }

        [HttpPost("element-content")]
        public async Task<IActionResult> GetElementContent([FromBody] GetElementContentRequest request)
        {
            var (success, result, error) = await _broker.GetBrowserElementContent(
                request.TerminalId, request.TabId, request.Selector, request.Property);

            if (!success)
                return Problem(detail: error, statusCode: 400);

            return Ok(new { content = result });
        }

        [HttpPost("capture-screenshot")]
        public async Task<IActionResult> CaptureScreenshot([FromBody] CaptureScreenshotRequest request)
        {
            var (success, result, error) = await _broker.CaptureBrowserScreenshot(
                request.TerminalId, request.TabId);

            if (!success)
                return Problem(detail: error, statusCode: 400);

            return Ok(new { imageBase64 = result });
        }

        [HttpPost("post-message")]
        public async Task<IActionResult> PostMessage([FromBody] PostBrowserMessageRequest request)
        {
            var (success, error) = await _broker.PostBrowserMessage(
                request.TerminalId, request.TabId, request.Data);

            if (!success)
                return Problem(detail: error, statusCode: 400);

            return Ok();
        }

        [HttpPost("get-messages")]
        public async Task<IActionResult> GetMessages([FromBody] GetBrowserMessagesRequest request)
        {
            var (success, result, error) = await _broker.GetBrowserMessages(
                request.TerminalId, request.TabId, request.Limit);

            if (!success)
                return Problem(detail: error, statusCode: 400);

            return Ok(new { messages = result });
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

    public class ExecuteScriptRequest
    {
        public string TerminalId { get; set; }
        public string TabId { get; set; }
        public string Script { get; set; }
    }

    public class GetConsoleLogsRequest
    {
        public string TerminalId { get; set; }
        public string TabId { get; set; }
        public int? Limit { get; set; }
    }

    public class GetElementContentRequest
    {
        public string TerminalId { get; set; }
        public string TabId { get; set; }
        public string Selector { get; set; }
        public string Property { get; set; }
    }

    public class CaptureScreenshotRequest
    {
        public string TerminalId { get; set; }
        public string TabId { get; set; }
    }

    public class PostBrowserMessageRequest
    {
        public string TerminalId { get; set; }
        public string TabId { get; set; }
        public string Data { get; set; }
    }

    public class GetBrowserMessagesRequest
    {
        public string TerminalId { get; set; }
        public string TabId { get; set; }
        public int? Limit { get; set; }
    }
}
