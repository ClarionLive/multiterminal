using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.Services;

namespace MultiTerminal.MCPServer.Services
{
    /// <summary>
    /// Bridges Claude Code interaction surfaces (elicitation, choice, plan approval,
    /// notification, legacy tool permission) to the Cloudflare Worker permission relay
    /// so agent prompts can be answered from MultiRemote when off-network.
    ///
    /// Flow (elicitation bridge):
    ///   1. ElicitationsController.PostElicitation stores the elicitation in MessageBroker
    ///   2. If enabled, this service POSTs to Worker /permissions and starts a background poll
    ///   3. When the Worker returns status != pending, the decision is written back to
    ///      MessageBroker.SubmitElicitationResponse so the local Node.js hook's 2s poll
    ///      picks it up and unblocks the agent
    ///
    /// Schema-based dispatch on Bridge(ElicitationRequest):
    ///   - no properties                     → tool_permission shape (yes/no, backward-compat)
    ///   - exactly one plain string property → elicitation shape (text input, returns {text})
    ///   - anything else                     → local-only (not relayed)
    ///
    /// Status mapping (tool_permission Worker status → elicitation action):
    ///   approved → accept
    ///   denied   → decline
    ///   expired  → decline   (translates Worker timeout so the agent doesn't hang)
    ///
    /// Config (read at construction, re-read from SettingsService each bridge call):
    ///   permissionRelay.enabled = "1" | "0"
    ///   permissionRelay.baseUrl = "https://mt-mcp-server.clarionlive.workers.dev"
    ///   permissionRelay.apiKey  = "..." (sent as X-API-Key header)
    /// </summary>
    public class PermissionRelayService : IDisposable
    {
        private const string SettingEnabled = "permissionRelay.enabled";
        private const string SettingBaseUrl = "permissionRelay.baseUrl";
        private const string SettingApiKey = "permissionRelay.apiKey";
        private const string DefaultBaseUrl = "https://mt-mcp-server.clarionlive.workers.dev";

        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan PollTimeout = TimeSpan.FromMinutes(5); // aligns with Worker's 5-min expiry + MessageBroker 5-min eviction

        // Expected Worker row ids are short alphanumeric tokens; reject anything else so
        // untrusted response payloads can't steer our subsequent GET/DELETE requests to
        // arbitrary paths on the same host (see PermissionRelayService.cs security audit).
        private static readonly Regex WorkerIdAllowlist = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

        // Cap on how much of a Worker response body we echo into debug logs on the error
        // paths. A reflective / compromised Worker could otherwise pack large payloads
        // (or the sent X-API-Key) into an error body that ends up persisted in log files.
        private const int MaxLoggedBodyChars = 256;

        // Cap on any single HTTP response body we buffer in memory. Expected shape is a
        // few-KB JSON row — 64KB is generous headroom and blocks memory-DoS from a
        // compromised / malicious relay.
        private const int MaxResponseBytes = 64 * 1024;

        private static readonly JsonSerializerOptions RequestSerializerOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly MessageBroker _broker;
        private readonly SettingsService _settings;
        private readonly HttpClient _http;
        private readonly ConcurrentDictionary<string, BridgeEntry> _active = new();
        private bool _disposed;

        private sealed class BridgeEntry
        {
            public CancellationTokenSource Cts { get; }

            // Volatile field (not property): Cancel() may read WorkerId on a different
            // thread than the Task.Run that writes it. Properties can't be volatile in C#,
            // so a public field is used deliberately. Scope is limited — the class itself
            // is private and sealed.
            public volatile string WorkerId;

            public BridgeEntry(CancellationTokenSource cts) { Cts = cts; }
        }

