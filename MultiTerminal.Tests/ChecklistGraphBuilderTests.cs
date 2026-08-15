using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Unit coverage for the checklist dependency-graph builder (task 60665c6c).
    /// <para>The builder is pure, so this is where the feature's correctness actually lives —
    /// the HUD tab that renders the result can only be exercised by the Owner after a deploy,
    /// and an agent running inside MultiTerminal cannot deploy.</para>
    /// <para>The first test is the important one. Everything else guards against crashes on
    /// malformed input; that one guards against the failure mode that makes the whole feature
    /// worse than useless — drawing stored list order as if it were declared dependency.</para>
    /// </summary>
    public class ChecklistGraphBuilderTests
    {
        // ---- The anti-invention guarantee ----

        [Fact]
        public void No_declared_dependencies_produces_no_edges()
        {
            // Six items in a stored order, none declaring a dependency. A naive renderer would
            // chain them 0->1->2->3->4->5 because that is how they sit in the array. That chain
            // is a constraint nobody declared, and drawing it asserts something false with more
            // confidence than the plan prose ever did.
            var task = TaskWith(
                Item("Inventory the region"),
                Item("Extract TaskService"),
                Item("Verification assertion"),
                Item("Unit tests"),
                Item("Template doc"),
                Item("Second region"));

            var graph = ChecklistGraphBuilder.Build(task);

            Assert.Equal(6, graph.Nodes.Count);
            Assert.Empty(graph.Edges);
            Assert.Empty(graph.Warnings);
            Assert.All(graph.Nodes, n => Assert.Equal(0, n.Tier));
        }

        [Fact]
        public void Empty_checklist_produces_an_empty_graph_not_a_crash()
        {
            var graph = ChecklistGraphBuilder.Build(TaskWith());

            Assert.Empty(graph.Nodes);
            Assert.Empty(graph.Edges);
            Assert.Empty(graph.Warnings);
        }

        // ---- The payoff: parallelism becomes visible ----

        [Fact]
        public void Independent_siblings_land_on_the_same_tier()
        {
            // The real shape of ticket e7e89f4b: inventory gates the extraction, and the
            // extraction gates three items that are independent OF EACH OTHER. The flat
            // checklist could not say so, and they were run as a queue because of it.
            var task = TaskWith(
                Item("Inventory"),
                Item("Extract", 0),
                Item("Verify", 1),
                Item("Tests", 1),
                Item("Template", 1),
                Item("Follow-ups", 2, 3, 4));

            var graph = ChecklistGraphBuilder.Build(task);

            Assert.Equal(0, TierOf(graph, 0));
            Assert.Equal(1, TierOf(graph, 1));
            Assert.Equal(2, TierOf(graph, 2));
            Assert.Equal(2, TierOf(graph, 3));
            Assert.Equal(2, TierOf(graph, 4));
            Assert.Equal(3, TierOf(graph, 5));

            // Seven declared dependencies in (1<-0, 2<-1, 3<-1, 4<-1, 5<-2, 5<-3, 5<-4),
            // seven edges out — nothing invented, nothing lost.
            Assert.Equal(7, graph.Edges.Count);
            Assert.All(graph.Edges, e => Assert.Equal("depends", e.Kind));
            Assert.Contains(graph.Edges, e => e.From == "item:1" && e.To == "item:3");
            Assert.Empty(graph.Warnings);
        }

        [Fact]
        public void Tier_follows_the_longest_path_not_the_shortest()
        {
            // Item 2 depends on both 0 (a root) and 1 (which is itself downstream of 0).
            // It must sit below BOTH, or an arrow would visibly point upward.
            var task = TaskWith(
                Item("A"),
                Item("B", 0),
                Item("C", 0, 1));

            var graph = ChecklistGraphBuilder.Build(task);

            Assert.Equal(0, TierOf(graph, 0));
            Assert.Equal(1, TierOf(graph, 1));
            Assert.Equal(2, TierOf(graph, 2));
        }

        // ---- Malformed, agent-authored input must degrade, not throw ----

        [Fact]
        public void Cycle_is_broken_and_the_graph_still_renders()
        {
            var task = TaskWith(
                Item("A", 2),
                Item("B", 0),
                Item("C", 1));

            var graph = ChecklistGraphBuilder.Build(task);

            // One edge sacrificed to break the loop; the other two survive so the view still
            // shows the user what they declared, plus a warning naming the problem.
            Assert.Equal(3, graph.Nodes.Count);
            Assert.Equal(2, graph.Edges.Count);
            Assert.Contains(graph.Warnings, w => w.Contains("cycle", System.StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Cycle_breaking_is_deterministic()
        {
            var first = ChecklistGraphBuilder.Build(TaskWith(Item("A", 2), Item("B", 0), Item("C", 1)));
            var second = ChecklistGraphBuilder.Build(TaskWith(Item("A", 2), Item("B", 0), Item("C", 1)));

            // Same input must lose the same edge, or the picture would shuffle between refreshes.
            Assert.Equal(
                first.Edges.Select(e => e.From + "->" + e.To).OrderBy(s => s),
                second.Edges.Select(e => e.From + "->" + e.To).OrderBy(s => s));
        }

        [Fact]
        public void Out_of_range_dependency_is_dropped_with_a_warning()
        {
            var task = TaskWith(Item("A"), Item("B", 99));

            var graph = ChecklistGraphBuilder.Build(task);

            Assert.Empty(graph.Edges);
            Assert.Contains(graph.Warnings, w => w.Contains("99"));
        }

        [Fact]
        public void Negative_dependency_index_is_dropped_with_a_warning()
        {
            var task = TaskWith(Item("A"), Item("B", -1));

            var graph = ChecklistGraphBuilder.Build(task);

            Assert.Empty(graph.Edges);
            Assert.Single(graph.Warnings);
        }

        [Fact]
        public void Self_reference_is_dropped_with_a_warning()
        {
            var task = TaskWith(Item("A"), Item("B", 1));

            var graph = ChecklistGraphBuilder.Build(task);

            Assert.Empty(graph.Edges);
            Assert.Contains(graph.Warnings, w => w.Contains("itself", System.StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Duplicate_dependency_yields_one_edge_and_a_warning()
        {
            var task = TaskWith(Item("A"), Item("B", 0, 0));

            var graph = ChecklistGraphBuilder.Build(task);

            Assert.Single(graph.Edges);
            Assert.Single(graph.Warnings);
        }

        // ---- Cross-task relationships ----

        [Fact]
        public void Upstream_dependency_edges_into_the_root_items_and_shifts_tiers_down()
        {
            var task = TaskWith(Item("A"), Item("B", 0));
            var rels = new[] { Rel("T", "UP", "depends_on") };

            var graph = ChecklistGraphBuilder.Build(task, rels);

            var external = graph.Nodes.Single(n => n.Kind == "task");
            Assert.Equal("task:UP", external.Id);
            Assert.Equal(0, external.Tier);

            // Items make room for the row above them.
            Assert.Equal(1, TierOf(graph, 0));
            Assert.Equal(2, TierOf(graph, 1));

            // The upstream task gates the root only — not every item.
            var crossEdges = graph.Edges.Where(e => e.Kind == "relationship").ToList();
            Assert.Single(crossEdges);
            Assert.Equal("task:UP", crossEdges[0].From);
            Assert.Equal("item:0", crossEdges[0].To);
        }

        [Fact]
        public void Inverse_relationship_row_is_read_from_the_target_side()
        {
            // The API mirrors every relationship, so this task may appear as either side.
            var task = TaskWith(Item("A"));
            var rels = new[] { Rel("OTHER", "T", "blocks") };

            var graph = ChecklistGraphBuilder.Build(task, rels);

            // OTHER blocks T, so OTHER is upstream of this task's work.
            var external = graph.Nodes.Single(n => n.Kind == "task");
            Assert.Equal("task:OTHER", external.Id);
            Assert.Equal(0, external.Tier);
            Assert.Contains(graph.Edges, e => e.From == "task:OTHER" && e.To == "item:0");
        }

        [Fact]
        public void Related_to_is_drawn_as_context_with_no_edges()
        {
            // related_to carries no ordering, so inventing an arrow for it would be the same
            // dishonesty as drawing list order.
            var task = TaskWith(Item("A"));
            var rels = new[] { Rel("T", "SIDE", "related_to") };

            var graph = ChecklistGraphBuilder.Build(task, rels);

            Assert.Contains(graph.Nodes, n => n.Id == "task:SIDE");
            Assert.Empty(graph.Edges);
        }

        // ---- Gloss and derived file list ----

        [Fact]
        public void Gloss_survives_the_json_round_trip()
        {
            var item = Item("A");
            item.Gloss = new ChecklistItemGloss
            {
                What = "Moves the task code into its own file.",
                Why = "Gated on the inventory being finished.",
                Without = "You find the coupling from compiler errors, mid-move.",
            };

            var graph = ChecklistGraphBuilder.Build(TaskWith(item));

            var gloss = graph.Nodes[0].Gloss;
            Assert.NotNull(gloss);
            Assert.Equal("Moves the task code into its own file.", gloss.What);
            Assert.Contains("compiler errors", gloss.Without);
        }

        [Fact]
        public void Blank_gloss_is_reported_as_absent()
        {
            // "Nobody wrote an explanation" and "someone wrote three empty strings" should look
            // the same to the view — neither is worth rendering three empty rows for.
            var item = Item("A");
            item.Gloss = new ChecklistItemGloss { What = "  ", Why = null, Without = string.Empty };

            var graph = ChecklistGraphBuilder.Build(TaskWith(item));

            Assert.Null(graph.Nodes[0].Gloss);
        }

        [Fact]
        public void Item_scoped_file_links_populate_that_node_only()
        {
            var task = TaskWith(Item("A"), Item("B"));
            var links = new[]
            {
                new TaskFileLink { FilePath = @"C:\repo\Services\TaskService.cs", ChecklistItemIndex = 0 },
                new TaskFileLink { FilePath = @"C:\repo\Tests\TaskServiceTests.cs", ChecklistItemIndex = 1 },
                new TaskFileLink { FilePath = @"C:\repo\README.md", ChecklistItemIndex = null },
                new TaskFileLink { FilePath = @"C:\repo\Nope.cs", ChecklistItemIndex = 42 },
            };

            var graph = ChecklistGraphBuilder.Build(task, null, links);

            Assert.Equal(new[] { "TaskService.cs" }, graph.Nodes[0].Files);
            Assert.Equal(new[] { "TaskServiceTests.cs" }, graph.Nodes[1].Files);

            // Task-scoped and out-of-range links belong to no single step.
            Assert.DoesNotContain(graph.Nodes, n => n.Files.Contains("README.md"));
            Assert.DoesNotContain(graph.Nodes, n => n.Files.Contains("Nope.cs"));
        }

        [Fact]
        public void Item_status_and_cycle_count_reach_the_node()
        {
            var item = Item("A");
            item.Status = "testing";
            item.CycleCount = 3;
            item.AssignedTo = "Diana";

            var graph = ChecklistGraphBuilder.Build(TaskWith(item));

            Assert.Equal("testing", graph.Nodes[0].Status);
            Assert.Equal(3, graph.Nodes[0].CycleCount);
            Assert.Equal("Diana", graph.Nodes[0].AssignedTo);
        }

        // ---- the wire contract with the view (Run-1 CRITICAL regression) ----

        [Fact]
        public void Graph_serializes_with_the_camelCase_keys_the_view_reads()
        {
            // Run 1 shipped a renderer that serialized these DTOs with default options, emitting
            // PascalCase. hud-graph.html reads camelCase, so every field arrived undefined: all
            // nodes collapsed onto one map key, every label read "(untitled)", and not one edge
            // drew. Build was clean and 442 tests passed — nothing exercised this seam, and the
            // REST transport hid it by camelCasing independently via ASP.NET.
            // This asserts against the SAME options object the renderer uses, so it fails if
            // anyone drops them or renames a DTO member the view depends on.
            var task = TaskWith(Item("Inventory"), Item("Extract", 0));
            var graph = ChecklistGraphBuilder.Build(task, new[] { Rel("T", "UP", "depends_on") });

            var json = JsonSerializer.Serialize(
                new { nodes = graph.Nodes, edges = graph.Edges },
                ChecklistGraphBuilder.ViewJsonOptions);

            foreach (var key in new[] { "id", "kind", "itemIndex", "label", "status", "tier", "files", "from", "to" })
            {
                Assert.Contains("\"" + key + "\":", json, StringComparison.Ordinal);
            }

            foreach (var pascal in new[] { "Id", "Kind", "ItemIndex", "Label", "Status", "Tier", "From", "To" })
            {
                Assert.DoesNotContain("\"" + pascal + "\":", json, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void Gloss_serializes_with_the_camelCase_keys_the_panel_reads()
        {
            var item = Item("A");
            item.Gloss = new ChecklistItemGloss { What = "w", Why = "y", Without = "n" };
            var graph = ChecklistGraphBuilder.Build(TaskWith(item));

            var json = JsonSerializer.Serialize(graph.Nodes, ChecklistGraphBuilder.ViewJsonOptions);

            Assert.Contains("\"gloss\":", json, StringComparison.Ordinal);
            Assert.Contains("\"what\":", json, StringComparison.Ordinal);
            Assert.Contains("\"why\":", json, StringComparison.Ordinal);
            Assert.Contains("\"without\":", json, StringComparison.Ordinal);
        }

        // ---- contradictory cross-task links (Run-1 LOW regression) ----

        [Fact]
        public void Contradictory_relationship_pair_yields_one_node_and_a_warning()
        {
            // Node ids are "task:{id}", so emitting both an upstream and a downstream node for the
            // same task produced two nodes sharing one id — and the view's id->element map keeps
            // only the last, silently redirecting every edge aimed at the first.
            var task = TaskWith(Item("A"));
            var rels = new[] { Rel("T", "B", "depends_on"), Rel("T", "B", "blocks") };

            var graph = ChecklistGraphBuilder.Build(task, rels);

            Assert.Single(graph.Nodes, n => n.Id == "task:B");
            Assert.Contains(graph.Warnings, w => w.Contains("both ways", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Cycle_warning_names_the_waiting_item_first()
        {
            // The dropped back-edge is node -> child where child is a DFS ancestor, so the
            // PRE-EXISTING constraint ("already waiting") is node-waits-on-child. Run 1 printed
            // the inverse, which in a feature about not misleading the reader is a real defect.
            var graph = ChecklistGraphBuilder.Build(TaskWith(Item("A", 1), Item("B", 0)));

            var cycle = graph.Warnings.Single(w => w.Contains("cycle", StringComparison.OrdinalIgnoreCase));
            var droppedFrom = cycle.Substring(cycle.IndexOf("Edge ", StringComparison.Ordinal) + 5).Split(' ')[0];
            Assert.Contains($"item {droppedFrom} is already waiting on", cycle, StringComparison.Ordinal);
        }

        // ---- helpers ----

        private static ChecklistItem Item(string text, params int[] dependsOn)
        {
            return new ChecklistItem
            {
                Item = text,
                Status = "pending",
                DependsOn = dependsOn == null ? new List<int>() : dependsOn.ToList(),
            };
        }

        /// <summary>
        /// Builds a task and pushes the items through the real SetChecklist/GetChecklist
        /// serialization, so every test also exercises the JSON round-trip of the new fields.
        /// </summary>
        private static KanbanTask TaskWith(params ChecklistItem[] items)
        {
            var task = new KanbanTask { Id = "T", Title = "Test task" };
            task.SetChecklist(items.ToList());
            return task;
        }

        private static TaskRelationship Rel(string source, string target, string type)
        {
            return new TaskRelationship { SourceTaskId = source, TargetTaskId = target, Type = type };
        }

        private static int TierOf(ChecklistGraph graph, int itemIndex)
        {
            return graph.Nodes.Single(n => n.ItemIndex == itemIndex).Tier;
        }
    }
}
