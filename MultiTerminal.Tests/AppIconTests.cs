using System;
using System.IO;
using System.Linq;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// GH #36 (task 158d60ac): the app icon is one checked-in .ico, embedded for the window icon and
    /// used as the exe's Win32 icon. These facts read the EMBEDDED copy, which is what the running
    /// window actually gets.
    ///
    /// <para>What they do not cover: the Win32 resource in the apphost exe (that is
    /// <c>&lt;ApplicationIcon&gt;</c>, checked by building and inspecting the exe), and whether the
    /// design looks right (checked by eye against <c>scripts\generate-app-icon.ps1</c>'s previews).</para>
    /// </summary>
    public class AppIconTests
    {
        private static byte[] ReadEmbeddedIcon()
        {
            using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream(AppIcon.ResourceName);
            Assert.True(stream != null, $"embedded resource '{AppIcon.ResourceName}' is missing; check the EmbeddedResource LogicalName in MultiTerminal.csproj");
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        [Fact]
        public void The_embedded_icon_carries_every_size_from_16_to_256()
        {
            var bytes = ReadEmbeddedIcon();

            // ICONDIR: reserved 0, type 1 (icon), then the entry count.
            Assert.Equal(0, BitConverter.ToInt16(bytes, 0));
            Assert.Equal(1, BitConverter.ToInt16(bytes, 2));
            int count = BitConverter.ToInt16(bytes, 4);

            var sizes = Enumerable.Range(0, count)
                .Select(i => bytes[6 + 16 * i] == 0 ? 256 : bytes[6 + 16 * i])   // width byte 0 means 256
                .OrderBy(s => s)
                .ToArray();

            Assert.Equal(new[] { 16, 24, 32, 48, 64, 128, 256 }, sizes);
        }

        [Fact]
        public void Every_frame_lies_inside_the_file()
        {
            // The size census above reads only the directory, so it would pass a file whose directory is
            // right but whose image bytes are missing or truncated. That happened once while writing the
            // generator (PowerShell unrolled a byte[] into the pipeline; the file came out at 125 bytes).
            // This checks each entry's bytes are actually present. It was NOT re-run against that file.
            var bytes = ReadEmbeddedIcon();
            int count = BitConverter.ToInt16(bytes, 4);
            for (int i = 0; i < count; i++)
            {
                int length = BitConverter.ToInt32(bytes, 6 + 16 * i + 8);
                int offset = BitConverter.ToInt32(bytes, 6 + 16 * i + 12);
                Assert.True(length > 40, $"frame {i} is {length} bytes");
                Assert.True(offset + length <= bytes.Length, $"frame {i} runs past the end of the file");
            }
        }

        [Fact]
        public void The_window_icon_loads_from_the_resource()
        {
            Assert.NotNull(AppIcon.Value);
        }
    }
}
