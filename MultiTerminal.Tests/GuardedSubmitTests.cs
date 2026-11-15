using System.Collections.Generic;
using System.Threading.Tasks;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Pins <see cref="GuardedSubmit"/> (ticket 354f1efa, pipeline Run 1, Codex): an agent-requested /compact
    /// is never submitted together with text the Owner starts typing while it is being entered. The fake page
    /// models terminal.html's contract: a keystroke bumps the input count, and the conditional Enter sends a CR
    /// only when the count is unchanged. That page-side step itself is executed live only.
    /// </summary>
    public class GuardedSubmitTests
    {
        private sealed class FakePage
        {
            public long Epoch { get; private set; } = 7;
            public bool Readable { get; set; } = true;
            public bool OwnerTypesDuringWrite { get; set; }
            public bool OwnerTypesDuringSettle { get; set; }
            public List<string> Log { get; } = new List<string>();

            public Task<long?> ReadEpoch()
            {
                Log.Add("read");
                return Task.FromResult(Readable ? Epoch : (long?)null);
            }

            public void Write(string text)
            {
                Log.Add("write:" + text);
                if (OwnerTypesDuringWrite) OwnerTypes();
            }

            public Task Delay(int ms)
            {
                Log.Add("settle:" + ms);
                if (OwnerTypesDuringSettle) OwnerTypes();
                return Task.CompletedTask;
            }

            public Task<bool?> SendEnterIfNoInputSince(long epoch)
            {
                if (epoch != Epoch) return Task.FromResult((bool?)false);
                Log.Add("CR");
                return Task.FromResult((bool?)true);
            }

            private void OwnerTypes()
            {
                Epoch++;
                Log.Add("owner-typed");
            }
        }

        private static Task<GuardedSubmitResult> Run(FakePage page) =>
            GuardedSubmit.RunAsync("/compact keep", 300, page.ReadEpoch, page.Write, page.Delay, page.SendEnterIfNoInputSince);

        [Fact]
        public async Task Nobody_typing_writes_settles_then_presses_Enter()
        {
            var page = new FakePage();

            var result = await Run(page);

            Assert.Equal(GuardedSubmitOutcome.Submitted, result.Outcome);
            Assert.Equal(7, result.Epoch);
            Assert.Equal(new[] { "read", "write:/compact keep", "settle:300", "CR" }, page.Log);
        }

        [Fact]
        public async Task Owner_typing_during_the_settle_gets_no_Enter()
        {
            var page = new FakePage { OwnerTypesDuringSettle = true };

            var result = await Run(page);

            Assert.Equal(GuardedSubmitOutcome.OwnerTyped, result.Outcome);
            Assert.DoesNotContain("CR", page.Log);
        }

        [Fact]
        public async Task Owner_typing_as_the_text_is_written_gets_no_Enter()
        {
            // The count must be read BEFORE the write: read after it, this keystroke would be missed.
            var page = new FakePage { OwnerTypesDuringWrite = true };

            var result = await Run(page);

            Assert.Equal(GuardedSubmitOutcome.OwnerTyped, result.Outcome);
            Assert.DoesNotContain("CR", page.Log);
        }

        [Fact]
        public async Task A_page_that_cannot_be_asked_gets_nothing_written()
        {
            var page = new FakePage { Readable = false };

            var result = await Run(page);

            Assert.Equal(GuardedSubmitOutcome.Failed, result.Outcome);
            Assert.Null(result.Epoch);
            Assert.Equal(new[] { "read" }, page.Log);
        }
    }
}
