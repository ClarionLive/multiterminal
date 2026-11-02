using System;
using System.IO;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    public class ForcedStatuslineTempSweepTests
    {
        private static readonly DateTime NowUtc = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);

        [Fact]
        public void Old_orphan_is_deleted()
        {
            WithTempDirectory(directory =>
            {
                string orphan = CreateFile(directory, "forced-statusline-project.settings.json.abc.tmp", NowUtc.AddHours(-2));
                int deleted = LaunchCommandBuilder.SweepStaleForcedStatuslineTempFiles(directory, MaxAge, NowUtc);

                Assert.Equal(1, deleted);
                Assert.False(File.Exists(orphan));
            });
        }

        [Fact]
        public void Recent_orphan_is_kept()
        {
            WithTempDirectory(directory =>
            {
                string orphan = CreateFile(directory, "forced-statusline-project.settings.json.abc.tmp", NowUtc.AddMinutes(-30));
                int deleted = LaunchCommandBuilder.SweepStaleForcedStatuslineTempFiles(directory, MaxAge, NowUtc);

                Assert.Equal(0, deleted);
                Assert.True(File.Exists(orphan));
            });
        }

        [Fact]
        public void Old_settings_file_is_kept()
        {
            WithTempDirectory(directory =>
            {
                string settings = CreateFile(directory, "forced-statusline-project.settings.json", NowUtc.AddHours(-2));
                int deleted = LaunchCommandBuilder.SweepStaleForcedStatuslineTempFiles(directory, MaxAge, NowUtc);

                Assert.Equal(0, deleted);
                Assert.True(File.Exists(settings));
            });
        }

        [Fact]
        public void Old_unrelated_temp_file_is_kept()
        {
            WithTempDirectory(directory =>
            {
                string unrelated = CreateFile(directory, "other.settings.json.abc.tmp", NowUtc.AddHours(-2));
                int deleted = LaunchCommandBuilder.SweepStaleForcedStatuslineTempFiles(directory, MaxAge, NowUtc);

                Assert.Equal(0, deleted);
                Assert.True(File.Exists(unrelated));
            });
        }

        [Fact]
        public void Missing_folder_returns_zero()
        {
            string missing = Path.Combine(Path.GetTempPath(), "MultiTerminal-sweep-missing-" + Guid.NewGuid().ToString("N"));

            Assert.Equal(0, LaunchCommandBuilder.SweepStaleForcedStatuslineTempFiles(missing, MaxAge, NowUtc));
        }

        [Fact]
        public void Locked_orphan_is_skipped_while_another_orphan_is_deleted()
        {
            WithTempDirectory(directory =>
            {
                string locked = CreateFile(directory, "forced-statusline-locked.settings.json.abc.tmp", NowUtc.AddHours(-2));
                string deletable = CreateFile(directory, "forced-statusline-deletable.settings.json.def.tmp", NowUtc.AddHours(-2));

                using (File.Open(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    int deleted = LaunchCommandBuilder.SweepStaleForcedStatuslineTempFiles(directory, MaxAge, NowUtc);

                    Assert.Equal(1, deleted);
                    Assert.True(File.Exists(locked));
                    Assert.False(File.Exists(deletable));
                }
            });
        }

        private static string CreateFile(string directory, string name, DateTime lastWriteUtc)
        {
            string path = Path.Combine(directory, name);
            File.WriteAllText(path, "test");
            File.SetLastWriteTimeUtc(path, lastWriteUtc);
            return path;
        }

        private static void WithTempDirectory(Action<string> test)
        {
            string directory = Path.Combine(Path.GetTempPath(), "MultiTerminal-sweep-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                test(directory);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
