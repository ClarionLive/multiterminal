// MultiRemote — Inbox view + unread badge

const INBOX_USER_ID = 'John';
let inboxPollTimer = null;
let lastKnownUnread = 0;
let inboxShowUnreadOnly = false;
let inboxMessages = []; // cached for filtering without re-fetch
let inboxGroupsByKey = {}; // { groupKey: [messages sorted] } — built by deduplicateByTask
let inboxExpandedGroups = new Set(); // groupKeys currently expanded inline
let inboxMessagesById = {}; // { messageId: message } — lookup for detail sheet

// ── Badge ────────────────────────────────────────────────────

function startInboxBadgePoll() {
    updateInboxBadge();
    if (inboxPollTimer) clearInterval(inboxPollTimer);
    inboxPollTimer = setInterval(updateInboxBadge, 30000);
}

async function updateInboxBadge() {
    const data = await api(`/api/inbox/${INBOX_USER_ID}?unreadOnly=true&limit=200`);
    if (!data) return;
    const msgs = (data.messages || data || []).filter(m => !m.isRead);
    // Count deduplicated task groups, not individual notifications
    const count = deduplicateByTask(msgs).length;
    lastKnownUnread = count;
    setInboxBadge(count);
}

function setInboxBadge(count) {
    const badge = document.getElementById('inbox-badge');
    if (!badge) return;
    if (count > 0) {
        badge.textContent = count > 99 ? '99+' : count;
        badge.classList.remove('d-none');
    } else {
        badge.classList.add('d-none');
    }
}

// ── Inbox View ───────────────────────────────────────────────

async function loadInbox() {
    const list = document.getElementById('inbox-list');
    if (!list) return;

    list.innerHTML = '<div class="cr-skeleton"></div><div class="cr-skeleton"></div>';

    const data = await api(`/api/inbox/${INBOX_USER_ID}?limit=50`);
    if (!data) {
        list.innerHTML = '<div class="cr-empty"><i class="bi bi-wifi-off"></i>Could not load inbox</div>';
        return;
    }

    inboxMessages = data.messages || data;
    renderInboxList();
    updateFilterButton();
}

function renderInboxList() {
    const list = document.getElementById('inbox-list');
    if (!list) return;
    attachInboxListeners(list);

    const filtered = inboxShowUnreadOnly
        ? inboxMessages.filter(m => !m.isRead)
        : inboxMessages;

    if (!filtered || filtered.length === 0) {
        const msg = inboxShowUnreadOnly ? 'No unread messages' : 'Inbox is empty';
        list.innerHTML = `<div class="cr-empty"><i class="bi bi-inbox"></i>${msg}</div>`;
        return;
    }

    const grouped = deduplicateByTask(filtered);

    // Rebuild id → message index for detail lookups
    inboxMessagesById = {};
    for (const m of filtered) {
        if (m.id) inboxMessagesById[m.id] = m;
    }

    list.innerHTML = grouped.map(m => {
        const expanded = m._groupKey && inboxExpandedGroups.has(m._groupKey);
        let html = renderInboxMessage(m, { expanded });
        if (expanded && m._groupCount > 1) {
            const siblings = (inboxGroupsByKey[m._groupKey] || []).slice(1);
            html += siblings.map(s => renderInboxMessage(s, { sibling: true })).join('');
        }
        return html;
    }).join('');
}

// Group messages by task — show one card per task with the most significant message
function deduplicateByTask(messages) {
    const groups = {};
    for (const m of messages) {
        const key = m.taskTitle || m.id || 'no-task';
        if (!groups[key]) groups[key] = [];
        groups[key].push(m);
    }

    const typePriority = { task_complete: 0, escalation: 1, permission_request: 2 };
    for (const key of Object.keys(groups)) {
        groups[key].sort((a, b) => {
            const ap = typePriority[a.type || a.notificationType] ?? 10;
            const bp = typePriority[b.type || b.notificationType] ?? 10;
            if (ap !== bp) return ap - bp;
            return new Date(b.createdAt || b.timestamp || 0) - new Date(a.createdAt || a.timestamp || 0);
        });
    }
    inboxGroupsByKey = groups;

    const result = [];
    for (const [key, msgs] of Object.entries(groups)) {
        const best = { ...msgs[0], _groupKey: key, _groupCount: msgs.length, _groupUnread: msgs.filter(m => !m.isRead).length };
        result.push(best);
    }

    result.sort((a, b) => new Date(b.createdAt || b.timestamp || 0) - new Date(a.createdAt || a.timestamp || 0));
    return result;
}

async function markAllInboxRead() {
    await api(`/api/inbox/${INBOX_USER_ID}/read-all`, { method: 'POST' });
    // Update local state
    inboxMessages.forEach(m => m.isRead = true);
    renderInboxList();
    setInboxBadge(0);
    lastKnownUnread = 0;
}

function toggleInboxFilter() {
    inboxShowUnreadOnly = !inboxShowUnreadOnly;
    renderInboxList();
    updateFilterButton();
}

