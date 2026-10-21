using System.Collections.Generic;
using System.Text;

namespace MultiTerminal.Controls
{
    /// <summary>
    /// Decides how <see cref="TerminalControl.InjectInputAsync"/> splits a text into submissions
    /// (ticket c0ebcfd4). Pure, so the rule can be tested without a terminal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Text over <see cref="MaxChunkBytes"/> is split into "[n/m] " chunks, each submitted on its own,
    /// because PowerShell's bracketed paste mode starts at ~500-600 characters and then waits for
    /// confirmation.
    /// </para>
    /// <para>
    /// <b>A slash command is never chunked.</b> Only the first submission is read as a command, and
    /// the "[1/2] " prefix stops even that one being one: a long <c>/compact focus</c> arrived as a plain
    /// message, and its second chunk sat in the input box unsent. So text starting with "/" is ONE
    /// submission with no prefix, cut to <see cref="MaxChunkBytes"/>. The cut keeps the PowerShell
    /// protection for every pane, so the pane does not need to be identified as Claude.
    /// </para>
    /// </remarks>
    internal static class InjectionChunking
    {
        /// <summary>
        /// Maximum bytes per submission. Set to 500 to stay below PowerShell's bracketed paste mode.
        /// </summary>
        internal const int MaxChunkBytes = 500;

        /// <summary>
        /// The submissions to make for <paramref name="text"/>, in order.
        /// </summary>
        /// <param name="text">The text to inject. Not null or empty.</param>
        /// <param name="truncatedFromBytes">The original UTF-8 length when a slash command was cut, otherwise null.</param>
        internal static List<string> Plan(string text, out int? truncatedFromBytes)
        {
            truncatedFromBytes = null;
            int bytes = Encoding.UTF8.GetByteCount(text);

            if (bytes <= MaxChunkBytes)
            {
                return new List<string> { text };
            }

            if (text.StartsWith('/'))
            {
                truncatedFromBytes = bytes;
                return new List<string> { TruncateToBytes(text, MaxChunkBytes) };
            }

            var chunks = SplitIntoChunks(text, MaxChunkBytes);
            var result = new List<string>(chunks.Count);
            for (int i = 0; i < chunks.Count; i++)
            {
                result.Add($"[{i + 1}/{chunks.Count}] " + chunks[i]);
            }

            return result;
        }

        /// <summary>
        /// The longest prefix of <paramref name="text"/> that fits in <paramref name="maxBytes"/> UTF-8
        /// bytes, never ending between the two halves of a surrogate pair.
        /// </summary>
        internal static string TruncateToBytes(string text, int maxBytes)
        {
            int fit = LongestPrefixWithin(text, maxBytes);
            if (fit > 0 && char.IsHighSurrogate(text[fit - 1]))
            {
                fit--;
            }

            return text.Substring(0, fit);
        }

        /// <summary>
        /// Splits text into chunks at word boundaries where possible.
        /// </summary>
        /// <param name="text">Text to split.</param>
        /// <param name="maxChunkBytes">Maximum bytes per chunk (excluding chunk marker).</param>
        /// <returns>List of text chunks.</returns>
        private static List<string> SplitIntoChunks(string text, int maxChunkBytes)
        {
            var chunks = new List<string>();

            // Reserve space for chunk marker like "[99/99] " (9 bytes max)
            var effectiveMaxBytes = maxChunkBytes - 10;

            var remaining = text;
            while (!string.IsNullOrEmpty(remaining))
            {
                var chunk = GetChunkAtWordBoundary(remaining, effectiveMaxBytes);
                chunks.Add(chunk);
                remaining = remaining.Substring(chunk.Length).TrimStart();
            }

            return chunks;
        }

        /// <summary>
        /// Gets a chunk from the start of text, breaking at word boundary if possible.
        /// </summary>
        private static string GetChunkAtWordBoundary(string text, int maxBytes)
        {
            // If text fits, return it all
            if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
            {
                return text;
            }

            int bestFit = LongestPrefixWithin(text, maxBytes);

            if (bestFit == 0) bestFit = 1; // At minimum take one character

            // Try to break at word boundary (look back for space/newline)
            var candidate = text.Substring(0, bestFit);
            var lastSpace = candidate.LastIndexOfAny(new[] { ' ', '\n', '\r', '\t' });

            // Only use word boundary if it's reasonably close (within 20% of max)
            if (lastSpace > bestFit * 0.8)
            {
                return text.Substring(0, lastSpace);
            }

            return candidate;
        }

        /// <summary>Length of the longest prefix whose UTF-8 encoding fits in <paramref name="maxBytes"/>.</summary>
        private static int LongestPrefixWithin(string text, int maxBytes)
        {
            int low = 0;
            int high = text.Length;
            int bestFit = 0;

            while (low <= high)
            {
                int mid = (low + high) / 2;
                if (Encoding.UTF8.GetByteCount(text.Substring(0, mid)) <= maxBytes)
                {
                    bestFit = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            return bestFit;
        }
    }
}