        public PermissionRelayService(MessageBroker broker, SettingsService settings)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10),
                MaxResponseContentBufferSize = MaxResponseBytes
            };
        }

        // ===================================================================
        // Public API
        // ===================================================================

        /// <summary>
        /// Read-only check matching the gate inside Bridge/BridgeChoice/BridgePlanApproval/Notify.
        /// Callers (e.g. the smoke-test controller) can surface an accurate enabled/disabled
        /// status without relying on the fire-and-forget paths which silently no-op when off.
        /// </summary>
        public bool IsRelayEnabled() => IsEnabled(out _, out _);

        /// <summary>
        /// Begins bridging an elicitation to the Cloudflare Worker. Fire-and-forget —
        /// callers should not await or rely on the return for ordering.
        /// If the relay is disabled or misconfigured, returns immediately with no effect.
        /// </summary>
        public void Bridge(ElicitationRequest elicitation)
        {
            // remoteMode gate — no phone pushes when user is at the desk.
            if (!_broker.IsRemoteMode) return;

            if (elicitation == null || string.IsNullOrWhiteSpace(elicitation.ElicitationId))
                return;

            if (!IsEnabled(out var baseUrl, out var apiKey))
                return;

            // Inspect the elicitation schema to pick the right wire shape. We only relay
            // shapes we can round-trip without data loss: yes/no (no form fields) and
            // single-string-field forms. Anything richer (enum, number, multi-field) stays
            // local because the Worker contract doesn't express those inputs yet.
            var shape = ClassifySchema(elicitation.SchemaJson, out var stringFieldName);
            if (shape == SchemaShape.Unsupported)
            {
                _broker.DebugLogService?.Info("PermissionRelay",
                    $"Skipping relay for elicitation {ScrubForLog(elicitation.ElicitationId)} — schema shape not supported (local-only)");
                return;
            }

            if (_active.ContainsKey(elicitation.ElicitationId))
                return;

            var cts = new CancellationTokenSource();
            var entry = new BridgeEntry(cts);
            if (!_active.TryAdd(elicitation.ElicitationId, entry))
            {
                cts.Dispose();
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    object body = BuildElicitationBody(elicitation, shape);

                    var workerId = await PostCreateAsync(body, baseUrl, apiKey, cts.Token).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(workerId))
                        return;

                    entry.WorkerId = workerId;

                    // Race: if Cancel() fired while PostCreateAsync was in flight, it couldn't
                    // DELETE the Worker row because WorkerId was still null. Catch it now.
                    // Note: if Cancel() fired AFTER entry.WorkerId was published but before
                    // this check, both paths will fire DELETE — that's harmless because the
                    // Worker endpoint is idempotent (returns 204 for any id).
                    if (cts.IsCancellationRequested)
                    {
                        _ = DeleteWorkerRowAsync(workerId, baseUrl, apiKey);
                        return;
                    }

                    var (outcome, row) = await PollUntilDecidedAsync(workerId, baseUrl, apiKey,
                        shouldSkip: () =>
                            _broker.GetElicitation(elicitation.ElicitationId) == null ||
                            _broker.GetElicitationResponse(elicitation.ElicitationId) != null,
                        cts.Token).ConfigureAwait(false);
                    if (outcome == PollOutcome.Skipped)
                        return;

                    ElicitationResponse response = BuildElicitationResponse(shape, stringFieldName, row, workerId);
                    if (response == null)
                        return;

                    if (_broker.SubmitElicitationResponse(elicitation.ElicitationId, response))
                    {
                        _broker.DebugLogService?.Info("PermissionRelay",
                            $"Bridged {shape} {(row?.Status ?? "timeout")}→{response.Action} for elicitation {ScrubForLog(elicitation.ElicitationId)}");
                    }
                }
                catch (OperationCanceledException) { /* cancelled — expected */ }
                catch (Exception ex)
                {
                    _broker.DebugLogService?.Error("PermissionRelay",
                        $"Bridge failed for elicitation {ScrubForLog(elicitation.ElicitationId)}: {ScrubForLog(ex.Message)}");
                }
                finally
                {
                    if (_active.TryRemove(elicitation.ElicitationId, out var removed))
                        removed.Cts.Dispose();
                }
            });
        }

        /// <summary>
        /// Cancel the background poll for a given elicitation (e.g. if it was answered locally).
        /// </summary>
        public void Cancel(string elicitationId)
        {
            if (_active.TryGetValue(elicitationId, out var entry))
            {
                try { entry.Cts.Cancel(); } catch { /* already disposed */ }

                // If PostCreateAsync already created a Worker row, clean it up so the phone
                // doesn't show a stale card and D1 doesn't accumulate orphans. If the push
                // is still in flight, the Task.Run completion path catches the race via
                // cts.IsCancellationRequested after PostCreateAsync returns. A narrow
                // overlap can cause BOTH paths to fire DELETE — idempotent, so harmless.
                var workerId = entry.WorkerId;
                if (!string.IsNullOrEmpty(workerId) && IsEnabled(out var baseUrl, out var apiKey))
                {
                    _ = DeleteWorkerRowAsync(workerId, baseUrl, apiKey);
                }
            }
        }

        /// <summary>
        /// Relay a choice request (from AskUserQuestion, menu picker, etc.) and await
        /// the user's selection. Returns the chosen <c>value</c> string exactly as it
        /// was sent (no trim / normalization — must round-trip to match the Worker's
        /// stored options), or null on timeout, cancellation, relay-disabled, or error.
        /// </summary>
        public async Task<string> BridgeChoiceAsync(
            string agentName,
            string prompt,
            IEnumerable<(string label, string value)> options,
            string description = null,
            CancellationToken ct = default)
        {
            // remoteMode gate — no phone pushes when user is at the desk.
            if (!_broker.IsRemoteMode) return null;

            if (!IsEnabled(out var baseUrl, out var apiKey))
                return null;

            var opts = options?.Select(o => new { label = o.label, value = o.value }).ToArray() ?? Array.Empty<object>();
            var body = new
            {
                request_type = "choice",
                agent_name = agentName ?? "unknown",
                prompt = prompt ?? string.Empty,
                options = opts,
                description = string.IsNullOrWhiteSpace(description) ? null : description
            };

            try
            {
                var workerId = await PostCreateAsync(body, baseUrl, apiKey, ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(workerId)) return null;

                var (outcome, row) = await PollUntilDecidedAsync(workerId, baseUrl, apiKey, shouldSkip: null, ct).ConfigureAwait(false);
                if (outcome != PollOutcome.Decided || row == null || row.Status != "answered")
                    return null;

                return new PermissionResponse(row.Response).AsValue();
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                _broker.DebugLogService?.Error("PermissionRelay",
                    $"BridgeChoice failed: {ScrubForLog(ex.Message)}");
                return null;
            }
        }

        /// <summary>
        /// High-level "ask the owner a question" bridge used by the ask_owner MCP tool.
        /// Thin wrapper over <see cref="BridgeChoiceAsync"/> that:
        ///   1. Reports remote-mode OFF explicitly (<see cref="AskOwnerResult.SourceLocal"/>)
        ///      so the agent knows to fall back to a chat-side question.
        ///   2. Honors a caller-supplied timeout shorter than the 5-min Worker TTL by
        ///      linking a cancel-after CTS onto the caller's token.
        ///   3. Distinguishes "owner answered" from "timed out, used default" from
        ///      "timed out, no default" on the wire so the caller can branch.
        /// </summary>
        public async Task<AskOwnerResult> BridgeAskOwnerAsync(
            string agentName,
            string prompt,
            IEnumerable<(string label, string value)> options,
            string description = null,
            int? timeoutSeconds = null,
            string defaultOnTimeout = null,
            CancellationToken ct = default)
        {
            // Remote-mode gate is reported explicitly so the caller (REST controller /
            // ask_owner MCP tool) can fall back to a local chat question instead of
            // silently hanging. This mirrors BridgeChoiceAsync's gate but makes the
            // "why was this null" question answerable on the wire.
            if (!_broker.IsRemoteMode)
                return new AskOwnerResult(null, AskOwnerResult.SourceLocal);

            CancellationTokenSource linkedCts = null;
            try
            {
                CancellationToken effectiveCt = ct;
                if (timeoutSeconds.HasValue && timeoutSeconds.Value > 0)
                {
                    linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    linkedCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds.Value));
                    effectiveCt = linkedCts.Token;
                }

                var answer = await BridgeChoiceAsync(agentName, prompt, options, description, effectiveCt).ConfigureAwait(false);
                if (answer != null)
                    return new AskOwnerResult(answer, AskOwnerResult.SourceOwner);

                // Null from BridgeChoiceAsync collapses three states (our linked timeout,
                // relay-disabled, transport error) into one. For ask_owner semantics we
                // treat all three as "owner didn't answer in time": if the caller wanted
                // a default, use it; otherwise report timeout so the agent can decide.
                return defaultOnTimeout != null
                    ? new AskOwnerResult(defaultOnTimeout, AskOwnerResult.SourceDefault)
                    : new AskOwnerResult(null, AskOwnerResult.SourceTimeout);
            }
            finally
            {
                linkedCts?.Dispose();
            }
        }

        /// <summary>
        /// Relay a plan-approval request (markdown plan sent when ExitPlanMode fires)
        /// and await the user's decision. Returns the decision + optional revise comment,
        /// or null on timeout, cancellation, relay-disabled, or error.
        /// </summary>
        public async Task<PlanApprovalResult> BridgePlanApprovalAsync(
            string agentName,
            string markdownPlan,
            string description = null,
            CancellationToken ct = default)
        {
            // remoteMode gate — no phone pushes when user is at the desk.
            if (!_broker.IsRemoteMode) return null;

            if (!IsEnabled(out var baseUrl, out var apiKey))
                return null;

            var body = new
            {
                request_type = "plan_approval",
                agent_name = agentName ?? "unknown",
                prompt = markdownPlan ?? string.Empty,
                description = string.IsNullOrWhiteSpace(description) ? null : description
            };

            try
            {
                var workerId = await PostCreateAsync(body, baseUrl, apiKey, ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(workerId)) return null;

                var (outcome, row) = await PollUntilDecidedAsync(workerId, baseUrl, apiKey, shouldSkip: null, ct).ConfigureAwait(false);
                if (outcome != PollOutcome.Decided || row == null || row.Status != "answered")
                    return null;

                var parsed = new PermissionResponse(row.Response);
                var decision = parsed.AsDecision();
                if (string.IsNullOrEmpty(decision)) return null;

                return new PlanApprovalResult(decision, parsed.AsComment());
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                _broker.DebugLogService?.Error("PermissionRelay",
                    $"BridgePlanApproval failed: {ScrubForLog(ex.Message)}");
                return null;
            }
        }

        /// <summary>
        /// Fire a notification ping to the phone (build finished, test passed, etc.).
        /// No response tracking — the phone displays until the user dismisses or the
        /// Worker TTL (5 min) expires. Safe to call whether or not the relay is enabled.
        /// </summary>
        public void Notify(string agentName, string description)
        {
            // remoteMode gate — no phone pushes when user is at the desk.
            if (!_broker.IsRemoteMode) return;

            if (!IsEnabled(out var baseUrl, out var apiKey))
                return;

            var body = new
            {
                request_type = "notification",
                agent_name = agentName ?? "unknown",
                description = description ?? string.Empty
            };

            _ = Task.Run(async () =>
            {
                try
                {
                    await PostCreateAsync(body, baseUrl, apiKey, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _broker.DebugLogService?.Error("PermissionRelay",
                        $"Notify failed: {ScrubForLog(ex.Message)}");
                }
            });
        }

        // ===================================================================
        // Shape dispatch (item 6)
        // ===================================================================

        private enum SchemaShape
        {
            YesNo,         // no properties → tool_permission (legacy)
            SingleString,  // exactly one plain string property → elicitation (new)
            Unsupported    // multi-field, enum, number, malformed → keep local
        }

        // Classifies an elicitation's JSON Schema to decide which wire shape to use.
        // Returns the property name for SingleString (so we can rebuild contentJson after
        // the phone answers). YesNo shapes have stringFieldName=null.
        //
        // Fail closed: anything we cannot verify as yes/no-only or single-string-only is
        // treated as Unsupported (local-only). This matches the original HasFormFields
        // contract — no silent data loss if we can't express the form on the wire.
        private SchemaShape ClassifySchema(string schemaJson, out string stringFieldName)
        {
            stringFieldName = null;
            if (string.IsNullOrWhiteSpace(schemaJson))
                return SchemaShape.YesNo;

            try
            {
                using var doc = JsonDocument.Parse(schemaJson);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return SchemaShape.Unsupported;
                if (!root.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
                    return SchemaShape.YesNo;

                JsonElement firstProp = default;
                string firstName = null;
                int count = 0;
                foreach (var p in props.EnumerateObject())
                {
                    if (count == 0)
                    {
                        firstProp = p.Value;
                        firstName = p.Name;
                    }
                    count++;
                    if (count > 1)
                        return SchemaShape.Unsupported;
                }
                if (count == 0)
                    return SchemaShape.YesNo;

                // Single property — must be a plain string (no enum constraint).
                if (firstProp.ValueKind != JsonValueKind.Object) return SchemaShape.Unsupported;
                if (!firstProp.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String) return SchemaShape.Unsupported;
                if (typeEl.GetString() != "string") return SchemaShape.Unsupported;
                if (firstProp.TryGetProperty("enum", out _)) return SchemaShape.Unsupported;

                stringFieldName = firstName;
                return SchemaShape.SingleString;
            }
            catch (Exception ex)
            {
                _broker.DebugLogService?.Warning("PermissionRelay",
                    $"Schema parse failed, treating as local-only: {ScrubForLog(ex.Message)}");
                return SchemaShape.Unsupported;
            }
        }

        private static object BuildElicitationBody(ElicitationRequest elicitation, SchemaShape shape)
        {
            var agent = elicitation.AgentName ?? "unknown";
            var msg = elicitation.Message ?? string.Empty;
            var mcp = string.IsNullOrWhiteSpace(elicitation.McpServerName) ? null : elicitation.McpServerName;

            return shape == SchemaShape.YesNo
                ? (object)new
                {
                    agent_name = agent,
                    tool_name = mcp ?? "Agent Request",
                    description = msg
                }
                : new
                {
                    request_type = "elicitation",
                    agent_name = agent,
                    prompt = msg,
                    description = mcp // phone banner hint; omitted if null by serializer
                };
        }

        // Translates a polled Worker row (or null for poll timeout) into the
        // ElicitationResponse the local MCP hook is waiting for. Returns null if the
        // response should be suppressed (e.g. unknown Worker status — log and drop).
        private ElicitationResponse BuildElicitationResponse(SchemaShape shape, string stringFieldName, WorkerRow row, string workerId)
        {
            // Poll timeout path — both shapes decline so the hook unblocks.
            // Skip if a local response already landed (MessageBroker is last-write-wins,
            // don't clobber a real answer with a timeout decline).
            if (row == null)
                return new ElicitationResponse { Action = "decline", ContentJson = "{}" };

            if (shape == SchemaShape.YesNo)
            {
                var action = MapStatusToAction(row.Status);
                if (action == null)
                {
                    _broker.DebugLogService?.Warning("PermissionRelay",
                        $"Unknown Worker status '{ScrubForLog(row.Status)}' for {workerId} — dropping bridge");
                    return null;
                }
                return new ElicitationResponse { Action = action, ContentJson = "{}" };
            }

            // SingleString: expect status="answered" + response={text:"..."}
            if (row.Status != "answered")
                return new ElicitationResponse { Action = "decline", ContentJson = "{}" };

            var text = new PermissionResponse(row.Response).AsText() ?? string.Empty;
            var contentJson = JsonSerializer.Serialize(new Dictionary<string, string> { [stringFieldName] = text });
            return new ElicitationResponse { Action = "accept", ContentJson = contentJson };
        }

        private static string MapStatusToAction(string status)
        {
            return status switch
            {
                "approved" => "accept",
                "denied" => "decline",
                "expired" => "decline", // Diana's suggestion: translate timeout to decline
                _ => null
            };
        }

        // ===================================================================
        // HTTP primitives
        // ===================================================================

        private bool IsEnabled(out string baseUrl, out string apiKey)
        {
            baseUrl = null;
            apiKey = null;

            var enabled = _settings.Get(SettingEnabled);
            if (enabled != "1" && !string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
                return false;

            apiKey = _settings.Get(SettingApiKey);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _broker.DebugLogService?.Warning("PermissionRelay",
                    "permissionRelay.enabled=1 but permissionRelay.apiKey is empty — bridge disabled");
                return false;
            }

            baseUrl = _settings.Get(SettingBaseUrl);
            if (string.IsNullOrWhiteSpace(baseUrl))
                baseUrl = DefaultBaseUrl;
            baseUrl = baseUrl.TrimEnd('/');

            return true;
        }

        // Generic create: serializes any body shape and returns the Worker's row id.
        // Null bodies, malformed responses, and non-2xx statuses all return null so
        // callers can bail cleanly without exceptions.
        private async Task<string> PostCreateAsync<TBody>(TBody body, string baseUrl, string apiKey, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/permissions")
            {
                Content = new StringContent(JsonSerializer.Serialize(body, RequestSerializerOptions), Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("X-API-Key", apiKey);

            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var json = await res.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!res.IsSuccessStatusCode)
            {
                _broker.DebugLogService?.Warning("PermissionRelay",
                    $"Worker POST /permissions returned {(int)res.StatusCode}: {ScrubForLog(json)}");
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
                    return null;

                var raw = id.GetString();
                if (!WorkerIdAllowlist.IsMatch(raw ?? string.Empty))
                {
                    _broker.DebugLogService?.Warning("PermissionRelay",
                        $"Rejecting Worker id with unexpected format: {ScrubForLog(raw)}");
                    return null;
                }
                return raw;
            }
            catch (Exception ex)
            {
                _broker.DebugLogService?.Warning("PermissionRelay",
                    $"Could not parse Worker response: {ScrubForLog(ex.Message)} / body={ScrubForLog(json)}");
                return null;
            }
        }

        // Fire-and-forget DELETE to clean up a Worker row when the elicitation was
        // answered locally. Idempotent on the Worker side (returns 204 for missing ids).
        // Never throws; logs failures at Warning level.
        private async Task DeleteWorkerRowAsync(string workerId, string baseUrl, string apiKey)
        {
            if (string.IsNullOrEmpty(workerId) || string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiKey))
                return;

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Delete, $"{baseUrl}/permissions/{Uri.EscapeDataString(workerId)}");
                req.Headers.TryAddWithoutValidation("X-API-Key", apiKey);

                using var res = await _http.SendAsync(req).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                {
                    _broker.DebugLogService?.Warning("PermissionRelay",
                        $"Worker DELETE /permissions/{workerId} returned {(int)res.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                _broker.DebugLogService?.Warning("PermissionRelay",
                    $"Worker DELETE /permissions/{workerId} failed: {ScrubForLog(ex.Message)}");
            }
        }

        // Polls the Worker until a non-pending row arrives, the elicitation is answered
        // locally, or the 5-min deadline hits. Transient fetch failures are logged and
        // retried. Three distinct outcomes so callers don't conflate "skip submission"
        // with "submit a timeout decline":
        //   Decided  — Worker produced a terminal row; caller builds response from row
        //   TimedOut — 5-min deadline elapsed with no Worker answer; caller submits decline
        //   Skipped  — elicitation expired or was answered locally; caller does nothing
        private enum PollOutcome { Decided, TimedOut, Skipped }

        private async Task<(PollOutcome outcome, WorkerRow row)> PollUntilDecidedAsync(string workerId, string baseUrl, string apiKey, Func<bool> shouldSkip, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + PollTimeout;

            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);

                if (shouldSkip != null && shouldSkip())
                    return (PollOutcome.Skipped, null);

                WorkerRow row;
                try
                {
                    row = await FetchRowAsync(workerId, baseUrl, apiKey, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _broker.DebugLogService?.Warning("PermissionRelay",
                        $"Poll error for {workerId}: {ScrubForLog(ex.Message)} — will retry");
                    continue;
                }

                if (row == null || string.IsNullOrEmpty(row.Status) || row.Status == "pending")
                    continue;

                return (PollOutcome.Decided, row);
            }

            // Timed out. Re-check shouldSkip so a late local answer doesn't get clobbered
            // (MessageBroker is last-write-wins).
            if (shouldSkip != null && shouldSkip())
                return (PollOutcome.Skipped, null);

            return (PollOutcome.TimedOut, null);
        }

        // Fetches the full Worker row (id, status, request_type, response). Callers
        // typed-parse the response JsonElement per shape.
        private async Task<WorkerRow> FetchRowAsync(string workerId, string baseUrl, string apiKey, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/permissions/{Uri.EscapeDataString(workerId)}/status");
            req.Headers.TryAddWithoutValidation("X-API-Key", apiKey);

            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                _broker.DebugLogService?.Warning("PermissionRelay",
                    $"Worker GET /permissions/{workerId}/status returned {(int)res.StatusCode} — treating as pending, will retry");
                return null;
            }

            var json = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var row = new WorkerRow
                {
                    Id = TryReadString(root, "id"),
                    Status = TryReadString(root, "status"),
                    RequestType = TryReadString(root, "request_type")
                };
                // Clone the response element because the owning JsonDocument is disposed.
                if (root.TryGetProperty("response", out var respEl) && respEl.ValueKind != JsonValueKind.Null)
                    row.Response = respEl.Clone();
                return row;
            }
            catch
            {
                return null;
            }
        }

        // ===================================================================
        // JSON helpers
        // ===================================================================

        private static string TryReadString(JsonElement el, string prop)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty(prop, out var v)) return null;
            return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        // Sanitizes an untrusted string for debug logs: replaces control characters
        // (CR/LF/TAB -> space, other control chars -> '?') so a malicious/reflective
        // Worker can't forge spoofed entries in the text log file, and caps the total
        // length so it can't pack the API key or PII into a persisted log record.
        private static string ScrubForLog(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;

            var sb = new StringBuilder(Math.Min(s.Length, MaxLoggedBodyChars + 1));
            foreach (var c in s)
            {
                // Avoid orphaning a high surrogate at the truncation boundary — stop one
                // code unit early so the pair isn't split. The low surrogate (if any) is
                // dropped with the rest of the tail.
                if (sb.Length >= MaxLoggedBodyChars - 1 && char.IsHighSurrogate(c))
                {
                    sb.Append('…');
                    break;
                }

                if (c == '\r' || c == '\n' || c == '\t') sb.Append(' ');
                else if (char.IsControl(c)) sb.Append('?');
                else sb.Append(c);

                if (sb.Length >= MaxLoggedBodyChars) { sb.Append('…'); break; }
            }
            return sb.ToString();
        }

        // ===================================================================
        // Disposal
        // ===================================================================

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (disposing)
            {
                foreach (var kvp in _active)
                {
                    try { kvp.Value.Cts.Cancel(); kvp.Value.Cts.Dispose(); } catch { }
                }
                _active.Clear();
                _http.Dispose();
            }
        }

        // ===================================================================
        // Types
        // ===================================================================

        // Parsed Worker row. Response is a cloned JsonElement so per-type callers can
        // destructure via TryGetProperty. Undefined when no response yet.
        private sealed class WorkerRow
        {
            public string Id { get; set; }
            public string Status { get; set; }
            public string RequestType { get; set; }
            public JsonElement Response { get; set; }
        }
    }
}
