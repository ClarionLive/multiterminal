using System;
using System.Drawing;

namespace MultiTerminal
{
    /// <summary>
    /// The application icon, read from <c>Assets\MultiTerminal.ico</c> embedded in the assembly
    /// (GH #36, task 158d60ac). The same file is the exe's Win32 icon via
    /// <c>&lt;ApplicationIcon&gt;</c>, so the running window and the pinned/closed taskbar entry show
    /// one design. The source is <c>scripts\generate-app-icon.ps1</c>.
    /// </summary>
    internal static class AppIcon
    {
        internal const string ResourceName = "MultiTerminal.AppIcon.ico";

        private static readonly Lazy<Icon> _icon = new Lazy<Icon>(Load);

        /// <summary>
        /// One shared instance, never disposed: a Form does not dispose the Icon it is given, and
        /// the icon lives as long as the process. Null only if the resource is missing, in which
        /// case WinForms falls back to its default icon rather than the app failing to start.
        /// </summary>
        internal static Icon Value => _icon.Value;

        private static Icon Load()
        {
            using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName);
            // new Icon(Stream) keeps every frame, so WinForms can pick the 16/32/48 frame matching
            // the window's DPI instead of scaling one bitmap.
            return stream == null ? null : new Icon(stream);
        }
    }
}
