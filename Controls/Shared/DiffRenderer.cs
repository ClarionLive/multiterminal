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

        /// <summary>
        /// Renders a single-file unified diff in the Code Review overlay's visual style:
        /// line numbers, hunk markers, "Changes (N)" navigator sidebar, dark theme matching
        /// the Tasks panel's <c>showCodeReviewOverlay</c>. Read-only — no comment input or
        /// verdict footer (those belong to the formal task-bound Code Review flow, not the
        /// Git tab's exploratory popup).
        ///
        /// <para>When <paramref name="taskId"/> is non-empty, an "Open Code Review" button
        /// appears in the popup header. The button posts
        /// <c>{type:'open_code_review',taskId,filePath}</c> to the WebView2 host so the
        /// host can escalate into the task-bound Code Review overlay in the Tasks panel
        /// pre-selected on the file the popup was showing.</para>
        /// </summary>
        public static string RenderCodeReviewStylePopup(
            string fileName, string fullPath, string diff,
            string taskId, string taskTitle)
        {
            string fileForJs = HtmlEncodeForAttr(fullPath ?? fileName ?? string.Empty);
            string diffEncoded = WebUtility.HtmlEncode(diff ?? string.Empty);
            string taskIdAttr = HtmlEncodeForAttr(taskId ?? string.Empty);
            string taskTitleAttr = HtmlEncodeForAttr(taskTitle ?? string.Empty);

            // Show the open-review button only when we have a task linkage. The button
            // is rendered always (so JS can wire it consistently) but hidden via CSS
            // when there's no task. That keeps the JS path-of-least-resistance.
            string buttonStyle = string.IsNullOrEmpty(taskId) ? " style=\"display:none\"" : string.Empty;
            string buttonLabel = string.IsNullOrEmpty(taskTitle)
                ? "Open Code Review"
                : $"Open Code Review for &quot;{WebUtility.HtmlEncode(taskTitle)}&quot;";

            // Template uses __TOKENS__ so we don't have to double-brace every CSS rule.
            string template = TEMPLATE_CODE_REVIEW_POPUP;
            return template
                .Replace("__FILE_PATH__", fileForJs)
                .Replace("__DIFF_ENCODED__", diffEncoded)
                .Replace("__TASK_ID__", taskIdAttr)
                .Replace("__TASK_TITLE__", taskTitleAttr)
                .Replace("__BUTTON_STYLE__", buttonStyle)
                .Replace("__BUTTON_LABEL__", buttonLabel);
        }

        /// <summary>
        /// HTML-attribute-safe encoding (also safe for use as a JS string literal once
        /// `JSON.parse` reads the textContent). Encodes all five HTML special chars.
        /// </summary>
        private static string HtmlEncodeForAttr(string s)
            => string.IsNullOrEmpty(s) ? string.Empty : WebUtility.HtmlEncode(s);

        // Template for RenderCodeReviewStylePopup. Tokens: __FILE_PATH__ (HTML-encoded),
        // __DIFF_ENCODED__ (HTML-encoded raw diff text — script reads via textContent so
        // entities decode automatically), __TASK_ID__, __TASK_TITLE__ (both attribute-safe),
        // __BUTTON_STYLE__ (inline style hiding the button when no task), __BUTTON_LABEL__
        // (already HTML-formatted with optional entity-encoded title).
        private const string TEMPLATE_CODE_REVIEW_POPUP = @"<!DOCTYPE html>
