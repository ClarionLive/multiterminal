using System;

namespace MultiTerminal.Models
{
    /// <summary>
    /// Lightweight entry in the central project registry.
    /// Contains only reference information for quick listing and lookup.
    /// Full project data is stored in .claude/project.json within each project folder.
    /// </summary>
    public class ProjectRegistryEntry
    {
        /// <summary>
        /// Project ID matching the .claude/project.json Id field.
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Cached project name for quick display without loading full project.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Path to the project folder.
        /// </summary>
        public string Path { get; set; }

        /// <summary>
        /// When last opened (for Recent projects list sorting).
        /// </summary>
        public DateTime LastOpenedAt { get; set; }

        /// <summary>
        /// Whether pinned in the registry for quick access.
        /// </summary>
        public bool IsPinned { get; set; }

        /// <summary>
        /// Creates a registry entry from a full Project object.
        /// </summary>
        public static ProjectRegistryEntry FromProject(Project project)
        {
            return new ProjectRegistryEntry
            {
                Id = project.Id,
                Name = project.Name,
                Path = project.Path,
                LastOpenedAt = project.LastOpenedAt,
                IsPinned = project.IsPinned
            };
        }
    }
}
