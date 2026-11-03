using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Pins the one model decision (ticket b0d2f090): order of the rules, each kind of launch, and the
    /// allow-pattern that stands between an untrusted model name and a PowerShell command line.
    /// </summary>
    public class ClaudeModelLaunchPolicyTests
    {
        private static ClaudeModelSettingsValues Settings(string project = null, string helper = null, string sub = null) =>
            new ClaudeModelSettingsValues { GlobalProjectModel = project, GlobalHelperModel = helper, GlobalSubagentModel = sub };

        [Fact]
        public void A_machine_with_no_settings_starts_at_opus_sonnet_sonnet()
        {
            var project = ClaudeModelLaunchPolicy.Decide(ClaudeModelRole.ProjectPane, null, null, null, null);
            var helper = ClaudeModelLaunchPolicy.Decide(ClaudeModelRole.Helper, null, null, null, null);

            Assert.Equal("opus", project.Model);
            Assert.Equal("sonnet", helper.Model);
            Assert.Equal("sonnet", project.SubagentModel);
            Assert.Equal("sonnet", helper.SubagentModel);
        }

        [Fact]
        public void A_project_pane_takes_project_model_then_global_project_model()
        {
            var s = Settings(project: "haiku");
            Assert.Equal("fable", ClaudeModelLaunchPolicy.Decide(ClaudeModelRole.ProjectPane, null, "fable", "other", s).Model);
            Assert.Equal("haiku", ClaudeModelLaunchPolicy.Decide(ClaudeModelRole.ProjectPane, null, null, "other", s).Model);
            Assert.Equal("haiku", ClaudeModelLaunchPolicy.Decide(ClaudeModelRole.ProjectPane, null, "  ", "other", s).Model);
        }

        [Fact]
        public void A_helper_takes_requested_then_project_helper_model_then_global_helper_model()
        {
            var s = Settings(project: "haiku", helper: "opus");
            Assert.Equal("fable", ClaudeModelLaunchPolicy.Decide(ClaudeModelRole.Helper, "fable", "p", "ph", s).Model);
            Assert.Equal("ph", ClaudeModelLaunchPolicy.Decide(ClaudeModelRole.Helper, null, "p", "ph", s).Model);
            Assert.Equal("opus", ClaudeModelLaunchPolicy.Decide(ClaudeModelRole.Helper, null, "p", null, s).Model);
        }

        [Fact]
        public void A_helper_with_no_project_takes_the_global_helper_model()
        {
            Assert.Equal("haiku", ClaudeModelLaunchPolicy.Decide(ClaudeModelRole.Helper, null, null, null, Settings(helper: "haiku")).Model);
        }

        [Fact]
        public void A_requested_model_is_ignored_for_a_project_pane()
        {
            Assert.Equal("opus", ClaudeModelLaunchPolicy.Decide(ClaudeModelRole.ProjectPane, "haiku", null, null, null).Model);
        }

        [Fact]
        public void Default_means_no_flag_at_every_level()
        {
            Assert.Null(ClaudeModelLaunchPolicy.Decide(ClaudeModelRole.ProjectPane, null, "default", null, Settings(project: "haiku")).Model);
            Assert.Null(ClaudeModelLaunchPolicy.Decide(ClaudeModelRole.ProjectPane, null, null, null, Settings(project: "Default")).Model);
            Assert.Null(ClaudeModelLaunchPolicy.Decide(ClaudeModelRole.Helper, "default", "p", "ph", null).Model);
            Assert.Null(ClaudeModelLaunchPolicy.Decide(ClaudeModelRole.Helper, null, null, null, Settings(helper: "default")).Model);
            Assert.Null(ClaudeModelLaunchPolicy.Decide(ClaudeModelRole.Helper, null, null, null, Settings(sub: "default")).SubagentModel);
        }

        [Theory]
        [InlineData("opus")]
        [InlineData("claude-opus-4-8")]
        [InlineData("claude-sonnet-4.6")]
        [InlineData("opus[1m]")]
        [InlineData("fable")]
        public void Ordinary_names_are_valid(string name) => Assert.True(ClaudeModelLaunchPolicy.IsValidModelName(name));

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData(null)]
        [InlineData("opus sonnet")]
        [InlineData("opus; calc")]
        [InlineData("opus'")]
        [InlineData("\"opus\"")]
        [InlineData("$(calc)")]
        [InlineData("opus`n")]
        [InlineData("opus|x")]
        [InlineData("opus&x")]
        [InlineData("-opus")]
        [InlineData(".opus")]
        [InlineData("opus\n")]
        [InlineData("opus[2m]")]
        [InlineData("opus[1m]x")]
        [InlineData("opus)")]
        [InlineData("a>b")]
        public void Hostile_or_malformed_names_are_invalid(string name) => Assert.False(ClaudeModelLaunchPolicy.IsValidModelName(name));

        [Fact]
        public void An_invalid_requested_name_is_rejected_and_carries_no_model()
        {
            var d = ClaudeModelLaunchPolicy.Decide(ClaudeModelRole.Helper, "opus; calc", null, null, null);
            Assert.True(d.RequestedNameRejected);
            Assert.NotNull(d.Problem);
            Assert.Null(d.Model);
        }

        [Fact]
        public void An_invalid_stored_name_launches_with_no_flag_and_a_warning()
        {
            var d = ClaudeModelLaunchPolicy.Decide(ClaudeModelRole.ProjectPane, null, "x' ; calc", null, Settings(project: "haiku"));
            Assert.Null(d.Model);
            Assert.False(d.RequestedNameRejected);
            Assert.NotNull(d.Problem);

            var g = ClaudeModelLaunchPolicy.Decide(ClaudeModelRole.Helper, null, null, null, Settings(helper: "$(x)"));
            Assert.Null(g.Model);
            Assert.NotNull(g.Problem);
        }

        [Fact]
        public void An_invalid_subagent_name_sets_none()
        {
            var d = ClaudeModelLaunchPolicy.Decide(ClaudeModelRole.ProjectPane, null, null, null, Settings(sub: "a b"));
            Assert.Null(d.SubagentModel);
            Assert.NotNull(d.Problem);
        }
    }
}
