// ── News Feed View ──────────────────────────────────────────────────────────
// Renders the daily AI/Claude ecosystem digest as a news feed.

let newsLoaded = false;
let currentDigestDate = null;
let newsRegenerating = false;
let newsItemData = new Map();
let newsItemCounter = 0;
let unfurlCache = new Map();
let digestSources = [];

async function loadNews(forceRefresh = false) {
    const container = document.getElementById('news-content');
    if (!container) return;

    if (newsLoaded && !forceRefresh) return;

    // On manual refresh, spin the button and fade content; on first load, show skeletons
    const refreshBtn = container.querySelector('.news-refresh-btn');
    if (forceRefresh && refreshBtn) {
        refreshBtn.classList.add('spinning');
        container.style.opacity = '0.4';
        container.style.transition = 'opacity 0.2s';
    } else {
        container.innerHTML = '<div class="cr-skeleton"></div><div class="cr-skeleton"></div><div class="cr-skeleton"></div>';
    }

    try {
        const data = await api('/api/digest/latest');
        if (!data || !data.digest) {
            container.style.opacity = '1';
            container.innerHTML = `
                <div class="cr-empty">
                    <i class="bi bi-newspaper"></i>
                    <p>No digest available yet today.<br>Tap regenerate to create one.</p>
                </div>`;
            return;
        }

        // Preserve scroll position on refresh
        const scrollParent = container.closest('.cr-content') || container.parentElement;
        const scrollTop = forceRefresh && scrollParent ? scrollParent.scrollTop : 0;

        currentDigestDate = data.date;
        const html = renderDigest(data);
        container.innerHTML = html;
        newsLoaded = true;
        container.style.opacity = '1';

        // Restore scroll position after refresh
        if (forceRefresh && scrollParent) {
            requestAnimationFrame(() => { scrollParent.scrollTop = scrollTop; });
        }
    } catch (err) {
        container.style.opacity = '1';
        if (refreshBtn) {
            refreshBtn.classList.remove('spinning');
        } else {
            container.innerHTML = `
                <div class="cr-empty">
                    <i class="bi bi-exclamation-triangle"></i>
                    <p>Failed to load digest</p>
                </div>`;
        }
    }
}

async function regenerateDigest() {
    if (newsRegenerating) return;
    newsRegenerating = true;

    const container = document.getElementById('news-content');
    if (!container) return;

    const btn = container.querySelector('.news-refresh-btn');
    const statusEl = container.querySelector('.news-regen-status');

    if (btn) {
        btn.classList.add('spinning');
        btn.disabled = true;
    }
    if (statusEl) {
        statusEl.textContent = 'Fetching latest data...';
        statusEl.style.display = 'block';
    }

    try {
        const data = await api('/api/digest/regenerate', { method: 'POST' });

        if (!data || !data.digest) {
            if (statusEl) {
                statusEl.textContent = 'Regeneration returned no data';
                setTimeout(() => { statusEl.style.display = 'none'; }, 3000);
            }
            return;
        }

        // Render the fresh digest
        currentDigestDate = data.date;
        const html = renderDigest(data);
        container.innerHTML = html;
        newsLoaded = true;

        // Scroll to top to see fresh content
        const scrollParent = container.closest('.cr-content') || container.parentElement;
        if (scrollParent) scrollParent.scrollTop = 0;
    } catch (err) {
        if (statusEl) {
            statusEl.textContent = 'Regeneration failed — try again later';
            setTimeout(() => { statusEl.style.display = 'none'; }, 5000);
        }
        if (btn) btn.classList.remove('spinning');
    } finally {
        newsRegenerating = false;
        if (btn) {
            btn.classList.remove('spinning');
            btn.disabled = false;
        }
    }
}

