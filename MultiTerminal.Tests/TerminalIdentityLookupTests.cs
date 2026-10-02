using System.Collections.Generic;
using MultiTerminal.Docking;
using MultiTerminal.Terminal;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 158d60ac: since GH #26 a project with no team lead shows its tab as just "Proj". Chat
    /// injection, chat replies, the registration fallback and the agent-panel spawner scan used to accept
    /// a TabText match, so a message for an agent NAMED "Proj" could be typed into that placeholder pane.
    /// They now resolve through <see cref="TerminalDocument.FindByIdentity{T}"/>.
    ///
    /// <para>The structural part: <c>FindByIdentity</c> takes no display-title input at all, so it cannot
    /// match on one. The guarantee is that signature, not a red test: the first fact documents the real
    /// collision and stays green however the body is written, because no body can see the display
    /// title. Falsified, with the result predicted beforehand: collapsing the two passes into one OR made
    /// exactly <c>The_confirmed_agent_name_beats_a_tab_renamed_to_it</c> go red, and nothing else.</para>
    ///
    /// <para>Not covered: whether each MainForm site calls this rather than reading TabText. That wiring
    /// is checked by review, not by a text census.</para>
    /// </summary>
    public class TerminalIdentityLookupTests
    {
        private sealed class Doc
        {
            public Doc(string original, string custom, string project)
            {
                Original = original;
                Custom = custom;
                Display = TerminalDocument.ComposeTabTitle(custom, project, TerminalRole.None);
            }

            public string Original { get; }
            public string Custom { get; }
            public string Display { get; }
        }

        private static Doc Find(IEnumerable<Doc> docs, string name) =>
            TerminalDocument.FindByIdentity(docs, name, d => d.Original, d => d.Custom);

        [Fact]
        public void An_agent_named_like_a_project_is_not_confused_with_that_projects_placeholder_tab()
        {
            var placeholder = new Doc(original: null, custom: "Unassigned", project: "Proj");
            var agent = new Doc(original: "Proj", custom: "Proj", project: "Other");

            // The collision is real: the placeholder's displayed title IS the agent's name.
            Assert.Equal("Proj", placeholder.Display);

            // Placeholder first, so a display-text match would win on enumeration order.
            Assert.Same(agent, Find(new[] { placeholder, agent }, "Proj"));
        }

        [Fact]
        public void A_name_held_only_as_a_displayed_title_matches_nothing()
        {
            var placeholder = new Doc(original: null, custom: "Unassigned", project: "Proj");
            Assert.Null(Find(new[] { placeholder }, "Proj"));
        }

        [Fact]
        public void The_confirmed_agent_name_beats_a_tab_renamed_to_it()
        {
            // A cosmetic rename writes CustomTitle; the broker-confirmed name stays on the real terminal.
            var renamed = new Doc(original: "Carol", custom: "Bob", project: null);
            var realBob = new Doc(original: "Bob", custom: "Bob's scratch", project: null);

            Assert.Same(realBob, Find(new[] { renamed, realBob }, "bob"));
        }

        [Fact]
        public void A_not_yet_promoted_terminal_is_found_by_its_custom_title()
        {
            // Restored tabs carry their identity in CustomTitle until the agent re-registers.
            var restored = new Doc(original: null, custom: "Alice", project: "MultiTerminal");
            Assert.Same(restored, Find(new[] { restored }, "Alice"));
        }
    }
}