function updateFilterButton() {
    const btn = document.getElementById('inbox-filter-btn');
    if (!btn) return;
    if (inboxShowUnreadOnly) {
        btn.classList.remove('btn-outline-secondary');
        btn.classList.add('btn-primary');
    } else {
        btn.classList.remove('btn-primary');
        btn.classList.add('btn-outline-secondary');
    }
}

function toggleInboxGroup(groupKey, event) {
    if (event) {
        event.preventDefault();
        event.stopPropagation();
    }
    if (inboxExpandedGroups.has(groupKey)) {
        inboxExpandedGroups.delete(groupKey);
    } else {
        inboxExpandedGroups.add(groupKey);
    }
    renderInboxList();
}

function renderInboxMessage(m, opts = {}) {
    const unread = !m.isRead ? 'unread' : '';
    const siblingClass = opts.sibling ? 'inbox-card-sibling' : '';
    const type = m.type || m.notificationType || '';
    const icon = inboxIcon(type);
    const title = escapeHtml(m.title || formatInboxType(type));
    const body = escapeHtml(m.message || m.body || '');
    const from = m.fromAgent || m.agentName || m.createdBy || '';
    const time = timeAgo(m.createdAt || m.timestamp);
    const taskTitle = (m.taskTitle && !opts.sibling) ? `<div class="inbox-task">${escapeHtml(m.taskTitle)}</div>` : '';
    const groupKey = m._groupKey ? escapeAttr(m._groupKey) : '';
    const groupBadge = (!opts.sibling && m._groupCount && m._groupCount > 1)
        ? `<span class="inbox-group-badge${opts.expanded ? ' expanded' : ''}" data-group-key="${groupKey}">
             <i class="bi bi-chevron-${opts.expanded ? 'up' : 'down'}"></i> ${m._groupCount} notifications
           </span>`
        : '';
    const mid = m.id ? escapeAttr(m.id) : '';

    return `
        <div class="cr-card inbox-card ${unread} ${siblingClass}"${mid ? ` data-message-id="${mid}"` : ''}>
            <div class="d-flex align-items-start gap-3">
                <div class="inbox-icon">${icon}</div>
                <div class="flex-grow-1 min-width-0">
                    <div class="inbox-title">${title} ${groupBadge}</div>
                    ${taskTitle}
                    <div class="inbox-body">${body}</div>
                    <div class="inbox-meta">
                        ${from ? `<span class="inbox-agent">${escapeHtml(from)}</span>` : ''}
                        <span>${time}</span>
                    </div>
                </div>
                ${!m.isRead ? '<div class="inbox-unread-dot"></div>' : ''}
            </div>
        </div>`;
}

// One delegated listener per #inbox-list — idempotent
let inboxListenersAttached = false;
function attachInboxListeners(list) {
    if (inboxListenersAttached) return;
    inboxListenersAttached = true;
    list.addEventListener('click', (e) => {
        // Group expand/collapse takes priority
        const badge = e.target.closest('.inbox-group-badge');
        if (badge) {
            e.preventDefault();
            e.stopPropagation();
            const key = badge.getAttribute('data-group-key');
            if (key) toggleInboxGroup(key);
            return;
        }
        // Card tap → detail sheet
        const card = e.target.closest('.inbox-card');
        if (card) {
            const mid = card.getAttribute('data-message-id');
            if (mid) showInboxDetail(mid);
        }
    });
}

