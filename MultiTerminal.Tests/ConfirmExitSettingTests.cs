using System;
using System.IO;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// The "ask before exiting" preference (GitHub #19): on by default, off only for a stored "false",
    /// and surviving a restart (a fresh <see cref="SettingsService"/> over the same folder). Not covered:
    /// MainForm's exit TaskDialog and its "Don't ask again" checkbox, or the Settings dialog's checkbox.
    /// </summary>
    public sealed class ConfirmExitSettingTests : IDisposable
    {
        private readonly string _dir;

        public ConfirmExitSettingTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"mt_confirmexit_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
            }
            catch
            {
                // A leaked temp folder is not worth failing a test run over.
            }
        }

        [Fact]
        public void An_unconfigured_install_asks()
            => Assert.True(new SettingsService(_dir).GetConfirmExit());

        [Fact]
        public void Turning_it_off_survives_a_restart_and_turning_it_back_on_does_too()
        {
            new SettingsService(_dir).SetConfirmExit(false);
            Assert.False(new SettingsService(_dir).GetConfirmExit());

            new SettingsService(_dir).SetConfirmExit(true);
            Assert.True(new SettingsService(_dir).GetConfirmExit());
        }

        [Theory]
        [InlineData("false", false)]
        [InlineData(" FALSE ", false)]
        [InlineData("true", true)]
        [InlineData(null, true)]
        [InlineData("", true)]
        [InlineData("0", true)]
        [InlineData("no", true)]
        [InlineData("garbage", true)]
        public void Only_a_stored_false_turns_the_question_off(string stored, bool expected)
            => Assert.Equal(expected, SettingsService.ParseConfirmExit(stored));
    }
}
