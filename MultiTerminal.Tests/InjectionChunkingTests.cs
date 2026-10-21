using System;
using System.Text;
using MultiTerminal.Controls;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Pins <see cref="InjectionChunking.Plan"/> (ticket c0ebcfd4): a slash command over 500 bytes was
    /// split into "[1/2] " / "[2/2] " submissions, so the first arrived as a plain message and the
    /// second sat unsent. These facts were falsified by removing the slash branch from Plan (the
    /// three slash facts went red, the non-slash fact stayed green) and by truncating without the
    /// surrogate guard (the surrogate fact went red); see the ticket notes for the runs.
    /// </summary>
    public class InjectionChunkingTests
    {
        private static string SlashText(int bytes)
        {
            var sb = new StringBuilder("/compact ");
            while (sb.Length < bytes) sb.Append('x');
            return sb.ToString(0, bytes);
        }

        [Fact]
        public void A_600_byte_slash_command_is_one_submission_without_a_chunk_prefix()
        {
            string text = SlashText(600);
            Assert.Equal(600, Encoding.UTF8.GetByteCount(text));

            var plan = InjectionChunking.Plan(text, out int? truncatedFrom);

            Assert.Single(plan);
            Assert.StartsWith("/compact ", plan[0], StringComparison.Ordinal);
            Assert.DoesNotContain("[1/", plan[0], StringComparison.Ordinal);
            Assert.Equal(600, truncatedFrom);
        }

        [Fact]
        public void A_long_slash_command_is_cut_to_the_500_byte_limit_and_keeps_its_start()
        {
            string text = SlashText(1400);

            var plan = InjectionChunking.Plan(text, out _);

            Assert.Single(plan);
            Assert.Equal(InjectionChunking.MaxChunkBytes, Encoding.UTF8.GetByteCount(plan[0]));
            Assert.Equal(text.Substring(0, plan[0].Length), plan[0]);
        }

        [Fact]
        public void A_slash_command_cut_never_ends_on_half_a_surrogate_pair()
        {
            // "/" + 496 'x' is 497 bytes. The emoji after it is two UTF-16 chars; its high half ALONE
            // encodes as a 3-byte replacement character, so the 498-char prefix ending in that lone
            // half measures exactly 500 bytes and a cut without the surrogate guard keeps it.
            string text = "/" + new string('x', 496) + "\U0001F600" + new string('y', 50);

            var plan = InjectionChunking.Plan(text, out _);

            Assert.Single(plan);
            Assert.False(char.IsHighSurrogate(plan[0][^1]), "the cut left a lone high surrogate");
            Assert.Equal(497, plan[0].Length);
            Assert.True(Encoding.UTF8.GetByteCount(plan[0]) <= InjectionChunking.MaxChunkBytes);
        }

        [Fact]
        public void A_short_slash_command_is_unchanged_and_not_reported_as_truncated()
        {
            var plan = InjectionChunking.Plan("/clear", out int? truncatedFrom);

            Assert.Equal(new[] { "/clear" }, plan);
            Assert.Null(truncatedFrom);
        }

        [Fact]
        public void Long_plain_text_is_still_chunked_with_markers_for_powershell()
        {
            string text = new string('a', 300) + " " + new string('b', 300) + " " + new string('c', 300);

            var plan = InjectionChunking.Plan(text, out int? truncatedFrom);

            Assert.True(plan.Count >= 2);
            Assert.Null(truncatedFrom);
            for (int i = 0; i < plan.Count; i++)
            {
                Assert.StartsWith($"[{i + 1}/{plan.Count}] ", plan[i], StringComparison.Ordinal);
                Assert.True(Encoding.UTF8.GetByteCount(plan[i]) <= InjectionChunking.MaxChunkBytes);
            }
        }
    }
}
