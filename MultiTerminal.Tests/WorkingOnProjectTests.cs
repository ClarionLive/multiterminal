using System;
using System.Collections.Generic;
using MultiTerminal.AttentionPanel;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 19a26090, Owner decision 2026-10-01: header and Attention card show the project the
    /// session runs in, plus "working on X" when the active task belongs to another project.
    ///
    /// <para>The case that prompted it: Grace runs in MultiTerminal, her active task is in Clarion
    /// Addin Registry. Her header said MultiTerminal, her card said Clarion Addin Registry, and
    /// neither said why. <see cref="A_pane_header_beats_a_sticky_notification_project"/> is that
    /// card.</para>
    /// </summary>
    public class WorkingOnProjectTests
    {
        private const string Mt = "5d7853b8";
        private const string Registry = "c0ffee01";

        private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
        {
            [Mt] = "MultiTerminal",
            [Registry] = "Clarion Addin Registry",
        };

        private static KanbanTask Task(string id, string assignee, string projectId, string status = "in_progress", string sub = "active") =>
            new KanbanTask { Id = id, Assignee = assignee, ProjectId = projectId, Status = status, SubStatus = sub };

        [Fact]
        public void An_active_task_in_another_project_is_reported()
        {
            var tasks = new[] { Task("d924a287", "Grace", Registry) };

            Assert.Equal("Clarion Addin Registry",
                WorkingOnProject.Resolve(tasks, "Grace", Mt, "MultiTerminal", Names));
        }

        [Fact]
        public void An_active_task_in_the_sessions_own_project_adds_nothing()
        {
            var tasks = new[] { Task("19a26090", "Alice", Mt) };

            Assert.Null(WorkingOnProject.Resolve(tasks, "Alice", Mt, "MultiTerminal", Names));
        }

        [Theory]
        [InlineData("in_progress", "paused")]
        [InlineData("todo", "active")]
        public void Only_the_ACTIVE_task_counts(string status, string sub)
        {
            var tasks = new[] { Task("t1", "Grace", Registry, status, sub) };

            Assert.Null(WorkingOnProject.Resolve(tasks, "Grace", Mt, "MultiTerminal", Names));
        }

        [Fact]
        public void The_project_id_decides_when_it_is_known()
        {
            // A folder-name fallback can coincide with another project's display name; the id cannot.
            var tasks = new[] { Task("t1", "Grace", Registry) };

            Assert.Equal("Clarion Addin Registry",
                WorkingOnProject.Resolve(tasks, "Grace", Mt, "Clarion Addin Registry", Names));
        }

        [Fact]
        public void Another_agents_active_task_is_ignored()
        {
            var tasks = new[] { Task("t1", "Charlie", Registry) };

            Assert.Null(WorkingOnProject.Resolve(tasks, "Grace", Mt, "MultiTerminal", Names));
        }

        private static AgentAttentionEntry Entry(string agent, string notifiedProject) => new AgentAttentionEntry
        {
            SessionId = "s-" + agent,
            AgentName = agent,
            State = AttentionState.Idle,
            EnteredAtUtc = DateTime.UtcNow,
            Project = notifiedProject,
        };

        [Fact]
        public void A_pane_header_beats_a_sticky_notification_project()
        {
            var cards = AttentionCardProjector.Project(
                new[] { Entry("Grace", "Clarion Addin Registry") },
                agentColors: null, claims: null, nowUtc: DateTime.UtcNow,
                paneProjects: new Dictionary<string, AttentionPaneProject>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Grace"] = new AttentionPaneProject { Project = "MultiTerminal", WorkingOn = "Clarion Addin Registry" },
                });

            Assert.Equal("MultiTerminal", cards[0].Project);
            Assert.Equal("Clarion Addin Registry", cards[0].WorkingOn);
        }

        [Fact]
        public void Without_a_pane_the_marker_comes_from_the_active_task_when_it_differs()
        {
            var active = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["CA-tab"] = "ClarionAssistant",
                ["Same"] = "MultiTerminal",
            };

            var cards = AttentionCardProjector.Project(
                new[] { Entry("CA-tab", "MultiTerminal"), Entry("Same", "MultiTerminal") },
                agentColors: null, claims: null, nowUtc: DateTime.UtcNow,
                activeTaskProjects: active);

            Assert.Equal("ClarionAssistant", cards[0].WorkingOn);
            Assert.Null(cards[1].WorkingOn);
        }
    }
}
