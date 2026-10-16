using System;
using System.Collections.Generic;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;

namespace MultiTerminal.ProjectSettings
{
    /// <summary>
    /// The live <see cref="IProjectSettingsHost"/> (ticket e20e2ce0): routes a project settings save's
    /// outside effects to the broker and the gateway.
    /// </summary>
    internal sealed class ProjectSettingsHost : IProjectSettingsHost
    {
        private readonly MessageBroker _broker;
        private readonly ProjectService _projectService;
        private readonly ProjectDatabase _projectDb;
        private readonly Func<GatewayIntegrationService> _gateway;
        private readonly DebugLogService _log;

        public ProjectSettingsHost(MessageBroker broker, ProjectService projectService, ProjectDatabase projectDb,
            Func<GatewayIntegrationService> gateway, DebugLogService log)
        {
            _broker = broker;
            _projectService = projectService;
            _projectDb = projectDb;
            _gateway = gateway;
            _log = log;
        }

        public string RenameProject(string projectId, string name, string description)
        {
            if (_broker != null)
            {
                var result = _broker.UpdateProject(projectId, name, description, "project-settings");
                if (result.Success)
                    return null;

                // The broker caches only the projects in the JSON registry. A project with a database row
                // but no registry entry is still a real project: write it the way the broker would have.
                if (result.Error == null || !result.Error.StartsWith("Project not found", StringComparison.Ordinal))
                    return result.Error ?? "the broker refused the change";
            }

            if (!string.IsNullOrWhiteSpace(name) && !_projectDb.UpdateProjectField(projectId, "name", name))
                return "the project row was not found";
            if (description != null)
                _projectDb.UpdateProjectField(projectId, "description", description);

            var path = _projectDb.GetRichProject(projectId)?.Path;
            if (!string.IsNullOrWhiteSpace(path) && _projectService.IsProjectDirectory(path))
                _projectService.UpdateProjectJsonNameAndDescription(path, projectId, name, description);
            return null;
        }

        public void NotifyProjectsChanged() => _broker?.NotifyProjectsChanged();

        public void SyncGatewayProfile(string projectId, string projectName, ISet<string> enabledServerNames)
        {
            try
            {
                var gateway = _gateway?.Invoke();
                if (gateway == null || !gateway.IsGatewayInstalled() || string.IsNullOrWhiteSpace(projectName))
                    return;
                gateway.SyncProjectProfile(projectId, projectName, enabledServerNames);
            }
            catch (Exception ex)
            {
                _log?.Error("ProjectSettings", $"Gateway profile sync failed for {projectId}: {ex.Message}");
            }
        }
    }
}
