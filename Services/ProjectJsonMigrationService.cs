using System;
using System.Collections.Generic;
using System.IO;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Migrates existing project.json files into the enhanced SQLite schema.
    /// The migration is additive and non-destructive:
    /// - Projects already in the database are updated with data from their project.json
    /// - New projects found in project.json but not yet in SQLite are inserted
    /// - No existing SQLite data is deleted; only missing fields are filled in
    /// - The source project.json files are never modified
    /// </summary>
    public class ProjectJsonMigrationService
    {
        private readonly ProjectDatabase _projectDb;
        private readonly ProjectService _projectService;

        public ProjectJsonMigrationService(ProjectDatabase projectDb, ProjectService projectService)
        {
            _projectDb = projectDb ?? throw new ArgumentNullException(nameof(projectDb));
            _projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
        }

        /// <summary>
        /// Runs the full migration: reads all registered project paths via ProjectService,
        /// loads their project.json files, and upserts the data into the SQLite schema.
        /// </summary>
        /// <returns>Migration result with counts of processed/skipped/failed entries.</returns>
        public ProjectMigrationResult MigrateAll()
        {
            var result = new ProjectMigrationResult();

            var registryEntries = _projectService.GetAllRegisteredProjects();
            result.TotalFound = registryEntries.Count;

            foreach (var registryEntry in registryEntries)
            {
                try
                {
                    MigrateRegistryEntry(registryEntry, result);
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    result.Errors.Add($"[{registryEntry.Name}] {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"[ProjectJsonMigration] Failed to migrate project '{registryEntry.Name}': {ex.Message}");
                }
            }

            return result;
        }

        /// <summary>
        /// Migrates a single project by path. Useful for migrating a project on-demand
        /// when it is first opened.
        /// </summary>
        public bool MigrateProject(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
                return false;

            var fileProject = _projectService.LoadProject(projectPath);
            if (fileProject == null)
                return false;

            try
            {
                UpsertProjectIntoDatabase(fileProject);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectJsonMigration] Failed for '{projectPath}': {ex.Message}");
                return false;
            }
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private void MigrateRegistryEntry(MultiTerminal.Models.ProjectRegistryEntry registryEntry, ProjectMigrationResult result)
        {
            if (string.IsNullOrWhiteSpace(registryEntry.Path) || !Directory.Exists(registryEntry.Path))
            {
                result.Skipped++;
                return;
            }

            var fileProject = _projectService.LoadProject(registryEntry.Path);
            if (fileProject == null)
            {
                // No project.json found — still ensure the project exists in SQLite
                // using whatever registry data we have
                EnsureProjectInDatabase(registryEntry);
                result.Skipped++;
                return;
            }

            UpsertProjectIntoDatabase(fileProject);

            // Migrate prompts from project.json into project_prompts table
            if (fileProject.Prompts != null && fileProject.Prompts.Count > 0)
            {
                MigratePrompts(fileProject.Id, fileProject.Prompts);
            }

            // Migrate team agents from project.json into project_agents table
            if (fileProject.TeamAgents != null && fileProject.TeamAgents.Count > 0)
            {
                MigrateTeamAgents(fileProject.Id, fileProject.TeamAgents);
            }

            result.Migrated++;
        }

        /// <summary>
        /// Upserts the core project record into the projects table.
        /// Preserves any new SQLite-only fields that don't exist in project.json.
        /// </summary>
        private void UpsertProjectIntoDatabase(MultiTerminal.Models.Project fileProject)
        {
            // Check if record already exists
            var existing = _projectDb.GetRichProject(fileProject.Id);
            if (existing != null)
            {
                // Merge: fill in empty SQLite fields from the JSON data, don't overwrite non-empty ones
                if (string.IsNullOrEmpty(existing.SourcePath) && !string.IsNullOrEmpty(fileProject.Path))
                    existing.SourcePath = fileProject.Path;

                if (string.IsNullOrEmpty(existing.Description) && !string.IsNullOrEmpty(fileProject.Description))
                    existing.Description = fileProject.Description;

                if (string.IsNullOrEmpty(existing.ChangeLog) && !string.IsNullOrEmpty(fileProject.ChangeLog))
                    existing.ChangeLog = fileProject.ChangeLog;

                if (string.IsNullOrEmpty(existing.CurrentVersion) || existing.CurrentVersion == "0.1.0")
                    existing.CurrentVersion = fileProject.CurrentVersion ?? "0.1.0";

                if (!existing.IsPinned && fileProject.IsPinned)
                    existing.IsPinned = true;

                if (existing.LastOpenedAt == default && fileProject.LastOpenedAt != default)
                    existing.LastOpenedAt = fileProject.LastOpenedAt;

                _projectDb.SaveRichProject(existing);
            }
            else
            {
                // Insert new record built from the project.json data
                var newRecord = new MultiTerminal.Models.Project
                {
                    Id = fileProject.Id,
                    Name = fileProject.Name ?? Path.GetFileName(fileProject.Path ?? "Unknown"),
                    Path = fileProject.Path,
                    SourcePath = fileProject.Path,
                    Description = fileProject.Description,
                    ChangeLog = fileProject.ChangeLog,
                    CurrentVersion = fileProject.CurrentVersion ?? "0.1.0",
                    IsPinned = fileProject.IsPinned,
                    CreatedAt = fileProject.CreatedAt == default ? DateTime.UtcNow : fileProject.CreatedAt,
                    UpdatedAt = DateTime.UtcNow,
                    LastOpenedAt = fileProject.LastOpenedAt
                };
                _projectDb.SaveRichProject(newRecord);
            }
        }

        /// <summary>
        /// Ensures a project exists in SQLite using only registry-level data (no project.json).
        /// </summary>
        private void EnsureProjectInDatabase(MultiTerminal.Models.ProjectRegistryEntry registryEntry)
        {
            if (_projectDb.GetRichProject(registryEntry.Id) == null)
            {
                var stub = new MultiTerminal.Models.Project
                {
                    Id = registryEntry.Id,
                    Name = registryEntry.Name,
                    Path = registryEntry.Path,
                    SourcePath = registryEntry.Path,
                    IsPinned = registryEntry.IsPinned,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    LastOpenedAt = registryEntry.LastOpenedAt
                };
                _projectDb.SaveRichProject(stub);
            }
        }

        /// <summary>
        /// Migrates Prompt entries from project.json into the project_prompts table.
        /// Only inserts rows that do not yet exist (additive).
        /// </summary>
        private void MigratePrompts(string projectId, List<Prompt> prompts)
        {
            var existingPrompts = _projectDb.GetProjectPrompts(projectId);

            int order = existingPrompts.Count;
            foreach (var p in prompts)
            {
                // Use the Prompt description as a deduplication key (best-effort)
                bool alreadyExists = existingPrompts.Exists(e =>
                    string.Equals(e.PromptText, p.Text, StringComparison.Ordinal));

                if (!alreadyExists && !string.IsNullOrWhiteSpace(p.Text))
                {
                    _projectDb.SaveProjectPrompt(new ProjectPromptEntry
                    {
                        ProjectId = projectId,
                        PromptType = p.Category ?? "general",
                        PromptText = p.Text,
                        DisplayOrder = order++
                    });
                }
            }
        }

        /// <summary>
        /// Migrates team agent names from project.json into the project_agents table.
        /// Only inserts agents that are not already present (additive).
        /// </summary>
        private void MigrateTeamAgents(string projectId, List<string> agentNames)
        {
            var existingAgents = _projectDb.GetProjectAgents(projectId);

            foreach (var agentName in agentNames)
            {
                if (string.IsNullOrWhiteSpace(agentName))
                    continue;

                bool alreadyExists = existingAgents.Exists(a =>
                    string.Equals(a.AgentName, agentName, StringComparison.OrdinalIgnoreCase));

                if (!alreadyExists)
                {
                    _projectDb.SaveProjectAgent(new ProjectAgent
                    {
                        ProjectId = projectId,
                        AgentName = agentName
                    });
                }
            }
        }
    }

    /// <summary>
    /// Result of a ProjectJsonMigrationService.MigrateAll() run.
    /// </summary>
    public class ProjectMigrationResult
    {
        /// <summary>Total project entries found in the registry.</summary>
        public int TotalFound { get; set; }

        /// <summary>Projects successfully read and upserted into SQLite.</summary>
        public int Migrated { get; set; }

        /// <summary>Projects skipped (missing path, no project.json found).</summary>
        public int Skipped { get; set; }

        /// <summary>Projects that threw exceptions during migration.</summary>
        public int Failed { get; set; }

        /// <summary>Error messages collected during the run.</summary>
        public List<string> Errors { get; set; } = new List<string>();

        public override string ToString() =>
            $"Migration complete: {Migrated} migrated, {Skipped} skipped, {Failed} failed of {TotalFound} total.";
    }
}
