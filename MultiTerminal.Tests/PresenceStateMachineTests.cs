using System;
using System.Collections.Generic;
using MultiTerminal.Services.Presence;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Unit coverage for <see cref="PresenceStateMachine"/> (task 9f9c3141, item [7]): all three
    /// states, transitions, debounce/hysteresis, and BLE-stale graceful degradation (item [3]).
    /// The machine is pure logic with an injectable clock, so these tests replay (signal, time)
    /// sequences with zero MQTT / zero real time.
    /// </summary>
    public sealed class PresenceStateMachineTests
    {
        private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private static PresenceConfig Config(double debounce, double stale, params (string id, int threshold)[] phones)
        {
            var cfg = new PresenceConfig { DebounceSeconds = debounce, BleStaleSeconds = stale };
            cfg.Phones = new List<PhoneConfig>();
            foreach (var p in phones)
                cfg.Phones.Add(new PhoneConfig { DeviceId = p.id, RssiThreshold = p.threshold });
            return cfg;
        }

        // Establish a committed AtDesk baseline (first Evaluate commits immediately with no event).
        private static PresenceStateMachine PrimedAtDesk(PresenceConfig cfg, out List<PresenceTransition> events)
        {
            var m = new PresenceStateMachine(cfg);
            var captured = new List<PresenceTransition>();
            m.StateChanged += (s, t) => captured.Add(t);
            m.SetOccupancy(true, T0);
            m.Evaluate(T0);
            Assert.Equal(PresenceState.AtDesk, m.CurrentState);
            Assert.Empty(captured); // first commit fires no transition event
            events = captured;
            return m;
        }

        [Fact]
        public void OccupancyOn_IsAtDesk_RegardlessOfBle()
        {
            var m = new PresenceStateMachine(Config(5, 30, ("p", -75)));
            m.SetPhoneRssi("p", -100, T0); // out of range, but mmWave wins
            m.SetOccupancy(true, T0);
            Assert.Equal(PresenceState.AtDesk, m.Evaluate(T0));
            Assert.False(m.IsDegraded);
        }

        [Fact]
        public void MmwaveOff_PhoneInRange_CommitsNearbyAfterDebounce()
        {
            var m = PrimedAtDesk(Config(5, 30, ("p", -75)), out var events);

            m.SetOccupancy(false, T0.AddSeconds(10));
            m.SetPhoneRssi("p", -60, T0.AddSeconds(10)); // ≥ -75 → in range
            Assert.Equal(PresenceState.AtDesk, m.Evaluate(T0.AddSeconds(10))); // pending, not yet committed
            Assert.Equal(PresenceState.Nearby, m.Evaluate(T0.AddSeconds(15))); // debounce (5s) elapsed

            Assert.Single(events);
            Assert.Equal(PresenceState.AtDesk, events[0].OldState);
            Assert.Equal(PresenceState.Nearby, events[0].NewState);
            Assert.False(events[0].Degraded);
        }

        [Fact]
        public void MmwaveOff_PhoneOutOfRange_IsAway_NotDegraded()
        {
            var m = PrimedAtDesk(Config(5, 30, ("p", -75)), out _);

            m.SetOccupancy(false, T0.AddSeconds(10));
            m.SetPhoneRssi("p", -90, T0.AddSeconds(10)); // fresh but < -75 → out of range
            m.Evaluate(T0.AddSeconds(10));
            Assert.Equal(PresenceState.Away, m.Evaluate(T0.AddSeconds(15)));
            Assert.False(m.IsDegraded); // BLE was available (fresh signal), just out of range
        }

        [Fact]
        public void TransientFlip_DoesNotCommit_NoEvent()
        {
            var m = PrimedAtDesk(Config(5, 30, ("p", -75)), out var events);

            // Leave at t+10, return at t+12 (before the 5s debounce) — must never commit a flip.
            m.SetOccupancy(false, T0.AddSeconds(10));
            m.Evaluate(T0.AddSeconds(10));               // pending Away, still AtDesk
            m.SetOccupancy(true, T0.AddSeconds(12));
            Assert.Equal(PresenceState.AtDesk, m.Evaluate(T0.AddSeconds(12))); // desired==committed → cancels pending
            Assert.Equal(PresenceState.AtDesk, m.Evaluate(T0.AddSeconds(20)));
            Assert.Empty(events);
        }

        [Fact]
        public void NoBle_DegradesToAway_WithFlag()
        {
            var m = PrimedAtDesk(Config(5, 30, ("p", -75)), out var events);

            m.SetOccupancy(false, T0.AddSeconds(10)); // no BLE signal ever received → degraded
            m.Evaluate(T0.AddSeconds(10));
            Assert.Equal(PresenceState.Away, m.Evaluate(T0.AddSeconds(15)));
            Assert.True(m.IsDegraded);
            Assert.True(events[0].Degraded);
        }

        [Fact]
        public void AvailabilityOffline_ForcesDegradedAway_EvenWithPhoneInRange()
        {
            var m = PrimedAtDesk(Config(5, 30, ("p", -75)), out _);

            m.SetPhoneRssi("p", -50, T0.AddSeconds(10));      // strongly in range...
            m.SetAvailability(false, T0.AddSeconds(10));        // ...but sensor/bridge LWT says offline
            m.SetOccupancy(false, T0.AddSeconds(10));
            m.Evaluate(T0.AddSeconds(10));
            Assert.Equal(PresenceState.Away, m.Evaluate(T0.AddSeconds(15)));
            Assert.True(m.IsDegraded);
        }

        [Fact]
        public void FreshPresenceTopic_IsAuthoritative_OverRssi()
        {
            // presence=ON overrides an out-of-range RSSI → Nearby.
            var m = PrimedAtDesk(Config(5, 30, ("p", -75)), out _);
            m.SetOccupancy(false, T0.AddSeconds(10));
            m.SetPhoneRssi("p", -95, T0.AddSeconds(10));        // would be out of range
            m.SetPhonePresence("p", true, T0.AddSeconds(10));   // but presence says in range
            m.Evaluate(T0.AddSeconds(10));
            Assert.Equal(PresenceState.Nearby, m.Evaluate(T0.AddSeconds(15)));

            // presence=OFF overrides an in-range RSSI → Away.
            var m2 = PrimedAtDesk(Config(5, 30, ("p", -75)), out _);
            m2.SetOccupancy(false, T0.AddSeconds(10));
            m2.SetPhoneRssi("p", -50, T0.AddSeconds(10));       // would be in range
            m2.SetPhonePresence("p", false, T0.AddSeconds(10)); // but presence says gone
            m2.Evaluate(T0.AddSeconds(10));
            Assert.Equal(PresenceState.Away, m2.Evaluate(T0.AddSeconds(15)));
        }

        [Fact]
        public void StaleBle_Degrades_FromNearbyToAway()
        {
            var m = PrimedAtDesk(Config(5, 10, ("p", -75)), out _); // stale window 10s

            // Commit Nearby with a fresh in-range signal at t+10.
            m.SetOccupancy(false, T0.AddSeconds(10));
            m.SetPhoneRssi("p", -60, T0.AddSeconds(10));
            m.Evaluate(T0.AddSeconds(10));
            Assert.Equal(PresenceState.Nearby, m.Evaluate(T0.AddSeconds(15)));
            Assert.False(m.IsDegraded);

            // No new signal; by t+10+11=21 the RSSI is stale (>10s) → degrade to Away.
            m.Evaluate(T0.AddSeconds(21));                       // desired flips to Away (degraded)
            Assert.Equal(PresenceState.Away, m.Evaluate(T0.AddSeconds(26))); // debounce
            Assert.True(m.IsDegraded);
        }

        [Fact]
        public void OfflineOverridesStaleOccupancy()
        {
            // Regression (pipeline Run-2 adversary HIGH): a retained occupancy=ON from before the sensor
            // died must NOT pin AtDesk once status=offline arrives — offline distrusts stale occupancy.
            var m = new PresenceStateMachine(Config(0, 30, ("p", -75))); // debounce 0 → commits immediately
            m.SetOccupancy(true, T0);
            Assert.Equal(PresenceState.AtDesk, m.Evaluate(T0));

            m.SetAvailability(false, T0.AddSeconds(1)); // sensor LWT offline; occupancy never updated again
            Assert.Equal(PresenceState.Away, m.Evaluate(T0.AddSeconds(1)));
            Assert.True(m.IsDegraded);
        }

        [Fact]
        public void UnregisteredPhone_IsIgnored()
        {
            var m = PrimedAtDesk(Config(5, 30, ("p", -75)), out _);

            m.SetOccupancy(false, T0.AddSeconds(10));
            m.SetPhoneRssi("stranger", -40, T0.AddSeconds(10)); // not registered → ignored
            m.Evaluate(T0.AddSeconds(10));
            // No fresh signal for a REGISTERED phone → degraded Away.
            Assert.Equal(PresenceState.Away, m.Evaluate(T0.AddSeconds(15)));
            Assert.True(m.IsDegraded);
        }

        [Theory]
        [InlineData(PresenceState.AtDesk, false)]
        [InlineData(PresenceState.Nearby, false)] // v1 collapse: Nearby routes as At desk
        [InlineData(PresenceState.Away, true)]
        public void Routing_MapsStateToRemoteMode(PresenceState state, bool expected)
        {
            Assert.Equal(expected, PresenceRouting.ToRemoteMode(state));
        }

        [Theory]
        [InlineData("Johns_iPhone", "johns-iphone")]
        [InlineData("  Pixel 7 Pro ", "pixel-7-pro")]
        [InlineData("a//b__c", "a-b-c")]
        [InlineData("--Edge--", "edge")]
        public void PhoneRegistry_NormalizesIds(string raw, string expected)
        {
            Assert.Equal(expected, PhoneRegistry.NormalizeId(raw));
        }

        [Fact]
        public void PhoneRegistry_ResolvesDriftedTopicSlug()
        {
            var reg = new PhoneRegistry(new[] { new PhoneConfig { DeviceId = "johns-pixel", RssiThreshold = -70 } });
            Assert.True(reg.TryResolve("Johns_Pixel", out var phone));
            Assert.Equal("johns-pixel", phone.DeviceId);
            Assert.Equal(-70, phone.RssiThreshold);
        }
    }
}