function renderDigest(data) {
    const md = data.digest;
    const sections = parseDigestSections(md);
    let html = '';
    newsItemData.clear();
    newsItemCounter = 0;
    digestSources = data.sources || [];

    // Header with date, refresh button, and stats
    const genTime = data.generated_at ? new Date(data.generated_at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : '';
    html += `<div class="news-header">
        <div class="news-header-top">
            <div class="news-date">${formatNewsDate(data.date)}</div>
            <button class="news-refresh-btn" onclick="regenerateDigest()" title="Regenerate digest with fresh data">
                <i class="bi bi-arrow-clockwise"></i>
            </button>
        </div>`;
    if (data.stats) {
        const s = data.stats;
        html += `<div class="news-stats">
            ${s.github_repos} repos · ${s.reddit_subs} subs · ${s.hn_stories} HN stories${genTime ? ` · updated ${genTime}` : ''}
        </div>`;
    }
    html += `<div class="news-regen-status" style="display:none"></div>`;
    html += `</div>`;

    // Headlines as hero cards (tap to expand)
    if (sections.headlines) {
        html += '<div class="news-section">';
        const items = parseListItems(sections.headlines);
        items.forEach((item, i) => {
            const { title, body } = splitHeadlineItem(item);
            const itemId = `news-item-${newsItemCounter++}`;
            newsItemData.set(itemId, { title, body: body || title });
            html += `<div class="cr-card news-headline-card" id="${itemId}" onclick="toggleNewsExpand(this)">
                <div class="news-headline-rank">${i + 1}</div>
                <div class="news-headline-content">
                    <div class="news-headline-title">${escapeHtml(title)}</div>
                    ${body ? `<div class="news-headline-body">${linkify(escapeHtml(body))}</div>` : ''}
                    <div class="news-item-actions">
                        <button class="news-create-task-btn" onclick="event.stopPropagation(); createTaskFromNews(this)">
                            <i class="bi bi-plus-circle"></i> Create Task
                        </button>
                    </div>
                </div>
            </div>`;
        });
        html += '</div>';
    }

    // Remaining sections as collapsible cards
    const sectionOrder = ['Claude Code & MCP', 'Community Highlights', 'New Tools & Repos', 'Competitor Watch', 'Action Items'];
    const sectionIcons = {
        'Claude Code & MCP': 'bi-code-slash',
        'Community Highlights': 'bi-people',
        'New Tools & Repos': 'bi-box-seam',
        'Competitor Watch': 'bi-binoculars',
        'Action Items': 'bi-lightning-charge',
    };

    sectionOrder.forEach(name => {
        const content = sections[name];
        if (!content || !content.trim()) return;

        const icon = sectionIcons[name] || 'bi-card-text';
        const id = name.replace(/[^a-zA-Z]/g, '').toLowerCase();

        html += `<div class="news-section">
            <div class="news-section-header" onclick="toggleNewsSection('${id}')">
                <i class="bi ${icon}"></i>
                <span>${escapeHtml(name)}</span>
                <i class="bi bi-chevron-down news-chevron" id="chevron-${id}"></i>
            </div>
            <div class="news-section-body" id="section-${id}">
                ${renderMarkdownLite(content)}
            </div>
        </div>`;
    });

    return html;
}

function toggleNewsSection(id) {
    const body = document.getElementById(`section-${id}`);
    const chevron = document.getElementById(`chevron-${id}`);
    if (!body) return;
    body.classList.toggle('collapsed');
    if (chevron) chevron.classList.toggle('rotated');
}

// ── Markdown-lite renderer ──────────────────────────────────────────────────
// Converts simple markdown to HTML (headers, bold, links, lists, code)

function renderMarkdownLite(md) {
    return md
        .split('\n')
        .map(line => {
            // Headers
            if (line.startsWith('### ')) return `<h4 class="news-h4">${escapeHtml(line.slice(4))}</h4>`;
            if (line.startsWith('## ')) return `<h3 class="news-h3">${escapeHtml(line.slice(3))}</h3>`;
            // List items (tap to expand)
            if (line.startsWith('- **') || line.startsWith('- ')) {
                const raw = line.startsWith('- **') ? line.slice(2) : line.slice(2);
                const itemId = `news-item-${newsItemCounter++}`;
                // Extract bold text as task title, rest as description
                const boldMatch = raw.match(/^\*\*(.+?)\*\*\s*[—–-]?\s*(.*)/s);
                const taskTitle = boldMatch ? boldMatch[1] : raw.replace(/\*\*/g, '').slice(0, 80);
                const taskDesc = boldMatch ? (boldMatch[2] || raw) : raw;
                newsItemData.set(itemId, { title: taskTitle, body: taskDesc });
                return `<div class="news-list-item" id="${itemId}" onclick="toggleNewsExpand(this)">
                    <div class="news-list-text">${boldAndLink(raw)}</div>
                    <div class="news-item-actions">
                        <button class="news-create-task-btn" onclick="event.stopPropagation(); createTaskFromNews(this)">
                            <i class="bi bi-plus-circle"></i> Create Task
                        </button>
                    </div>
                </div>`;
            }
            // Blank lines
            if (!line.trim()) return '';
            // Regular text
            return `<p class="news-para">${boldAndLink(line)}</p>`;
        })
        .join('\n');
}

function boldAndLink(text) {
    // Escape first, then apply formatting
    let out = escapeHtml(text);
    // Bold: **text**
    out = out.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');
    // Inline code: `text`
    out = out.replace(/`(.+?)`/g, '<code>$1</code>');
    // Links: [text](url)
    out = out.replace(/\[([^\]]+)\]\(([^)]+)\)/g, '<a href="$2" target="_blank" rel="noopener">$1</a>');
    return out;
}

