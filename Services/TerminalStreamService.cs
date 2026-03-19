using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MultiTerminal.Terminal;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Bridges WebSocket clients to ConPTY terminal instances.
    /// Binary WebSocket frames carry raw terminal I/O (VT/ANSI escape sequences).
    /// Text WebSocket frames carry JSON control messages (resize, disconnect).
    /// Supports multiple concurrent subscribers per terminal.
    /// </summary>
    public class TerminalStreamService
    {
        /// <summary>
        /// Represents a single WebSocket subscriber attached to a terminal.
        /// </summary>
        private class Subscriber
        {
            public WebSocket Socket { get; set; }
            public CancellationTokenSource Cts { get; set; }
            // Serializes SendAsync calls — WebSocket throws if concurrent sends overlap
            public SemaphoreSlim SendLock { get; } = new SemaphoreSlim(1, 1);
        }

        /// <summary>
        /// Thread-safe subscriber list for a single terminal.
        /// Lock + List is correct and simple for low subscriber counts (1-2 phone clients).
        /// </summary>
        private class SubscriberList
        {
            private readonly object _lock = new();
            private readonly List<Subscriber> _subscribers = new();

            public void Add(Subscriber subscriber)
            {
                lock (_lock) { _subscribers.Add(subscriber); }
            }

            public void Remove(Subscriber subscriber)
            {
                lock (_lock) { _subscribers.Remove(subscriber); }
            }

            public int CountOpen()
            {
                lock (_lock) { return _subscribers.Count(s => s.Socket.State == WebSocketState.Open); }
            }

            public bool IsEmpty()
            {
                lock (_lock) { return _subscribers.Count == 0; }
            }

            public bool HasOpenSubscribers()
            {
                lock (_lock) { return _subscribers.Any(s => s.Socket.State == WebSocketState.Open); }
            }

            /// <summary>
            /// Gets a snapshot of open subscribers, purging dead ones in the same pass.
            /// </summary>
            public List<Subscriber> GetOpenAndPurgeDead()
            {
                lock (_lock)
                {
                    var dead = _subscribers.Where(s => s.Socket.State != WebSocketState.Open).ToList();
                    foreach (var d in dead)
                        _subscribers.Remove(d);

                    return _subscribers.ToList();
                }
            }
        }

        // terminalId → subscriber list
        private readonly ConcurrentDictionary<string, SubscriberList> _subscribers = new();

        // Callback to resolve a terminal DocId to its ConPtyTerminal instance.
        // Set by MainForm during startup wiring.
        private volatile Func<string, ConPtyTerminal> _terminalResolver;

        // Track which terminals we've subscribed DataReceived on
        private readonly ConcurrentDictionary<string, bool> _hookedTerminals = new();

        /// <summary>
        /// Sets the function that resolves a terminal identifier to its ConPtyTerminal instance.
        /// </summary>
        public void SetTerminalResolver(Func<string, ConPtyTerminal> resolver)
        {
            _terminalResolver = resolver;
        }

        /// <summary>
        /// Handles a new WebSocket connection for the given terminal ID.
        /// Streams terminal output to the client and forwards client input to the terminal.
        /// Returns when the WebSocket closes or the terminal exits.
        /// </summary>
        public async Task HandleConnectionAsync(string terminalId, WebSocket webSocket, CancellationToken cancellationToken)
        {
            if (_terminalResolver == null)
                throw new InvalidOperationException("Terminal resolver not configured");

            var terminal = _terminalResolver(terminalId);
            if (terminal == null)
            {
                var errorBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "error", message = "Terminal not found" }));
                await webSocket.SendAsync(new ArraySegment<byte>(errorBytes), WebSocketMessageType.Text, true, cancellationToken);
                await webSocket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "Terminal not found", cancellationToken);
                return;
            }

            if (!terminal.IsRunning)
            {
                var errorBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "error", message = "Terminal not running" }));
                await webSocket.SendAsync(new ArraySegment<byte>(errorBytes), WebSocketMessageType.Text, true, cancellationToken);
                await webSocket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "Terminal not running", cancellationToken);
                return;
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var subscriber = new Subscriber { Socket = webSocket, Cts = cts };

            // Add to subscriber list
            var list = _subscribers.GetOrAdd(terminalId, _ => new SubscriberList());
            list.Add(subscriber);

            // Hook DataReceived if this is the first subscriber for this terminal
            EnsureDataReceivedHooked(terminalId, terminal);

            // Send initial connection confirmation
            var connectedMsg = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                type = "connected",
                terminalId = terminalId,
                cols = terminal.Columns,
                rows = terminal.Rows
            }));
            await webSocket.SendAsync(new ArraySegment<byte>(connectedMsg), WebSocketMessageType.Text, true, cts.Token);

            System.Diagnostics.Debug.WriteLine($"[TerminalStream] Client connected to terminal {terminalId}");

            try
            {
                // Read loop: receive input from WebSocket client and forward to terminal
                var buffer = new byte[4096];
                while (webSocket.State == WebSocketState.Open && !cts.Token.IsCancellationRequested)
                {
                    WebSocketReceiveResult result;
                    try
                    {
                        result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (WebSocketException)
                    {
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                    else if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        // Binary frame = raw terminal input
                        var inputData = new byte[result.Count];
                        Array.Copy(buffer, inputData, result.Count);
                        terminal.Write(inputData);
                    }
                    else if (result.MessageType == WebSocketMessageType.Text)
                    {
                        // Text frame = JSON control message
                        var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        HandleControlMessage(terminalId, terminal, text);
                    }
                }
            }
            finally
            {
                // Remove subscriber
                list.Remove(subscriber);
                if (list.IsEmpty())
                    _subscribers.TryRemove(terminalId, out _);

                cts.Dispose();
                subscriber.SendLock.Dispose();

                // Close WebSocket if still open
                if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
                {
                    try
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnected", CancellationToken.None);
                    }
                    catch { }
                }

                System.Diagnostics.Debug.WriteLine($"[TerminalStream] Client disconnected from terminal {terminalId}");
            }
        }

        /// <summary>
        /// Gets the number of active subscribers for a terminal.
        /// </summary>
        public int GetSubscriberCount(string terminalId)
        {
            if (_subscribers.TryGetValue(terminalId, out var list))
                return list.CountOpen();
            return 0;
        }

        /// <summary>
        /// Gets all terminal IDs that have active WebSocket subscribers.
        /// </summary>
        public IReadOnlyList<string> GetActiveStreams()
        {
            return _subscribers
                .Where(kvp => kvp.Value.HasOpenSubscribers())
                .Select(kvp => kvp.Key)
                .ToList();
        }

        private void EnsureDataReceivedHooked(string terminalId, ConPtyTerminal terminal)
        {
            // DataReceived hook is intentionally permanent per terminal lifetime.
            // Unhooking on last subscriber leave would require tracking the delegate instance
            // and re-hooking on next connect — not worth the complexity for a lightweight no-op
            // when no subscribers exist (BroadcastRaw early-exits on empty list).
            if (_hookedTerminals.TryAdd(terminalId, true))
            {
                terminal.DataReceived += (data) => BroadcastToSubscribers(terminalId, data);

                terminal.ProcessExited += (sender, e) =>
                {
                    var exitMsg = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "exited" }));
                    BroadcastControlMessage(terminalId, exitMsg);
                    _hookedTerminals.TryRemove(terminalId, out _);
                };
            }
        }

        private void BroadcastToSubscribers(string terminalId, byte[] data)
        {
            BroadcastRaw(terminalId, data, WebSocketMessageType.Binary);
        }

        private void BroadcastControlMessage(string terminalId, byte[] jsonMessage)
        {
            BroadcastRaw(terminalId, jsonMessage, WebSocketMessageType.Text);
        }

        private void BroadcastRaw(string terminalId, byte[] data, WebSocketMessageType messageType)
        {
            if (!_subscribers.TryGetValue(terminalId, out var list))
                return;

            var openSubscribers = list.GetOpenAndPurgeDead();
            if (openSubscribers.Count == 0)
                return;

            foreach (var subscriber in openSubscribers)
            {
                _ = Task.Run(async () =>
                {
                    bool acquired = false;
                    try
                    {
                        await subscriber.SendLock.WaitAsync(subscriber.Cts.Token);
                        acquired = true;
                        await subscriber.Socket.SendAsync(
                            new ArraySegment<byte>(data),
                            messageType,
                            true,
                            subscriber.Cts.Token);
                    }
                    catch (ObjectDisposedException) { }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[TerminalStream] Send error: {ex.Message}");
                        try { subscriber.Cts.Cancel(); } catch (ObjectDisposedException) { }
                    }
                    finally
                    {
                        if (acquired)
                            try { subscriber.SendLock.Release(); } catch (ObjectDisposedException) { }
                    }
                });
            }
        }

        private void HandleControlMessage(string terminalId, ConPtyTerminal terminal, string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeProp))
                    return;

                var type = typeProp.GetString();

                switch (type)
                {
                    case "resize":
                        if (root.TryGetProperty("cols", out var cols) && root.TryGetProperty("rows", out var rows)
                            && cols.TryGetInt32(out int c) && rows.TryGetInt32(out int r))
                        {
                            c = Math.Clamp(c, 1, 500);
                            r = Math.Clamp(r, 1, 200);
                            terminal.Resize(c, r);
                            System.Diagnostics.Debug.WriteLine($"[TerminalStream] Terminal {terminalId} resized to {c}x{r}");
                        }
                        break;

                    default:
                        System.Diagnostics.Debug.WriteLine($"[TerminalStream] Unknown control message type: {type}");
                        break;
                }
            }
            catch (JsonException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TerminalStream] Invalid control message: {ex.Message}");
            }
        }
    }
}
