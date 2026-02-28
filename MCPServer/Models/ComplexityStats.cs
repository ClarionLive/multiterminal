using System;

namespace MultiTerminal.MCPServer.Models
{
    /// <summary>
    /// Statistics for complexity analysis decisions, used to tune the learnable heuristic.
    /// </summary>
    public class ComplexityStats
    {
        /// <summary>
        /// Total number of tasks that have been analyzed for complexity.
        /// </summary>
        public int TotalAnalyzed { get; set; }

        /// <summary>
        /// Number of times the system suggested creating a plan.
        /// </summary>
        public int PlansSuggested { get; set; }

        /// <summary>
        /// Number of times the user accepted the plan suggestion.
        /// </summary>
        public int PlansAccepted { get; set; }

        /// <summary>
        /// Number of times the user declined the plan suggestion.
        /// </summary>
        public int PlansDeclined { get; set; }

        /// <summary>
        /// Acceptance rate as a percentage (0-100). Returns 0 if no decisions have been made.
        /// </summary>
        public double AcceptanceRate
        {
            get
            {
                var totalDecisions = PlansAccepted + PlansDeclined;
                if (totalDecisions == 0) return 0;
                return (double)PlansAccepted / totalDecisions * 100;
            }
        }

        /// <summary>
        /// Number of pending decisions (suggested but user hasn't decided yet).
        /// </summary>
        public int PendingDecisions => PlansSuggested - PlansAccepted - PlansDeclined;
    }
}
