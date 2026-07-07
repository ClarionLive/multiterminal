using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultiTerminal.Services.Startup
{
    /// <summary>
    /// Self-probes <c>http://127.0.0.1:port/api/health</c> after a :5050 bind failure to
    /// decide whether the holder is another MultiTerminal (task 4fec40e2). Thin I/O adapter:
    /// all classification lives in <see cref="StartupPortContentionClassifier"/>.
    /// </summary>
    public static class StartupHealthProbe
    {
        /// <summary>Default probe timeout. Short — the holder is local; a wedged/foreign holder must not stall startup.</summary>
        public const int DefaultTimeoutMs = 2000;

        /// <summary>
        /// Hard cap on how many bytes of the probe response we read. A genuine health identity
        /// is a few hundred bytes; a hostile process squatting on :5050 could stream a huge body
        /// to exhaust memory during startup (the timeout bounds duration, not size). 64 KB is far
        /// more than any identity payload and far too little to hurt.
        /// </summary>
        private const int MaxBodyBytes = 64 * 1024;

        /// <summary>
        /// Probe the loopback health endpoint. Never throws: a timeout, connection reset,
        /// non-200, or non-MT body all resolve to a <see cref="HealthProbeResult"/> the
        /// classifier reads (unreached / not-MT → treated as unknown/foreign holder).
        /// </summary>
        public static async Task<HealthProbeResult> ProbeAsync(int port, int timeoutMs = DefaultTimeoutMs)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));

            try
            {
                string url = $"http://127.0.0.1:{port}/api/health";
                // ResponseHeadersRead so we can inspect Content-Length and cap the body read
                // BEFORE buffering a potentially hostile response (task 4fec40e2 security finding).
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    // Something answered but not a healthy MT — reached, but not identified as MT.
                    return new HealthProbeResult { Reached = true, IsMultiTerminal = false };
                }

                // Reject an oversized advertised body outright; otherwise read at most MaxBodyBytes.
                if (response.Content.Headers.ContentLength is long len && len > MaxBodyBytes)
                {
                    return new HealthProbeResult { Reached = true, IsMultiTerminal = false };
                }

                var (body, truncated) = await ReadCappedAsync(response, cts.Token).ConfigureAwait(false);
                if (truncated)
                {
                    // Body ran past the cap (e.g. a padded response with no honest Content-Length):
                    // a real identity is tiny, so treat it as not-MT rather than parse it.
                    return new HealthProbeResult { Reached = true, IsMultiTerminal = false };
                }

                return Parse(body);
            }
            catch (Exception)
            {
                // Timeout / connection refused / reset / malformed — holder unknown.
                return HealthProbeResult.NotReached();
            }
        }

        /// <summary>
        /// Synchronous convenience for the WinForms bind-failure path (already on a
        /// background thread). Delegates to <see cref="ProbeAsync"/>.
        /// </summary>
        public static HealthProbeResult Probe(int port, int timeoutMs = DefaultTimeoutMs)
            => ProbeAsync(port, timeoutMs).GetAwaiter().GetResult();

        /// <summary>
        /// Read at most <see cref="MaxBodyBytes"/> from the response stream. A body that fills
        /// the cap is simply truncated — Parse then fails to find a valid identity and reports
        /// not-MT, which is the safe outcome for an oversized/hostile response.
        /// </summary>
        private static async Task<(string Body, bool Truncated)> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
        {
            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await ReadCappedAsync(stream, MaxBodyBytes, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Read at most <paramref name="maxBytes"/> from <paramref name="stream"/>, reporting whether
        /// the body ran PAST the cap (Truncated). Exposed internally so the cap is unit-testable
        /// without a live socket. A truncated body is treated as not-MT by the caller — a genuine
        /// identity is tiny, so a body that fills the cap (especially with no honest Content-Length)
        /// is hostile/padded, not a real health response (task 4fec40e2).
        /// </summary>
        internal static async Task<(string Body, bool Truncated)> ReadCappedAsync(Stream stream, int maxBytes, CancellationToken ct = default)
        {
            var buffer = new byte[maxBytes];
            int total = 0;
            while (total < maxBytes)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(total, maxBytes - total), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            bool truncated = false;
            if (total == maxBytes)
            {
                // Cap filled exactly — probe one more byte to learn whether the body exceeded it.
                var extra = new byte[1];
                int more = await stream.ReadAsync(extra.AsMemory(0, 1), ct).ConfigureAwait(false);
                truncated = more > 0;
            }

            return (System.Text.Encoding.UTF8.GetString(buffer, 0, total), truncated);
        }

        /// <summary>
        /// Parse a health body. A genuine MT host carries <see cref="HealthIdentity.ServiceMarker"/>
        /// in its <c>service</c> field; anything else is "reached but not MT". Exposed internally
        /// so the parse contract can be unit tested without a live socket.
        /// </summary>
        internal static HealthProbeResult Parse(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return new HealthProbeResult { Reached = true, IsMultiTerminal = false };
            }

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("service", out var svc) &&
                    svc.ValueKind == JsonValueKind.String &&
                    string.Equals(svc.GetString(), HealthIdentity.ServiceMarker, StringComparison.Ordinal))
                {
                    var identity = new HealthIdentity
                    {
                        Service = HealthIdentity.ServiceMarker,
                        App = GetString(root, "app", "MultiTerminal"),
                        Version = GetString(root, "version", string.Empty),
                        Machine = GetString(root, "machine", string.Empty),
                        User = GetString(root, "user", string.Empty),
                        Pid = GetInt(root, "pid"),
                        SessionId = GetInt(root, "sessionId"),
                        Port = GetInt(root, "port"),
                        StartedUtc = GetString(root, "startedUtc", string.Empty),
                    };
                    return new HealthProbeResult { Reached = true, IsMultiTerminal = true, Identity = identity };
                }
            }
            catch (JsonException)
            {
                // Non-JSON body from a foreign server — reached, not MT.
            }

            return new HealthProbeResult { Reached = true, IsMultiTerminal = false };
        }

        private static string GetString(JsonElement obj, string name, string fallback)
            => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString() ?? fallback
                : fallback;

        private static int GetInt(JsonElement obj, string name)
            => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v)
                ? v
                : 0;
    }
}
