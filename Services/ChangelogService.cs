using System;
using System.Text.RegularExpressions;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Service for automated changelog generation based on conventional commits.
    /// Triggered when kanban tasks are marked as "done".
    /// </summary>
    public class ChangelogService
    {
        private readonly ProjectService _projectService;
        private readonly VersioningService _versioningService;

        public ChangelogService(ProjectService projectService, VersioningService versioningService)
        {
            _projectService = projectService;
            _versioningService = versioningService;
        }

        /// <summary>
        /// Add a changelog entry for a completed task.
        /// Parses the task title for conventional commit prefix, bumps the version, and updates the changelog.
        /// </summary>
        /// <param name="task">The completed kanban task</param>
        /// <param name="projectPath">Path to the project</param>
        public void AddChangelogEntry(KanbanTask task, string projectPath)
        {
            if (task == null)
                throw new ArgumentNullException(nameof(task));

            if (string.IsNullOrEmpty(projectPath))
                throw new ArgumentException("Project path is required", nameof(projectPath));

            // Load the project
            var project = _projectService.LoadProject(projectPath);
            if (project == null)
            {
                System.Diagnostics.Debug.WriteLine($"[ChangelogService] Project not found at path: {projectPath}");
                return;
            }

            // Parse the task title for conventional commit format
            var (bumpType, message) = ParseConventionalCommit(task.Title);

            // Bump the version
            string newVersion;
            try
            {
                newVersion = _versioningService.BumpVersion(projectPath, bumpType);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ChangelogService] Failed to bump version: {ex.Message}");
                return;
            }

            // Generate changelog entry
            var entry = FormatChangelogEntry(newVersion, message, task);

            // Append to changelog
            project.ChangeLog = entry + "\n\n" + (project.ChangeLog ?? "");

            // Save project
            try
            {
                _projectService.SaveProject(project);
                System.Diagnostics.Debug.WriteLine($"[ChangelogService] Added changelog entry for task {task.Id}: {message}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ChangelogService] Failed to save project: {ex.Message}");
            }
        }

        /// <summary>
        /// Parse a task title for conventional commit format.
        /// Returns the bump type and cleaned message.
        /// </summary>
        /// <param name="title">Task title (e.g., "feat: Add user authentication")</param>
        /// <returns>Tuple of (BumpType, message)</returns>
        private (VersioningService.BumpType bumpType, string message) ParseConventionalCommit(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return (VersioningService.BumpType.Patch, "No description");

            // Check for BREAKING CHANGE (can appear anywhere in title/description)
            if (title.Contains("BREAKING CHANGE", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("BREAKING:", StringComparison.OrdinalIgnoreCase))
            {
                var message = Regex.Replace(title, @"BREAKING\s*(CHANGE)?:?\s*", "", RegexOptions.IgnoreCase).Trim();
                return (VersioningService.BumpType.Major, message.Length > 0 ? message : title);
            }

            // Match conventional commit prefix: type(scope): message
            var match = Regex.Match(title, @"^(feat|feature|fix|chore|docs|style|refactor|perf|test|build|ci)(\([^)]+\))?:\s*(.+)$", RegexOptions.IgnoreCase);

            if (match.Success)
            {
                var type = match.Groups[1].Value.ToLower();
                var message = match.Groups[3].Value.Trim();

                // Determine bump type based on prefix
                var bumpType = type switch
                {
                    "feat" or "feature" => VersioningService.BumpType.Minor,
                    "fix" => VersioningService.BumpType.Patch,
                    _ => VersioningService.BumpType.Patch // chore, docs, style, refactor, test, etc. → patch
                };

                return (bumpType, message);
            }

            // No conventional commit prefix found - default to patch with original title
            return (VersioningService.BumpType.Patch, title);
        }

        /// <summary>
        /// Format a changelog entry.
        /// </summary>
        /// <param name="version">Version number (e.g., "1.2.3")</param>
        /// <param name="message">Change message</param>
        /// <param name="task">The kanban task</param>
        /// <returns>Formatted changelog entry</returns>
        private string FormatChangelogEntry(string version, string message, KanbanTask task)
        {
            var date = DateTime.Now.ToString("yyyy-MM-dd");
            var taskId = task.Id.Substring(0, Math.Min(8, task.Id.Length));

            return $"## v{version} - {date}\n- {message} [Task #{taskId}]";
        }
    }
}
