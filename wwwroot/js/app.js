// MultiRemote — Main app logic

// Check auth on load
(async function checkAuth() {
    try {
        const res = await fetch('/api/auth/status');
        const data = await res.json();
        if (!data.authenticated) {
            window.location.href = '/login.html';
            return;
        }
        init();
    } catch {
        window.location.href = '/login.html';
    }
})();

function init() {
    setupNavigation();
    loadTasks();
    loadTerminals();
    setupMessageForm();
    requestNotificationPermission();
    initRemoteMode();
    restoreFontSize();
    if (typeof startInboxBadgePoll === 'function') startInboxBadgePoll();

    // Deep-link: open the view specified in ?view= query param (e.g. from notification tap)
    const params = new URLSearchParams(window.location.search);
    const startView = params.get('view');
    if (startView) {
        switchView(startView, params.get('agent'));
        history.replaceState(null, '', '/');
    }

    // Auto-refresh every 30 seconds
    setInterval(() => refreshCurrentView(), 30000);

    // Instant refresh when a push notification arrives
    if ('serviceWorker' in navigator) {
        navigator.serviceWorker.addEventListener('message', (event) => {
            if (event.data?.type === 'push-received') {
                if (typeof loadNotifications === 'function') loadNotifications();
                if (typeof updateInboxBadge === 'function') updateInboxBadge();
            }
            if (event.data?.type === 'switch-view' && event.data.view) {
                switchView(event.data.view, event.data.agent_name);
            }
        });
    }
}

// Browser notifications
async function requestNotificationPermission() {
    const banner = document.getElementById('push-permission-banner');
    if ('Notification' in window && Notification.permission === 'granted') {
        if (banner) banner.classList.add('d-none');
        // Register service worker and subscribe to push
        if ('serviceWorker' in navigator) {
            try {
                await navigator.serviceWorker.register('/sw.js');
                await subscribePush();
            } catch (err) {
                console.log('SW registration failed:', err.message);
            }
        }
    } else {
        // Show banner — user needs to tap it (iOS requires user gesture)
        if (banner) banner.classList.remove('d-none');
    }
}

async function enableNotifications() {
    const banner = document.getElementById('push-permission-banner');

    if (!('Notification' in window)) {
        alert('Add this app to your Home Screen first, then tap the bell again.');
        return;
    }

    try {
        // Register service worker first (required before push subscribe)
        if ('serviceWorker' in navigator) {
            await navigator.serviceWorker.register('/sw.js');
        }
        const permission = await Notification.requestPermission();
        if (permission !== 'granted') {
            alert('Notifications blocked. Check Settings → Safari → Notifications, or re-add to Home Screen.');
            return;
        }
        await subscribePush();
        if (banner) banner.classList.add('d-none');
    } catch (err) {
        alert('Could not enable notifications: ' + err.message);
    }
}

async function subscribePush() {
    if (!('serviceWorker' in navigator)) return;
    try {
        const reg = await navigator.serviceWorker.ready;
        const keyRes = await fetch('/api/push/key');
        const { publicKey } = await keyRes.json();
        if (!publicKey) return;

        let sub = await reg.pushManager.getSubscription();
        if (!sub) {
            const vapidKey = urlBase64ToUint8Array(publicKey);
            sub = await reg.pushManager.subscribe({
                userVisibleOnly: true,
                applicationServerKey: vapidKey
            });
        }

        const subJson = sub.toJSON();
        await fetch('/api/push/subscribe', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                endpoint: sub.endpoint,
                p256dh: subJson.keys.p256dh,
                auth: subJson.keys.auth,
                deviceId: getDeviceId()
            })
        });
        console.log('Push subscription active');
    } catch (err) {
        console.log('Push subscribe failed:', err.message);
    }
}

// Stable per-install identifier so the server can replace this device's prior (ghost)
// subscription on re-subscribe instead of accumulating dead ones (item [11], Finding 3).
// Persisted in localStorage; survives push re-subscription (which mints a fresh endpoint).
function getDeviceId() {
    try {
        let id = localStorage.getItem('cr-device-id');
        if (!id) {
            if (crypto && crypto.randomUUID) {
                id = crypto.randomUUID();
            } else if (crypto && crypto.getRandomValues) {
                const buf = new Uint8Array(16);
                crypto.getRandomValues(buf);
                id = 'dev-' + Array.from(buf, b => b.toString(16).padStart(2, '0')).join('');
            } else {
                id = 'dev-' + Date.now() + '-' + Math.random().toString(36).slice(2, 10);
            }
            localStorage.setItem('cr-device-id', id);
        }
        return id;
    } catch (_e) {
        return '';
    }
}

