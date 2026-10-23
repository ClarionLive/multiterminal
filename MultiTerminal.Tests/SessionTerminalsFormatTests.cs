using System.Linq;
using MultiTerminal.Models;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// The malformed-value branches of <see cref="SessionTerminalsFormat.Read"/> (task d71bb221,
    /// pipeline Run 2). The class promises it never throws and restores nothing rather than guessing;
    /// these pin each branch that keeps that promise. The round-trip and anti-forgery facts live in
    /// <c>AttentionTeamLayoutTests</c>, beside the resolver they protect.
    /// </summary>
    public sealed class SessionTerminalsFormatTests
    {
        private const string Good = "{\"v\":2,\"terminals\":[{\"dir\":\"C:\\\\p\",\"size\":10,\"title\":\"Owen\"}]}";

        [Fact]
        public void The_current_version_reads()
        {
            var restored = SessionTerminalsFormat.Read(Good);

            Assert.NotNull(restored);
            Assert.Equal("Owen", Assert.Single(restored).CustomTitle);
        }

        /// <summary>A version this build does not know restores nothing: null, not a guess.</summary>
        [Theory]
        [InlineData("{\"v\":3,\"terminals\":[{\"dir\":\"C:\\\\p\",\"size\":10,\"title\":\"Owen\"}]}")]
        [InlineData("{\"v\":\"2\",\"terminals\":[{\"dir\":\"C:\\\\p\",\"size\":10,\"title\":\"Owen\"}]}")]
        [InlineData("{\"terminals\":[{\"dir\":\"C:\\\\p\",\"size\":10,\"title\":\"Owen\"}]}")]
        [InlineData("{\"v\":2,\"terminals\":{\"dir\":\"C:\\\\p\"}}")]
        public void An_unknown_version_or_shape_restores_nothing(string value)
        {
            Assert.Null(SessionTerminalsFormat.Read(value));
        }

        /// <summary>
        /// A value that starts like JSON but does not parse is read as legacy lines, and yields no
        /// pane from the broken text. It never throws.
        /// </summary>
        [Fact]
        public void Truncated_json_does_not_throw_and_restores_no_pane()
        {
            var restored = SessionTerminalsFormat.Read(Good.Substring(0, Good.Length / 2));

            Assert.NotNull(restored);
            Assert.Empty(restored);
        }

        /// <summary>An entry whose size is missing or not a number is skipped; its neighbours survive.</summary>
        [Fact]
        public void An_entry_without_a_numeric_size_is_skipped()
        {
            const string value = "{\"v\":2,\"terminals\":["
                + "{\"dir\":\"C:\\\\a\",\"size\":\"big\",\"title\":\"Bad\"},"
                + "{\"dir\":\"C:\\\\b\",\"title\":\"NoSize\"},"
                + "{\"dir\":\"C:\\\\c\",\"size\":11,\"title\":\"Kept\"}]}";

            var restored = SessionTerminalsFormat.Read(value);

            Assert.Equal("Kept", Assert.Single(restored).CustomTitle);
        }

        /// <summary>A field over the length cap reads as absent; the pane itself is still restored.</summary>
        [Fact]
        public void An_oversized_field_reads_as_absent()
        {
            string longTitle = new string('x', SessionTerminalsFormat.MaxFieldLength + 1);
            string value = SessionTerminalsFormat.Write(new[]
            {
                new TerminalSessionInfo { WorkingDirectory = @"C:\p", FontSize = 10f, CustomTitle = longTitle },
            });

            var pane = Assert.Single(SessionTerminalsFormat.Read(value));

            Assert.Null(pane.CustomTitle);
            Assert.Equal(@"C:\p", pane.WorkingDirectory);
        }

        /// <summary>A field exactly at the cap is kept, so the cap is a boundary, not an off-by-one.</summary>
        [Fact]
        public void A_field_at_the_cap_is_kept()
        {
            string title = new string('x', SessionTerminalsFormat.MaxFieldLength);
            string value = SessionTerminalsFormat.Write(new[]
            {
                new TerminalSessionInfo { WorkingDirectory = @"C:\p", FontSize = 10f, CustomTitle = title },
            });

            Assert.Equal(title, SessionTerminalsFormat.Read(value).Single().CustomTitle);
        }
    }
}
