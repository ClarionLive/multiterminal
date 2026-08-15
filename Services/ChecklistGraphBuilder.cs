using System;
using System.Collections.Generic;
using System.Linq;
using MultiTerminal.MCPServer.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Turns a task's checklist plus its cross-task relationships into a renderable
    /// dependency graph (task 60665c6c).
    /// <para>Pure and static by design: no broker, no database, no UI. The graph is a DERIVED
    /// VIEW of the live checklist, which is what makes it impossible for it to drift out of
    /// sync the way a hand-drawn diagram does — and what makes it directly unit-testable.</para>
    /// <para><b>The load-bearing rule:</b> stored checklist array order is presentation order,
    /// NOT dependency. An item that declares no <see cref="ChecklistItem.DependsOn"/> gets NO
    /// incoming edge. Drawing array order as arrows would invent a constraint chain nobody
    /// declared and assert it more confidently than the plan prose does — a confidently wrong
    /// picture is worse than no picture. Every edge in the output traces to something a human
    /// or agent actually declared.</para>
    /// <para>Checklist JSON is agent-authored, so it is treated as untrusted: out-of-range
    /// indices, self-references, and cycles are all reachable states. Each is dropped and
    /// recorded in <see cref="ChecklistGraph.Warnings"/> — the builder never throws, because a
    /// malformed dependency must not be able to take down the view.</para>
    /// </summary>
    public static class ChecklistGraphBuilder
    {
        /// <summary>Node id prefix for checklist items.</summary>
        private const string ItemPrefix = "item:";

        /// <summary>Node id prefix for external (cross-task) nodes.</summary>
        private const string TaskPrefix = "task:";

        /// <summary>
        /// Builds the dependency graph for a task.
        /// </summary>
        /// <param name="task">The task whose checklist is being rendered. Required.</param>
        /// <param name="relationships">Cross-task relationship rows involving this task. Optional.</param>
        /// <param name="fileLinks">File links; those scoped to a checklist item index populate that node's file list. Optional.</param>
        /// <returns>A graph that is always renderable, even when the input declares nonsense.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="task"/> is null.</exception>
        public static ChecklistGraph Build(
            KanbanTask task,
            IEnumerable<TaskRelationship> relationships = null,
            IEnumerable<TaskFileLink> fileLinks = null)
        {
            if (task == null)
            {
                throw new ArgumentNullException(nameof(task));
            }

            var graph = new ChecklistGraph
            {
                TaskId = task.Id,
                TaskTitle = task.Title,
            };

            var items = task.GetChecklist() ?? new List<ChecklistItem>();
            int count = items.Count;

            var filesByItem = GroupFilesByItem(fileLinks, count);

            for (int i = 0; i < count; i++)
            {
                var item = items[i];
                graph.Nodes.Add(new ChecklistGraphNode
                {
                    Id = ItemPrefix + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Kind = "item",
                    ItemIndex = i,
                    Label = item.Item,
                    Status = item.Status ?? (item.Done ? "done" : "pending"),
                    AssignedTo = item.AssignedTo,
                    CycleCount = item.CycleCount,
                    Gloss = item.Gloss != null && item.Gloss.HasContent ? item.Gloss : null,
                    Files = filesByItem.TryGetValue(i, out var f) ? f : new List<string>(),
                });
            }

            var declared = CollectDeclaredEdges(items, graph.Warnings);
            var kept = DropCycles(declared, count, graph.Warnings);

            foreach (var (from, to) in kept)
            {
                graph.Edges.Add(new ChecklistGraphEdge
                {
                    From = ItemPrefix + from.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    To = ItemPrefix + to.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Kind = "depends",
                });
            }

            var tiers = AssignTiers(kept, count);
            AttachExternalNodes(graph, task, relationships, kept, count, tiers);

            return graph;
        }

        /// <summary>
        /// Buckets file links by the checklist item index they are scoped to. Task-scoped links
        /// (null index) and out-of-range indices are skipped — they belong to the whole task,
        /// not to any one step.
        /// </summary>
        private static Dictionary<int, List<string>> GroupFilesByItem(IEnumerable<TaskFileLink> fileLinks, int itemCount)
        {
            var map = new Dictionary<int, List<string>>();
            if (fileLinks == null)
            {
                return map;
            }

            foreach (var link in fileLinks)
            {
                if (link?.ChecklistItemIndex == null)
                {
                    continue;
                }

                int idx = link.ChecklistItemIndex.Value;
                if (idx < 0 || idx >= itemCount)
                {
                    continue;
                }

                if (!map.TryGetValue(idx, out var list))
                {
                    list = new List<string>();
                    map[idx] = list;
                }

                string name = link.FilePath;
                try
                {
                    name = System.IO.Path.GetFileName(link.FilePath);
                }
                catch (ArgumentException)
                {
                    // Malformed path characters — fall back to the raw string rather than dropping the row.
                }

                if (!string.IsNullOrEmpty(name) && !list.Contains(name))
                {
                    list.Add(name);
                }
            }

            return map;
        }

        /// <summary>
        /// Reads every item's declared dependencies and returns the (from, to) pairs that survive
        /// validation. An edge points FROM the prerequisite TO the item that declared it, so arrow
        /// direction reads as "must happen before".
        /// <para>Rejects, with a warning each: indices outside the checklist, self-references, and
        /// duplicates. An item with an empty list contributes nothing — deliberately, since that is
        /// the whole anti-invention guarantee.</para>
        /// </summary>
        private static List<(int From, int To)> CollectDeclaredEdges(List<ChecklistItem> items, List<string> warnings)
        {
            var edges = new List<(int From, int To)>();
            var seen = new HashSet<(int, int)>();

            for (int i = 0; i < items.Count; i++)
            {
                var deps = items[i].DependsOn;
                if (deps == null || deps.Count == 0)
                {
                    continue;
                }

                foreach (int dep in deps)
                {
                    if (dep < 0 || dep >= items.Count)
                    {
                        warnings.Add($"Item {i} depends on index {dep}, which is outside the checklist (0..{items.Count - 1}). Edge dropped.");
                        continue;
                    }

                    if (dep == i)
                    {
                        warnings.Add($"Item {i} depends on itself. Edge dropped.");
                        continue;
                    }

                    if (!seen.Add((dep, i)))
                    {
                        warnings.Add($"Item {i} lists index {dep} as a dependency more than once. Duplicate ignored.");
                        continue;
                    }

                    edges.Add((dep, i));
                }
            }

            return edges;
        }

        /// <summary>
        /// Removes back-edges so the remainder is a DAG, walking nodes and edges in index order so
        /// the same input always loses the same edge. A cycle is a planning mistake, not a crash:
        /// the graph still renders, minus the edge that closed the loop, with a warning naming it.
        /// </summary>
        private static List<(int From, int To)> DropCycles(List<(int From, int To)> edges, int nodeCount, List<string> warnings)
        {
            var adjacency = new List<int>[nodeCount];
            for (int i = 0; i < nodeCount; i++)
            {
                adjacency[i] = new List<int>();
            }

            foreach (var (from, to) in edges)
            {
                adjacency[from].Add(to);
            }

            for (int i = 0; i < nodeCount; i++)
            {
                adjacency[i].Sort();
            }

            // 0 = unvisited, 1 = on the current DFS stack, 2 = fully explored.
            var state = new int[nodeCount];
            var dropped = new HashSet<(int, int)>();

            for (int root = 0; root < nodeCount; root++)
            {
                if (state[root] != 0)
                {
                    continue;
                }

                // Iterative DFS — a deeply chained checklist must not blow the stack.
                var stack = new Stack<(int Node, int NextChild)>();
                state[root] = 1;
                stack.Push((root, 0));

                while (stack.Count > 0)
                {
                    var (node, nextChild) = stack.Pop();
                    if (nextChild >= adjacency[node].Count)
                    {
                        state[node] = 2;
                        continue;
                    }

                    stack.Push((node, nextChild + 1));
                    int child = adjacency[node][nextChild];

                    if (state[child] == 1)
                    {
                        // Child is on the current stack, so this edge closes a loop.
                        if (dropped.Add((node, child)))
                        {
                            warnings.Add($"Dependency cycle detected: item {child} is already waiting on item {node}. Edge {node} -> {child} dropped so the graph still renders.");
                        }

                        continue;
                    }

                    if (state[child] == 0)
                    {
                        state[child] = 1;
                        stack.Push((child, 0));
                    }
                }
            }

            return edges.Where(e => !dropped.Contains((e.From, e.To))).ToList();
        }

        /// <summary>
        /// Assigns each item a tier by longest path from a root, so siblings that are genuinely
        /// independent land on the SAME row. That shared row is the whole visual payoff — it is how
        /// a three-way parallel lane stops looking like a three-step queue.
        /// </summary>
        private static int[] AssignTiers(List<(int From, int To)> edges, int nodeCount)
        {
            var tiers = new int[nodeCount];
            var indegree = new int[nodeCount];
            var adjacency = new List<int>[nodeCount];
            for (int i = 0; i < nodeCount; i++)
            {
                adjacency[i] = new List<int>();
            }

            foreach (var (from, to) in edges)
            {
                adjacency[from].Add(to);
                indegree[to]++;
            }

            var queue = new Queue<int>();
            for (int i = 0; i < nodeCount; i++)
            {
                if (indegree[i] == 0)
                {
                    queue.Enqueue(i);
                }
            }

            while (queue.Count > 0)
            {
                int node = queue.Dequeue();
                foreach (int child in adjacency[node])
                {
                    if (tiers[child] < tiers[node] + 1)
                    {
                        tiers[child] = tiers[node] + 1;
                    }

                    if (--indegree[child] == 0)
                    {
                        queue.Enqueue(child);
                    }
                }
            }

            return tiers;
        }

        /// <summary>
        /// Adds cross-task context nodes and shifts item tiers to make room above them.
        /// <para>Edge policy, chosen so every drawn arrow is defensible: an UPSTREAM task gates the
        /// whole checklist, so it edges into the root items (those with nothing before them). A
        /// DOWNSTREAM task waits on the whole checklist, so it edges out of the leaf items.
        /// <c>related_to</c> carries no ordering at all, so it is drawn as a floating node with NO
        /// edges — the same discipline that refuses to draw list order.</para>
        /// </summary>
        private static void AttachExternalNodes(
            ChecklistGraph graph,
            KanbanTask task,
            IEnumerable<TaskRelationship> relationships,
            List<(int From, int To)> edges,
            int itemCount,
            int[] tiers)
        {
            int maxItemTier = 0;
            for (int i = 0; i < itemCount; i++)
            {
                if (tiers[i] > maxItemTier)
                {
                    maxItemTier = tiers[i];
                }
            }

            var externals = ClassifyRelationships(task.Id, relationships);
            bool hasUpstream = externals.Any(e => e.Direction == "upstream");
            int shift = hasUpstream ? 1 : 0;

            for (int i = 0; i < itemCount; i++)
            {
                graph.Nodes[i].Tier = tiers[i] + shift;
            }

            if (externals.Count == 0)
            {
                return;
            }

            var hasIncoming = new bool[itemCount];
            var hasOutgoing = new bool[itemCount];
            foreach (var (from, to) in edges)
            {
                hasOutgoing[from] = true;
                hasIncoming[to] = true;
            }

            var roots = Enumerable.Range(0, itemCount).Where(i => !hasIncoming[i]).ToList();
            var leaves = Enumerable.Range(0, itemCount).Where(i => !hasOutgoing[i]).ToList();
            int downstreamTier = maxItemTier + shift + 1;

            foreach (var ext in externals)
            {
                string nodeId = TaskPrefix + ext.TaskId;
                graph.Nodes.Add(new ChecklistGraphNode
                {
                    Id = nodeId,
                    Kind = "task",
                    ItemIndex = null,
                    Label = ext.TaskId,
                    Status = "external",
                    Relation = ext.Type,
                    Tier = ext.Direction == "upstream" ? 0 : downstreamTier,
                });

                if (ext.Direction == "upstream")
                {
                    foreach (int root in roots)
                    {
                        graph.Edges.Add(new ChecklistGraphEdge
                        {
                            From = nodeId,
                            To = ItemPrefix + root.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            Kind = "relationship",
                            Label = ext.Type,
                        });
                    }
                }
                else if (ext.Direction == "downstream")
                {
                    foreach (int leaf in leaves)
                    {
                        graph.Edges.Add(new ChecklistGraphEdge
                        {
                            From = ItemPrefix + leaf.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            To = nodeId,
                            Kind = "relationship",
                            Label = ext.Type,
                        });
                    }
                }

                // "floating" (related_to) deliberately gets no edges.
            }
        }

        /// <summary>
        /// Works out, for each relationship row, which side of this task the other task sits on.
        /// Rows arrive in both directions because the API mirrors every relationship, so both the
        /// source and target orientations are handled.
        /// </summary>
        private static List<(string TaskId, string Type, string Direction)> ClassifyRelationships(
            string taskId,
            IEnumerable<TaskRelationship> relationships)
        {
            var result = new List<(string TaskId, string Type, string Direction)>();
            if (relationships == null)
            {
                return result;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rel in relationships)
            {
                if (rel == null || string.IsNullOrEmpty(rel.Type))
                {
                    continue;
                }

                bool isSource = string.Equals(rel.SourceTaskId, taskId, StringComparison.OrdinalIgnoreCase);
                string other = isSource ? rel.TargetTaskId : rel.SourceTaskId;
                if (string.IsNullOrEmpty(other) || string.Equals(other, taskId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string direction;
                if (string.Equals(rel.Type, "related_to", StringComparison.OrdinalIgnoreCase))
                {
                    direction = "floating";
                }
                else if (string.Equals(rel.Type, "depends_on", StringComparison.OrdinalIgnoreCase))
                {
                    direction = isSource ? "upstream" : "downstream";
                }
                else if (string.Equals(rel.Type, "blocks", StringComparison.OrdinalIgnoreCase))
                {
                    direction = isSource ? "downstream" : "upstream";
                }
                else
                {
                    direction = "floating";
                }

                if (seen.Add(other + "|" + direction))
                {
                    result.Add((other, rel.Type, direction));
                }
            }

            return result;
        }
    }

    /// <summary>
    /// A renderable dependency graph derived from one task's checklist.
    /// </summary>
    public class ChecklistGraph
    {
        /// <summary>Gets or sets the task this graph was built from.</summary>
        public string TaskId { get; set; }

        /// <summary>Gets or sets the task title, for the view header.</summary>
        public string TaskTitle { get; set; }

        /// <summary>Gets the graph nodes. Checklist items come first, in index order.</summary>
        public List<ChecklistGraphNode> Nodes { get; } = new List<ChecklistGraphNode>();

        /// <summary>Gets the graph edges. Every edge traces to a declared dependency or relationship.</summary>
        public List<ChecklistGraphEdge> Edges { get; } = new List<ChecklistGraphEdge>();

        /// <summary>
        /// Gets problems found in the declared dependencies — out-of-range indices, self-references,
        /// duplicates, and broken cycles. Surfaced in the view so a bad plan is visible rather than
        /// silently simplified away.
        /// </summary>
        public List<string> Warnings { get; } = new List<string>();
    }

    /// <summary>
    /// One node: either a checklist item or an external task referenced by a relationship.
    /// </summary>
    public class ChecklistGraphNode
    {
        /// <summary>Gets or sets the stable node id ("item:N" or "task:ID").</summary>
        public string Id { get; set; }

        /// <summary>Gets or sets the node kind: "item" or "task".</summary>
        public string Kind { get; set; }

        /// <summary>Gets or sets the checklist index for item nodes; null for external tasks.</summary>
        public int? ItemIndex { get; set; }

        /// <summary>Gets or sets the display text.</summary>
        public string Label { get; set; }

        /// <summary>Gets or sets the workflow status, or "external" for cross-task nodes.</summary>
        public string Status { get; set; }

        /// <summary>Gets or sets the relationship type for external nodes; null for items.</summary>
        public string Relation { get; set; }

        /// <summary>Gets or sets the agent assigned to this item, if any.</summary>
        public string AssignedTo { get; set; }

        /// <summary>Gets or sets how many coding/testing cycles this item has been through.</summary>
        public int CycleCount { get; set; }

        /// <summary>Gets or sets the row this node renders on. Shared tier means genuinely parallel.</summary>
        public int Tier { get; set; }

        /// <summary>
        /// Gets or sets the plain-language explanation, or null when nobody wrote one.
        /// </summary>
        public ChecklistItemGloss Gloss { get; set; }

        /// <summary>
        /// Gets the file names touched by this specific item, derived from item-scoped file links
        /// rather than written by hand — so it cannot go stale.
        /// </summary>
        public List<string> Files { get; set; } = new List<string>();
    }

    /// <summary>
    /// A directed edge meaning "the source must happen before the target".
    /// </summary>
    public class ChecklistGraphEdge
    {
        /// <summary>Gets or sets the prerequisite node id.</summary>
        public string From { get; set; }

        /// <summary>Gets or sets the dependent node id.</summary>
        public string To { get; set; }

        /// <summary>Gets or sets the edge kind: "depends" (within the checklist) or "relationship" (cross-task).</summary>
        public string Kind { get; set; }

        /// <summary>Gets or sets the relationship type for cross-task edges; null within a checklist.</summary>
        public string Label { get; set; }
    }
}