function urlBase64ToUint8Array(base64String) {
    const padding = '='.repeat((4 - base64String.length % 4) % 4);
    const base64 = (base64String + padding).replace(/-/g, '+').replace(/_/g, '/');
    const raw = atob(base64);
    const arr = new Uint8Array(raw.length);
    for (let i = 0; i < raw.length; i++) arr[i] = raw.charCodeAt(i);
    return arr;
}

function showNotification(title, body) {
    if ('Notification' in window && Notification.permission === 'granted') {
        const n = new Notification(title, {
            body: body,
            icon: '/img/icon-192.png',
            badge: '/img/icon-192.png',
            vibrate: [200, 100, 200],
            tag: 'cr-' + Date.now(),
            renotify: true
        });
        n.onclick = () => {
            window.focus();
            switchView('messages');
            n.close();
        };
    }
}

// Navigation
function setupNavigation() {
    document.querySelectorAll('.cr-bottomnav a').forEach(link => {
        link.addEventListener('click', (e) => {
            e.preventDefault();
            const view = link.dataset.view;
            switchView(view);
        });
    });
}

let currentView = 'home';

// Views that have their own bottom nav button
const NAV_VIEWS = new Set(['home', 'tasks', 'messages', 'console']);

function switchView(view, agentName) {
    currentView = view;
    const viewEl = document.getElementById(`view-${view}`);
    if (!viewEl) return; // unknown view
    document.querySelectorAll('.cr-view').forEach(v => v.classList.remove('active'));
    viewEl.classList.add('active');

    // Highlight bottom nav: if view is in nav, highlight it; otherwise highlight Home
    const navView = NAV_VIEWS.has(view) ? view : 'home';
    document.querySelectorAll('.cr-bottomnav a').forEach(a => {
        a.classList.toggle('active', a.dataset.view === navView);
    });

    if (view === 'inbox') {
        if (typeof loadInbox === 'function') loadInbox();
    }
    if (view === 'notifications') {
        if (typeof loadNotifications === 'function') loadNotifications();
    }
    if (view === 'permissions') {
        if (typeof loadPermissions === 'function') loadPermissions();
        if (typeof startPermissionsPolling === 'function') startPermissionsPolling();
    } else {
        if (typeof stopPermissionsPolling === 'function') stopPermissionsPolling();
    }
    if (view === 'news') {
        if (typeof loadNews === 'function') loadNews();
    }
    if (view === 'messages') {
        if (typeof clearMessageBadge === 'function') clearMessageBadge();
        if (typeof loadChatTerminalDropdown === 'function') loadChatTerminalDropdown(agentName);
    }
    if (view === 'console') {
        if (typeof initConsoleTerminal === 'function') initConsoleTerminal();
        if (typeof refreshConsoleTerminals === 'function') refreshConsoleTerminals();
        // Refit after view transition completes
        setTimeout(() => { if (typeof consoleFitAddon !== 'undefined' && consoleFitAddon) consoleFitAddon.fit(); }, 150);
    }
    if (view === 'settings') {
        // Sync slider with saved value
        const saved = localStorage.getItem('cr-font-size');
        const level = saved !== null ? parseInt(saved) : 2;
        const slider = document.getElementById('font-size-slider');
        if (slider) slider.value = level;
        const label = document.getElementById('font-size-label');
        if (label) label.textContent = FONT_SIZE_NAMES[level] || 'Medium';
    }
}

function refreshCurrentView() {
    switch (currentView) {
        case 'tasks': loadTasks(); break;
        case 'terminals': loadTerminals(); break;
        case 'console': if (typeof refreshConsoleTerminals === 'function') refreshConsoleTerminals(); break;
        case 'inbox': break; // manual refresh only — no auto-reload
        case 'notifications': if (typeof loadNotifications === 'function') loadNotifications(); break;
        case 'permissions': if (typeof loadPermissions === 'function') loadPermissions(); break;
        case 'news': break; // news uses manual refresh button, not auto-refresh
        case 'messages': break; // don't rebuild dropdown on auto-refresh — it clobbers the user's selection
        case 'home': break; // static grid, no refresh needed
    }
}

