using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 5e1dea4c: the debug log FILE stopped growing seconds after startup while the in-memory
    /// buffer kept rolling (2026-10-01's file ends at 08:28:40, 15 KB). Cause: the background writer
    /// looped on <c>while (TryTake(out e, interval))</c>, and a timeout returns false exactly like
    /// completion does, so the first idle interval ended the writer thread for the session.
    ///
    /// <para>Writes to a temp directory through the internal constructor, never to the live
    /// %APPDATA% log directory, with a 50 ms idle interval so the gap below is ten intervals long.</para>
    ///
    /// <para>Falsified by restoring the old loop condition: red, the second line missing from the file,
    /// as predicted. Cannot pass by luck in that direction: the old writer would have had to sleep
    /// through ten consecutive 50 ms timeouts.</para>
    /// </summary>
    public class DebugLogFileSinkTests
    {
        [Fact]
        public void The_file_sink_keeps_writing_after_an_idle_interval()
        {
            string dir = Path.Combine(Path.GetTempPath(), "mt-debuglog-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                string path;
                using (var log = new DebugLogService(dir, writerFlushIntervalMs: 50))
                {
                    path = log.LogFilePath;
                    Assert.NotNull(path);

                    log.Warning("Test", "before-the-lull");
                    // Wait until the writer has actually written it, so the lull starts from an
                    // idle writer rather than from one still holding the first entry.
                    var sw = Stopwatch.StartNew();
                    while (!DebugLogService.ReadLogFile(path, 0).Any(l => l.Contains("before-the-lull")))
                    {
                        Assert.True(sw.ElapsedMilliseconds < 5000, "the writer never wrote the first entry");
                        Thread.Sleep(10);
                    }

                    Thread.Sleep(500);   // ten idle intervals

                    log.Warning("Test", "after-the-lull");
                }   // Dispose drains the queue and joins the writer

                var lines = DebugLogService.ReadLogFile(path, 0);
                Assert.Contains(lines, l => l.Contains("before-the-lull"));
                Assert.Contains(lines, l => l.Contains("after-the-lull"));
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }
    }
}
