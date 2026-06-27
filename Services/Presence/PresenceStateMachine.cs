using System;
using System.Collections.Generic;

namespace MultiTerminal.Services.Presence
{
    /// <summary>
    /// Deterministic 3-state presence machine (At desk / Nearby / Away) with debounce/hysteresis on
    /// transitions (item [2]) and graceful degradation to mmWave-only when BLE is stale/unavailable
    /// (item [3]).
    ///
    /// PURE logic — no MQTT, no wall clock baked in. Callers push normalized signals via the Set*
    /// methods (each stamped with the message time) and drive transitions by calling
    /// <see cref="Evaluate"/> with the current time. The adapter calls Evaluate on every inbound
    /// message AND on a periodic tick, so a debounce can commit (or staleness can trip) even when no
    /// new message arrives. This shape makes the machine fully unit-testable by replaying a
    /// (signal, time) sequence — no broker or real time required (items [7]/[8]).
    ///
    /// Thread-safe: all mutable state is guarded by a single lock; <see cref="StateChanged"/> fires
    /// OUTSIDE the lock to avoid reentrancy into caller code while holding it.
    /// </summary>
    public sealed class PresenceStateMachine
    {
        private readonly object _lock = new object();

        private double _debounceSeconds;
        private double _bleStaleSeconds;
        private PhoneRegistry _registry;

        // Latest raw signals.
        private bool _occupancy;            // mmWave: someone at the desk zone
        private bool _availabilityOffline;  // LWT said the sensor/bridge is offline
        private readonly Dictionary<string, PhoneSignal> _phoneSignals =
            new Dictionary<string, PhoneSignal>(StringComparer.OrdinalIgnoreCase);

        // Committed + pending (debounce) state.
        private PresenceState _state = PresenceState.AtDesk;
        private bool _hasCommitted;
        private PresenceState _pending;
        private DateTimeOffset _pendingSince;
        private bool _hasPending;
        private bool _lastDegraded;

        private sealed class PhoneSignal
        {
            public int Rssi;
            public DateTimeOffset? RssiTime;
            public bool Presence;
            public DateTimeOffset? PresenceTime;
        }

        /// <summary>Creates a machine seeded with the given config (debounce, staleness, phones).</summary>
        public PresenceStateMachine(PresenceConfig config)
        {
            ApplyConfig(config);
            _state = PresenceState.AtDesk;
        }

        /// <summary>The current committed state.</summary>
        public PresenceState CurrentState
        {
            get { lock (_lock) { return _state; } }
        }

        /// <summary>True when the last evaluation ran in BLE-degraded (mmWave-only) mode.</summary>
        public bool IsDegraded
        {
            get { lock (_lock) { return _lastDegraded; } }
        }

        /// <summary>Fires (outside the lock) when a transition commits after debounce.</summary>
        public event EventHandler<PresenceTransition> StateChanged;

        /// <summary>Re-applies tuning config (debounce/staleness/phones). Latest signals are kept.</summary>
        public void ApplyConfig(PresenceConfig config)
        {
            lock (_lock)
            {
                _debounceSeconds = config?.DebounceSeconds ?? 5;
                _bleStaleSeconds = config?.BleStaleSeconds ?? 30;
                _registry = new PhoneRegistry(config?.Phones);
            }
        }

        /// <summary>Set the latest mmWave occupancy reading.</summary>
        public void SetOccupancy(bool present, DateTimeOffset now)
        {
            lock (_lock) { _occupancy = present; }
        }

        /// <summary>Set sensor availability from the LWT topic (false = offline → degradation).</summary>
        public void SetAvailability(bool online, DateTimeOffset now)
        {
            lock (_lock) { _availabilityOffline = !online; }
        }

        /// <summary>
        /// Record a per-phone RSSI reading (dBm). The deviceId is normalized via the registry;
        /// readings for unregistered phones are ignored (bounds the signal map to known devices).
        /// </summary>
        public void SetPhoneRssi(string deviceId, int rssi, DateTimeOffset now)
        {
            lock (_lock)
            {
                var id = ResolveRegisteredId(deviceId);
                if (id == null) return;
                var s = GetOrAdd(id);
                s.Rssi = rssi;
                s.RssiTime = now;
            }
        }

        /// <summary>Record a per-phone BLE presence flag (in-range vs gone). Unregistered ids are ignored.</summary>
        public void SetPhonePresence(string deviceId, bool present, DateTimeOffset now)
        {
            lock (_lock)
            {
                var id = ResolveRegisteredId(deviceId);
                if (id == null) return;
                var s = GetOrAdd(id);
                s.Presence = present;
                s.PresenceTime = now;
            }
        }

