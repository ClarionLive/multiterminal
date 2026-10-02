using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using MultiTerminal.Terminal;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Covers <see cref="ClipboardPaste"/> — what a terminal paste sends (GH #24, task 94ae8f12).
    /// Each fact builds its own <see cref="DataObject"/>, so none touches the real clipboard.
    ///
    /// <para>What is NOT covered here: that the resolved text reaches xterm.js's
    /// <c>term.paste()</c> and arrives at the app bracketed. That path runs through WebView2 and
    /// was not executed by any test; it needs the live check listed on the ticket.</para>
    /// </summary>
    public sealed class ClipboardPasteTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "mt-clipboardpaste-tests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
        }

        /// <summary>
        /// Word, Excel and browsers put text AND a bitmap on the clipboard; the user meant the text.
        /// Falsified: swapping the two branches in ResolvePasteText turns this red (a path is
        /// returned and a PNG is written).
        /// </summary>
        [Fact]
        public void Text_wins_when_the_clipboard_holds_text_and_a_bitmap()
        {
            using var bmp = new Bitmap(4, 3);
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, "hello\r\nworld");
            data.SetData(DataFormats.Bitmap, bmp);

            string result = ClipboardPaste.ResolvePasteText(data, _dir, new DateTime(2026, 10, 2, 12, 0, 0));

            Assert.Equal("hello\r\nworld", result);
            Assert.False(Directory.Exists(_dir), "no image may be saved when text is pasted");
        }

        [Fact]
        public void A_bitmap_only_clipboard_is_saved_as_a_png_and_its_path_is_pasted()
        {
            using var bmp = new Bitmap(7, 5);
            var data = new DataObject();
            data.SetData(DataFormats.Bitmap, bmp);

            string result = ClipboardPaste.ResolvePasteText(data, _dir, new DateTime(2026, 10, 2, 12, 34, 56, 789));

            string expectedPath = Path.Combine(_dir, "paste-20261002-123456-789.png");
            Assert.Equal(ClipboardPaste.FormatPathForPaste(expectedPath), result);
            using var saved = Image.FromFile(expectedPath);
            Assert.Equal(ImageFormat.Png.Guid, saved.RawFormat.Guid);
            Assert.Equal(new Size(7, 5), saved.Size);
        }

        [Fact]
        public void Nothing_pasteable_resolves_to_null()
        {
            Assert.Null(ClipboardPaste.ResolvePasteText(null, _dir, DateTime.Now));
            Assert.Null(ClipboardPaste.ResolvePasteText(new DataObject(), _dir, DateTime.Now));

            var emptyText = new DataObject();
            emptyText.SetData(DataFormats.UnicodeText, string.Empty);
            Assert.Null(ClipboardPaste.ResolvePasteText(emptyText, _dir, DateTime.Now));
        }

        [Fact]
        public void Two_saves_in_the_same_millisecond_get_distinct_files()
        {
            using var bmp = new Bitmap(2, 2);
            var now = new DateTime(2026, 10, 2, 9, 0, 0);

            string first = ClipboardPaste.SaveImage(bmp, _dir, now);
            string second = ClipboardPaste.SaveImage(bmp, _dir, now);

            Assert.NotEqual(first, second);
            Assert.True(File.Exists(first));
            Assert.True(File.Exists(second));
        }

        [Fact]
        public void Saving_prunes_images_older_than_the_retention_window_and_keeps_newer_ones()
        {
            Directory.CreateDirectory(_dir);
            var now = new DateTime(2026, 10, 2, 12, 0, 0);
            string stale = Path.Combine(_dir, "paste-old.png");
            string fresh = Path.Combine(_dir, "paste-recent.png");
            string unrelated = Path.Combine(_dir, "notes.png");
            foreach (string f in new[] { stale, fresh, unrelated })
            {
                File.WriteAllBytes(f, new byte[] { 1 });
            }
            File.SetLastWriteTime(stale, now - ClipboardPaste.ImageRetention - TimeSpan.FromMinutes(1));
            File.SetLastWriteTime(fresh, now - ClipboardPaste.ImageRetention + TimeSpan.FromMinutes(1));
            File.SetLastWriteTime(unrelated, now - TimeSpan.FromDays(365));

            using var bmp = new Bitmap(2, 2);
            ClipboardPaste.SaveImage(bmp, _dir, now);

            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(fresh));
            Assert.True(File.Exists(unrelated), "only MT's own paste-*.png files are pruned");
        }

        /// <summary>
        /// Paste-jacking: clipboard text carrying its own ESC[201~ would end the bracketed paste
        /// early, and everything after it would reach the app as typed input — here a command plus
        /// Enter. Every ESC must be gone. Falsified: letting 0x1B through the filter turns this red.
        /// </summary>
        [Fact]
        public void An_embedded_bracketed_paste_terminator_arrives_without_its_ESC()
        {
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, "notes\u001b[201~rm -rf ~\r");

            string result = ClipboardPaste.ResolvePasteText(data, _dir, DateTime.Now);

            Assert.Equal("notes[201~rm -rf ~\r", result);
            Assert.DoesNotContain('\u001b', result);
        }

        [Fact]
        public void Tab_CR_and_LF_survive_and_other_C0_controls_are_stripped()
        {
            Assert.Equal("a\tb\r\nc", ClipboardPaste.StripControlCharacters("a\tb\r\nc"));
            Assert.Equal("xyz", ClipboardPaste.StripControlCharacters("\u0000x\u0003y\u0007\u001bz\u001f"));
        }

        [Fact]
        public void Text_made_only_of_control_characters_pastes_nothing()
        {
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, "\u001b\u0003");

            Assert.Null(ClipboardPaste.ResolvePasteText(data, _dir, DateTime.Now));
        }

        [Theory]
        [InlineData(@"C:\Temp\paste-1.png", @"C:\Temp\paste-1.png")]
        [InlineData(@"C:\Users\JOHNHI~1\AppData\Local\Temp\paste-1.png", @"C:\Users\JOHNHI~1\AppData\Local\Temp\paste-1.png")]
        [InlineData(@"C:\Users\John Smith\AppData\Local\Temp\paste-1.png", "\"C:\\Users\\John Smith\\AppData\\Local\\Temp\\paste-1.png\"")]
        [InlineData(@"C:\Users\R&D\paste-1.png", "\"C:\\Users\\R&D\\paste-1.png\"")]
        [InlineData(@"C:\Users\o'brien\paste-1.png", "\"C:\\Users\\o'brien\\paste-1.png\"")]
        [InlineData(@"C:\Users\a;b\paste-1.png", "\"C:\\Users\\a;b\\paste-1.png\"")]
        [InlineData(@"C:\Users\$x\paste-1.png", "\"C:\\Users\\$x\\paste-1.png\"")]
        [InlineData(@"C:\Users\a(1)\paste-1.png", "\"C:\\Users\\a(1)\\paste-1.png\"")]
        [InlineData(@"C:\Users\a`b\paste-1.png", "\"C:\\Users\\a`b\\paste-1.png\"")]
        public void A_path_is_quoted_when_it_contains_a_space_or_shell_metacharacter(string path, string expected)
        {
            Assert.Equal(expected, ClipboardPaste.FormatPathForPaste(path));
        }
    }
}
