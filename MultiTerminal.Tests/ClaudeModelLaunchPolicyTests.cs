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
        public void An_invalid_project_value_falls_to_the_valid_global_value_with_a_warning_naming_it()
        {
            var d = ClaudeModelLaunchPolicy.Decide(ClaudeModelRole.ProjectPane, null, "x' ; calc", null, Settings(project: "haiku"));

            Assert.Equal("haiku", d.Model);
            Assert.Equal("global", d.Source);
            Assert.False(d.RequestedNameRejected);
            Assert.Contains("x' ; calc", d.Problem);
            Assert.Contains("project", d.Problem);
        }

        [Fact]
        public void An_invalid_value_at_every_level_ends_at_the_built_in_model_never_no_flag()
        {
            var project = ClaudeModelLaunchPolicy.Decide(ClaudeModelRole.ProjectPane, null, "a b", null, Settings(project: "$(x)"));
            Assert.Equal("opus", project.Model);
            Assert.Equal("built-in", project.Source);
            Assert.Contains("a b", project.Problem);
            Assert.Contains("$(x)", project.Problem);
            Assert.Contains("Settings", project.Problem);

            var helper = ClaudeModelLaunchPolicy.Decide(ClaudeModelRole.Helper, null, null, "a b", Settings(helper: "$(x)"));
            Assert.Equal("sonnet", helper.Model);
            Assert.Equal("built-in", helper.Source);
        }

        [Fact]
        public void An_invalid_global_value_alone_ends_at_the_built_in_model()
        {
            var d = ClaudeModelLaunchPolicy.Decide(ClaudeModelRole.Helper, null, null, null, Settings(helper: "$(x)"));
            Assert.Equal("sonnet", d.Model);
            Assert.NotNull(d.Problem);
        }

        [Fact]
        public void The_default_token_is_still_no_flag_and_is_not_replaced_by_a_later_level()
        {
            var d = ClaudeModelLaunchPolicy.Decide(ClaudeModelRole.ProjectPane, null, "default", null, Settings(project: "haiku"));
            Assert.Null(d.Model);
            Assert.Null(d.Problem);
        }

        [Fact]
        public void An_invalid_subagent_name_uses_the_built_in_subagent_model_with_a_warning()
        {
            var d = ClaudeModelLaunchPolicy.Decide(ClaudeModelRole.ProjectPane, null, null, null, Settings(sub: "a b"));
            Assert.Equal("sonnet", d.SubagentModel);
            Assert.Contains("a b", d.Problem);
            Assert.Contains("Subagent", d.Problem);
        }

        [Fact]
        public void A_resume_with_an_invalid_stored_project_row_or_setting_still_passes_a_model_flag()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mt_resmodel_" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                const string id = "0f8fad5b-d9cb-469f-a165-70867728950e";

                // A project row holding a bad name, nothing in Settings: the built-in project model.
                var fromRow = ClaudeModelLaunchPolicy.Decide(ClaudeModelRole.ProjectPane, null, "opus; calc", null, null);
                string rowCmd = LaunchCommandBuilder.BuildClaudeCommand(null, dir, id, fromRow.Model).AutoRunCommand;
                Assert.Contains(" --model opus --resume " + id, rowCmd);

                // A bad Settings value, no project row: a helper resumed with its PM gets the built-in helper model.
                var fromSettings = ClaudeModelLaunchPolicy.Decide(ClaudeModelRole.Helper, null, null, null, Settings(helper: "$(calc)"));
                string settingsCmd = LaunchCommandBuilder.BuildClaudeCommand(null, dir, id, fromSettings.Model).AutoRunCommand;
                Assert.Contains(" --model sonnet --resume " + id, settingsCmd);
            }
            finally
            {
                try { System.IO.Directory.Delete(dir, true); } catch (System.IO.IOException) { }
            }
        }
    }
}