<html>
<head>
<meta charset=""UTF-8"">
<style>
:root {
    --bg: #1a1a2e;
    --bg-elevated: #16213e;
    --border: #2a2a4a;
    --fg: #e0e0e0;
    --fg-muted: #888;
    --fg-dim: #4a4a6a;
    --accent: #4A90D9;
    --green: #a6e3a1;
    --red: #f38ba8;
}
html, body {
    margin: 0; padding: 0; height: 100%;
    font-family: -apple-system, 'Segoe UI', system-ui, sans-serif;
    font-size: 12px;
    background: var(--bg);
    color: var(--fg);
    overflow: hidden;
    display: flex; flex-direction: column;
}
.popup-header {
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 8px 12px;
    background: var(--bg-elevated);
    border-bottom: 1px solid var(--border);
    flex: 0 0 auto;
}
.popup-header .file-path {
    font-family: 'Cascadia Code', 'Consolas', monospace;
    color: var(--accent);
    font-size: 12px;
    overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
    flex: 1; min-width: 0;
}
.open-review-btn {
    background: var(--accent);
    color: #fff;
    border: none;
    padding: 5px 12px;
    border-radius: 4px;
    cursor: pointer;
    font-size: 11px;
    font-weight: 600;
    flex: 0 0 auto;
}
.open-review-btn:hover { background: #3a7bc8; }
.cr-body {
    display: flex;
    flex: 1;
    min-height: 0;
}
.cr-diff-area {
    flex: 1;
    overflow: scroll;
    background: var(--bg);
    font-family: 'Cascadia Code', 'Consolas', monospace;
    font-size: 12px;
    min-width: 0;
}
.cr-diff-header {
    padding: 8px 12px;
    background: rgba(255,255,255,0.03);
    border-bottom: 1px solid var(--border);
    color: var(--fg-muted);
    font-size: 11px;
    position: sticky; top: 0; z-index: 1;
    white-space: nowrap;
}
.diff-line {
    padding: 0 12px;
    line-height: 20px;
    white-space: pre;
    display: flex;
}
.diff-line:hover { background: rgba(255,255,255,0.04); }
.diff-line.add { background: rgba(76,175,80,0.10); color: #a5d6a7; }
.diff-line.add:hover { background: rgba(76,175,80,0.18); }
.diff-line.remove { background: rgba(244,67,54,0.10); color: #ef9a9a; }
.diff-line.remove:hover { background: rgba(244,67,54,0.18); }
.diff-line.hunk { color: var(--accent); background: rgba(74,144,217,0.05); padding: 4px 12px; margin: 4px 0; }
.diff-line.context { color: var(--fg); }
.diff-line.hunk.highlighted { background: rgba(74,144,217,0.15); }
.diff-line-num {
    width: 50px; text-align: right; padding-right: 10px;
    color: #666; user-select: none; flex: 0 0 50px;
    font-size: 11px;
}
.diff-line-content { flex: 1; min-width: 0; }
.diff-line.hunk .diff-line-num { color: transparent; }

.cr-summary-sidebar {
    width: 240px;
    border-left: 1px solid var(--border);
    flex: 0 0 240px;
    display: flex; flex-direction: column;
    background: var(--bg-elevated);
}
.cr-sidebar-header {
    padding: 8px 12px;
    font-size: 11px;
    font-weight: 600;
    color: var(--fg);
    border-bottom: 1px solid var(--border);
    flex: 0 0 auto;
}
.cr-sidebar-content { flex: 1; overflow-y: auto; padding: 4px 0; }
.cr-summary-card {
    padding: 8px 12px;
    margin: 4px 8px;
    border-radius: 4px;
    cursor: pointer;
    font-size: 11px;
    border-left: 3px solid #555;
    background: rgba(255,255,255,0.02);
    transition: background 0.15s, border-left-color 0.15s;
}
.cr-summary-card:hover { background: rgba(255,255,255,0.06); }
.cr-summary-card.active { background: rgba(74,144,217,0.12); border-left-color: var(--accent) !important; }
.cr-summary-card.additions { border-left-color: #4CAF50; }
.cr-summary-card.deletions { border-left-color: #F44336; }
.cr-summary-card.mixed { border-left-color: #FF9800; }
.cr-scard-title { color: var(--fg); margin-bottom: 3px; line-height: 1.3; }
.cr-scard-meta { color: var(--fg-muted); font-size: 10px; }
.cr-scard-empty { padding: 12px; color: var(--fg-muted); font-size: 11px; font-style: italic; text-align: center; }

::-webkit-scrollbar { width: 12px; height: 12px; }
::-webkit-scrollbar-track { background: var(--bg); }
::-webkit-scrollbar-corner { background: var(--bg); }
::-webkit-scrollbar-thumb { background: var(--border); border-radius: 6px; }
::-webkit-scrollbar-thumb:hover { background: var(--fg-dim); }
</style>
</head>
<body>
<div class=""popup-header"">
    <span class=""file-path"" id=""file-path"">__FILE_PATH__</span>
    <button class=""open-review-btn"" id=""open-review-btn"" data-task-id=""__TASK_ID__""__BUTTON_STYLE__>&#x1F4CB; __BUTTON_LABEL__</button>
</div>
<div class=""cr-body"">
    <div class=""cr-diff-area"" id=""cr-diff-area""></div>
    <div class=""cr-summary-sidebar"">
        <div class=""cr-sidebar-header"" id=""cr-sidebar-header"">Changes</div>
        <div class=""cr-sidebar-content"" id=""cr-sidebar-content""></div>
    </div>
</div>
<pre id=""diff-source"" style=""display:none"">__DIFF_ENCODED__</pre>
<script>
(function(){
    // Read task id and file path from the DOM (HTML-encoded by C#) rather
    // than building JS string literals — avoids any escaping pitfalls if the
    // values ever contain quotes / backslashes / non-ASCII.
    const FILE_PATH = document.getElementById('file-path').textContent;
    const reviewBtnEl = document.getElementById('open-review-btn');
    const TASK_ID = reviewBtnEl ? (reviewBtnEl.getAttribute('data-task-id') || '') : '';

    function escapeHtml(s) {
        return String(s).replace(/[&<>""']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','""':'&quot;',""'"":'&#39;'}[c]));
    }

    const diffText = document.getElementById('diff-source').textContent;
    const lines = diffText.split(/\r?\n/);
    const diffArea = document.getElementById('cr-diff-area');
    const sidebar = document.getElementById('cr-sidebar-content');
    const sidebarHeader = document.getElementById('cr-sidebar-header');

    let html = '<div class=""cr-diff-header"">' + escapeHtml(FILE_PATH) + '</div>';
    let lineNum = 0;
    let hunkIndex = -1;
    let currentHunk = null;
    const hunks = [];

    function finalizeHunk(h) {
        let type = 'mixed';
        if (h.added > 0 && h.removed === 0) type = 'additions';
        else if (h.removed > 0 && h.added === 0) type = 'deletions';
        let summary;
        if (type === 'additions') summary = 'Added ' + h.added + ' line' + (h.added!==1?'s':'');
        else if (type === 'deletions') summary = 'Removed ' + h.removed + ' line' + (h.removed!==1?'s':'');
        else summary = 'Changed: +' + h.added + ' −' + h.removed + ' lines';
        hunks.push(Object.assign({}, h, { type: type, summary: summary }));
    }

    for (let i = 0; i < lines.length; i++) {
        const line = lines[i];
        if (line.startsWith('@@')) {
            if (currentHunk) finalizeHunk(currentHunk);
            const m = line.match(/@@ -(\d+),?(\d*) \+(\d+),?(\d*) @@/);
            hunkIndex++;
            const hunkId = 'hunk-' + hunkIndex;
            currentHunk = {
                oldStart: m ? parseInt(m[1]) : 0,
                newStart: m ? parseInt(m[3]) : 0,
                added: 0, removed: 0,
                hunkId: hunkId, hunkIndex: hunkIndex
            };
            if (m) lineNum = parseInt(m[3]) - 1;
            html += '<div class=""diff-line hunk"" id=""' + hunkId + '"" data-hunkidx=""' + hunkIndex + '""><span class=""diff-line-num""></span><span class=""diff-line-content"">' + escapeHtml(line) + '</span></div>';
        } else if (line.startsWith('+') && !line.startsWith('+++')) {
            lineNum++;
            if (currentHunk) currentHunk.added++;
            html += '<div class=""diff-line add""><span class=""diff-line-num"">' + lineNum + '</span><span class=""diff-line-content"">' + escapeHtml(line) + '</span></div>';
        } else if (line.startsWith('-') && !line.startsWith('---')) {
            if (currentHunk) currentHunk.removed++;
            html += '<div class=""diff-line remove""><span class=""diff-line-num"">&minus;</span><span class=""diff-line-content"">' + escapeHtml(line) + '</span></div>';
        } else if (line.startsWith('---') || line.startsWith('+++')) {
            continue;
        } else {
            lineNum++;
            html += '<div class=""diff-line context""><span class=""diff-line-num"">' + lineNum + '</span><span class=""diff-line-content"">' + escapeHtml(line) + '</span></div>';
        }
    }
    if (currentHunk) finalizeHunk(currentHunk);
    diffArea.innerHTML = html;

    sidebarHeader.textContent = 'Changes (' + hunks.length + ')';
    if (hunks.length === 0) {
        sidebar.innerHTML = '<div class=""cr-scard-empty"">No changes detected.</div>';
    } else {
        sidebar.innerHTML = hunks.map(h =>
            '<div class=""cr-summary-card ' + h.type + '"" data-hunkid=""' + h.hunkId + '"">' +
            '<div class=""cr-scard-title"">' + escapeHtml(h.summary) + '</div>' +
            '<div class=""cr-scard-meta"">Around line ' + h.newStart + '</div>' +
            '</div>'
        ).join('');
        sidebar.querySelectorAll('.cr-summary-card').forEach(card => {
            card.addEventListener('click', () => {
                const id = card.getAttribute('data-hunkid');
                const target = document.getElementById(id);
                if (target) {
                    target.scrollIntoView({behavior: 'smooth', block: 'start'});
                    document.querySelectorAll('.cr-summary-card.active').forEach(c => c.classList.remove('active'));
                    card.classList.add('active');
                    document.querySelectorAll('.diff-line.hunk.highlighted').forEach(c => c.classList.remove('highlighted'));
                    target.classList.add('highlighted');
                }
            });
        });
    }

    // Wire ""Open Code Review"" button (visible only when TASK_ID is set).
    // FILE_PATH is forwarded so the Tasks panel overlay can pre-select the
    // linked-file tab matching the file the user was looking at — without it
    // the overlay defaults to file 0, which is rarely the right one.
    if (reviewBtnEl && TASK_ID) {
        reviewBtnEl.addEventListener('click', () => {
            try {
                window.chrome.webview.postMessage({type: 'open_code_review', taskId: TASK_ID, filePath: FILE_PATH});
            } catch(e) {}
        });
    }
})();
</script>
</body>
</html>";
    }
}