function linkify(text) {
    return text.replace(/(https?:\/\/[^\s)<]+)/g, '<a href="$1" target="_blank" rel="noopener">link</a>');
}

// ── Parsing helpers ─────────────────────────────────────────────────────────

function parseDigestSections(md) {
    const sections = {};
    let currentSection = null;
    const lines = md.split('\n');

    for (const line of lines) {
        if (line.startsWith('## ')) {
            currentSection = line.slice(3).trim();
            sections[currentSection] = '';
        } else if (currentSection) {
            // Skip the --- dividers
            if (line.trim() === '---') continue;
            sections[currentSection] += line + '\n';
        }
    }

    // Special case: headlines
    if (sections['Headlines']) {
        sections.headlines = sections['Headlines'];
    }

    return sections;
}

function parseListItems(text) {
    return text.split('\n')
        .filter(l => l.startsWith('- '))
        .map(l => l.slice(2));
}

function splitHeadlineItem(text) {
    // Headlines format: **Bold title** — description (links)
    const match = text.match(/^\*\*(.+?)\*\*\s*[—–-]\s*(.*)/s);
    if (match) return { title: match[1], body: match[2] };
    // Fallback: just bold text
    const boldMatch = text.match(/^\*\*(.+?)\*\*(.*)/s);
    if (boldMatch) return { title: boldMatch[1], body: boldMatch[2] };
    return { title: text, body: '' };
}

function formatNewsDate(dateStr) {
    const d = new Date(dateStr + 'T12:00:00');
    return d.toLocaleDateString('en-US', { weekday: 'long', month: 'long', day: 'numeric', year: 'numeric' });
}

// ── Tap to expand ───────────────────────────────────────────────────────────

function toggleNewsExpand(el) {
    // Single-expand: collapse any other expanded item first
    const current = document.querySelector('.news-headline-card.expanded, .news-list-item.expanded');
    if (current && current !== el) {
        current.classList.remove('expanded');
    }
    el.classList.toggle('expanded');

    // Show matching sources on expand
    if (el.classList.contains('expanded')) {
        showSources(el);
    }
}

function showSources(el) {
    const actions = el.querySelector('.news-item-actions');
    if (!actions || actions.querySelector('.news-sources')) return;

    const data = newsItemData.get(el.id);
    if (!data) return;

    // Find sources matching this headline by keyword overlap
    const matches = findMatchingSources(data.title, data.body);
    if (matches.length === 0) return;

    const container = document.createElement('div');
    container.className = 'news-sources';
    container.innerHTML = `<div class="news-sources-label">Sources</div>` +
        matches.map(s => {
            const icon = sourceIcon(s.type);
            const meta = s.repo ? escapeHtml(s.repo) : escapeHtml(s.type);
            const scoreText = s.score > 0 ? ` · ${s.score} ${s.type === 'reddit' ? 'pts' : s.type === 'hn' ? 'pts' : 'reactions'}` : '';
            return `<div class="news-source-card" onclick="event.stopPropagation(); unfurlSource(this, '${escapeHtml(s.url)}')">
                <div class="news-source-icon">${icon}</div>
                <div class="news-source-text">
                    <div class="news-source-title">${escapeHtml(s.title)}</div>
                    <div class="news-source-meta">${meta}${scoreText}</div>
                </div>
                <i class="bi bi-chevron-right news-source-arrow"></i>
            </div>`;
        }).join('');
    actions.insertBefore(container, actions.firstChild);
}

