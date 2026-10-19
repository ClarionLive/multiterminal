using System.Collections.Generic;
using MultiTerminal.Models;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Ticket c8ee0eec item 9: TokenMeterService's transcript-path guard accepts a transcript under any
    /// configured Claude config root, and nothing else. All roots are hand-supplied or built from an
    /// injected <see cref="ClaudeProfileContext"/>; nothing touches the real settings, environment or disk.
    /// </summary>
    public sealed class TokenMeterTranscriptGuardTests
    {
        private const string Home = @"C:\Users\Dev";
        private const string DefaultDir = Home + @"\.claude";
        private const string OtherDir = Home + @"\.claude-other";

        /// <summary>Roots exactly as the resolver produces them for Default + one "Other" profile.</summary>
        private static IReadOnlyList<string> ResolverRoots() =>
            ClaudeProfileResolver.GetAllRoots(new ClaudeProfileContext(
                new[] { new ClaudeProfile("Other", OtherDir) }, null, Home, _ => true));

        private static bool Allowed(string path) => TokenMeterService.IsAllowedTranscriptPath(path, ResolverRoots());

        [Fact]
        public void Transcript_under_a_second_configured_root_is_accepted()
        {
            Assert.True(Allowed(OtherDir + @"\projects\H--proj\abc.jsonl"));
        }

        [Fact]
        public void Transcript_under_the_default_root_is_still_accepted()
        {
            Assert.True(Allowed(DefaultDir + @"\projects\H--proj\abc.jsonl"));
        }

        [Fact]
        public void Transcript_in_an_unconfigured_sibling_directory_is_rejected()
        {
            Assert.False(Allowed(Home + @"\.claude-personal\projects\H--proj\abc.jsonl"));
            Assert.False(Allowed(@"C:\Temp\abc.jsonl"));
        }

        [Fact]
        public void Dotdot_traversal_out_of_a_root_is_rejected()
        {
            // Raw text starts with the root prefix; only the canonical form shows it escapes to C:\Users\evil.
            Assert.False(Allowed(OtherDir + @"\projects\..\..\..\evil\x.jsonl"));
            Assert.False(Allowed(DefaultDir + @"\projects\..\..\evil\x.jsonl"));
        }

        /// <summary>
        /// Falsified: replacing the trailing-separator compare with a plain StartsWith on the root without
        /// its separator turns this test red (".claude-otherX" then starts with ".claude-other").
        /// </summary>
        [Fact]
        public void Prefix_sibling_of_a_configured_root_is_rejected()
        {
            Assert.False(Allowed(Home + @"\.claude-otherX\projects\H--proj\abc.jsonl"));
            Assert.False(Allowed(Home + @"\.claudeX\projects\H--proj\abc.jsonl"));
        }

        [Fact]
        public void Relative_path_is_rejected()
        {
            Assert.False(Allowed(@"projects\H--proj\abc.jsonl"));
            Assert.False(Allowed(@".claude\projects\abc.jsonl"));
            Assert.False(Allowed(@"\Users\Dev\.claude\projects\abc.jsonl")); // rooted but not drive-qualified
        }

        [Fact]
        public void Non_jsonl_file_under_a_root_is_still_rejected()
        {
            Assert.False(Allowed(OtherDir + @"\settings.json"));
        }

        /// <summary>
        /// The resolver already drops drive roots; this asserts the guard ALSO ignores them when a list
        /// containing one is supplied directly, while still honouring the valid root beside it.
        /// </summary>
        [Fact]
        public void Hand_supplied_volume_root_never_makes_everything_accepted()
        {
            var roots = new[] { @"C:\", @"C:", @"\\server\share\", "relative\\dir\\", null, string.Empty, OtherDir + @"\" };

            Assert.False(TokenMeterService.IsAllowedTranscriptPath(@"C:\Temp\abc.jsonl", roots));
            Assert.False(TokenMeterService.IsAllowedTranscriptPath(@"\\server\share\x\abc.jsonl", roots));
            Assert.True(TokenMeterService.IsAllowedTranscriptPath(OtherDir + @"\projects\abc.jsonl", roots));
        }

        [Fact]
        public void Root_supplied_without_a_trailing_separator_still_blocks_the_prefix_sibling()
        {
            var roots = new[] { OtherDir };
            Assert.True(TokenMeterService.IsAllowedTranscriptPath(OtherDir + @"\projects\abc.jsonl", roots));
            Assert.False(TokenMeterService.IsAllowedTranscriptPath(Home + @"\.claude-otherX\projects\abc.jsonl", roots));
        }
    }
}
