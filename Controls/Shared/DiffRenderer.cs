using System.Collections.Generic;
using System.Net;
using System.Text;

namespace MultiTerminal.Controls.Shared
{
    /// <summary>
    /// Classification of a single line within a unified diff. Drives both the
    /// styled-HTML rendering used by <see cref="DiffRenderer.RenderUnifiedDiff"/>
    /// and the structured form used by the HUD Git tab to render unified or
    /// side-by-side diffs while preserving textContent discipline (no
    /// pre-rendered HTML crosses the WebView2 boundary).
    /// </summary>
    public enum DiffLineKind
    {
        /// <summary>File-header lines like <c>+++ b/foo.cs</c> or <c>--- a/foo.cs</c>.</summary>
        Meta,

        /// <summary>Hunk-header lines like <c>@@ -45,7 +45,9 @@</c>.</summary>
        Hunk,

        /// <summary>Added line (begins with <c>+</c>).</summary>
        Add,

        /// <summary>Deleted line (begins with <c>-</c>).</summary>
        Del,

        /// <summary>Context line (no prefix or any other prefix).</summary>
        Ctx,
    }

    /// <summary>
    /// One classified line of a unified diff. <see cref="Text"/> is the raw line
    /// text including any leading <c>+</c> / <c>-</c> / <c>@</c>; consumers
    /// rendering per-line spans should write the text as-is via
    /// <c>textContent</c> and apply CSS classes based on <see cref="Kind"/>.
    /// </summary>
    public sealed class DiffLine
    {
        public DiffLineKind Kind { get; set; }
        public string Text { get; set; }
    }

    /// <summary>
    /// Renders unified diff text as a styled HTML document for display in WebView2 panels.
    /// Shared between TerminalDocument's per-file diff popup and the HUD Git tab so both
    /// surfaces use one renderer (and one stylesheet) — keeping diff visuals consistent
    /// across the app.
    /// </summary>
    public static class DiffRenderer
    {
        /// <summary>
        /// Parses unified diff text into a list of classified lines. Used by both
        /// <see cref="RenderUnifiedDiff"/> (which builds HTML from this) and the
        /// HUD Git tab (which ships the structured form to JS for textContent
        /// rendering with per-line CSS classes — no pre-rendered HTML crosses
        /// the WebView2 boundary).
        /// </summary>
        public static IReadOnlyList<DiffLine> ParseUnifiedDiff(string diff)
        {
            if (string.IsNullOrEmpty(diff)) return System.Array.Empty<DiffLine>();
            var lines = diff.Split('\n');
            var result = new List<DiffLine>(lines.Length);
            foreach (var line in lines)
            {
                DiffLineKind kind;
                if (line.StartsWith("+++") || line.StartsWith("---"))
                    kind = DiffLineKind.Meta;
                else if (line.StartsWith("@@"))
                    kind = DiffLineKind.Hunk;
                else if (line.StartsWith("+"))
                    kind = DiffLineKind.Add;
                else if (line.StartsWith("-"))
                    kind = DiffLineKind.Del;
                else
                    kind = DiffLineKind.Ctx;
                result.Add(new DiffLine { Kind = kind, Text = line });
            }
            return result;
        }

        /// <summary>
        /// Renders a unified diff string as a complete HTML document, themed for dark backgrounds.
        /// </summary>
        /// <param name="fileName">Display name shown in the diff header (e.g. "MainForm.cs").</param>
        /// <param name="fullPath">Full path shown beneath the file name in the header.</param>
        /// <param name="diff">Unified diff text. Lines beginning with '+++' / '---' / '@@' / '+' / '-' are styled; all other lines render as context.</param>
        public static string RenderUnifiedDiff(string fileName, string fullPath, string diff)
        {
            string escapedName = WebUtility.HtmlEncode(fileName);
            string escapedPath = WebUtility.HtmlEncode(fullPath);

            var sb = new StringBuilder();
            foreach (var entry in ParseUnifiedDiff(diff))
            {
                string escaped = WebUtility.HtmlEncode(entry.Text);
                switch (entry.Kind)
                {
                    case DiffLineKind.Meta: sb.Append($"<div class=\"diff-meta\">{escaped}</div>"); break;
                    case DiffLineKind.Hunk: sb.Append($"<div class=\"diff-hunk\">{escaped}</div>"); break;
                    case DiffLineKind.Add:  sb.Append($"<div class=\"diff-add\">{escaped}</div>");  break;
                    case DiffLineKind.Del:  sb.Append($"<div class=\"diff-del\">{escaped}</div>");  break;
                    default:                sb.Append($"<div class=\"diff-ctx\">{escaped}</div>");  break;
                }
            }

            return $@"<!DOCTYPE html>
<html><head><meta charset=""UTF-8""><style>
body {{
    margin: 0; padding: 12px;
    font-family: 'Cascadia Code', 'Consolas', 'Courier New', monospace;
    font-size: 12px;
    background: #1a1a2e;
    color: #e0e0e0;
}}
.diff-header {{
    padding: 8px 12px;
    background: #16213e;
    border: 1px solid #2a2a4a;
    border-radius: 6px;
    margin-bottom: 12px;
}}
.diff-header h2 {{
    margin: 0 0 4px 0;
    font-size: 14px;
    color: #89b4fa;
}}
.diff-header .path {{
    font-size: 10px;
    color: #707088;
}}
.diff-content {{
    border: 1px solid #2a2a4a;
    border-radius: 4px;
    overflow-x: auto;
    background: #12122a;
}}
.diff-content > div {{
    padding: 1px 10px;
    white-space: pre;
    line-height: 1.5;
}}
.diff-add {{ background: rgba(166, 227, 161, 0.12); color: #a6e3a1; }}
.diff-del {{ background: rgba(243, 139, 168, 0.12); color: #f38ba8; }}
.diff-hunk {{ background: rgba(137, 180, 250, 0.08); color: #89b4fa; font-style: italic; }}
.diff-meta {{ color: #707088; font-weight: bold; }}
.diff-ctx {{ color: #a0a0b8; }}
</style></head><body>
<div class=""diff-header"">
    <h2>{escapedName}</h2>
    <div class=""path"">{escapedPath}</div>
</div>
<div class=""diff-content"">
{sb}
</div>
</body></html>";
        }

    }
}