function escapeAttr(v) {
    return String(v == null ? '' : v)
        .replace(/&/g, '&amp;')
        .replace(/'/g, '&#39;')
        .replace(/"/g, '&quot;');
}

// ── Detail sheet ─────────────────────────────────────────────

async function showInboxDetail(messageId) {
    const m = inboxMessagesById[messageId];
    if (!m) return;

    // Mark read in background (don't await — don't block UI)
    if (!m.isRead && m.id) {
        api(`/api/inbox/${encodeURIComponent(m.id)}/read`, { method: 'POST' });
        m.isRead = true;
        renderInboxList();
        // Refresh badge count
        updateInboxBadge();
    }

    const type = m.type || m.notificationType || '';
    const icon = inboxIcon(type);
    const title = escapeHtml(m.title || formatInboxType(type));
    const body = escapeHtml(m.message || m.body || '(no message body)');
    const from = m.fromAgent || m.agentName || m.createdBy || '';
    const timeStr = new Date(m.createdAt || m.timestamp || Date.now()).toLocaleString();
    const taskTitle = m.taskTitle || '';

    // Sibling notifications in the same group
    const key = m._groupKey || m.taskTitle || m.id || 'no-task';
    const siblings = (inboxGroupsByKey[key] || []).filter(s => s.id !== m.id);
    const siblingsHtml = siblings.length > 0
        ? `<div class="task-detail-section">
            <div class="task-detail-heading">Other notifications in this task <span class="task-detail-count">${siblings.length}</span></div>
            <ul class="inbox-sibling-list">
                ${siblings.map(s => {
                    const sType = s.type || s.notificationType || '';
                    const sTime = timeAgo(s.createdAt || s.timestamp);
                    const sBody = escapeHtml(s.message || s.body || '');
                    const sTitle = escapeHtml(s.title || formatInboxType(sType));
                    const sUnread = !s.isRead ? 'unread' : '';
                    const sid = s.id ? escapeAttr(s.id) : '';
                    return `<li class="inbox-sibling-item ${sUnread}"${sid ? ` data-sibling-id="${sid}"` : ''}>
                        <div class="inbox-sibling-icon">${inboxIcon(sType)}</div>
                        <div class="inbox-sibling-content">
                            <div class="inbox-sibling-title">${sTitle}</div>
                            <div class="inbox-sibling-body">${sBody}</div>
                            <div class="inbox-sibling-time">${sTime}</div>
                        </div>
                    </li>`;
                }).join('')}
            </ul>
        </div>`
        : '';

    const modal = document.createElement('div');
    modal.className = 'position-fixed top-0 start-0 w-100 h-100';
    modal.style.cssText = 'z-index:200; background:rgba(0,0,0,0.6);';
    modal.onclick = (e) => { if (e.target === modal) modal.remove(); };

    modal.innerHTML = `
        <div class="task-detail-modal" id="inbox-detail-sheet">
            <div class="task-sheet-handle" id="inbox-sheet-handle"><div class="task-sheet-bar"></div></div>
            <div class="task-detail-header">
                <h5 class="fw-bold mb-0 d-flex align-items-start gap-2">
                    <span class="inbox-detail-icon">${icon}</span>
                    <span>${title}</span>
                </h5>
            </div>
            <div class="task-detail-meta">
                <span class="inbox-type-chip">${escapeHtml(formatInboxType(type))}</span>
                ${from ? `<span><i class="bi bi-person"></i> ${escapeHtml(from)}</span>` : ''}
                <span><i class="bi bi-clock"></i> ${escapeHtml(timeStr)}</span>
            </div>
            ${taskTitle ? `<div class="task-detail-section">
                <div class="task-detail-heading">Task</div>
                <p class="task-detail-text"><i class="bi bi-kanban"></i> ${escapeHtml(taskTitle)}</p>
            </div>` : ''}
            <div class="task-detail-section">
                <div class="task-detail-heading">Message</div>
                <p class="task-detail-text inbox-detail-body">${body}</p>
            </div>
            ${siblingsHtml}
        </div>`;
    document.body.appendChild(modal);

    // Prevent background scroll while sheet is open
    document.body.style.overflow = 'hidden';
    modal.addEventListener('touchmove', (e) => {
        const sheet = modal.querySelector('.task-detail-modal');
        if (sheet && !sheet.contains(e.target)) e.preventDefault();
    }, { passive: false });

    const observer = new MutationObserver(() => {
        if (!document.body.contains(modal)) {
            document.body.style.overflow = '';
            observer.disconnect();
        }
    });
    observer.observe(document.body, { childList: true });

    // Reuse the swipe-to-dismiss from tasks.js
    if (typeof setupSheetDismiss === 'function') {
        setupSheetDismiss(modal);
    }

    // Sibling click → swap sheet to that message's detail
    modal.querySelectorAll('.inbox-sibling-item').forEach(el => {
        el.addEventListener('click', () => {
            const sid = el.getAttribute('data-sibling-id');
            if (!sid) return;
            modal.remove();
            showInboxDetail(sid);
        });
    });
}

function formatInboxType(type) {
    const labels = {
        'ready_for_testing': 'Ready for Testing',
        'task_complete': 'Task Complete',
        'escalation': 'Escalation',
        'helper_request': 'Helper Request',
        'agent_stopped': 'Agent Stopped',
        'permission_request': 'Permission Request',
        'message': 'Message',
        'inbox': 'Inbox'
    };
    return labels[type] || (type || 'Message').replace(/_/g, ' ').replace(/\b\w/g, c => c.toUpperCase());
}

function inboxIcon(type) {
    const icons = {
        'ready_for_testing': '<i class="bi bi-clipboard-check" style="color:var(--cr-warning)"></i>',
        'escalation': '<i class="bi bi-exclamation-triangle-fill" style="color:var(--cr-danger)"></i>',
        'task_complete': '<i class="bi bi-check-circle-fill" style="color:var(--cr-success)"></i>',
        'helper_request': '<i class="bi bi-person-raised-hand" style="color:var(--cr-info)"></i>',
        'agent_stopped': '<i class="bi bi-stop-circle-fill" style="color:var(--cr-text-muted)"></i>',
        'permission_request': '<i class="bi bi-shield-lock-fill" style="color:var(--cr-warning)"></i>',
        'message': '<i class="bi bi-envelope-fill" style="color:var(--cr-primary)"></i>',
        'inbox': '<i class="bi bi-inbox-fill" style="color:var(--cr-primary)"></i>'
    };
    return icons[type] || '<i class="bi bi-inbox-fill" style="color:var(--cr-primary)"></i>';
}
