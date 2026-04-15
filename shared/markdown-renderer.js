/**
 * MultiTerminal Shared Markdown Renderer
 *
 * Provides markdown rendering with:
 * - marked.js for GFM markdown parsing
 * - Mermaid.js for diagram rendering (```mermaid code blocks)
 * - Shiki for VS Code-quality syntax highlighting
 *
 * CDN Dependencies (load via <script> before this file):
 *   marked.js:  https://cdn.jsdelivr.net/npm/marked@14/marked.min.js
 *   mermaid.js: https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.min.js
 *
 * Shiki is loaded dynamically via import() — no extra script tag needed.
 */
(function () {
    'use strict';

    var shikiHighlighter = null;
    var isDark = true;
    var initialized = false;

    var SHIKI_CDN = 'https://cdn.jsdelivr.net/npm/shiki@1/+esm';
    var DARK_THEME = 'github-dark';
    var LIGHT_THEME = 'github-light';
    var PRELOAD_LANGS = [
        'javascript', 'typescript', 'python', 'csharp', 'json',
        'html', 'css', 'bash', 'sql', 'markdown', 'yaml', 'xml', 'powershell'
    ];

    // ── Helpers ──────────────────────────────────────────────────

    function escapeHtml(text) {
        if (!text) return '';
        return text
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    // ── Shiki (async, non-blocking) ─────────────────────────────

    async function loadShiki() {
        try {
            var shiki = await import(SHIKI_CDN);
            shikiHighlighter = await shiki.createHighlighter({
                themes: [DARK_THEME, LIGHT_THEME],
                langs: PRELOAD_LANGS
            });
            console.log('[MarkdownRenderer] Shiki loaded (' + PRELOAD_LANGS.length + ' langs)');
        } catch (err) {
            console.warn('[MarkdownRenderer] Shiki unavailable, using fallback highlighting:', err.message);
            shikiHighlighter = null;
        }
    }

    // ── Marked custom renderer ──────────────────────────────────

    function buildRenderer() {
        return {
            code: function (token) {
                var text = token.text;
                var lang = (token.lang || '').trim().toLowerCase();

                // Mermaid blocks → <div class="mermaid"> for mermaid.run()
                if (lang === 'mermaid') {
                    return '<div class="mermaid">' + escapeHtml(text) + '</div>\n';
                }

                // Shiki highlighting (if loaded and language recognised)
                if (shikiHighlighter && lang) {
                    try {
                        var loaded = shikiHighlighter.getLoadedLanguages();
                        if (loaded.includes(lang)) {
                            return shikiHighlighter.codeToHtml(text, {
                                lang: lang,
                                theme: isDark ? DARK_THEME : LIGHT_THEME
                            }) + '\n';
                        }
                    } catch (_) { /* fall through */ }
                }

                // Fallback: plain escaped code block
                var escaped = escapeHtml(text);
                var cls = lang ? ' class="language-' + escapeHtml(lang) + '"' : '';
                return '<pre class="md-code-block"><code' + cls + '>' + escaped + '</code></pre>\n';
            },

            codespan: function (token) {
                return '<code class="md-inline-code">' + escapeHtml(token.text) + '</code>';
            },

            // Block raw HTML → escape to prevent XSS
            html: function (token) {
                return escapeHtml(token.text);
            },

            link: function (token) {
                var href = token.href || '';
                // Block dangerous URI schemes
                var hrefLower = href.toLowerCase().replace(/\s/g, '');
                if (hrefLower.indexOf('javascript:') === 0 || hrefLower.indexOf('vbscript:') === 0 || hrefLower.indexOf('data:') === 0) {
                    return token.text || escapeHtml(href);
                }
                var title = token.title ? ' title="' + escapeHtml(token.title) + '"' : '';
                return '<a href="' + escapeHtml(href) + '"' + title + ' target="_blank" rel="noopener">' + (token.text || escapeHtml(href)) + '</a>';
            }
        };
    }

    // ── Mermaid config (shared between init and setTheme) ─────

    function configureMermaid() {
        if (typeof mermaid !== 'undefined') {
            mermaid.initialize({
                startOnLoad: false,
                theme: isDark ? 'dark' : 'default',
                securityLevel: 'strict',
                fontFamily: '"Segoe UI", system-ui, sans-serif'
            });
        }
    }

    // ── Public API ──────────────────────────────────────────────

    /**
     * Initialise the renderer. Call once on page load.
     * @param {Object} [options]
     * @param {boolean} [options.isDark=true]  Dark theme?
     */
    function init(options) {
        options = options || {};
        if (initialized) {
            // Allow re-init for theme change at startup
            isDark = options.isDark !== false;
            return;
        }
        initialized = true;
        isDark = options.isDark !== false;

        // Configure marked
        if (typeof marked !== 'undefined') {
            marked.setOptions({ breaks: true, gfm: true });
            marked.use({ renderer: buildRenderer() });
        } else {
            console.warn('[MarkdownRenderer] marked.js not loaded — falling back to basic rendering');
        }

        // Configure Mermaid
        configureMermaid();

        // Load Shiki in the background
        loadShiki().then(function () {
            // Re-apply renderer so subsequent calls pick up Shiki
            if (typeof marked !== 'undefined') {
                marked.use({ renderer: buildRenderer() });
            }
        });
    }

    /**
     * Render markdown text → HTML string.
     * Synchronous. Uses Shiki if already loaded, plain fallback otherwise.
     * @param {string} text
     * @returns {string}
     */
    function render(text) {
        if (!text) return '';

        if (typeof marked !== 'undefined') {
            try {
                return marked.parse(text);
            } catch (e) {
                console.error('[MarkdownRenderer] parse error:', e);
            }
        }

        // Fallback: basic escaping (no markdown library available)
        return fallbackRender(text);
    }

    /**
     * Minimal regex-based fallback when marked.js is unavailable.
     */
    function fallbackRender(text) {
        var html = escapeHtml(text);
        html = html.replace(/```(\w*)\n([\s\S]*?)```/g, function (m, lang, code) {
            return '<pre class="md-code-block"><code>' + code.trimEnd() + '</code></pre>';
        });
        html = html.replace(/`([^`\n]+)`/g, '<code class="md-inline-code">$1</code>');
        html = html.replace(/^### (.+)$/gm, '<h3>$1</h3>');
        html = html.replace(/^## (.+)$/gm, '<h2>$1</h2>');
        html = html.replace(/^# (.+)$/gm, '<h1>$1</h1>');
        html = html.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');
        html = html.replace(/(?<!\*)\*([^*]+)\*(?!\*)/g, '<em>$1</em>');
        html = html.replace(/\n\n/g, '</p><p>');
        html = html.replace(/\n/g, '<br>');
        return '<p>' + html + '</p>';
    }

    /**
     * Post-process a container element: render Mermaid diagrams.
     * Call AFTER inserting rendered HTML into the DOM.
     * @param {HTMLElement} container
     */
    async function postProcess(container) {
        if (!container || typeof mermaid === 'undefined') return;

        var nodes = container.querySelectorAll('.mermaid:not([data-processed])');
        if (nodes.length === 0) return;

        // Save original source before mermaid can mutate the DOM
        var originals = [];
        nodes.forEach(function (n) { originals.push(n.textContent); });

        try {
            await mermaid.run({ nodes: nodes });
        } catch (e) {
            // Mark failed nodes so we don't retry
            nodes.forEach(function (n, i) {
                if (!n.querySelector('svg')) {
                    n.setAttribute('data-processed', 'error');
                    n.classList.add('mermaid-error');
                    n.innerHTML = '<pre class="md-code-block"><code>' + escapeHtml(originals[i]) + '</code></pre>'
                        + '<div class="mermaid-error-msg">Diagram render failed</div>';
                }
            });
            console.warn('[MarkdownRenderer] Mermaid error:', e);
        }
    }

    /**
     * Switch theme. Affects subsequent render() calls and Mermaid diagrams.
     * isDark is a shared mutable variable read by renderer closures at call time.
     * @param {boolean} dark
     */
    function setTheme(dark) {
        isDark = dark;
        configureMermaid();
    }

    /**
     * Whether Shiki is loaded and ready.
     * @returns {boolean}
     */
    function isShikiReady() {
        return shikiHighlighter !== null;
    }

    // ── Export ───────────────────────────────────────────────────

    window.MarkdownRenderer = {
        init: init,
        render: render,
        postProcess: postProcess,
        setTheme: setTheme,
        isShikiReady: isShikiReady,
        escapeHtml: escapeHtml
    };
})();
