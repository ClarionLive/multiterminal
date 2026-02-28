using System;
using System.Collections.Generic;
using MultiTerminal.Services;

namespace MultiTerminal.Models
{
    /// <summary>
    /// Represents a development project with associated metadata and prompts.
    /// Stored in .claude/project.json within the project folder for portability.
    /// </summary>
    public class Project
    {
        /// <summary>
        /// Unique identifier (GUID).
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Human-readable project name (independent of folder name).
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Absolute path to the project folder.
        /// </summary>
        public string Path { get; set; }

        /// <summary>
        /// Project description/purpose.
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Change history notes (user-maintained changelog).
        /// </summary>
        public string ChangeLog { get; set; }

        /// <summary>
        /// Current semantic version of the project (e.g., "1.2.3").
        /// Automatically updated by ChangelogService when tasks are completed.
        /// </summary>
        public string CurrentVersion { get; set; } = "0.1.0";

        /// <summary>
        /// When the project was first registered.
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// When the project was last opened in MultiTerminal.
        /// </summary>
        public DateTime LastOpenedAt { get; set; }

        /// <summary>
        /// Project-specific prompts (stored in .claude/project.json).
        /// </summary>
        public List<Prompt> Prompts { get; set; } = new List<Prompt>();

        /// <summary>
        /// Agent names from the team.agents array in project.json.
        /// Used for team assembly workflow (spawning native Team Agents with profile data).
        /// </summary>
        public List<string> TeamAgents { get; set; } = new List<string>();

        /// <summary>
        /// Whether this project is pinned/favorite.
        /// </summary>
        public bool IsPinned { get; set; }

        /// <summary>
        /// Creates a new Project with a generated GUID and current timestamp.
        /// </summary>
        public static Project Create(string name, string path)
        {
            return new Project
            {
                Id = Guid.NewGuid().ToString(),
                Name = name,
                Path = path,
                CreatedAt = DateTime.Now,
                LastOpenedAt = DateTime.Now,
                Prompts = new List<Prompt>()
            };
        }
    }
}
