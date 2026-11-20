using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MultiTerminal.Models;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 925c8cff (review of PR #100): how a <see cref="TranscriptTailer"/> reads. One read pass at
    /// a time per tailer, events raised after the pass releases its lock, the file watcher live before
    /// the catch-up read, and bounds on what a long-lived tailer keeps. All facts run over temp files.
    /// </summary>
    public sealed class TranscriptTailerReadTests : IDisposable
    {
        private const string SessionId = "5f1c2b9e-0a4d-4f7e-9c1b-2d3e4f5a6b7c";

        /// <summary>How long a fact waits for something that should happen in well under a second. Never reached when it works.</summary>
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mt_tailer_read_{Guid.NewGuid():N}");

        public TranscriptTailerReadTests()
        {
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private string NewTranscript(params string[] lines)
        {
            string path = Path.Combine(_dir, $"agent-{Guid.NewGuid():N}.jsonl");
            File.WriteAllLines(path, lines);
            return path;
        }

        private static string Assistant(string uuid, string text, string stopReason = "tool_use") =>
            "{\"type\":\"assistant\",\"uuid\":\"" + uuid + "\",\"sessionId\":\"" + SessionId + "\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\""
            + text + "\"}],\"stop_reason\":" + (stopReason == null ? "null" : "\"" + stopReason + "\"") + "}}";

        private static IEnumerable<string> Texts(TranscriptTailer tailer) =>
            tailer.Messages.Where(m => m.Type == AgentMessageType.Assistant).Select(m => m.Content);

        // ── Item 6: one read pass at a time; the watcher before the catch-up ─────

        /// <summary>
        /// The catch-up read of a file and a read started by that file's watcher must not run at once.
        /// The first pass appends a line from inside itself and then holds on for a second; the append
        /// makes the watcher start a second pass. With passes serialized that pass waits for the first
        /// to end; without, it runs to its end, and so into the hook, while the first is still inside.
        /// The appended line is then read exactly once. (It also needs the watcher to be live during
        /// the catch-up read, which the next fact pins: with the watcher started after it, no second
        /// pass comes and this fails on its first assert.)
        /// </summary>
        [Fact]
        public async Task A_watcher_read_waits_for_a_read_pass_already_running()
        {
            string path = NewTranscript(Assistant("u1", "first"));
            using var tailer = new TranscriptTailer(path, null, "reader");

            int inside = 0, maxInside = 0, passes = 0;
            var secondPassDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            tailer.ReadPassHook = () =>
            {
                int now = Interlocked.Increment(ref inside);
                InterlockedMax(ref maxInside, now);
                int pass = Interlocked.Increment(ref passes);
                if (pass == 1)
                {
                    File.AppendAllText(path, Assistant("u2", "second") + Environment.NewLine);
                    Thread.Sleep(1000);
                }
                else
                {
                    secondPassDone.TrySetResult(true);
                }
                Interlocked.Decrement(ref inside);
            };

            await tailer.StartAsync();

            Assert.True(await Task.WhenAny(secondPassDone.Task, Task.Delay(Wait)) == secondPassDone.Task, "the watcher never read the appended line");
            Assert.Equal(1, Volatile.Read(ref maxInside));
            Assert.Equal(new[] { "first", "second" }, Texts(tailer).ToArray());
        }

        /// <summary>
        /// A line appended just after the catch-up read reached the end of the file is read. The line
        /// is appended from the end of that read pass (before its lock is released), the latest point
        /// at which an append can still be missed by the catch-up. Before 925c8cff the watcher was
        /// started after the catch-up read, so no event came for that append and it was never read.
        /// </summary>
        [Fact]
        public async Task A_line_appended_as_the_catch_up_read_ends_is_read()
        {
            string path = NewTranscript(Assistant("u1", "first"));
            using var tailer = new TranscriptTailer(path, null, "reader");

            var late = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int passes = 0;
            tailer.ReadPassHook = () =>
            {
                if (Interlocked.Increment(ref passes) == 1)
                    File.AppendAllText(path, Assistant("u2", "late") + Environment.NewLine);
            };
            tailer.MessageReceived += (s, m) =>
            {
                if (m.Content == "late") late.TrySetResult(true);
            };

            await tailer.StartAsync();

            Assert.True(await Task.WhenAny(late.Task, Task.Delay(Wait)) == late.Task, "the line appended as the catch-up read ended was never read");
            Assert.Equal(new[] { "first", "late" }, Texts(tailer).ToArray());
        }

        /// <summary>Events a read pass produces are raised once its lock is released, never inside it.</summary>
        [Fact]
        public async Task Messages_are_raised_outside_the_read_lock()
        {
            string path = NewTranscript(Assistant("u1", "first"));
            using var tailer = new TranscriptTailer(path, null, "reader");

            var heldWhenRaised = new ConcurrentQueue<bool>();
            tailer.MessageReceived += (s, m) => heldWhenRaised.Enqueue(tailer.ReadLockHeldByCurrentThread);

            await tailer.StartAsync();

            Assert.NotEmpty(heldWhenRaised);
            Assert.DoesNotContain(true, heldWhenRaised);
        }

        // ── Item 3: what a long-lived tailer keeps is bounded ──────────────────

        [Fact]
        public async Task Messages_keep_only_the_newest_past_the_cap()
        {
            string path = NewTranscript(Enumerable.Range(1, 10).Select(i => Assistant("u" + i, "m" + i)).ToArray());
            using var tailer = new TranscriptTailer(path, null, "reader") { MaxRetainedMessages = 4 };

            await tailer.StartAsync();

            Assert.Equal(new[] { "m7", "m8", "m9", "m10" }, Texts(tailer).ToArray());
        }

        /// <summary>
        /// The ids the cross-file repeat check (GH #89) remembers are capped, oldest out first. A line
        /// repeated from a still-remembered id is skipped; one whose id was evicted is shown again,
        /// which is the price of the cap.
        /// </summary>
        [Fact]
        public async Task The_line_ids_kept_for_the_repeat_check_are_capped_oldest_first()
        {
            string main = NewTranscript(Enumerable.Range(1, 5).Select(i => Assistant("u" + i, "m" + i)).ToArray());
            using var tailer = new TranscriptTailer(main, null, "reader") { MaxTrackedLineIds = 3 };
            await tailer.StartAsync();
            Assert.Equal(3, tailer.TrackedLineIdCount);

            // u5 is remembered (skipped as a repeat); u1 was evicted (shown again).
            string continuation = NewTranscript(Assistant("u5", "m5"), Assistant("u1", "m1"));
            tailer.AddTranscriptFile(continuation);

            Assert.Equal(new[] { "m1", "m2", "m3", "m4", "m5", "m1" }, Texts(tailer).ToArray());
            Assert.Equal(3, tailer.TrackedLineIdCount);
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int seen;
            do
            {
                seen = Volatile.Read(ref target);
                if (value <= seen) return;
            }
            while (Interlocked.CompareExchange(ref target, value, seen) != seen);
        }
    }
}
