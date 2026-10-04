using MultiTerminal.DashboardHeader;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Only the header's own page may trigger native actions (task 4cac608c, pipeline Run 1 security
    /// finding). These pin the check every header and popup message passes through before it can
    /// reach Exit, Open PowerShell or a project switch.
    /// </summary>
    public class WebViewPagePinTests
    {
        private const string Page = "file:///C:/Program%20Files/MultiTerminal/DashboardHeader/header-popup.html";

        [Fact]
        public void The_page_itself_is_trusted()
        {
            Assert.True(WebViewPagePin.IsFromPage(Page, Page));
        }

        [Fact]
        public void Drive_letter_and_path_case_do_not_matter()
        {
            Assert.True(WebViewPagePin.IsFromPage(
                "file:///c:/program%20files/multiterminal/dashboardheader/HEADER-POPUP.html", Page));
        }

        [Theory]
        [InlineData("file:///C:/Program%20Files/MultiTerminal/DashboardHeader/dashboard.html")] // a sibling page
        [InlineData("file:///C:/Users/x/Downloads/header-popup.html")]                          // same name, elsewhere
        [InlineData("file:///C:/Program%20Files/MultiTerminal/DashboardHeader/header-popup.html?x=1")]
        [InlineData("file:///C:/Program%20Files/MultiTerminal/DashboardHeader/header-popup.html#evil")]
        [InlineData("https://example.com/header-popup.html")]
        [InlineData("about:blank")]
        [InlineData("data:text/html,<script>chrome.webview.postMessage({})</script>")]
        [InlineData("")]
        [InlineData(null)]
        public void Anything_else_is_not(string source)
        {
            Assert.False(WebViewPagePin.IsFromPage(source, Page));
        }

        [Fact]
        public void No_known_page_trusts_nothing()
        {
            // The header's page path is only known once its html was found; until then, nothing counts.
            Assert.False(WebViewPagePin.IsFromPage(Page, null));
        }
    }
}