// Logout
async function logout() {
    await fetch('/api/auth/logout', { method: 'POST' });
    window.location.href = '/login.html';
}

// Utility: format relative time
function timeAgo(dateStr) {
    if (!dateStr) return '';
    const now = new Date();
    const date = new Date(dateStr);
    const seconds = Math.floor((now - date) / 1000);

    if (seconds < 60) return 'just now';
    if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
    if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
    return `${Math.floor(seconds / 86400)}d ago`;
}

// Utility: status badge
function statusBadge(status) {
    const cls = `badge badge-${(status || 'todo').replace('_', '-')}`;
    const label = (status || 'todo').replace('_', ' ');
    return `<span class="${cls}">${label}</span>`;
}

// Utility: escape HTML to prevent XSS (shared across all JS files)
function escapeHtml(str) {
    if (str == null) return '';
    return String(str)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}

// Utility: API helper
async function api(path, options = {}) {
    try {
        const res = await fetch(path, options);
        if (res.status === 401) {
            window.location.href = '/login.html';
            return null;
        }
        if (!res.ok) {
            console.error(`API ${res.status}: ${path}`);
            return null;
        }
        return await res.json();
    } catch (err) {
        console.error(`API error: ${path}`, err);
        updateConnectionStatus(false);
        return null;
    }
}

function updateConnectionStatus(online) {
    const dot = document.getElementById('status-dot');
    dot.className = `status-dot ${online ? 'online' : 'offline'}`;
}

// ── Font Size Setting ───────────────────────────────────────

const FONT_SIZE_NAMES = ['Tiny', 'Small', 'Medium', 'Large', 'X-Large'];

function restoreFontSize() {
    const saved = localStorage.getItem('cr-font-size');
    const level = saved !== null ? parseInt(saved) : 2; // default Medium
    applyFontSizeClass(level);
    // Sync slider when settings view is opened
    const slider = document.getElementById('font-size-slider');
    if (slider) slider.value = level;
    const label = document.getElementById('font-size-label');
    if (label) label.textContent = FONT_SIZE_NAMES[level] || 'Medium';
}

function applyFontSize(level) {
    level = parseInt(level);
    applyFontSizeClass(level);
    localStorage.setItem('cr-font-size', level);
    const label = document.getElementById('font-size-label');
    if (label) label.textContent = FONT_SIZE_NAMES[level] || 'Medium';
}

function applyFontSizeClass(level) {
    document.body.classList.remove('font-size-0', 'font-size-1', 'font-size-2', 'font-size-3', 'font-size-4');
    document.body.classList.add(`font-size-${level}`);
}

// ── Remote Mode Toggle ──────────────────────────────────────

let remoteToggleInflight = false;

async function initRemoteMode() {
    await refreshRemoteMode();
    setInterval(refreshRemoteMode, 5000);
}

async function refreshRemoteMode() {
    if (remoteToggleInflight) return;
    const data = await api('/api/remote-mode');
    if (data) applyRemoteToggle(data.remote_mode);
}

function applyRemoteToggle(enabled) {
    const el = document.getElementById('remote-toggle');
    const label = document.getElementById('remote-toggle-label');
    if (!el) return;
    if (enabled) {
        el.classList.add('active');
        label.textContent = 'On';
    } else {
        el.classList.remove('active');
        label.textContent = 'Off';
    }
}

async function toggleRemoteMode() {
    const el = document.getElementById('remote-toggle');
    const isOn = el.classList.contains('active');
    const newState = !isOn;
    applyRemoteToggle(newState);
    remoteToggleInflight = true;
    try {
        const res = await api('/api/remote-mode', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ enabled: newState })
        });
        // api() returns null on any non-2xx (incl. 403 origin reject); the success body is
        // now the raw resource { remote_mode } with no `success` flag (7ce19175). So !res
        // fully captures failure — on error, revert the optimistic toggle.
        if (!res) {
            applyRemoteToggle(isOn);
        }
    } finally {
        remoteToggleInflight = false;
    }
}
