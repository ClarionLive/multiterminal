using System;
using System.IO;
using MultiTerminal.Docking;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 19a26090: which statusline file a pane's header reads
    /// (<see cref="TerminalDocument.PickStatusLineFile"/>).
    ///
    /// <para><see cref="A_pane_with_the_wrong_name_still_reads_its_own_file"/> is the 2026-10-01
    /// state: Charlie's pane polled as "Grace" and read Grace's file. Falsified by restoring the
    /// name-scoped exact lookup (predicted beforehand: this fact and the relaunch fact red, the other
    /// three green; observed exactly that).</para>
    /// </summary>
    public sealed class StatusLineFilePickTests : IDisposable
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("mt-statusline-pick-").FullName;
        private static readonly long Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }

        private string Write(string name, string docId, long timestampMs, DateTime? writtenUtc = null)
        {
            string path = Path.Combine(_dir, $"mt-statusline-{name}-{docId}.json");
            File.WriteAllText(path, $"{{\"terminalName\":\"{name}\",\"timestamp\":{timestampMs}}}");
            if (writtenUtc.HasValue) File.SetLastWriteTimeUtc(path, writtenUtc.Value);
            return path;
        }

        [Fact]
        public void A_pane_with_the_wrong_name_still_reads_its_own_file()
        {
            Write("Grace", "15fbd989", Now);                        // Grace's pane
            string charlies = Write("Charlie", "5cbc12d2", Now);    // Charlie's pane

            // Charlie's pane, mislabelled "Grace" as it was on 2026-10-01.
            var pick = TerminalDocument.PickStatusLineFile(_dir, "Grace", "5cbc12d2", Now - 60_000, Now);

            Assert.Equal(charlies, pick.Path);
            Assert.True(pick.OwnPane);
        }

        [Fact]
        public void A_relaunched_pane_reads_the_most_recently_written_of_its_files()
        {
            var t = DateTime.UtcNow;
            Write("Alice", "aaaa1111", Now, t.AddMinutes(-30));         // previous launch, exited
            string bob = Write("Bob", "aaaa1111", Now - 1, t);          // current launch

            var pick = TerminalDocument.PickStatusLineFile(_dir, "Alice", "aaaa1111", 0, Now);

            Assert.Equal(bob, pick.Path);
        }

        [Fact]
        public void A_pane_with_no_file_of_its_own_falls_back_to_a_fresh_same_named_file()
        {
            string restored = Write("Alice", "oldd0c1d", Now - 1_000);

            var pick = TerminalDocument.PickStatusLineFile(_dir, "Alice", "newd0c1d", Now - 60_000, Now);

            Assert.Equal(restored, pick.Path);
            Assert.False(pick.OwnPane);
        }

        [Fact]
        public void The_fallback_still_rejects_stale_and_future_dated_files()
        {
            Write("Alice", "stale001", Now - 3_600_000);
            Write("Alice", "future01", Now + 3_600_000);

            var pick = TerminalDocument.PickStatusLineFile(_dir, "Alice", "newd0c1d", Now - 60_000, Now);

            Assert.Null(pick.Path);
        }

        [Fact]
        public void An_unsafe_docId_never_widens_the_search()
        {
            Write("Alice", "aaaa1111", Now);

            var pick = TerminalDocument.PickStatusLineFile(_dir, "Nobody", "*", 0, Now);

            Assert.Null(pick.Path);
        }
    }
}
