using System;
using System.Collections.Generic;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Ticket c8ee0eec (GitHub #23): where MT looks for its Claude Code plugin. Lookup order is the
    /// installer's shared location (HKCU\Software\MultiTerminal\PluginMarketplacePath) first, then the
    /// default profile's marketplace checkout. The registry value and the filesystem are injected; no real
    /// registry or profile is read.
    /// </summary>
    public class LaunchCommandBuilderPluginPathTests
    {
        private const string Profile = @"C:\Users\Dev";
        private const string SharedRoot = @"D:\Shared\multiterminal-marketplace";
        private const string SharedPlugin = SharedRoot + @"\plugins\multiterminal";
        private const string DefaultPlugin = Profile + @"\.claude\plugins\marketplaces\multiterminal-marketplace\plugins\multiterminal";

        private static Func<string, bool> Existing(params string[] paths)
        {
            var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            return set.Contains;
        }

        private static string Resolve(string registryValue, Func<string, bool> directoryExists) =>
            LaunchCommandBuilder.ResolveMtPluginPath(
                LaunchCommandBuilder.MtPluginPathCandidates(registryValue, Profile), directoryExists);

        /// <summary>
        /// Both locations exist and the shared one wins. Discriminates the ORDER: with the default probed
        /// first this returns the default plugin.
        /// </summary>
        [Fact]
        public void The_shared_location_wins_when_it_exists()
        {
            Assert.Equal(SharedPlugin, Resolve(SharedRoot, Existing(SharedPlugin, DefaultPlugin)));
        }

        [Fact]
        public void Falls_back_to_the_default_profile_when_the_shared_directory_is_missing()
        {
            Assert.Equal(DefaultPlugin, Resolve(SharedRoot, Existing(DefaultPlugin)));
        }

        [Fact]
        public void No_registry_value_means_the_default_profile_only()
        {
            Assert.Equal(new[] { DefaultPlugin }, LaunchCommandBuilder.MtPluginPathCandidates(null, Profile));
            Assert.Equal(DefaultPlugin, Resolve(null, Existing(DefaultPlugin)));
        }

        /// <summary>
        /// A garbage value is dropped from the candidate list rather than resolved. A relative value matters
        /// most: Path.Combine would happily resolve it against MT's current directory, which could exist.
        /// The probe here accepts EVERY path, so a garbage value that leaked into the list would win.
        /// </summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(@"relative\marketplace")]
        [InlineData(@"C:drive-relative")]
        [InlineData("\0bad")]
        public void A_garbage_registry_value_falls_through_to_the_default(string registryValue)
        {
            Assert.Equal(new[] { DefaultPlugin }, LaunchCommandBuilder.MtPluginPathCandidates(registryValue, Profile));
            Assert.Equal(DefaultPlugin, Resolve(registryValue, _ => true));
        }

        [Fact]
        public void A_quoted_registry_value_is_accepted()
        {
            Assert.Equal(SharedPlugin, Resolve("\"" + SharedRoot + "\"", Existing(SharedPlugin)));
        }

        [Fact]
        public void Null_when_nothing_exists()
        {
            Assert.Null(Resolve(SharedRoot, Existing()));
        }

        /// <summary>A probe that throws on one candidate skips that candidate; it does not abort the lookup.</summary>
        [Fact]
        public void A_throwing_probe_skips_only_that_candidate()
        {
            Func<string, bool> probe = p => p == SharedPlugin ? throw new UnauthorizedAccessException() : p == DefaultPlugin;

            Assert.Equal(DefaultPlugin, Resolve(SharedRoot, probe));
        }
    }
}
