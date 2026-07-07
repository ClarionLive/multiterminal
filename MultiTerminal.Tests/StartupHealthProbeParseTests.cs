using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MultiTerminal.Services.Startup;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Unit coverage for the health-body parse contract (task 4fec40e2). The self-probe's
    /// positive-MT signal is the <see cref="HealthIdentity.ServiceMarker"/> in the JSON
    /// <c>service</c> field; a foreign server's body must NOT be mistaken for MultiTerminal.
    /// The live HTTP call is not exercised here — only the parse, which is what decides the
    /// contention verdict.
    /// </summary>
    public class StartupHealthProbeParseTests
    {
        private static string MtBody() =>
            "{" +
            $"\"service\":\"{HealthIdentity.ServiceMarker}\"," +
            "\"app\":\"MultiTerminal\",\"version\":\"1.2.3\",\"pid\":1234," +
            "\"machine\":\"BOX\",\"user\":\"alice\",\"sessionId\":2,\"port\":5050," +
            "\"startedUtc\":\"2026-07-06T00:00:00.0000000Z\"}";

        [Fact]
        public void Parse_identifies_MT_by_service_marker()
        {
            var result = StartupHealthProbe.Parse(MtBody());

            Assert.True(result.Reached);
            Assert.True(result.IsMultiTerminal);
            Assert.NotNull(result.Identity);
            Assert.Equal(1234, result.Identity.Pid);
            Assert.Equal("alice", result.Identity.User);
            Assert.Equal(2, result.Identity.SessionId);
            Assert.Equal(5050, result.Identity.Port);
            Assert.Equal("1.2.3", result.Identity.Version);
        }

        [Fact]
        public void Parse_rejects_json_without_service_marker()
        {
            var result = StartupHealthProbe.Parse("{\"status\":\"ok\",\"port\":5050}");

            Assert.True(result.Reached);        // something answered
            Assert.False(result.IsMultiTerminal); // ...but it isn't us
        }

        [Fact]
        public void Parse_rejects_json_with_wrong_service_value()
        {
            var result = StartupHealthProbe.Parse("{\"service\":\"some-other-server\"}");

            Assert.True(result.Reached);
            Assert.False(result.IsMultiTerminal);
        }

        [Fact]
        public void Parse_rejects_non_json_body()
        {
            var result = StartupHealthProbe.Parse("hello from a foreign server");

            Assert.True(result.Reached);
            Assert.False(result.IsMultiTerminal);
        }

        [Fact]
        public void Parse_handles_empty_body()
        {
            var result = StartupHealthProbe.Parse("");

            Assert.True(result.Reached);
            Assert.False(result.IsMultiTerminal);
        }

        [Fact]
        public void Parse_round_trips_web_serialized_identity()
        {
            // The real /api/health <-> self-probe contract: HealthController returns
            // HealthIdentity.Current(port), ASP.NET Core serializes it with the Web defaults
            // (camelCase), and the probe must recognize it. Mirroring JsonSerializerDefaults.Web
            // here locks the producer/consumer field-name agreement (a PascalCase drift would
            // silently break the classifier) — coverage we can't get live because :5050 is
            // held by the running MultiTerminal.
            var identity = HealthIdentity.Current(5050);
            string body = JsonSerializer.Serialize(identity, new JsonSerializerOptions(JsonSerializerDefaults.Web));

            var result = StartupHealthProbe.Parse(body);

            Assert.True(result.IsMultiTerminal);
            Assert.NotNull(result.Identity);
            Assert.Equal(HealthIdentity.ServiceMarker, result.Identity.Service);
            Assert.Equal(identity.Pid, result.Identity.Pid);
            Assert.True(result.Identity.Pid > 0);
            Assert.Equal(5050, result.Identity.Port);
        }

        // ---- Probe body cap (task 4fec40e2 security finding — hostile-holder OOM guard) ----

        [Fact]
        public async Task ReadCapped_truncates_and_flags_oversized_body()
        {
            const int cap = 64 * 1024;
            var big = new byte[cap * 3];
            for (int i = 0; i < big.Length; i++)
            {
                big[i] = (byte)'a';
            }

            using var ms = new MemoryStream(big);
            var (body, truncated) = await StartupHealthProbe.ReadCappedAsync(ms, cap);

            // Never buffers more than the cap, no matter how much a hostile holder streams...
            Assert.Equal(cap, body.Length);
            // ...and reports the overflow so the caller can reject it as not-MT.
            Assert.True(truncated);
        }

        [Fact]
        public async Task ReadCapped_returns_small_body_whole_not_truncated()
        {
            var bytes = Encoding.UTF8.GetBytes("{\"service\":\"multiterminal-rest-api\"}");
            using var ms = new MemoryStream(bytes);

            var (body, truncated) = await StartupHealthProbe.ReadCappedAsync(ms, 64 * 1024);

            Assert.Equal("{\"service\":\"multiterminal-rest-api\"}", body);
            Assert.False(truncated);
        }

        [Fact]
        public async Task ReadCapped_body_exactly_at_cap_is_not_truncated()
        {
            // Boundary: a body exactly the size of the cap with nothing after it is complete.
            const int cap = 1024;
            var exact = Encoding.UTF8.GetBytes(new string('a', cap));
            using var ms = new MemoryStream(exact);

            var (body, truncated) = await StartupHealthProbe.ReadCappedAsync(ms, cap);

            Assert.Equal(cap, body.Length);
            Assert.False(truncated);
        }
    }
}
