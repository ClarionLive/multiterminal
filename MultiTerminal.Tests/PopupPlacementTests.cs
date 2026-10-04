using System.Drawing;
using MultiTerminal.DashboardHeader;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Where the header popups open (task 4cac608c): under or beside their anchor, flipped when the
    /// preferred side runs off the screen, and always fully on the working area.
    /// </summary>
    public class PopupPlacementTests
    {
        private static readonly Rectangle Screen = new Rectangle(0, 0, 1920, 1040);
        private static readonly Size Menu = new Size(250, 330);

        [Fact]
        public void Opens_under_the_button_with_left_edges_aligned()
        {
            var anchor = new Rectangle(14, 16, 40, 40);
            var placed = PopupPlacement.Place(anchor, Menu, Screen, PopupSide.Below);
            Assert.Equal(new Rectangle(14, 56 + PopupPlacement.Gap, 250, 330), placed);
        }

        [Fact]
        public void Opens_above_the_button_when_there_is_no_room_below()
        {
            var anchor = new Rectangle(100, 900, 40, 40);
            var placed = PopupPlacement.Place(anchor, Menu, Screen, PopupSide.Below);
            Assert.Equal(900 - PopupPlacement.Gap - 330, placed.Top);
        }

        [Fact]
        public void Is_pulled_back_onto_the_screen_at_the_right_edge()
        {
            var anchor = new Rectangle(1850, 16, 40, 40);
            var placed = PopupPlacement.Place(anchor, Menu, Screen, PopupSide.Below);
            Assert.Equal(1920 - 250, placed.Left);
        }

        [Fact]
        public void The_flyout_opens_to_the_right_with_top_edges_aligned()
        {
            var menu = new Rectangle(14, 60, 250, 330);
            var placed = PopupPlacement.Place(menu, new Size(220, 340), Screen, PopupSide.Right);
            Assert.Equal(new Rectangle(264 + PopupPlacement.Gap, 60, 220, 340), placed);
        }

        [Fact]
        public void The_flyout_opens_to_the_left_when_there_is_no_room_on_the_right()
        {
            var menu = new Rectangle(1600, 60, 250, 330);
            var placed = PopupPlacement.Place(menu, new Size(220, 340), Screen, PopupSide.Right);
            Assert.Equal(1600 - PopupPlacement.Gap - 220, placed.Left);
        }

        [Fact]
        public void Respects_a_working_area_that_does_not_start_at_zero()
        {
            // A second monitor to the left, with the taskbar at its top.
            var area = new Rectangle(-1920, 40, 1920, 1040);
            var anchor = new Rectangle(-30, 50, 40, 40);
            var placed = PopupPlacement.Place(anchor, Menu, area, PopupSide.Below);
            Assert.Equal(-250, placed.Left);
            Assert.True(area.Contains(placed), $"{placed} is not inside {area}");
        }

        [Fact]
        public void A_popup_taller_than_the_screen_starts_at_the_top()
        {
            var anchor = new Rectangle(14, 16, 40, 40);
            var placed = PopupPlacement.Place(anchor, new Size(250, 2000), Screen, PopupSide.Below);
            Assert.Equal(0, placed.Top);
        }
    }
}
