using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MultiTerminal.Services;
using MultiTerminal.Services.Presence;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// LIVE-BROKER integration check for <see cref="PresenceAdapter"/> (task 9f9c3141): runs the REAL
    /// MQTT connect → subscribe('mt/presence/#') → receive → decode path that the unit/replay tests
    /// deliberately mock out, against a real Mosquitto instance, and confirms a real wire publish flips
    /// the routing sink. Gated on env vars so it's a no-op in normal `dotnet test` / CI (no broker):
    ///   MT_PRESENCE_TEST_PORT  — broker port (e.g. 18883)
    ///   MT_MOSQUITTO_PUB       — absolute path to mosquitto_pub.exe
    /// Driven manually: start mosquitto on the port, set both env vars, run --filter RealBroker.
    /// </summary>
    public sealed class PresenceAdapterBrokerIntegrationTests
    {
        private sealed class ThreadSafeSink : IRemoteModeSink
        {
            private volatile bool _mode;

            public bool IsRemoteMode => _mode;

            public void SetRemoteMode(bool enabled) => _mode = enabled;
        }

        [Fact]
        public async Task RealBroker_ConnectsSubscribesAndRoutesOnPublish()
        {
            var portStr = Environment.GetEnvironmentVariable("MT_PRESENCE_TEST_PORT");
            var pubExe = Environment.GetEnvironmentVariable("MT_MOSQUITTO_PUB");
            if (string.IsNullOrEmpty(portStr) || string.IsNullOrEmpty(pubExe) || !File.Exists(pubExe))
                return; // integration-only — skipped (passes as no-op) when not explicitly configured.

            int port = int.Parse(portStr);
            var sink = new ThreadSafeSink();
            using var adapter = new PresenceAdapter(sink, new SettingsService(), (l, m) => { });
            var cfg = new PresenceConfig
            {
                DebounceSeconds = 0,
                BleStaleSeconds = 30,
                Mqtt = new PresenceMqttConfig { Host = "127.0.0.1", Port = port, TopicPrefix = "mt/presence" },
                Phones = new List<PhoneConfig> { new PhoneConfig { DeviceId = "test-phone", RssiThreshold = -75 } },
            };

            adapter.StartForTest(cfg);

            Assert.True(
                await WaitUntil(() => adapter.ConnectedForTest, TimeSpan.FromSeconds(8)),
                "adapter never connected to the broker");

            // Away from desk, no phone in range → a REAL publish over the wire must route to phone.
            Publish(pubExe, port, "mt/presence/desk/occupancy", "OFF");
            Assert.True(
                await WaitUntil(() => sink.IsRemoteMode, TimeSpan.FromSeconds(5)),
                "occupancy=OFF publish did not route to phone (remoteMode=true)");

            // Back at the desk → routes back to desktop.
            Publish(pubExe, port, "mt/presence/desk/occupancy", "ON");
            Assert.True(
                await WaitUntil(() => !sink.IsRemoteMode, TimeSpan.FromSeconds(5)),
                "occupancy=ON publish did not route back to desktop (remoteMode=false)");
        }

        private static void Publish(string pubExe, int port, string topic, string message)
        {
            var psi = new ProcessStartInfo
            {
                FileName = pubExe,
                Arguments = $"-h 127.0.0.1 -p {port} -t {topic} -m {message} -q 1",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            p.WaitForExit(5000);
        }

        private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                if (condition())
                    return true;
                await Task.Delay(50);
            }
            return condition();
        }
    }
}
