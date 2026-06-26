using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace MultiTerminal.Services.Presence
{
    /// <summary>
    /// Ingests the MSR-2 presence signals over MQTT (schema: docs/presence-routing.md), normalizes
    /// them into a <see cref="PresenceStateMachine"/>, and drives MT's binary desk/away gate
    /// (<see cref="IRemoteModeSink.SetRemoteMode"/>) on each committed state change (items [1]/[6]).
    ///
    /// Routing is driven from the machine's COMMITTED state under <c>_evalLock</c> (not from the
    /// machine's StateChanged event): every Evaluate+apply is serialized, so concurrent timer-tick
    /// and MQTT-receive calls can never apply SetRemoteMode out of commit order, and the FIRST real
    /// signal establishes remoteMode even though the machine's initial commit fires no event
    /// (pipeline Run-1: Debugger BUG1/BUG2 + Codex adversary HIGH).
    ///
    /// Lifecycle: <see cref="Start"/> spins a supervised connect/subscribe loop with fixed-delay
    /// reconnect; a periodic tick drives debounce-commit + staleness once at least one signal has
    /// arrived. <see cref="Stop"/>/<see cref="Dispose"/> tears it down. The whole thing NO-OPs when
    /// presence.enabled != "1", so it never hijacks the manual remoteMode pill until the Owner opts in.
    ///
    /// OWNERSHIP (Owner decision, pipeline Run-1): when enabled, presence is the AUTHORITATIVE source
    /// of remoteMode — a manual Local/Remote pill toggle is overridden on the next presence transition.
    /// </summary>
    public sealed class PresenceAdapter : IDisposable
    {
        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

        // DoS guards on attacker-influenceable MQTT data (pipeline Run-1 Codex security MEDIUM):
        // presence payloads are tiny ("ON"/"OFF"/"-67"/"online"); anything larger is dropped, and
        // parse-failure logging is rate-limited so a malicious publisher can't flood the debug log.
        private const int MaxPayloadBytes = 256;
        private const int MaxTopicLength = 512;
        private static readonly TimeSpan ParseWarnThrottle = TimeSpan.FromSeconds(5);

        private readonly IRemoteModeSink _remoteMode;
        private readonly SettingsService _settings;
        private readonly Action<string, string> _log; // (level, message); level: "info"|"warn"|"error"

        private PresenceConfig _config;
        private PresenceStateMachine _machine;

        // Disposed via Interlocked.Exchange(ref _client, null) in SafeDisposeClientAsync (the atomic
        // claim that fixes the double-dispose race); CA2213 can't trace disposal through the exchanged
        // local, so suppress the false positive here.
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2213:Disposable fields should be disposed", Justification = "Disposed via the Interlocked.Exchange local in SafeDisposeClientAsync; analyzer can't trace it.")]
        private IMqttClient _client;
        private CancellationTokenSource _cts;
        private Task _runLoop;
        private Timer _tick;
        private bool _disposed;

        // Routing state — all read/written under _evalLock so Evaluate+apply is serialized.
        private readonly object _evalLock = new object();
        private bool _lastDegraded;
        private volatile bool _seenSignal;     // true once any real sensor signal has been parsed
        private DateTime _lastParseWarnUtc = DateTime.MinValue;

        /// <summary>
        /// Creates the adapter. <paramref name="remoteMode"/> is the desk/away gate (MessageBroker in
        /// production, a fake in tests). <paramref name="log"/> takes (level, message); when null,
        /// messages go to Debug output. The REST host wires logging to the broker's DebugLogService.
        /// </summary>
        public PresenceAdapter(IRemoteModeSink remoteMode, SettingsService settings, Action<string, string> log = null)
        {
            _remoteMode = remoteMode ?? throw new ArgumentNullException(nameof(remoteMode));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _log = log ?? ((lvl, msg) => System.Diagnostics.Debug.WriteLine($"[Presence:{lvl}] {msg}"));
        }

        /// <summary>True when presence routing is enabled (presence.enabled == "1").</summary>
        public bool IsEnabled => PresenceConfig.IsEnabled(_settings);

        /// <summary>The current committed presence state (AtDesk before any signal arrives).</summary>
        public PresenceState CurrentState => _machine != null ? _machine.CurrentState : PresenceState.AtDesk;

        /// <summary>
        /// Start the adapter. No-op (with a log line) when disabled or already running. Reads the
        /// config snapshot, builds the state machine, and launches the MQTT supervisor + tick timer.
        /// </summary>
        public void Start()
        {
            if (!IsEnabled)
            {
                _log("info", "presence.enabled != 1 — adapter not starting (manual remoteMode unchanged).");
                return;
            }
            if (_runLoop != null)
                return;

            _config = PresenceConfig.Load(_settings);
            _machine = new PresenceStateMachine(_config);

            // Trust-boundary guard (Owner decision: warn + accept). A non-loopback broker with no
            // credentials means anyone able to publish can flip notification routing — surface it loudly.
            if (!IsLoopback(_config.Mqtt.Host) && string.IsNullOrEmpty(_config.Mqtt.Username))
            {
                _log("warn", $"SECURITY: presence broker '{_config.Mqtt.Host}' is non-loopback but no credentials are set — any device that can publish to it can flip your notification routing. Add broker auth (mqtt.username/password + Mosquitto password_file/ACL) or use a loopback broker. See docs/presence-mosquitto-setup.md.");
            }

            _cts = new CancellationTokenSource();
            _tick = new Timer(OnTick, null, TickInterval, TickInterval);
            _runLoop = Task.Run(() => RunAsync(_cts.Token));
            _log("info", $"Presence adapter starting — broker {_config.Mqtt.Host}:{_config.Mqtt.Port}, prefix '{_config.Mqtt.TopicPrefix}', {_config.Phones.Count} phone(s). Presence is authoritative for remoteMode while enabled.");
        }

        private async Task RunAsync(CancellationToken ct)
        {
            var factory = new MqttFactory();
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _client = factory.CreateMqttClient();
                    _client.ApplicationMessageReceivedAsync += OnMessageAsync;

                    var builder = new MqttClientOptionsBuilder()
                        .WithTcpServer(_config.Mqtt.Host, _config.Mqtt.Port)
                        .WithClientId("multiterminal-presence")
                        .WithCleanSession();
                    if (!string.IsNullOrEmpty(_config.Mqtt.Username))
                        builder = builder.WithCredentials(_config.Mqtt.Username, _config.Mqtt.Password ?? "");

                    await _client.ConnectAsync(builder.Build(), ct);

                    var prefix = (_config.Mqtt.TopicPrefix ?? "mt/presence").TrimEnd('/');
                    var qos = ToQos(_config.Mqtt.Qos);
                    await _client.SubscribeAsync(
                        new MqttClientSubscribeOptionsBuilder()
                            .WithTopicFilter(f => f.WithTopic(prefix + "/#").WithQualityOfServiceLevel(qos))
                            .Build(),
                        ct);

                    _log("info", $"Connected + subscribed to '{prefix}/#'.");

                    // Hold the connection until cancelled or the client drops; the outer loop reconnects.
                    while (!ct.IsCancellationRequested && _client.IsConnected)
                        await Task.Delay(ReconnectDelay, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log("warn", $"MQTT connect/subscribe failed: {ex.Message}. Retrying in {ReconnectDelay.TotalSeconds:0}s.");
                }
                finally
                {
                    await SafeDisposeClientAsync();
                }

                if (ct.IsCancellationRequested)
                    break;
                try
                {
                    await Task.Delay(ReconnectDelay, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e)
        {
            try
            {
                var topic = e.ApplicationMessage != null ? e.ApplicationMessage.Topic : "";
                var payload = DecodePayload(e.ApplicationMessage);
                HandleMessage(topic, payload, DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                LogParseFailureThrottled(ex.Message);
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Parse one (topic, payload) pair against the schema, feed the state machine, and (if a real
        /// signal was parsed) drive routing from the committed state. Side-effect-isolated and internal
        /// so tests can replay sequences without a live broker (item [8]).
        /// </summary>
        internal void HandleMessage(string topic, string payload, DateTimeOffset now)
        {
            if (_machine == null || string.IsNullOrEmpty(topic) || topic.Length > MaxTopicLength)
                return;

            var prefix = (_config.Mqtt.TopicPrefix ?? "mt/presence").TrimEnd('/');
            if (!topic.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                return;
            var rel = topic.Substring(prefix.Length + 1);

            bool parsed = false;
            bool occupancyParsed = false; // the PRIMARY (mmWave) gate — see readiness note below
            if (rel.Equals("desk/occupancy", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseBool(payload, out var present))
                {
                    _machine.SetOccupancy(present, now);
                    parsed = true;
                    occupancyParsed = true;
                }
            }
            else if (rel.Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                // Any status payload is authoritative; unknown/garbage => offline (fail-safe to degraded).
                _machine.SetAvailability(ParseOnline(payload), now);
                parsed = true;
            }
            else if (rel.StartsWith("ble/", StringComparison.OrdinalIgnoreCase))
            {
                // ble/<deviceId>/(rssi|presence)
                var rest = rel.Substring(4);
                int slash = rest.LastIndexOf('/');
                if (slash <= 0)
                    return;
                var deviceId = rest.Substring(0, slash);
                var field = rest.Substring(slash + 1);
                if (field.Equals("rssi", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse((payload ?? "").Trim(), out var rssi))
                    {
                        _machine.SetPhoneRssi(deviceId, rssi, now);
                        parsed = true;
                    }
                }
                else if (field.Equals("presence", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryParseBool(payload, out var present))
                    {
                        _machine.SetPhonePresence(deviceId, present, now);
                        parsed = true;
                    }
                }
            }

            if (parsed)
            {
                // Routing readiness is established ONLY by the primary occupancy signal — never by a
                // status (availability) or BLE message alone. MQTT gives no ordering guarantee for
                // retained topics, so a retained `status=online` (or a BLE reading) can be delivered
                // before retained `desk/occupancy`; acting on it would compute Away from a default-empty
                // machine and transiently flip to phone-push while the user is actually at the desk
                // (pipeline Run-3 adversary HIGH). Once occupancy has been seen once, every subsequent
                // signal (including status/BLE) drives normally.
                if (occupancyParsed)
                    _seenSignal = true;
                if (_seenSignal)
                    DriveFromState(now);
            }
        }

        private void OnTick(object state)
        {
            try
            {
                // Don't drive routing before the first real signal — otherwise an empty machine would
                // compute "Away" (no occupancy, no BLE) and flip to phone-push before any sensor data.
                if (_machine == null || !_seenSignal)
                    return;
                DriveFromState(DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                _log("warn", $"Presence tick failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Evaluate the machine and apply the committed state to the remoteMode gate. Serialized under
        /// _evalLock so concurrent tick + message calls apply in commit order (fixes the out-of-order
        /// race), and so the FIRST evaluation establishes remoteMode even though the machine's initial
        /// commit fires no StateChanged event (fixes away-at-startup never routing). Idempotent: only
        /// calls SetRemoteMode when the mapped value actually changes.
        /// </summary>
        private void DriveFromState(DateTimeOffset now)
        {
            lock (_evalLock)
            {
                var state = _machine.Evaluate(now);

                // Graceful-degradation logging (item [3]) — immediate (runs on message + tick paths),
                // edge-triggered so it can't spam.
                bool degraded = _machine.IsDegraded;
                if (degraded != _lastDegraded)
                {
                    _lastDegraded = degraded;
                    _log(
                        degraded ? "warn" : "info",
                        degraded
                            ? "Presence degraded (BLE stale or sensor offline) — routing on reduced confidence (desk vs away)."
                            : "Presence signal restored — full 3-state presence resumed.");
                }

                bool remoteMode = PresenceRouting.ToRemoteMode(state);

                // Presence is authoritative while enabled: reconcile against the gate's ACTUAL value,
                // not a local "last applied" cache. That way a manual Local/Remote pill flip (or any
                // other SetRemoteMode caller) is re-corrected on the very next evaluation/tick — not
                // left latched until the next presence transition (pipeline Run-2 adversary HIGH).
                bool current;
                try
                {
                    current = _remoteMode.IsRemoteMode;
                }
                catch
                {
                    current = !remoteMode; // can't read the gate → assume divergent and re-apply
                }

                if (current != remoteMode)
                {
                    _log("info", $"Presence={state}{(degraded ? " (degraded)" : "")} ⇒ remoteMode={remoteMode}.");
                    try
                    {
                        _remoteMode.SetRemoteMode(remoteMode);
                    }
                    catch (Exception ex)
                    {
                        _log("warn", $"SetRemoteMode({remoteMode}) failed: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>Stop the adapter and release the MQTT client + timer. Safe to call repeatedly.</summary>
        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _tick?.Dispose(); } catch { }
            _tick = null;
            try { _runLoop?.Wait(TimeSpan.FromSeconds(3)); } catch { }
            _runLoop = null;
            try { SafeDisposeClientAsync().GetAwaiter().GetResult(); } catch { }
            try { _cts?.Dispose(); } catch { }
            _cts = null;
        }

        private async Task SafeDisposeClientAsync()
        {
            // Claim the client atomically so a Stop()-timeout racing RunAsync's finally can't both
            // dispose it (pipeline Run-1 code-reviewer MINOR).
            var c = Interlocked.Exchange(ref _client, null);
            if (c == null)
                return;
            try { if (c.IsConnected) await c.DisconnectAsync(); } catch { }
            try { c.ApplicationMessageReceivedAsync -= OnMessageAsync; } catch { }
            try { c.Dispose(); } catch { }
        }

        /// <summary>Disposes the adapter (idempotent).</summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Stop();
        }

        private void LogParseFailureThrottled(string message)
        {
            var nowUtc = DateTime.UtcNow;
            lock (_evalLock)
            {
                if ((nowUtc - _lastParseWarnUtc) < ParseWarnThrottle)
                    return;
                _lastParseWarnUtc = nowUtc;
            }
            _log("warn", $"Failed handling MQTT message (rate-limited): {message}");
        }

        private static bool IsLoopback(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return true;
            var h = host.Trim();
            return h == "127.0.0.1" || h == "::1" || h.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        }

        private static string DecodePayload(MqttApplicationMessage msg)
        {
            if (msg == null)
                return "";
            try
            {
                var seg = msg.PayloadSegment;
                if (seg.Array == null || seg.Count == 0)
                    return "";
                if (seg.Count > MaxPayloadBytes)
                    return ""; // oversize → treat as unparseable + drop (DoS guard)
                return Encoding.UTF8.GetString(seg.Array, seg.Offset, seg.Count);
            }
            catch
            {
                return "";
            }
        }

        private static MqttQualityOfServiceLevel ToQos(int qos)
        {
            switch (qos)
            {
                case 0: return MqttQualityOfServiceLevel.AtMostOnce;
                case 2: return MqttQualityOfServiceLevel.ExactlyOnce;
                default: return MqttQualityOfServiceLevel.AtLeastOnce;
            }
        }

        private static bool ParseOnline(string payload)
        {
            var p = (payload ?? "").Trim();
            return p.Equals("online", StringComparison.OrdinalIgnoreCase)
                || p == "1"
                || p.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryParseBool(string payload, out bool value)
        {
            var p = (payload ?? "").Trim();
            if (p.Equals("ON", StringComparison.OrdinalIgnoreCase) || p == "1"
                || p.Equals("true", StringComparison.OrdinalIgnoreCase) || p.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                value = true;
                return true;
            }
            if (p.Equals("OFF", StringComparison.OrdinalIgnoreCase) || p == "0"
                || p.Equals("false", StringComparison.OrdinalIgnoreCase) || p.Equals("no", StringComparison.OrdinalIgnoreCase))
            {
                value = false;
                return true;
            }
            value = false;
            return false;
        }

        // ===================================================================
        // Test seams (exercised by MultiTerminal.Tests via InternalsVisibleTo).
        // Build the parse→state-machine→routing pipeline WITHOUT opening an MQTT
        // connection, so item [8] can replay sensor sequences offline.
        // ===================================================================

        /// <summary>Initialize the config + state machine for replay tests (no MQTT, no timer).</summary>
        internal void InitializeForTest(PresenceConfig config)
        {
            _config = config ?? new PresenceConfig();
            _machine = new PresenceStateMachine(_config);
        }

        /// <summary>
        /// Start the REAL MQTT supervisor + tick against an explicit config, bypassing the
        /// presence.enabled gate — used by the live-broker integration test to exercise the actual
        /// connect/subscribe/decode path the unit tests mock out.
        /// </summary>
        internal void StartForTest(PresenceConfig config)
        {
            _config = config ?? new PresenceConfig();
            _machine = new PresenceStateMachine(_config);
            _cts = new CancellationTokenSource();
            _tick = new Timer(OnTick, null, TickInterval, TickInterval);
            _runLoop = Task.Run(() => RunAsync(_cts.Token));
        }

        /// <summary>True once the live MQTT client is connected (integration test poll).</summary>
        internal bool ConnectedForTest => _client != null && _client.IsConnected;

        /// <summary>
        /// Drive a tick (debounce/staleness re-evaluation) at an explicit time (replay tests). Mirrors
        /// OnTick's readiness gate: no-ops until the primary occupancy signal has established readiness.
        /// </summary>
        internal void TickForTest(DateTimeOffset now)
        {
            if (!_seenSignal)
                return;
            DriveFromState(now);
        }
    }
}
