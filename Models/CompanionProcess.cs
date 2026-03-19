using System.Collections.Generic;

namespace MultiTerminal.Models
{
    /// <summary>
    /// Configuration root for companion processes loaded from companion-processes.json.
    /// </summary>
    public class CompanionConfig
    {
        public List<CompanionProcess> Companions { get; set; } = new List<CompanionProcess>();
    }

    /// <summary>
    /// Defines an external process that MultiTerminal can auto-launch on startup.
    /// </summary>
    public class CompanionProcess
    {
        /// <summary>
        /// Display name for the companion (e.g. "MCP Gateway", "Caddy").
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Executable or command to run.
        /// </summary>
        public string Command { get; set; }

        /// <summary>
        /// Command-line arguments.
        /// </summary>
        public string Args { get; set; }

        /// <summary>
        /// Optional health-check URL. If set, a GET request is made before launching.
        /// If the endpoint returns 200, the process is assumed already running and launch is skipped.
        /// </summary>
        public string HealthUrl { get; set; }

        /// <summary>
        /// Whether to auto-start this companion when MultiTerminal launches.
        /// </summary>
        public bool AutoStart { get; set; }

        /// <summary>
        /// Whether to kill the tracked process when MultiTerminal closes.
        /// Default is false — most companions are long-lived services.
        /// </summary>
        public bool StopOnExit { get; set; } = false;
    }
}
