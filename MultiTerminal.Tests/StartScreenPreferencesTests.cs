using System;
using System.IO;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// The start screen's Home view and Browse All sort preferences (ticket e20e2ce0 item 8): their
    /// defaults, how a stored value is cleaned, and that both survive a restart (a fresh
    /// <see cref="SettingsService"/> over the same settings folder). Not covered: the page reading them,
    /// the Settings dialog writing the Home view, or the WebView2 save_sort round trip.
    /// </summary>
    public sealed class StartScreenPreferencesTests : IDisposable
    {
        private readonly string _dir;

        public StartScreenPreferencesTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"mt_startprefs_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
            }
            catch
            {
                // A leaked temp folder is not worth failing a test run over.
            }
        }

        private SettingsService NewService() => new SettingsService(_dir);

        [Fact]
        public void An_unconfigured_install_opens_on_the_list_sorted_pinned_first()
        {
            var settings = NewService();
            Assert.Equal(SettingsService.StartScreenHomeViewList, settings.GetStartScreenHomeView());
            Assert.Equal("pin:asc,name:asc", settings.GetStartScreenSort());
        }

        /// <summary>
        /// Both preferences are read back by a NEW service over the same folder, which is what a restart
        /// does. Falsified: making SetStartScreenSort a no-op turned this red at the sort assertion.
        /// </summary>
        [Fact]
        public void The_home_view_and_the_sort_survive_a_restart()
        {
            var before = NewService();
            before.SetStartScreenHomeView("cards");
            before.SetStartScreenSort("pin:asc,opened:desc");

            var after = NewService();
            Assert.Equal(SettingsService.StartScreenHomeViewCards, after.GetStartScreenHomeView());
            Assert.Equal("pin:asc,opened:desc", after.GetStartScreenSort());
        }

        [Theory]
        [InlineData("cards", "cards")]
        [InlineData(" CARDS ", "cards")]
        [InlineData("list", "list")]
        [InlineData("", "list")]
        [InlineData("tiles", "list")]
        public void Only_cards_asked_for_exactly_gives_the_card_view(string stored, string expected)
        {
            Assert.Equal(expected, SettingsService.NormalizeStartScreenHomeView(stored));
        }

        /// <summary>
        /// What a page sends is cleaned before it is stored: unknown columns and repeats are dropped, odd
        /// directions read as asc, at most three keys, and nothing valid left means the default.
        /// </summary>
        [Theory]
        [InlineData("opened:DESC,pin:asc", "opened:desc,pin:asc")]
        [InlineData("name:sideways", "name:asc")]
        [InlineData("bogus:desc,name:desc,name:asc", "name:desc")]
        [InlineData("pin,opened:desc,type,lead:desc", "pin:asc,opened:desc,type:asc")]
        [InlineData("bogus", "pin:asc,name:asc")]
        [InlineData(null, "pin:asc,name:asc")]
        public void A_sort_spec_is_cleaned_before_it_is_stored(string sent, string stored)
        {
            Assert.Equal(stored, SettingsService.NormalizeStartScreenSort(sent));
        }
    }
}
