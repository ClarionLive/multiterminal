using System.Linq;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>The model dropdowns' pure side (ticket b0d2f090): what is selected for a stored value, and what a pick stores.</summary>
    public class ClaudeModelChoicesTests
    {
        [Fact]
        public void Nothing_stored_selects_the_built_in_value()
        {
            var pick = ClaudeModelChoices.BuildSettingsPick(null, "sonnet");
            Assert.Equal("sonnet", pick.Items[pick.SelectedIndex]);
            Assert.Equal("", pick.CustomText);
        }

        [Fact]
        public void A_stored_alias_is_found_ignoring_case()
        {
            var pick = ClaudeModelChoices.BuildSettingsPick(" Fable ", "opus");
            Assert.Equal("fable", pick.Items[pick.SelectedIndex]);
        }

        [Fact]
        public void Default_selects_the_default_entry()
        {
            var pick = ClaudeModelChoices.BuildSettingsPick("default", "opus");
            Assert.Equal(ClaudeModelChoices.DefaultLabel, pick.Items[pick.SelectedIndex]);
        }

        [Fact]
        public void A_stored_name_the_list_does_not_offer_stays_selected_as_other()
        {
            var pick = ClaudeModelChoices.BuildSettingsPick("claude-opus-4-8", "opus");
            Assert.Equal(ClaudeModelChoices.OtherLabel, pick.Items[pick.SelectedIndex]);
            Assert.Equal("claude-opus-4-8", pick.CustomText);

            // Saving the dialog untouched stores the same name again.
            Assert.True(ClaudeModelChoices.TryResolveSettingsPick(pick.SelectedIndex, pick.CustomText, out var value, out _));
            Assert.Equal("claude-opus-4-8", value);
        }

        [Fact]
        public void Every_alias_default_and_a_typed_name_round_trips()
        {
            foreach (var alias in ClaudeModelChoices.Aliases)
            {
                var pick = ClaudeModelChoices.BuildSettingsPick(alias, "opus");
                Assert.True(ClaudeModelChoices.TryResolveSettingsPick(pick.SelectedIndex, pick.CustomText, out var v, out _));
                Assert.Equal(alias, v);
            }

            var d = ClaudeModelChoices.BuildSettingsPick("default", "opus");
            Assert.True(ClaudeModelChoices.TryResolveSettingsPick(d.SelectedIndex, d.CustomText, out var dv, out _));
            Assert.Equal("default", dv);
        }

        [Theory]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData("opus; calc")]
        [InlineData("a b")]
        [InlineData("$(calc)")]
        [InlineData("x'y")]
        public void A_blank_or_hostile_typed_name_is_refused_with_a_message(string typed)
        {
            var other = ClaudeModelChoices.BuildSettingsPick("zzz", "opus").SelectedIndex;
            Assert.True(ClaudeModelChoices.IsOtherIndex(other));

            Assert.False(ClaudeModelChoices.TryResolveSettingsPick(other, typed, out var value, out var error));
            Assert.Null(value);
            Assert.False(string.IsNullOrEmpty(error));
        }

        [Fact]
        public void The_project_form_offers_the_aliases_then_default_and_every_value_passes_the_gate()
        {
            var options = ClaudeModelChoices.ProjectFormOptions();
            Assert.Equal(ClaudeModelChoices.Aliases.Count + 1, options.Count);
            Assert.Equal("default", options.Last().Value);
            Assert.All(options, o => Assert.True(
                ClaudeModelLaunchPolicy.IsValidModelName(o.Value), $"'{o.Value}' would be refused by the launch policy."));
        }
    }
}