        // Returns the canonical id if the device is registered, else null. Caller holds _lock.
        private string ResolveRegisteredId(string rawDeviceId)
        {
            if (string.IsNullOrWhiteSpace(rawDeviceId) || _registry == null)
                return null;
            return _registry.TryResolve(rawDeviceId, out var phone) ? phone.DeviceId : null;
        }

        /// <summary>
        /// Recompute the desired state from the latest signals, run debounce, and (if a transition
        /// commits) fire <see cref="StateChanged"/>. Returns the committed state. Idempotent when
        /// nothing changed. The first call commits immediately (no debounce on the initial state).
        /// </summary>
        public PresenceState Evaluate(DateTimeOffset now)
        {
            PresenceTransition transition = null;
            PresenceState committed;
            lock (_lock)
            {
                bool degraded;
                var desired = ComputeRaw(now, out degraded);

                if (!_hasCommitted)
                {
                    _state = desired;
                    _hasCommitted = true;
                    _hasPending = false;
                    _lastDegraded = degraded;
                }
                else if (desired == _state)
                {
                    // Desired matches committed — cancel any in-flight pending flip.
                    _hasPending = false;
                    _lastDegraded = degraded;
                }
                else
                {
                    if (!_hasPending || _pending != desired)
                    {
                        _pending = desired;
                        _pendingSince = now;
                        _hasPending = true;
                    }

                    if ((now - _pendingSince).TotalSeconds >= _debounceSeconds)
                    {
                        var old = _state;
                        _state = desired;
                        _hasPending = false;
                        _lastDegraded = degraded;
                        transition = new PresenceTransition(old, _state, degraded);
                    }
                }

                committed = _state;
            }

            if (transition != null)
                StateChanged?.Invoke(this, transition);
            return committed;
        }

        // Desired raw state, pre-debounce.
        private PresenceState ComputeRaw(DateTimeOffset now, out bool degraded)
        {
            degraded = false;

            // Sensor/bridge offline (LWT) means the WHOLE sensor is down — its retained mmWave and BLE
            // values are stale and untrustworthy. This MUST be checked BEFORE occupancy: otherwise a
            // latched retained occupancy=ON from before the sensor died would pin us AtDesk forever
            // (no fresh OFF can ever arrive), suppressing phone push indefinitely. Degrade to the safe
            // default — Away ⇒ phone push (pipeline Run-2 adversary HIGH).
            if (_availabilityOffline)
            {
                degraded = true;
                return PresenceState.Away;
            }

            // mmWave is the PRIMARY gate: present → at the desk, full stop.
            if (_occupancy)
                return PresenceState.AtDesk;

            // mmWave absent. Can we trust BLE to distinguish Nearby from Away?
            if (!AnyPhoneHasFreshSignal(now))
            {
                // Graceful degradation (item [3]): collapse to mmWave-only — absent ⇒ away.
                degraded = true;
                return PresenceState.Away;
            }

            return AnyPhoneInRange(now) ? PresenceState.Nearby : PresenceState.Away;
        }

        private bool AnyPhoneHasFreshSignal(DateTimeOffset now)
        {
            if (_registry == null) return false;
            foreach (var phone in _registry.Phones)
            {
                if (_phoneSignals.TryGetValue(phone.DeviceId ?? "", out var s) && HasFreshSignal(s, now))
                    return true;
            }
            return false;
        }

        private bool AnyPhoneInRange(DateTimeOffset now)
        {
            if (_registry == null) return false;
            foreach (var phone in _registry.Phones)
            {
                if (_phoneSignals.TryGetValue(phone.DeviceId ?? "", out var s) && InRange(phone, s, now))
                    return true;
            }
            return false;
        }

        private bool HasFreshSignal(PhoneSignal s, DateTimeOffset now) =>
            Fresh(s.RssiTime, now) || Fresh(s.PresenceTime, now);

        private bool InRange(PhoneConfig phone, PhoneSignal s, DateTimeOffset now)
        {
            // Fresh presence is authoritative (ESPHome BLE tracker ON/OFF beats a raw RSSI guess).
            if (Fresh(s.PresenceTime, now))
                return s.Presence;
            // Otherwise fall back to a fresh RSSI vs the per-device threshold.
            if (Fresh(s.RssiTime, now))
                return s.Rssi >= phone.RssiThreshold;
            return false;
        }

        private bool Fresh(DateTimeOffset? t, DateTimeOffset now) =>
            t.HasValue && (now - t.Value).TotalSeconds <= _bleStaleSeconds;

        private PhoneSignal GetOrAdd(string deviceId)
        {
            if (!_phoneSignals.TryGetValue(deviceId, out var s))
            {
                s = new PhoneSignal();
                _phoneSignals[deviceId] = s;
            }
            return s;
        }
    }
}
