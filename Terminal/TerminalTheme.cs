using System.Drawing;

namespace MultiTerminal.Terminal
{
    /// <summary>
    /// Defines color themes for the terminal.
    /// </summary>
    public class TerminalTheme
    {
        /// <summary>
        /// Terminal background color.
        /// </summary>
        public Color Background { get; set; }

        /// <summary>
        /// Default text foreground color.
        /// </summary>
        public Color Foreground { get; set; }

        /// <summary>
        /// Toolbar background color.
        /// </summary>
        public Color ToolbarBackground { get; set; }

        /// <summary>
        /// Toolbar text and button color.
        /// </summary>
        public Color ToolbarForeground { get; set; }

        /// <summary>
        /// Text selection background color.
        /// </summary>
        public Color SelectionBackground { get; set; }

        /// <summary>
        /// Selected text foreground color.
        /// </summary>
        public Color SelectionForeground { get; set; }

        /// <summary>
        /// Cursor color.
        /// </summary>
        public Color CursorColor { get; set; }

        /// <summary>
        /// Active tab background color.
        /// </summary>
        public Color TabActiveBackground { get; set; }

        /// <summary>
        /// Inactive tab background color.
        /// </summary>
        public Color TabInactiveBackground { get; set; }

        /// <summary>
        /// Inactive tab text color.
        /// </summary>
        public Color TabInactiveForeground { get; set; }

        /// <summary>
        /// Status label text color.
        /// </summary>
        public Color StatusForeground { get; set; }

        /// <summary>
        /// The 16-color ANSI palette for this theme.
        /// </summary>
        public Color[] AnsiColors { get; set; }

        /// <summary>
        /// Whether this is a dark theme (affects some color calculations).
        /// </summary>
        public bool IsDark { get; set; }

        /// <summary>
        /// Gets the dark terminal theme (default).
        /// </summary>
        public static TerminalTheme Dark => new TerminalTheme
        {
            IsDark = true,
            // Windows Terminal "Campbell" theme colors
            Background = Color.FromArgb(12, 12, 12),           // #0C0C0C
            Foreground = Color.FromArgb(204, 204, 204),        // #CCCCCC
            ToolbarBackground = Color.FromArgb(30, 30, 30),
            ToolbarForeground = Color.White,
            SelectionBackground = Color.FromArgb(58, 118, 179),
            SelectionForeground = Color.White,
            CursorColor = Color.FromArgb(200, 204, 204, 204),
            TabActiveBackground = Color.FromArgb(30, 30, 30),
            TabInactiveBackground = Color.FromArgb(12, 12, 12),
            TabInactiveForeground = Color.FromArgb(180, 180, 180),
            StatusForeground = Color.FromArgb(128, 128, 128),
            // Windows Terminal Campbell palette
            AnsiColors = new Color[]
            {
                Color.FromArgb(12, 12, 12),      // 0 Black
                Color.FromArgb(197, 15, 31),     // 1 Red
                Color.FromArgb(19, 161, 14),     // 2 Green
                Color.FromArgb(193, 156, 0),     // 3 Yellow
                Color.FromArgb(0, 55, 218),      // 4 Blue
                Color.FromArgb(136, 23, 152),    // 5 Magenta
                Color.FromArgb(58, 150, 221),    // 6 Cyan
                Color.FromArgb(204, 204, 204),   // 7 White
                Color.FromArgb(118, 118, 118),   // 8 Bright Black
                Color.FromArgb(231, 72, 86),     // 9 Bright Red
                Color.FromArgb(22, 198, 12),     // 10 Bright Green
                Color.FromArgb(249, 241, 165),   // 11 Bright Yellow
                Color.FromArgb(59, 120, 255),    // 12 Bright Blue
                Color.FromArgb(180, 0, 158),     // 13 Bright Magenta
                Color.FromArgb(97, 214, 214),    // 14 Bright Cyan
                Color.FromArgb(242, 242, 242)    // 15 Bright White
            }
        };

        /// <summary>
        /// Gets the light terminal theme.
        /// </summary>
        public static TerminalTheme Light => new TerminalTheme
        {
            IsDark = false,
            Background = Color.FromArgb(255, 255, 255),
            Foreground = Color.FromArgb(30, 30, 30),
            ToolbarBackground = Color.FromArgb(230, 236, 242),
            ToolbarForeground = Color.FromArgb(30, 30, 30),
            SelectionBackground = Color.FromArgb(173, 214, 255),
            SelectionForeground = Color.FromArgb(0, 0, 0),
            CursorColor = Color.FromArgb(200, 50, 50, 50),
            TabActiveBackground = Color.FromArgb(255, 255, 255),
            TabInactiveBackground = Color.FromArgb(230, 236, 242),
            TabInactiveForeground = Color.FromArgb(100, 100, 100),
            StatusForeground = Color.FromArgb(100, 100, 100),
            // ANSI colors adjusted for light background - darker/more saturated
            AnsiColors = new Color[]
            {
                Color.FromArgb(0, 0, 0),         // 0 Black
                Color.FromArgb(180, 0, 0),       // 1 Red (darker)
                Color.FromArgb(0, 135, 0),       // 2 Green (darker)
                Color.FromArgb(135, 135, 0),     // 3 Yellow (darker/olive)
                Color.FromArgb(0, 0, 180),       // 4 Blue (darker)
                Color.FromArgb(135, 0, 135),     // 5 Magenta (darker)
                Color.FromArgb(0, 135, 135),     // 6 Cyan (darker)
                Color.FromArgb(80, 80, 80),      // 7 White -> dark gray for visibility
                Color.FromArgb(100, 100, 100),   // 8 Bright Black -> gray
                Color.FromArgb(215, 0, 0),       // 9 Bright Red
                Color.FromArgb(0, 175, 0),       // 10 Bright Green
                Color.FromArgb(175, 175, 0),     // 11 Bright Yellow
                Color.FromArgb(0, 0, 215),       // 12 Bright Blue
                Color.FromArgb(175, 0, 175),     // 13 Bright Magenta
                Color.FromArgb(0, 175, 175),     // 14 Bright Cyan
                Color.FromArgb(60, 60, 60)       // 15 Bright White -> dark gray
            }
        };
    }
}