function findMatchingSources(title, body) {
    if (!digestSources.length) return [];

    // Build keyword set from title + body (words 3+ chars, lowercased)
    const text = ((title || '') + ' ' + (body || '')).toLowerCase();
    const keywords = text.match(/[a-z0-9#]+/g)?.filter(w => w.length >= 3) || [];
    if (keywords.length === 0) return [];

    // Score each source by keyword overlap
    const scored = digestSources
        .filter(s => s.url && s.title)
        .map(s => {
            const srcText = (s.title || '').toLowerCase();
            const hits = keywords.filter(kw => srcText.includes(kw)).length;
            return { ...s, hits };
        })
        .filter(s => s.hits >= 2)
        .sort((a, b) => b.hits - a.hits || b.score - a.score);

    return scored.slice(0, 5);
}

function sourceIcon(type) {
    switch (type) {
        case 'github': return '<i class="bi bi-github"></i>';
        case 'reddit': return '<i class="bi bi-reddit"></i>';
        case 'hn': return '<i class="bi bi-newspaper"></i>';
        case 'web': return '<i class="bi bi-globe"></i>';
        default: return '<i class="bi bi-link-45deg"></i>';
    }
}

async function unfurlSource(card, url) {
    // If already unfurled, toggle visibility
    const existing = card.querySelector('.news-unfurl');
    if (existing) {
        existing.remove();
        return;
    }

    // Check cache
    if (unfurlCache.has(url)) {
        renderUnfurl(card, unfurlCache.get(url));
        return;
    }

    // Show loading
    const loader = document.createElement('div');
    loader.className = 'news-unfurl loading';
    loader.innerHTML = '<i class="bi bi-hourglass-split"></i> Loading...';
    card.appendChild(loader);

    try {
        const result = await api('/api/unfurl?url=' + encodeURIComponent(url));
        loader.remove();
        if (result && !result.error && (result.title || result.description)) {
            unfurlCache.set(url, result);
            renderUnfurl(card, result);
        }
    } catch {
        loader.remove();
    }
}

function renderUnfurl(parent, data) {
    const existing = parent.querySelector('.news-unfurl');
    if (existing) existing.remove();

    const card = document.createElement('div');
    card.className = 'news-unfurl';
    card.onclick = (e) => e.stopPropagation();
    card.innerHTML = `
        ${data.image ? `<img class="news-unfurl-img" src="${escapeHtml(data.image)}" alt="" onerror="this.style.display='none'">` : ''}
        <div class="news-unfurl-text">
            <div class="news-unfurl-site">${escapeHtml(data.siteName || '')}</div>
            ${data.title ? `<div class="news-unfurl-title">${escapeHtml(data.title)}</div>` : ''}
            ${data.description ? `<div class="news-unfurl-desc">${escapeHtml(data.description)}</div>` : ''}
        </div>
        <a class="news-unfurl-link" href="${escapeHtml(data.url)}" target="_blank" rel="noopener">
            <i class="bi bi-box-arrow-up-right"></i> Open
        </a>`;
    parent.appendChild(card);
}

// ── Create Task from news item ──────────────────────────────────────────────

async function createTaskFromNews(btn) {
    if (btn.disabled) return;
    btn.disabled = true;

    const item = btn.closest('.news-headline-card, .news-list-item');
    if (!item) return;

    const data = newsItemData.get(item.id);
    if (!data) return;

    const origHtml = btn.innerHTML;
    btn.innerHTML = '<i class="bi bi-hourglass-split"></i> Creating...';

    try {
        const result = await api('/api/tasks', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                title: '[Digest] ' + data.title,
                description: data.body,
                createdBy: 'MultiRemote',
                status: 'suggestion'
            })
        });

        if (result) {
            btn.innerHTML = '<i class="bi bi-check-circle-fill"></i> Task Created';
            btn.classList.add('success');
            showNewsToast('Task created as suggestion');
        } else {
            btn.innerHTML = '<i class="bi bi-x-circle"></i> Failed';
            setTimeout(() => { btn.innerHTML = origHtml; btn.disabled = false; }, 2000);
        }
    } catch {
        btn.innerHTML = '<i class="bi bi-x-circle"></i> Failed';
        setTimeout(() => { btn.innerHTML = origHtml; btn.disabled = false; }, 2000);
    }
}

function showNewsToast(message) {
    let toast = document.getElementById('news-toast');
    if (!toast) {
        toast = document.createElement('div');
        toast.id = 'news-toast';
        toast.className = 'news-toast';
        document.body.appendChild(toast);
    }
    toast.textContent = message;
    toast.classList.add('visible');
    setTimeout(() => toast.classList.remove('visible'), 2500);
}
