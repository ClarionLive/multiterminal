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

        public void ProjectRenamed(string projectId, string previousName)
        {
            if (_broker != null && _broker.AdoptProjectNameFromDatabase(projectId, previousName, "project-settings"))
                return;

            // A project the broker does not cache: sync its project.json from the stored row ourselves.
            try
            {
                var stored = _projectDb.GetRichProject(projectId);
                if (stored != null && !string.IsNullOrWhiteSpace(stored.Path) && _projectService.IsProjectDirectory(stored.Path))
                    _projectService.UpdateProjectJsonNameAndDescription(stored.Path, projectId, stored.Name, stored.Description ?? string.Empty);
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                _log?.Error("ProjectSettings", $"project.json name sync failed for {projectId}: {ex.Message}");
            }
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
