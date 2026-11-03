using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Models;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    public class GlossWriterStopGateTests
    {
        [Fact]
        public void First_claim_wins_and_every_later_claim_loses()
        {
            var gate = new GlossWriterStopGate();
            Assert.False(gate.StopClaimed);
            Assert.True(gate.TryClaimStop());
            Assert.True(gate.StopClaimed);
            Assert.False(gate.TryClaimStop());
            Assert.False(gate.TryClaimStop());
        }

        [Fact]
        public async Task Racing_callers_produce_exactly_one_winner()
        {
            for (int round = 0; round < 50; round++)
            {
                var gate = new GlossWriterStopGate();
                var tasks = Enumerable.Range(0, 16)
                    .Select(_ => Task.Run(() => gate.TryClaimStop()))
                    .ToArray();
                bool[] results = await Task.WhenAll(tasks);
                Assert.Equal(1, results.Count(r => r));
            }
        }

        [Fact]
        public void Only_the_result_message_ends_a_turn()
        {
            Assert.True(GlossWriterStopGate.IsTurnEnd(new AgentMessage { Type = AgentMessageType.Result }));
            Assert.False(GlossWriterStopGate.IsTurnEnd(null));
            foreach (var type in new[] { AgentMessageType.System, AgentMessageType.Assistant, AgentMessageType.User,
                AgentMessageType.ToolUse, AgentMessageType.ToolResult, AgentMessageType.Thinking,
                AgentMessageType.StreamDelta, AgentMessageType.Error })
            {
                Assert.False(GlossWriterStopGate.IsTurnEnd(new AgentMessage { Type = type }), type.ToString());
            }
        }

        [Fact]
        public void Backstop_equals_the_default_per_ticket_cooldown()
        {
            FieldInfo field = typeof(GlossBackfillService).GetField(
                "DefaultCooldownMs", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(field);
            Assert.Equal(GlossWriterStopGate.BackstopMs, (int)field.GetRawConstantValue());
        }
    }
}
