#nullable enable
using System.Windows.Forms;
using MultiTerminal.Terminal;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Ctrl+Shift+H inside a terminal (GH #74 live check 3). The WinForms WebView2 raises its accelerator
    /// keys as KeyDown on itself and never calls ProcessCmdKey, so the shortcut never reached MainForm or
    /// a floating window from inside a terminal, docked or floating; the terminal's renderer now catches
    /// it on that KeyDown. These facts pin which keys count. That the WebView2 really delivers the key
    /// as KeyDown, and that the pane then resets, is live-checked only.
    /// </summary>
    public class ReturnToStartScreenKeyTests
    {
        [Fact]
        public void Ctrl_Shift_H_returns_the_pane_to_its_start_screen()
        {
            Assert.True(WebViewTerminalRenderer.IsReturnToStartScreenKey(Keys.Control | Keys.Shift | Keys.H));
        }

        [Theory]
        [InlineData(Keys.H)]
        [InlineData(Keys.Control | Keys.H)]
        [InlineData(Keys.Shift | Keys.H)]
        [InlineData(Keys.Control | Keys.Alt | Keys.H)]
        [InlineData(Keys.Control | Keys.Shift | Keys.Alt | Keys.H)]
        [InlineData(Keys.Control | Keys.Shift | Keys.G)]
        public void Other_keys_are_left_to_the_terminal(Keys keyData)
        {
            Assert.False(WebViewTerminalRenderer.IsReturnToStartScreenKey(keyData));
        }
    }
}
