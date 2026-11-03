using MultiTerminal.Services;

namespace MultiTerminal.Tests
{
    /// <summary>The Settings side of ticket b0d2f090: an unset machine reads as the built-in defaults, and values round-trip.</summary>
    public sealed class ClaudeModelSettingsStoreTests : System.IDisposable
    {
        private readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mt_modelset_" + System.Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { System.IO.Directory.Delete(_dir, true); } catch (System.IO.IOException) { }
            System.GC.SuppressFinalize(this);
        }

        [Xunit.Fact]
        public void A_machine_with_nothing_stored_decides_opus_sonnet_sonnet()
        {
            var values = new SettingsService(_dir).GetClaudeModelSettings();

            Xunit.Assert.Null(values.GlobalProjectModel);
            Xunit.Assert.Equal("opus", ClaudeModelLaunchPolicy.Decide(ClaudeModelRole.ProjectPane, null, null, null, values).Model);
            Xunit.Assert.Equal("sonnet", ClaudeModelLaunchPolicy.Decide(ClaudeModelRole.Helper, null, null, null, values).Model);
            Xunit.Assert.Equal("sonnet", ClaudeModelLaunchPolicy.Decide(ClaudeModelRole.Helper, null, null, null, values).SubagentModel);
        }

        [Xunit.Fact]
        public void Values_round_trip_and_blank_returns_to_the_built_in_default()
        {
            new SettingsService(_dir).SetClaudeModelSettings(" fable ", "default", "haiku");
            var values = new SettingsService(_dir).GetClaudeModelSettings();
            Xunit.Assert.Equal("fable", values.GlobalProjectModel);
            Xunit.Assert.Equal("default", values.GlobalHelperModel);
            Xunit.Assert.Equal("haiku", values.GlobalSubagentModel);

            new SettingsService(_dir).SetClaudeModelSettings("", null, "  ");
            values = new SettingsService(_dir).GetClaudeModelSettings();
            Xunit.Assert.Null(values.GlobalProjectModel);
            Xunit.Assert.Null(values.GlobalHelperModel);
            Xunit.Assert.Null(values.GlobalSubagentModel);
        }
    }
}
