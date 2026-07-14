// MultiRemote — Notifications view

let notificationsLoaded = false;

// ── Alerts badge ─────────────────────────────────────────────
// Home-tile arrival indicator for the Alerts (notifications) view. Counts unread
// notification_events; clears when the owner opens the Alerts view (markNotificationsSeen).
// Mirrors the inbox badge pattern in inbox.js. Note: notification history is only
// persisted in remote-mode / forcePush / persistLocal (task 7da88ea0 item 4), so this
// badge lights up for exactly the remote pushes the owner needs to notice.
let notificationsBadgePollTimer = null;

function startNotificationsBadgePoll() {
    updateNotificationsBadge();
    if (notificationsBadgePollTimer) clearInterval(notificationsBadgePollTimer);
    notificationsBadgePollTimer = setInterval(updateNotificationsBadge, 30000);
}

async function updateNotificationsBadge() {
    const data = await api('/api/notifications/unread-count');
    if (!data) return;
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

// Marks all alerts read so the badge clears. Called when the owner opens the Alerts
// view (an explicit "I've seen these" gesture) — NOT from the background push handler,
// which must not silently clear an unseen count.
async function markNotificationsSeen() {
    await api('/api/notifications/read-all', { method: 'POST' });
    setNotificationsBadge(0);
}

async function loadNotifications() {
    const list = document.getElementById('notifications-list');
    if (!list) return;

    const data = await api('/api/notifications?limit=50');
    if (!data) {
        list.innerHTML = '<div class="cr-empty"><i class="bi bi-wifi-off"></i>Could not load notifications</div>';
        return;
    }

    if (data.length === 0) {
        list.innerHTML = '<div class="cr-empty"><i class="bi bi-bell"></i>No notifications yet</div>';
        return;
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
