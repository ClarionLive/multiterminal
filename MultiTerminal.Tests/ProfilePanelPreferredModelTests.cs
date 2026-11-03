using System.Text.Json;
using MultiTerminal.ProfilePanel;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Ticket b0d2f090: the profile editor no longer sends <c>preferredModel</c>, so saving it must not
    /// overwrite the stored value with the <c>TeamMemberProfile</c> default of "sonnet".
    /// <c>ProfileService.UpdateProfile</c> leaves a null model alone, so the parsed profile must carry null
    /// when the payload lacks it. Not covered: the panel's forwarding in ProfilePanelDocument (it passes
    /// <c>profile.PreferredModel</c> straight through; read, not executed).
    /// </summary>
    public class ProfilePanelPreferredModelTests
    {
        private static MultiTerminal.MCPServer.Models.TeamMemberProfile Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return ProfilePanelRenderer.ParseProfile(doc.RootElement.Clone());
        }

        [Fact]
        public void A_payload_without_a_preferred_model_parses_to_null_so_an_update_leaves_it_alone()
        {
            var profile = Parse(@"{""id"":""diana"",""displayName"":""Diana"",""role"":""dev"",""isTeamLead"":false}");

            Assert.Null(profile.PreferredModel);
        }

        [Fact]
        public void A_payload_that_does_send_one_still_carries_it()
        {
            var profile = Parse(@"{""id"":""diana"",""preferredModel"":""opus""}");

            Assert.Equal("opus", profile.PreferredModel);
        }
    }
}
