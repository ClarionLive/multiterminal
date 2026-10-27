using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Ticket c72ec2af: a header height saved before the GitHub row existed clipped that row. The bar
    /// grows to what the page says its rows need, never shrinks, never past the settings maximum. The
    /// page's measurement and the pane's wiring run live only.
    /// </summary>
    public class StatusBarFitTests
    {
        [Fact]
        public void A_bar_too_short_for_its_rows_grows_to_fit_them()
        {
            Assert.Equal(141, StatusBarFit.GrowTo(110, 141));
        }

        [Fact]
        public void A_bar_that_already_fits_is_left_alone_and_never_shrinks()
        {
            Assert.Null(StatusBarFit.GrowTo(141, 141));
            Assert.Null(StatusBarFit.GrowTo(200, 141));
        }

        [Fact]
        public void Growth_stops_at_the_settings_maximum()
        {
            Assert.Equal(StatusBarFit.MaxHeight, StatusBarFit.GrowTo(110, StatusBarFit.MaxHeight + 50));
            Assert.Null(StatusBarFit.GrowTo(StatusBarFit.MaxHeight, StatusBarFit.MaxHeight + 50));
        }

        [Fact]
        public void A_report_that_is_not_a_height_changes_nothing()
        {
            Assert.Null(StatusBarFit.GrowTo(110, 0));
            Assert.Null(StatusBarFit.GrowTo(110, -5));
        }
    }
}
