using System;
using System.Text.RegularExpressions;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Service for semantic versioning (semver) management.
    /// Follows the pattern: major.minor.patch (e.g., 1.2.3)
    /// </summary>
    public class VersioningService
    {
        private readonly ProjectService _projectService;

        public VersioningService(ProjectService projectService)
        {
            _projectService = projectService;
        }

        /// <summary>
        /// Type of version bump to perform.
        /// </summary>
        public enum BumpType
        {
            /// <summary>
            /// Major version bump (breaking changes): 1.2.3 → 2.0.0
            /// </summary>
            Major,

            /// <summary>
            /// Minor version bump (new features): 1.2.3 → 1.3.0
            /// </summary>
            Minor,

            /// <summary>
            /// Patch version bump (bug fixes): 1.2.3 → 1.2.4
            /// </summary>
            Patch
        }

        /// <summary>
        /// Bump the version for a project based on the bump type.
        /// </summary>
        /// <param name="projectPath">Path to the project</param>
        /// <param name="bumpType">Type of version bump</param>
        /// <returns>New version string (e.g., "1.2.3")</returns>
        public string BumpVersion(string projectPath, BumpType bumpType)
        {
            if (string.IsNullOrEmpty(projectPath))
                throw new ArgumentException("Project path is required", nameof(projectPath));

            // Load the project
            var project = _projectService.LoadProject(projectPath);
            if (project == null)
                throw new InvalidOperationException($"Project not found at path: {projectPath}");

            // Parse current version (default to 0.1.0 if not set)
            var currentVersion = ParseVersion(project.CurrentVersion ?? "0.1.0");

            // Bump the version
            var newVersion = Bump(currentVersion, bumpType);

            // Update project
            project.CurrentVersion = FormatVersion(newVersion);
            _projectService.SaveProject(project);

            return project.CurrentVersion;
        }

        /// <summary>
        /// Parse a version string into components.
        /// </summary>
        /// <param name="versionString">Version string (e.g., "1.2.3")</param>
        /// <returns>Tuple of (major, minor, patch)</returns>
        private (int major, int minor, int patch) ParseVersion(string versionString)
        {
            if (string.IsNullOrWhiteSpace(versionString))
                return (0, 1, 0); // Default version

            // Remove 'v' prefix if present
            versionString = versionString.TrimStart('v', 'V');

            // Match semver pattern: major.minor.patch
            var match = Regex.Match(versionString, @"^(\d+)\.(\d+)\.(\d+)");
            if (!match.Success)
                return (0, 1, 0); // Default if parsing fails

            return (
                int.Parse(match.Groups[1].Value),
                int.Parse(match.Groups[2].Value),
                int.Parse(match.Groups[3].Value)
            );
        }

        /// <summary>
        /// Bump a version based on the bump type.
        /// </summary>
        private (int major, int minor, int patch) Bump((int major, int minor, int patch) version, BumpType bumpType)
        {
            return bumpType switch
            {
                BumpType.Major => (version.major + 1, 0, 0),
                BumpType.Minor => (version.major, version.minor + 1, 0),
                BumpType.Patch => (version.major, version.minor, version.patch + 1),
                _ => throw new ArgumentException($"Unknown bump type: {bumpType}")
            };
        }

        /// <summary>
        /// Format a version tuple as a string.
        /// </summary>
        private string FormatVersion((int major, int minor, int patch) version)
        {
            return $"{version.major}.{version.minor}.{version.patch}";
        }

        /// <summary>
        /// Get the current version for a project.
        /// </summary>
        public string GetCurrentVersion(string projectPath)
        {
            var project = _projectService.LoadProject(projectPath);
            return project?.CurrentVersion ?? "0.1.0";
        }
    }
}
