using System;

namespace MultiTerminal.Services
{
    /// <summary>
    /// How tall a pane's header must be to show all its rows (ticket c72ec2af). Owner report: "at first
    /// I didn't see it, I had to pull down to resize the height": a header height saved before the
    /// GitHub row existed (task e4924065) clipped that row. The header page reports the device pixels
    /// its rows need; the bar only ever grows to it. Pure.
    /// </summary>
    public static class StatusBarFit
    {
        /// <summary>The tallest header MT allows, the same as the settings clamp.</summary>
        public const int MaxHeight = 400;

        /// <summary>
        /// The height to grow the bar to, or null to leave it: null when it already fits
        /// (<paramref name="neededHeight"/> is no taller than <paramref name="currentHeight"/>) or the
        /// report is not a positive height. Never shrinks the bar, and never past <see cref="MaxHeight"/>.
        /// </summary>
        public static int? GrowTo(int currentHeight, int neededHeight)
        {
            if (neededHeight <= 0 || neededHeight <= currentHeight) return null;
            int target = Math.Min(neededHeight, MaxHeight);
            return target > currentHeight ? target : (int?)null;
        }
    }
}
