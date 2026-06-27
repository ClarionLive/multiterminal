using System;
using System.Collections.Generic;
using MultiTerminal.Services;
using MultiTerminal.Services.Presence;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Integration-style replay coverage for <see cref="PresenceAdapter"/> (task 9f9c3141, item [8]):
    /// feeds raw MQTT (topic, payload) sequences through the adapter's parse → state-machine → routing
    /// pipeline and asserts the resulting remoteMode, WITHOUT a live broker. The adapter is decoupled
    /// from MessageBroker via <see cref="IRemoteModeSink"/>, so a fake sink captures the routing output;
    /// <c>InitializeForTest</c> wires the machine with no MQTT connection. Debounce is set to 0 here so
    /// each message commits immediately — debounce/hysteresis itself is covered in the state-machine tests.
    /// </summary>
    public sealed class PresenceAdapterReplayTests
    {
        private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private const string Prefix = "mt/presence";

        private sealed class FakeRemoteModeSink : IRemoteModeSink
        {
            public bool IsRemoteMode { get; private set; }

            public int SetCount { get; private set; }

            public void SetRemoteMode(bool enabled)
            {
                IsRemoteMode = enabled;
                SetCount++;
            }
        }

        private static PresenceConfig Config(params (string id, int threshold)[] phones)
        {
            var cfg = new PresenceConfig
            {
                DebounceSeconds = 0,
                BleStaleSeconds = 30,
                Mqtt = new PresenceMqttConfig { TopicPrefix = Prefix },
                Phones = new List<PhoneConfig>(),
            };
            foreach (var p in phones)
                cfg.Phones.Add(new PhoneConfig { DeviceId = p.id, RssiThreshold = p.threshold });
            return cfg;
        }

        private static PresenceAdapter NewAdapter(out FakeRemoteModeSink sink, PresenceConfig cfg)
        {
            sink = new FakeRemoteModeSink();
            var adapter = new PresenceAdapter(sink, new SettingsService(), (lvl, msg) => { });
            adapter.InitializeForTest(cfg);
            return adapter;
        }

        [Fact]
        public void WalkAway_DesktopToPhone_FullSequence()
        {
            using var a = NewAdapter(out var sink, Config(("johns-pixel", -75)));

            // At desk: occupancy ON, phone present and in range.
            a.HandleMessage($"{Prefix}/desk/occupancy", "ON", T0);
            a.HandleMessage($"{Prefix}/ble/johns-pixel/rssi", "-60", T0);
            Assert.Equal(PresenceState.AtDesk, a.CurrentState);

            // Step away from the desk but phone still in range → Nearby (v1: still desktop).
            a.HandleMessage($"{Prefix}/desk/occupancy", "OFF", T0.AddSeconds(1));
            Assert.Equal(PresenceState.Nearby, a.CurrentState);
            Assert.False(sink.IsRemoteMode);

            // Phone leaves (presence OFF authoritative) → Away → route to phone push.
            a.HandleMessage($"{Prefix}/ble/johns-pixel/presence", "OFF", T0.AddSeconds(2));
            Assert.Equal(PresenceState.Away, a.CurrentState);
            Assert.True(sink.IsRemoteMode);
        }

        [Fact]
        public void DeviceIdSlug_NormalizesToRegisteredPhone()
        {
            using var a = NewAdapter(out _, Config(("johns-pixel", -75)));

            a.HandleMessage($"{Prefix}/desk/occupancy", "ON", T0);
            // Topic uses a drifted slug "Johns_Pixel"; must resolve to the registered "johns-pixel".
            a.HandleMessage($"{Prefix}/ble/Johns_Pixel/rssi", "-55", T0);
            a.HandleMessage($"{Prefix}/desk/occupancy", "OFF", T0.AddSeconds(1));

            Assert.Equal(PresenceState.Nearby, a.CurrentState);
        }

        [Fact]
        public void StatusOffline_DegradesToPhonePush()
        {
            using var a = NewAdapter(out var sink, Config(("johns-pixel", -75)));

            a.HandleMessage($"{Prefix}/desk/occupancy", "ON", T0);
            a.HandleMessage($"{Prefix}/ble/johns-pixel/rssi", "-50", T0); // strongly in range
            a.HandleMessage($"{Prefix}/status", "offline", T0.AddSeconds(1)); // LWT: sensor gone
            a.HandleMessage($"{Prefix}/desk/occupancy", "OFF", T0.AddSeconds(1));

            Assert.Equal(PresenceState.Away, a.CurrentState); // degraded → away despite phone in range
            Assert.True(sink.IsRemoteMode);
        }

        [Fact]
        public void PayloadAliases_AreParsed()
        {
            using var a = NewAdapter(out _, Config(("johns-pixel", -75)));

            // "1"/"true"/"yes" all mean occupancy present.
            a.HandleMessage($"{Prefix}/desk/occupancy", "1", T0);
            Assert.Equal(PresenceState.AtDesk, a.CurrentState);
            a.HandleMessage($"{Prefix}/desk/occupancy", "false", T0.AddSeconds(1)); // → not present
            // No BLE → degraded Away.
            Assert.Equal(PresenceState.Away, a.CurrentState);
        }

        [Fact]
        public void ForeignTopicsAndGarbage_AreIgnored()
        {
            using var a = NewAdapter(out var sink, Config(("johns-pixel", -75)));

            a.HandleMessage($"{Prefix}/desk/occupancy", "ON", T0); // AtDesk baseline
            int before = sink.SetCount;

            a.HandleMessage("some/other/topic", "ON", T0.AddSeconds(1));           // wrong prefix
            a.HandleMessage($"{Prefix}/desk/occupancy", "maybe", T0.AddSeconds(1)); // unparseable bool
            a.HandleMessage($"{Prefix}/ble/johns-pixel/rssi", "notanumber", T0.AddSeconds(1));
            a.HandleMessage($"{Prefix}/unknown/leaf", "ON", T0.AddSeconds(1));      // unknown subtopic

            Assert.Equal(PresenceState.AtDesk, a.CurrentState); // nothing moved it
            Assert.Equal(before, sink.SetCount);                // no spurious routing calls
        }

        [Fact]
        public void StaleBle_ViaTick_DegradesToPhonePush()
        {
            using var a = NewAdapter(out var sink, Config(("johns-pixel", -75)));

            // Away from desk but phone in range → Nearby (v1: still desktop).
            a.HandleMessage($"{Prefix}/desk/occupancy", "ON", T0);
            a.HandleMessage($"{Prefix}/ble/johns-pixel/rssi", "-60", T0);
            a.HandleMessage($"{Prefix}/desk/occupancy", "OFF", T0.AddSeconds(1));
            Assert.Equal(PresenceState.Nearby, a.CurrentState);
            Assert.False(sink.IsRemoteMode);

            // No new BLE signal; a tick past the 30s stale window degrades to Away → phone push.
            a.TickForTest(T0.AddSeconds(40));
            Assert.Equal(PresenceState.Away, a.CurrentState);
            Assert.True(sink.IsRemoteMode);
        }

        [Fact]
        public void FirstSignal_EstablishesRemoteMode_AwayAtStartup()
        {
            // Regression for pipeline Run-1 Debugger BUG1 / Codex adversary HIGH: the FIRST signal must
            // drive the sink even though the machine's initial commit fires no StateChanged event.
            using var a = NewAdapter(out var sink, Config(("johns-pixel", -75)));

            // Retained occupancy=OFF arrives first, no BLE → Away. remoteMode must be set true (phone),
            // not left at the stale manual default.
            a.HandleMessage($"{Prefix}/desk/occupancy", "OFF", T0);
            Assert.Equal(PresenceState.Away, a.CurrentState);
            Assert.True(sink.IsRemoteMode);
            Assert.Equal(1, sink.SetCount);
        }

        [Fact]
        public void StatusOnlineBeforeOccupancy_DoesNotFlipToPhone()
        {
            // Regression (pipeline Run-3 adversary HIGH): MQTT gives no retained-topic ordering, so
            // status=online can arrive before desk/occupancy. Availability alone must NOT establish
            // routing readiness — otherwise an empty machine computes Away and transiently flips to phone
            // while the user is actually at the desk.
            using var a = NewAdapter(out var sink, Config(("johns-pixel", -75)));

            a.HandleMessage($"{Prefix}/status", "online", T0); // availability only — no occupancy baseline
            a.TickForTest(T0.AddSeconds(1));                    // a tick in this window must not drive
            Assert.Equal(0, sink.SetCount);
            Assert.False(sink.IsRemoteMode);

            // Now the primary signal arrives → routing engages, and it's the correct (desktop) state.
            a.HandleMessage($"{Prefix}/desk/occupancy", "ON", T0.AddSeconds(2));
            Assert.Equal(PresenceState.AtDesk, a.CurrentState);
            Assert.False(sink.IsRemoteMode); // never flipped to phone in the interim
        }

        [Fact]
        public void OfflineSensor_DoesNotPinAtDesk_WithStaleRetainedOccupancy()
        {
            // Regression (pipeline Run-2 adversary HIGH): retained occupancy=ON, sensor then dies (LWT
            // offline) with no further occupancy update — must NOT stay pinned AtDesk / desktop.
            using var a = NewAdapter(out var sink, Config(("johns-pixel", -75)));

            a.HandleMessage($"{Prefix}/desk/occupancy", "ON", T0);
            Assert.Equal(PresenceState.AtDesk, a.CurrentState);

            a.HandleMessage($"{Prefix}/status", "offline", T0.AddSeconds(1));
            Assert.Equal(PresenceState.Away, a.CurrentState); // degraded off stale occupancy
            Assert.True(sink.IsRemoteMode);                    // routes to phone
        }

        [Fact]
        public void ManualFlip_IsReassertedByPresence_OnNextTick()
        {
            // Regression (pipeline Run-2 adversary HIGH): presence is authoritative — a manual pill flip
            // during a steady presence state is re-corrected on the next tick (reconcile vs IsRemoteMode).
            using var a = NewAdapter(out var sink, Config(("johns-pixel", -75)));

            a.HandleMessage($"{Prefix}/desk/occupancy", "ON", T0); // AtDesk → desktop (false)
            Assert.False(sink.IsRemoteMode);

            sink.SetRemoteMode(true); // user manually flips to Remote while still at the desk
            Assert.True(sink.IsRemoteMode);

            a.TickForTest(T0.AddSeconds(2)); // steady AtDesk; presence must re-assert desktop
            Assert.False(sink.IsRemoteMode);
        }

        [Fact]
        public void ReturnToDesk_RoutesBackToDesktop()
        {
            using var a = NewAdapter(out var sink, Config(("johns-pixel", -75)));

            a.HandleMessage($"{Prefix}/desk/occupancy", "ON", T0);
            a.HandleMessage($"{Prefix}/desk/occupancy", "OFF", T0.AddSeconds(1)); // → Away (no BLE), phone push
            Assert.True(sink.IsRemoteMode);

            a.HandleMessage($"{Prefix}/desk/occupancy", "ON", T0.AddSeconds(2));  // back at desk
            Assert.Equal(PresenceState.AtDesk, a.CurrentState);
            Assert.False(sink.IsRemoteMode); // routed back to desktop
        }
    }
}
