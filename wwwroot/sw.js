// MultiRemote Service Worker — push notifications

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', () => self.clients.claim());

self.addEventListener('push', (event) => {
    let data = { title: 'MultiRemote', body: 'New message' };
    try {
        data = event.data.json();
    } catch (e) {
        data.body = event.data?.text() || 'New message';
    }

    // Map notification_type to the app view to open on tap
    const viewMap = {
        task_complete: 'notifications',
        ready_for_testing: 'notifications',
        escalation: 'notifications',
        helper_request: 'notifications',
        agent_stopped: 'notifications',
        permission_request: 'permissions',
        error: 'notifications',
        message: 'messages',
        inbox: 'inbox'
    };
    const view = viewMap[data.notification_type] || 'notifications';

    event.waitUntil(
        self.registration.showNotification(data.title, {
            body: data.body,
            icon: '/img/icon-192.svg',
            badge: '/img/icon-192.svg',
            vibrate: [200, 100, 200],
            tag: 'cr-' + (data.notification_type || 'message'),
            renotify: true,
            data: { view, agent_name: data.agent_name }
        }).then(() =>
            self.clients.matchAll({ type: 'window' }).then(clients =>
                clients.forEach(c => c.postMessage({ type: 'push-received' }))
            )
        )
    );
});

self.addEventListener('notificationclick', (event) => {
    const view = event.notification.data?.view || 'notifications';
    const agentName = event.notification.data?.agent_name || '';
    event.notification.close();
    event.waitUntil(
        self.clients.matchAll({ type: 'window' }).then(clients => {
            if (clients.length > 0) {
                clients[0].focus();
                clients[0].postMessage({ type: 'switch-view', view, agent_name: agentName });
            } else {
                const params = new URLSearchParams({ view });
                if (agentName) params.set('agent', agentName);
                self.clients.openWindow('/?' + params.toString());
            }
        })
    );
});
