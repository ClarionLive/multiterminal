using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Pins the authored-vs-generated provenance contract ACROSS the C#/JS boundary (task a455e295).
    /// <para>The gloss marker is a string agreed between a C# property and a JavaScript comparison in
    /// a separate file, and no compiler checks that agreement. This is precisely the shape of the
    /// Run-1 CRITICAL on the parent ticket 60665c6c: a PascalCase renderer field against a camelCase
    /// view read, with a clean build and a green suite the whole time. A rename on either side turns
    /// every machine-written explanation back into something indistinguishable from a person's —
    /// silently, and in the one direction that misleads a reviewer.</para>
    /// <para>Assertions run over comment-stripped source, so the explanatory prose in the view
    /// cannot satisfy them and they fail on deleted code.</para>
    /// </summary>
    public class GlossProvenanceContractTests
    {
        private static string RepoRoot([CallerFilePath] string thisFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", ".."));

        private static string StripComments(string src)
        {
            string noBlocks = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            var kept = noBlocks
                .Split('\n')
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));
            return string.Join("\n", kept);
        }

        private static string ReadStripped(params string[] relativeParts)
        {
            string path = Path.Combine(new[] { RepoRoot() }.Concat(relativeParts).ToArray());
            Assert.True(File.Exists(path), $"Could not locate '{Path.Combine(relativeParts)}' at '{path}'.");
            return StripComments(File.ReadAllText(path));
        }

        private static string GraphView() => ReadStripped("Controls", "HudGraphPanel", "hud-graph.html");

        // ---- The C# end of the wire ----

        [Fact]
        public void A_generated_gloss_serializes_as_lowercase_source_generated()
        {
            // ViewJsonOptions is what HudGraphRenderer actually serializes with, so this is the real
            // wire shape rather than a guess about casing.
            var gloss = new ChecklistItemGloss
            {
                What = "It does a thing.",
                Source = ChecklistItemGloss.SourceGenerated,
            };

            string json = JsonSerializer.Serialize(gloss, ChecklistGraphBuilder.ViewJsonOptions);

            Assert.Contains("\"source\":\"generated\"", json);
        }

        [Fact]
        public void An_authored_gloss_does_not_serialize_as_generated()
        {
            var gloss = new ChecklistItemGloss
            {
                What = "A person wrote this.",
                Source = ChecklistItemGloss.SourceAuthored,
            };

            string json = JsonSerializer.Serialize(gloss, ChecklistGraphBuilder.ViewJsonOptions);

            Assert.Contains("\"source\":\"authored\"", json);
            Assert.DoesNotContain("\"source\":\"generated\"", json);
        }

        [Fact]
        public void The_graph_builder_forwards_provenance_to_the_view()
        {
            // The builder drops a blank gloss entirely; it must NOT drop the marker on a real one.
            var task = new KanbanTask { Id = "t1", Title = "T" };
            task.SetChecklist(new System.Collections.Generic.List<ChecklistItem>
            {
                new ChecklistItem
                {
                    Item = "Explained by a machine",
                    Gloss = new ChecklistItemGloss
                    {
                        What = "It does a thing.",
                        Source = ChecklistItemGloss.SourceGenerated,
                    },
                },
            });

            var graph = ChecklistGraphBuilder.Build(task, null, null);
            string json = JsonSerializer.Serialize(graph, ChecklistGraphBuilder.ViewJsonOptions);

            Assert.Contains("\"source\":\"generated\"", json);
        }

        [Fact]
        public void Provenance_survives_a_round_trip_through_stored_checklist_json()
        {
            // The marker is only worth anything if it survives the DB column, and GetChecklist's
            // normalization pass is where a casing divergence would be silently introduced.
            var task = new KanbanTask { Id = "t1", Title = "T" };
            task.ChecklistJson =
                "[{\"item\":\"x\",\"status\":\"pending\"," +
                "\"gloss\":{\"what\":\"It does a thing.\",\"source\":\"GENERATED\"}}]";

            var restored = task.GetChecklist()[0];

            Assert.NotNull(restored.Gloss);
            Assert.True(restored.Gloss.IsGenerated);

            // Canonicalized to lowercase — the view compares with === and would miss "GENERATED".
            Assert.Equal("generated", restored.Gloss.Source);
        }

        // ---- The JavaScript end of the wire ----

        [Fact]
        public void The_view_reads_the_same_field_and_value_the_server_writes()
        {
            string view = GraphView();

            // Both consumption points: the hover detail panel and the node meta line.
            int matches = Regex.Matches(view, @"\.source\s*===\s*'generated'").Count;

            Assert.True(
                matches >= 2,
                "hud-graph.html must compare gloss.source against 'generated' in BOTH the detail " +
                $"panel and the node meta line; found {matches} comparison(s). If a rename moved " +
                "this contract, update the C# side (ChecklistItemGloss.SourceGenerated) too.");
        }

        [Fact]
        public void The_view_labels_machine_written_gloss_visibly()
        {
            string view = GraphView();

            // The whole point of the feature: an unlabelled machine gloss reads as the planner's.
            Assert.Contains("Written by a machine", view);
            Assert.Contains("auto gloss", view);
        }

        [Fact]
        public void The_machine_notice_is_rendered_before_the_prose_it_qualifies()
        {
            // Order is load-bearing. A reader who meets the caveat AFTER the explanation has
            // already taken the explanation as the planner's own reasoning.
            string view = GraphView();

            int notice = view.IndexOf("Written by a machine", StringComparison.Ordinal);
            int whatBlock = view.IndexOf("'What it is'", StringComparison.Ordinal);

            Assert.True(notice >= 0, "machine-provenance notice missing from hud-graph.html");
            Assert.True(whatBlock >= 0, "'What it is' block missing from hud-graph.html");
            Assert.True(
                notice < whatBlock,
                "The machine-provenance notice must be added BEFORE the 'What it is' block, or the " +
                "reader absorbs the explanation as authored before learning it was not.");
        }

        [Fact]
        public void The_missing_gloss_state_is_still_distinct_from_a_machine_written_one()
        {
            // Three states must stay visually distinct: nobody wrote one, a machine wrote one, a
            // person wrote one. Collapsing the first two would hide the gap the backfill exists to
            // reveal when it cannot fill it honestly.
            string view = GraphView();

            Assert.Contains("No explanation written", view);
            Assert.Contains("no gloss", view);
        }
    }
}
