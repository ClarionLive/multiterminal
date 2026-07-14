// MultiRemote — Notifications view

let notificationsLoaded = false;

// ── Alerts badge ─────────────────────────────────────────────
// Home-tile arrival indicator for the Alerts (notifications) view. The unread count
// comes from the GATEWAY's notification history + a lastSeenAt watermark — the gateway
// mounts a controller whitelist, so these are gateway minimal-API routes, not
// NotificationsController. Mark-seen advances the watermark only to the newest entry
// this client actually RENDERED while VISIBLE (pipeline Run-1: a backgrounded tab must
// never clear arrivals it didn't display, and the badge is only cleared after the
// server confirms the write).
let notificationsBadgePollTimer = null;
let notificationsNewestRenderedAt = null; // newest receivedAt this client has rendered
let notificationsBadgeEpoch = 0;          // bumped on mark-seen; stale in-flight polls discard

function startNotificationsBadgePoll() {
    updateNotificationsBadge();
    if (notificationsBadgePollTimer) clearInterval(notificationsBadgePollTimer);
    notificationsBadgePollTimer = setInterval(updateNotificationsBadge, 30000);
}

async function updateNotificationsBadge() {
    const epoch = notificationsBadgeEpoch;
    const data = await api('/api/notifications/unread-count');
    if (!data) return;
    // A mark-seen committed while this GET was in flight — its count is stale.
    if (epoch !== notificationsBadgeEpoch) return;
    // While the owner is visibly looking at the Alerts list, the home tile is hidden
    // anyway — suppress the badge rather than "unread while reading".
    if (currentView === 'notifications' && document.visibilityState === 'visible') {
        setNotificationsBadge(0);
        return;
    }
    setNotificationsBadge(data.count || 0);
}

function setNotificationsBadge(count) {
    const badge = document.getElementById('notifications-badge');
    if (!badge) return;
    if (count > 0) {
        badge.textContent = count > 99 ? '99+' : count;
        badge.classList.remove('d-none');
    } else {
        badge.classList.add('d-none');
    }
}

// Advances the seen-watermark to the newest entry this client has rendered, and clears
// the badge ONLY once the server confirms. Never a blanket mark-all: entries that
// arrived after the rendered snapshot stay unread.
async function markNotificationsSeen() {
    if (!notificationsNewestRenderedAt) return; // nothing rendered yet — nothing to mark
    const res = await api('/api/notifications/read-all', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ seenThrough: notificationsNewestRenderedAt })
    });
    if (res) {
        notificationsBadgeEpoch++;
        setNotificationsBadge(0);
    } else {
        // Write failed — do NOT pretend the alerts were acknowledged; re-sync from server.
        updateNotificationsBadge();
    }
}

// The one sanctioned "owner saw the alerts" path: render the list, then — only if the
// page is actually visible — mark what was rendered as seen. Called on view open, on
// push arrival while visibly viewing, and on returning visibility to an open view.
async function loadNotificationsAndMarkSeen() {
    const loaded = await loadNotifications();
    if (loaded && document.visibilityState === 'visible') {
        await markNotificationsSeen();
    }
}

async function loadNotifications() {
    const list = document.getElementById('notifications-list');
    if (!list) return null;

    const data = await api('/api/notifications?limit=50');
    if (!data) {
        list.innerHTML = '<div class="cr-empty"><i class="bi bi-wifi-off"></i>Could not load notifications</div>';
        return null;
    }

    // History is newest-first; remember the newest entry this client has rendered —
    // it's the seen-watermark markNotificationsSeen() sends.
    if (data.length > 0 && data[0].receivedAt) {
        notificationsNewestRenderedAt = data[0].receivedAt;
    }

    if (data.length === 0) {
        list.innerHTML = '<div class="cr-empty"><i class="bi bi-bell"></i>No notifications yet</div>';
        return data;
    }

    list.innerHTML = data.map(n => `
        <div class="cr-card notification-card" data-type="${escapeHtml(n.notificationType)}">
            <div class="d-flex align-items-start gap-3">
                <div class="notification-icon">${notificationIcon(n.notificationType)}</div>
                <div class="flex-grow-1 min-width-0">
                    <div class="notification-title">${escapeHtml(n.title)}</div>
                    <div class="notification-body">${escapeHtml(n.body)}</div>
                    <div class="notification-meta">
                        ${n.agentName ? `<span class="notification-agent">${escapeHtml(n.agentName)}</span>` : ''}
                        <span>${timeAgo(n.receivedAt)}</span>
                    </div>
                </div>
            </div>
        </div>
    `).join('');

    notificationsLoaded = true;
    return data;
}

function notificationIcon(type) {
    const icons = {
        'task_complete': '<i class="bi bi-check-circle-fill" style="color:var(--cr-success)"></i>',
        'ready_for_testing': '<i class="bi bi-clipboard-check" style="color:var(--cr-warning)"></i>',
        'escalation': '<i class="bi bi-exclamation-triangle-fill" style="color:var(--cr-danger)"></i>',
        'helper_request': '<i class="bi bi-person-raised-hand" style="color:var(--cr-info)"></i>',
        'agent_stopped': '<i class="bi bi-stop-circle-fill" style="color:var(--cr-text-muted)"></i>',
        'permission_request': '<i class="bi bi-shield-lock-fill" style="color:var(--cr-warning)"></i>',
        'error': '<i class="bi bi-x-octagon-fill" style="color:var(--cr-danger)"></i>'
    };
    return icons[type] || '<i class="bi bi-bell-fill" style="color:var(--cr-primary)"></i>';
}

// Settings view
async function loadNotificationSettings() {
    const container = document.getElementById('notification-settings');
    if (!container) return;

    const data = await api('/api/notifications/settings');
    if (!data) return;

    container.innerHTML = data.map(s => `
        <div class="notification-toggle-row">
            <span class="notification-toggle-label">${escapeHtml(s.label)}</span>
            <div class="form-check form-switch">
                <input class="form-check-input" type="checkbox" role="switch"
                       ${s.enabled ? 'checked' : ''}
                       onchange="toggleNotificationType('${s.type}', this.checked)">
            </div>
        </div>
    `).join('');
}

async function toggleNotificationType(type, enabled) {
    await api('/api/notifications/settings', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ type, enabled })
    });
}

async function clearNotificationHistory() {
    if (!confirm('Clear all notification history?')) return;
    await api('/api/notifications', { method: 'DELETE' });
    loadNotifications();
}

function toggleNotificationSettings() {
    const panel = document.getElementById('notification-settings-panel');
    if (!panel) return;
    const hidden = panel.classList.contains('d-none');
    panel.classList.toggle('d-none');
    if (hidden) loadNotificationSettings();
}

// escapeHtml is defined in app.js (loaded first)
