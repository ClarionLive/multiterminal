using System;
using System.IO;
using MultiTerminal.Models;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Pins the working-directory contract of <see cref="LaunchCommandBuilder"/> (task 77d1182f).
    ///
    /// <para>WHY THE OVERRIDE EXISTS. Every docked terminal resolves its cwd FROM its project, so
    /// the builder could derive the directory itself. A spawned terminal is the other way round:
    /// the cwd arrives in the request and there may be no project at all. That matters because
    /// the flags are cwd-dependent — <c>BuildFlags</c> reads the cwd's
    /// <c>.claude/settings.local.json</c> to merge the project's hooks/deny rules before deciding
    /// whether dropping the LOCAL settings source is safe. Computed against the project-resolved
    /// default (the user profile, when there is no project) instead of the real cwd, a spawn into
    /// a project directory would run under <c>--dangerously-skip-permissions</c> with that
    /// project's local hooks/deny silently stripped. So the cwd must be passed explicitly, and this
    /// file pins that the builder honours it.</para>
    ///
    /// <para>WHAT IS DELIBERATELY NOT ASSERTED. Flag <em>contents</em> — the plugin path, the
    /// channel flag, the statusline merge — probe the real filesystem and would pass or fail on
    /// whether the machine has a plugin checkout. <c>ChannelFlagContractTests</c> explains why
    /// those are pinned at source level instead. The Codex builder is not exercised here either:
    /// it refreshes <c>~/.codex/config.toml</c> and probes the broker as side effects of being
    /// called, which is not something a unit test should do to the developer's machine.</para>
    /// </summary>
    public class LaunchCommandBuilderWorkingDirectoryTests
    {
        [Fact]
        public void Explicit_working_directory_is_honoured_for_claude()
        {
            string dir = MakeTempDir();
            try
            {
                var cmd = LaunchCommandBuilder.BuildClaudeCommand(project: null, workingDirectory: dir);

                Assert.Equal(dir, cmd.WorkingDirectory);
                Assert.Null(cmd.ProjectId);
                Assert.NotNull(cmd.AutoRunCommand);
                Assert.Null(cmd.BootstrapError);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void Without_an_override_a_null_project_resolves_to_the_user_profile()
        {
            // The pre-existing contract, kept: BuildClaudeCommand(project) with no project and no
            // override launches in the user profile. The override must not have changed this.
            var cmd = LaunchCommandBuilder.BuildClaudeCommand(project: null);

            Assert.Equal(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                cmd.WorkingDirectory);
        }

        [Fact]
        public void Explicit_working_directory_wins_over_the_project_path()
        {
            // A spawn can name a cwd that differs from the project's registered source path (a
            // worktree, for instance). The directory the terminal will actually run in is the one
            // the flags must be computed for, so the override beats the project.
            string projectDir = MakeTempDir();
            string spawnDir = MakeTempDir();
            try
            {
                var project = new Project { Id = "p1", Name = "P", SourcePath = projectDir, Path = projectDir };

                var cmd = LaunchCommandBuilder.BuildClaudeCommand(project, workingDirectory: spawnDir);

                Assert.Equal(spawnDir, cmd.WorkingDirectory);
                Assert.Equal("p1", cmd.ProjectId);
            }
            finally
            {
                Directory.Delete(projectDir, recursive: true);
                Directory.Delete(spawnDir, recursive: true);
            }
        }

        [Fact]
        public void BuildCommand_threads_the_override_to_the_claude_builder()
        {
            // The dispatching entry point is what MainForm.OnSpawnRequested calls; if it swallowed
            // the override the per-builder overloads would be correct and the spawn path would
            // still be wrong.
            string dir = MakeTempDir();
            try
            {
                var cmd = LaunchCommandBuilder.BuildCommand(TerminalKind.ClaudeCode, project: null, workingDirectory: dir);

                Assert.Equal(dir, cmd.WorkingDirectory);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        private static string MakeTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "mt-launch-cwd-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
