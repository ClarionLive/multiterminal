using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MultiTerminal.MCPServer.Services
{
    /// <summary>What <see cref="ProjectOpenService.OpenAsync"/> did.</summary>
    public enum ProjectOpenResult
    {
        /// <summary>The project was already open in a PM pane, which was brought to the front.</summary>
        Focused,

        /// <summary>The project was not open, so a launch was started on a Home tab, exactly as the
        /// header's Launch Project does. It may still show MT's usual questions (the session choice,
        /// a profile refusal) and the Owner may cancel it, so this is "started", not "running".</summary>
        Launching,

        /// <summary>No registered project has that id.</summary>
        NotFound,

        /// <summary>MT could not act: the window is not ready yet, or the request failed. See the error.</summary>
        Failed,
    }

    /// <summary>The answer to an open request.</summary>
    /// <param name="Result">What happened.</param>
    /// <param name="ProjectId">The project asked for.</param>
    /// <param name="ProjectName">Its name, when it was found.</param>
    /// <param name="PaneTitle">For <see cref="ProjectOpenResult.Focused"/>, the tab text of the pane brought forward.</param>
    /// <param name="Floating">For <see cref="ProjectOpenResult.Focused"/>, whether that pane is in a floating window.</param>
    /// <param name="OtherPanes">For <see cref="ProjectOpenResult.Focused"/>, how many more PM panes have the project open.</param>
    /// <param name="Error">For <see cref="ProjectOpenResult.Failed"/>, why.</param>
    public sealed record ProjectOpenOutcome(
        ProjectOpenResult Result,
        string ProjectId,
        string ProjectName = null,
        string PaneTitle = null,
        bool Floating = false,
        int OtherPanes = 0,
        string Error = null);

    /// <summary>
    /// Vouches for <see cref="Token"/> in <c>GET /api/health</c>'s capabilities: this MT has
    /// <c>POST /api/projects/{projectId}/open</c>. A caller can then tell a missing route (older MT) from
    /// an unknown project without reading the shape of a 404. The route exists in every build that
    /// carries this provider, so the token is always given; a window that is not ready yet still
    /// answers the route with 503.
    /// </summary>
    public sealed class ProjectOpenCapabilityProvider : ICapabilityProvider
    {
        /// <summary>The capability token. Clients compare it verbatim.</summary>
        public const string Token = "project-open-v1";

        /// <inheritdoc />
        public IEnumerable<string> GetCapabilities() => new[] { Token };
    }

    /// <summary>How many PM panes have one project open right now.</summary>
    /// <param name="ProjectId">The project.</param>
    /// <param name="OpenPanes">Panes with it open, by the "already open" rule (always at least 1 in an answer).</param>
    public sealed record ProjectOpenState(string ProjectId, int OpenPanes);

    /// <summary>
    /// Vouches for <see cref="Token"/>: this MT has <c>GET /api/projects/open-state</c>. Always given, as
    /// for <see cref="ProjectOpenCapabilityProvider"/>.
    /// </summary>
    public sealed class ProjectOpenStateCapabilityProvider : ICapabilityProvider
    {
        /// <summary>The capability token. Clients compare it verbatim.</summary>
        public const string Token = "project-open-state-v1";

        /// <inheritdoc />
        public IEnumerable<string> GetCapabilities() => new[] { Token };
    }

    /// <summary>
    /// <c>POST /api/projects/{projectId}/open</c> (for MT Conductor, ClarionLive#83): bring a project's PM
    /// pane to the front, or launch the project when no pane has it open. The REST side cannot touch
    /// windows, so MainForm registers <see cref="OnOpenRequested"/>, the same callback shape as
    /// <see cref="SpawnService"/>.
    /// </summary>
    public sealed class ProjectOpenService
    {
        /// <summary>Set by MainForm. Takes the project id; runs on the UI thread itself.</summary>
        public Func<string, Task<ProjectOpenOutcome>> OnOpenRequested { get; set; }

        /// <summary>Set by MainForm. The projects open in a PM pane now; runs on the UI thread itself.</summary>
        public Func<Task<IReadOnlyList<ProjectOpenState>>> OnOpenStateRequested { get; set; }

        /// <summary>
        /// The projects open in a PM pane, for <c>GET /api/projects/open-state</c>. Null when MT's window is
        /// not ready (or the read failed), so the caller can answer 503 rather than "nothing is open".
        /// </summary>
        public async Task<IReadOnlyList<ProjectOpenState>> GetOpenStateAsync()
        {
            var handler = OnOpenStateRequested;
            if (handler == null)
                return null;
            try
            {
                return await handler().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // A REST caller gets a 503, never an unhandled exception from the UI thread.
            catch (Exception)
#pragma warning restore CA1031
            {
                return null;
            }
        }

        /// <summary>Asks MainForm to open <paramref name="projectId"/>. Never throws.</summary>
        public async Task<ProjectOpenOutcome> OpenAsync(string projectId)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                return new ProjectOpenOutcome(ProjectOpenResult.NotFound, projectId, Error: "projectId is required");

            var handler = OnOpenRequested;
            if (handler == null)
                return new ProjectOpenOutcome(ProjectOpenResult.Failed, projectId, Error: "MultiTerminal's window is not ready yet.");

            try
            {
                return await handler(projectId).ConfigureAwait(false)
                    ?? new ProjectOpenOutcome(ProjectOpenResult.Failed, projectId, Error: "No answer from MultiTerminal's window.");
            }
#pragma warning disable CA1031 // A REST caller gets a Failed outcome, never an unhandled exception from the UI thread.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                // MainForm runs the work inside Control.Invoke, which wraps what it threw.
                return new ProjectOpenOutcome(ProjectOpenResult.Failed, projectId, Error: $"Open failed: {(ex.InnerException ?? ex).Message}");
            }
        }
    }
}
