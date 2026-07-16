using System.Collections.Generic;
using MultiTerminal.Models;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Unit coverage for the containment-based project resolution behind HUD scoping
    /// (task e8c6b52f). The end-to-end repro (statusline poll unscoping the Recent
    /// Project Activity feed after the agent cd's into a task worktree) needs a live
    /// WinForms terminal, so the path semantics that decide "which registered project
    /// owns this folder?" are asserted here.
    /// </summary>
    public class ProjectPathResolverTests
    {
        private static ProjectRegistryEntry Entry(string id, string path) =>
            new ProjectRegistryEntry { Id = id, Name = id, Path = path };

        [Fact]
        public void Resolves_exact_project_root()
        {
            var projects = new List<ProjectRegistryEntry> { Entry("mt", @"H:\Dev\MultiTerminal") };
            var match = ProjectPathResolver.ResolveByContainment(projects, @"H:\Dev\MultiTerminal");
            Assert.Equal("mt", match?.Id);
        }

        [Fact]
        public void Resolves_worktree_subfolder_to_containing_project()
        {
            var projects = new List<ProjectRegistryEntry> { Entry("mt", @"H:\Dev\MultiTerminal") };
            var match = ProjectPathResolver.ResolveByContainment(
                projects, @"H:\Dev\MultiTerminal\.claude\worktrees\e8c6b52f");
            Assert.Equal("mt", match?.Id);
        }

        [Fact]
        public void Unregistered_folder_resolves_null()
        {
            var projects = new List<ProjectRegistryEntry> { Entry("mt", @"H:\Dev\MultiTerminal") };
            Assert.Null(ProjectPathResolver.ResolveByContainment(projects, @"H:\Dev\SomethingElse"));
        }

        [Fact]
        public void Sibling_folder_sharing_name_prefix_is_not_contained()
        {
            var projects = new List<ProjectRegistryEntry> { Entry("mt", @"H:\Dev\MultiTerminal") };
            Assert.Null(ProjectPathResolver.ResolveByContainment(projects, @"H:\Dev\MultiTerminal-other"));
        }

        [Fact]
        public void Trailing_separators_casing_and_forward_slashes_are_tolerated()
        {
            var projects = new List<ProjectRegistryEntry> { Entry("mt", @"H:\Dev\MultiTerminal\") };
            var match = ProjectPathResolver.ResolveByContainment(
                projects, "h:/dev/multiterminal/.claude/worktrees/abc");
            Assert.Equal("mt", match?.Id);
        }

        [Fact]
        public void Deepest_containing_root_wins_when_projects_nest()
        {
            var projects = new List<ProjectRegistryEntry>
            {
                Entry("outer", @"H:\Dev"),
                Entry("inner", @"H:\Dev\MultiTerminal"),
            };
            var match = ProjectPathResolver.ResolveByContainment(
                projects, @"H:\Dev\MultiTerminal\src");
            Assert.Equal("inner", match?.Id);
        }

        [Fact]
        public void Duplicate_roots_resolve_to_first_entry_for_stability()
        {
            var projects = new List<ProjectRegistryEntry>
            {
                Entry("first", @"H:\Dev\ClarionLive"),
                Entry("second", @"h:\dev\clarionlive\"),
            };
            var match = ProjectPathResolver.ResolveByContainment(projects, @"H:\Dev\ClarionLive\sub");
            Assert.Equal("first", match?.Id);
        }

        [Fact]
        public void Null_or_empty_inputs_resolve_null()
        {
            var projects = new List<ProjectRegistryEntry>
            {
                Entry("mt", @"H:\Dev\MultiTerminal"),
                Entry("no-path", null),
                null,
            };
            Assert.Null(ProjectPathResolver.ResolveByContainment(null, @"H:\Dev\MultiTerminal"));
            Assert.Null(ProjectPathResolver.ResolveByContainment(projects, null));
            Assert.Null(ProjectPathResolver.ResolveByContainment(projects, ""));
            // Entries with null paths / null entries are skipped, not thrown on.
            Assert.Equal("mt", ProjectPathResolver.ResolveByContainment(projects, @"H:\Dev\MultiTerminal")?.Id);
        }
    }
}
