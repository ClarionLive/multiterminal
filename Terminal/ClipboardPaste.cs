using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace MultiTerminal.Terminal
{
    /// <summary>
    /// Decides what a terminal paste sends (GH #24). Every paste — Ctrl+V, right-click and the
    /// Shift+Right-click menu's Paste — resolves its text here, then goes to xterm.js's
    /// <c>term.paste()</c> so the app receives one bracketed paste.
    ///
    /// <para>Text wins over an image: copying from Word, Excel or a browser puts both text and a
    /// bitmap on the clipboard, and the user meant the text. A clipboard holding ONLY a bitmap
    /// (a screenshot) is saved as a PNG under <see cref="DefaultImageDirectory"/> and its path is
    /// pasted, which Claude Code recognises as an image attachment.</para>
    /// </summary>
    internal static class ClipboardPaste
    {
        /// <summary>Saved screenshots older than this are deleted the next time one is saved.</summary>
        internal static readonly TimeSpan ImageRetention = TimeSpan.FromDays(7);

        internal const string ImageFilePrefix = "paste-";

        /// <summary>Where pasted bitmaps are saved: <c>%TEMP%\MultiTerminal\pasted-images</c>.</summary>
        internal static string DefaultImageDirectory =>
            Path.Combine(Path.GetTempPath(), "MultiTerminal", "pasted-images");

        /// <summary>
        /// Returns the text a paste of <paramref name="data"/> should send, or null when there is
        /// nothing pasteable. A bitmap-only clipboard is written to a new PNG in
        /// <paramref name="imageDirectory"/> and the returned text is that file's path.
        /// </summary>
        internal static string ResolvePasteText(IDataObject data, string imageDirectory, DateTime now)
        {
            if (data == null)
            {
                return null;
            }

            if (data.GetDataPresent(DataFormats.UnicodeText, true) &&
                data.GetData(DataFormats.UnicodeText, true) is string text &&
                text.Length > 0)
            {
                return text;
            }

            if (data.GetDataPresent(DataFormats.Bitmap, true) &&
                data.GetData(DataFormats.Bitmap, true) is Image image)
            {
                string path = SaveImage(image, imageDirectory, now);
                return FormatPathForPaste(path);
            }

            return null;
        }

        /// <summary>
        /// Saves <paramref name="image"/> as a PNG named for <paramref name="now"/> (plus a counter if
        /// that name is taken) and prunes saved images older than <see cref="ImageRetention"/>.
        /// </summary>
        internal static string SaveImage(Image image, string directory, DateTime now)
        {
            Directory.CreateDirectory(directory);
            PruneOldImages(directory, now);

            string stamp = now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            string path = Path.Combine(directory, ImageFilePrefix + stamp + ".png");
            for (int n = 2; File.Exists(path); n++)
            {
                path = Path.Combine(directory, ImageFilePrefix + stamp + "-" + n.ToString(CultureInfo.InvariantCulture) + ".png");
            }

            image.Save(path, ImageFormat.Png);
            return path;
        }

        /// <summary>
        /// Quotes a path containing a space, as Windows Terminal does for a dropped file, so the
        /// app reads it as one path rather than several words.
        /// </summary>
        internal static string FormatPathForPaste(string path) =>
            path.Contains(' ', StringComparison.Ordinal) ? "\"" + path + "\"" : path;

        private static void PruneOldImages(string directory, DateTime now)
        {
            foreach (string file in Directory.GetFiles(directory, ImageFilePrefix + "*.png"))
            {
                try
                {
                    if (now - File.GetLastWriteTime(file) > ImageRetention)
                    {
                        File.Delete(file);
                    }
                }
                catch (IOException)
                {
                    // Best-effort: a file still open elsewhere is left for the next prune.
                }
                catch (UnauthorizedAccessException)
                {
                    // Same: never let housekeeping fail a paste.
                }
            }
        }
    }
}
