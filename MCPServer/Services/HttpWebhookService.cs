using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace MultiTerminal.MCPServer.Services
{
    /// <summary>
    /// HTTP webhook server for receiving agent ready notifications.
    /// Listens on http://localhost:5000/ for POST requests from SessionStart hooks.
    /// </summary>
    public class HttpWebhookService : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly MessageBroker _broker;
        private CancellationTokenSource _cancellationTokenSource;
        private Task _listenerTask;
        private bool _isDisposed;
        private const int Port = 5000;

        /// <summary>
        /// Raised when an agent sends a ready notification via webhook.
        /// </summary>
        public event EventHandler<AgentReadyEventArgs> AgentReady;

        /// <summary>
        /// Raised when a message is delivered via webhook.
        /// </summary>
        public event EventHandler<MessageDeliveredEventArgs> MessageDelivered;

        public HttpWebhookService(MessageBroker broker)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{Port}/");
        }

        /// <summary>
        /// Starts the HTTP listener on a background thread.
        /// </summary>
        public void Start()
        {
            if (_listenerTask != null)
            {
                System.Diagnostics.Trace.WriteLine("[HttpWebhookService] Already started");
                return;
            }

            try
            {
                _listener.Start();
                _cancellationTokenSource = new CancellationTokenSource();
                _listenerTask = Task.Run(() => ListenAsync(_cancellationTokenSource.Token));
                System.Diagnostics.Trace.WriteLine($"[HttpWebhookService] Started on http://localhost:{Port}/");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[HttpWebhookService] Failed to start: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Stops the HTTP listener.
        /// </summary>
        public void Stop()
        {
            if (_cancellationTokenSource != null)
            {
                _cancellationTokenSource.Cancel();
                _listener.Stop();
                // Don't block waiting for the listener task — cancellation + Stop()
                // is sufficient. The process exit failsafe handles any stragglers.
                System.Diagnostics.Trace.WriteLine("[HttpWebhookService] Stopped");
            }
        }

        private async Task ListenAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequestAsync(context), cancellationToken);
                }
                catch (HttpListenerException ex) when (ex.ErrorCode == 995) // ERROR_OPERATION_ABORTED
                {
                    // Listener was stopped, this is expected
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"[HttpWebhookService] Error accepting request: {ex.Message}");
                }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                System.Diagnostics.Trace.WriteLine($"[HttpWebhookService] {request.HttpMethod} {request.Url.AbsolutePath}");

                // Handle /agent-ready endpoint
                if (request.Url.AbsolutePath == "/agent-ready" && request.HttpMethod == "POST")
                {
                    await HandleAgentReadyAsync(context);
                }
                else if (request.Url.AbsolutePath == "/message" && request.HttpMethod == "POST")
                {
                    await HandleMessageAsync(context);
                }
                else if (request.Url.AbsolutePath == "/health" && request.HttpMethod == "GET")
                {
                    // Health check endpoint
                    await SendResponseAsync(response, 200, "OK");
                }
                else
                {
                    // 404 for unknown endpoints
                    await SendResponseAsync(response, 404, "Not Found");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[HttpWebhookService] Error handling request: {ex.Message}");
                await SendResponseAsync(response, 500, "Internal Server Error");
            }
        }

        private async Task HandleAgentReadyAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            string agentName = null;
            string docId = null;

            // Parse query string parameters
            var queryParams = request.QueryString;
            agentName = queryParams["name"];
            docId = queryParams["docId"];

            // If not in query string, try reading from POST body
            if (string.IsNullOrEmpty(agentName) || string.IsNullOrEmpty(docId))
            {
                using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                {
                    var body = await reader.ReadToEndAsync();
                    var bodyParams = HttpUtility.ParseQueryString(body);
                    agentName = agentName ?? bodyParams["name"];
                    docId = docId ?? bodyParams["docId"];
                }
            }

            if (string.IsNullOrEmpty(agentName))
            {
                await SendResponseAsync(response, 400, "Missing 'name' parameter");
                return;
            }

            if (string.IsNullOrEmpty(docId))
            {
                await SendResponseAsync(response, 400, "Missing 'docId' parameter");
                return;
            }

            System.Diagnostics.Trace.WriteLine($"[HttpWebhookService] Agent ready: {agentName} (docId: {docId})");

            // Mark agent as ready in broker
            var success = _broker.MarkAgentReady(agentName, docId);

            if (success)
            {
                // Raise event
                AgentReady?.Invoke(this, new AgentReadyEventArgs
                {
                    AgentName = agentName,
                    DocId = docId,
                    Timestamp = DateTime.UtcNow
                });

                await SendResponseAsync(response, 200, $"Agent {agentName} marked as ready");
            }
            else
            {
                await SendResponseAsync(response, 404, $"Agent {agentName} not found (not registered yet)");
            }
        }

        private async Task HandleMessageAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            string messageId = null;
            string to = null;
            string from = null;
            string content = null;

            // Parse query string parameters
            var queryParams = request.QueryString;
            messageId = queryParams["messageId"];
            to = queryParams["to"];
            from = queryParams["from"];
            content = queryParams["content"];

            // If not in query string, try reading from POST body (JSON or form-encoded)
            if (string.IsNullOrEmpty(messageId) || string.IsNullOrEmpty(to))
            {
                using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                {
                    var body = await reader.ReadToEndAsync();

                    // Try JSON first
                    if (request.ContentType?.Contains("application/json") == true)
                    {
                        try
                        {
                            var json = System.Text.Json.JsonDocument.Parse(body);
                            messageId = messageId ?? json.RootElement.GetProperty("messageId").GetString();
                            to = to ?? json.RootElement.GetProperty("to").GetString();
                            from = from ?? json.RootElement.GetProperty("from").GetString();
                            content = content ?? json.RootElement.GetProperty("content").GetString();
                        }
                        catch { /* Fall through to form-encoded */ }
                    }

                    // Try form-encoded
                    if (string.IsNullOrEmpty(messageId) || string.IsNullOrEmpty(to))
                    {
                        var bodyParams = HttpUtility.ParseQueryString(body);
                        messageId = messageId ?? bodyParams["messageId"];
                        to = to ?? bodyParams["to"];
                        from = from ?? bodyParams["from"];
                        content = content ?? bodyParams["content"];
                    }
                }
            }

            // Validate required fields
            if (string.IsNullOrEmpty(messageId))
            {
                await SendResponseAsync(response, 400, "Missing 'messageId' parameter");
                return;
            }

            if (string.IsNullOrEmpty(to))
            {
                await SendResponseAsync(response, 400, "Missing 'to' parameter");
                return;
            }

            if (string.IsNullOrEmpty(from))
            {
                await SendResponseAsync(response, 400, "Missing 'from' parameter");
                return;
            }

            if (string.IsNullOrEmpty(content))
            {
                await SendResponseAsync(response, 400, "Missing 'content' parameter");
                return;
            }

            System.Diagnostics.Trace.WriteLine($"[HttpWebhookService] Message webhook: {messageId} from {from} to {to}");

            // Deliver message via broker
            var success = await _broker.DeliverMessageViaWebhook(messageId, to, from, content);

            if (success)
            {
                // Raise event for UI updates (optional)
                MessageDelivered?.Invoke(this, new MessageDeliveredEventArgs
                {
                    MessageId = messageId,
                    From = from,
                    To = to,
                    Timestamp = DateTime.UtcNow
                });

                await SendResponseAsync(response, 200, $"Message {messageId} delivered to {to}");
            }
            else
            {
                await SendResponseAsync(response, 404, $"Recipient {to} not found or delivery failed");
            }
        }

        private async Task SendResponseAsync(HttpListenerResponse response, int statusCode, string message)
        {
            response.StatusCode = statusCode;
            response.ContentType = "text/plain";

            var buffer = Encoding.UTF8.GetBytes(message);
            response.ContentLength64 = buffer.Length;

            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            response.Close();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed) return;
            if (disposing)
            {
                Stop();
                _listener?.Close();
                _cancellationTokenSource?.Dispose();
            }
            _isDisposed = true;
        }
    }

    /// <summary>
    /// Event args for agent ready notifications.
    /// </summary>
    public class AgentReadyEventArgs : EventArgs
    {
        public string AgentName { get; set; }
        public string DocId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Event args for message delivery notifications.
    /// </summary>
    public class MessageDeliveredEventArgs : EventArgs
    {
        public string MessageId { get; set; }
        public string From { get; set; }
        public string To { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
