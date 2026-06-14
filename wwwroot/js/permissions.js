// MultiRemote — Pending Permissions view
//
// Relays five request_types from the Cloudflare Worker to the phone UI:
//   tool_permission  — yes/no approve/deny (legacy, unchanged)
//   elicitation      — free-form text input              (item 12)
//   choice           — radio-list from options[]         (item 13)
//   plan_approval    — markdown + approve/revise         (item 14)
//   notification     — display-only, no respond          (item 15)
//
// Tap actions call respondToPermission(id, body) which POSTs to the Worker
// via the MultiRemote proxy. Polls every 5s while the view is active.

let permissionsPollTimer = null;
let permissionsRequesting = new Set(); // IDs currently being responded to

async function loadPermissions() {
    const list = document.getElementById('permissions-list');
    if (!list) return;

    // Only show skeleton on first load (when list is empty)
    if (!list.dataset.loaded) {
        list.innerHTML = '<div class="cr-skeleton"></div><div class="cr-skeleton"></div>';
    }

    const data = await api('/api/permissions');
    if (!data) {
        list.innerHTML = '<div class="cr-empty"><i class="bi bi-wifi-off"></i>Could not load permissions</div>';
        updatePermissionsBadge(0);
        return;
    }

    const pending = Array.isArray(data.requests) ? data.requests : [];
    list.dataset.loaded = '1';
    updatePermissionsBadge(pending.length);

    if (!pending.length) {
        list.innerHTML = '<div class="cr-empty"><i class="bi bi-shield-check"></i>No pending permissions</div>';
        return;
    }

    // Capture dirty DOM state for cards that will survive the rebuild.
    // Without this, every 5s poll wipes in-progress textarea content,
    // radio selections, and the plan-card's revise pane open/closed state.
    const preserved = new Map();
    let focusedId = null;
    for (const card of list.querySelectorAll('.permission-card[data-id]')) {
        const id = card.dataset.id;
        if (!id) continue;
        const textarea = card.querySelector('textarea');
        const checkedRadio = card.querySelector('input[type="radio"]:checked');
        const revise = card.querySelector('.permission-revise');
        preserved.set(id, {
            textValue: textarea ? textarea.value : '',
            textareaId: textarea ? textarea.id : null,
            radioValue: checkedRadio ? checkedRadio.value : null,
            reviseOpen: revise ? revise.hidden === false : false
        });
        if (textarea && document.activeElement === textarea) focusedId = id;
    }

    list.innerHTML = pending.map(p => renderCard(p)).join('');

    // Restore captured state onto the new DOM for cards still present.
    for (const [id, state] of preserved) {
        const card = list.querySelector(`.permission-card[data-id="${CSS.escape(id)}"]`);
        if (!card) continue;

        if (state.reviseOpen && typeof showPlanRevise === 'function') {
            showPlanRevise(id);
        }

        if (state.textValue) {
            const textarea = card.querySelector('textarea');
            if (textarea) {
                textarea.value = state.textValue;
                textarea.dispatchEvent(new Event('input'));
            }
        }

        if (state.radioValue != null) {
            for (const radio of card.querySelectorAll('input[type="radio"]')) {
                if (radio.value === state.radioValue) {
                    radio.checked = true;
                    radio.dispatchEvent(new Event('change'));
                    break;
                }
            }
        }

        if (focusedId === id) {
            const textarea = card.querySelector('textarea');
            if (textarea) textarea.focus();
        }
    }
}

// Mirrors server-side ProxyHelpers.IsValidId — keeps Worker-supplied IDs
// out of inline onclick handlers as a defense-in-depth layer. GET /pending
// forwards the Worker payload without per-item validation; this guards the
// renderers against malformed or adversarial IDs before they hit the DOM.
function isValidPermissionId(id) {
    return typeof id === 'string' && id.length > 0 && id.length <= 64 && /^[a-zA-Z0-9_\-]+$/.test(id);
}

function updatePermissionsBadge(count) {
    const badge = document.getElementById('permissions-badge');
    if (!badge) return;
    if (count > 0) {
        badge.textContent = count > 99 ? '99+' : count;
        badge.classList.remove('d-none');
    } else {
        badge.classList.add('d-none');
    }
}

// Dispatch by request_type. Unknown/missing types fall through to a
// forward-compat "unsupported" card so a new type added in the Worker
// doesn't break older phones that haven't shipped a renderer yet.
function renderCard(p) {
    if (!isValidPermissionId(p && p.id)) {
        console.warn('Skipping permission with invalid id', p && p.id);
        return '';
    }
    const type = p.request_type || 'tool_permission';
    switch (type) {
        case 'tool_permission': return renderToolPermissionCard(p);
        case 'elicitation':     return renderElicitationCard(p);
        case 'choice':          return renderChoiceCard(p);
        case 'plan_approval':   return renderPlanApprovalCard(p);
        case 'notification':    return renderNotificationCard(p);
        default:                return renderUnsupportedCard(p, type);
    }
}

function renderToolPermissionCard(p) {
    const id = escapeHtml(p.id || '');
    const agent = escapeHtml(p.agent_name || p.agentName || 'Unknown');
    const tool = escapeHtml(p.tool_name || p.toolName || 'unknown tool');
    const description = escapeHtml(p.description || p.tool_input || '(no details)');
    const created = timeAgo(p.created_at || p.createdAt);
    const expires = p.expires_at || p.expiresAt;
    const expiresIn = expires ? formatExpiresIn(expires) : '';
    const busy = permissionsRequesting.has(p.id) ? 'disabled' : '';

    return `
        <div class="cr-card permission-card" data-id="${id}">
            <div class="permission-header">
                <div class="permission-icon">
                    <i class="bi bi-shield-lock-fill" style="color:var(--cr-warning)"></i>
                </div>
                <div class="flex-grow-1 min-width-0">
                    <div class="permission-agent">${agent}</div>
                    <div class="permission-tool">${tool}</div>
                </div>
                <div class="permission-time">${created}</div>
            </div>
            <div class="permission-body">${description}</div>
            ${expiresIn ? `<div class="permission-expires"><i class="bi bi-clock"></i> Expires ${expiresIn}</div>` : ''}
            <div class="permission-actions">
                <button class="btn btn-danger flex-grow-1" onclick="respondToPermission('${id}', { decision: 'denied' })" ${busy}>
                    <i class="bi bi-x-lg"></i> Deny
                </button>
                <button class="btn btn-success flex-grow-1" onclick="respondToPermission('${id}', { decision: 'approved' })" ${busy}>
                    <i class="bi bi-check-lg"></i> Approve
                </button>
            </div>
        </div>`;
}

function renderElicitationCard(p) {
    const id = escapeHtml(p.id || '');
    const agent = escapeHtml(p.agent_name || p.agentName || 'Unknown');
    const prompt = escapeHtml(p.prompt || '(no prompt)');
    const created = timeAgo(p.created_at || p.createdAt);
    const expires = p.expires_at || p.expiresAt;
    const expiresIn = expires ? formatExpiresIn(expires) : '';
    const busy = permissionsRequesting.has(p.id);
    const inputId = `elicit-input-${id}`;
    const submitId = `elicit-submit-${id}`;

    return `
        <div class="cr-card permission-card permission-card-elicitation" data-id="${id}">
            <div class="permission-header">
                <div class="permission-icon">
                    <i class="bi bi-chat-left-text-fill" style="color:var(--cr-info)"></i>
                </div>
                <div class="flex-grow-1 min-width-0">
                    <div class="permission-agent">${agent}</div>
                    <div class="permission-tool">Elicitation</div>
                </div>
                <div class="permission-time">${created}</div>
            </div>
            <div class="permission-body">${prompt}</div>
            ${expiresIn ? `<div class="permission-expires"><i class="bi bi-clock"></i> Expires ${expiresIn}</div>` : ''}
            <textarea id="${inputId}" class="permission-input" rows="2"
                placeholder="Type your answer…"
                ${busy ? 'disabled' : ''}
                oninput="onElicitationInput('${id}')"></textarea>
            <div class="permission-actions">
                <button id="${submitId}" class="btn btn-primary flex-grow-1" onclick="submitElicitation('${id}')" disabled>
                    <i class="bi bi-send"></i> Submit
                </button>
            </div>
        </div>`;
}

function onElicitationInput(id) {
    const input = document.getElementById(`elicit-input-${id}`);
    const submit = document.getElementById(`elicit-submit-${id}`);
    if (!input || !submit) return;
    submit.disabled = input.value.trim().length === 0 || permissionsRequesting.has(id);
}

function submitElicitation(id) {
    const input = document.getElementById(`elicit-input-${id}`);
    if (!input) return;
    const text = input.value.trim();
    if (!text) return;
    respondToPermission(id, { response: { text } });
}

function renderChoiceCard(p) {
    const id = escapeHtml(p.id || '');
    const agent = escapeHtml(p.agent_name || p.agentName || 'Unknown');
    const prompt = escapeHtml(p.prompt || '(no prompt)');
    const options = Array.isArray(p.options) ? p.options : [];
    const created = timeAgo(p.created_at || p.createdAt);
    const expires = p.expires_at || p.expiresAt;
    const expiresIn = expires ? formatExpiresIn(expires) : '';
    const busy = permissionsRequesting.has(p.id);
    const submitId = `choice-submit-${id}`;
    const radioName = `choice-radio-${id}`;

    const optionsHtml = options.length
        ? options.map((o, idx) => {
            const label = escapeHtml(o.label || o.value || `Option ${idx + 1}`);
            const value = escapeHtml(o.value != null ? String(o.value) : String(idx));
            const optId = `choice-opt-${id}-${idx}`;
            return `
                <label class="permission-choice-option" for="${optId}">
                    <input type="radio" id="${optId}" name="${radioName}" value="${value}"
                        ${busy ? 'disabled' : ''}
                        onchange="onChoiceSelect('${id}')">
                    <span>${label}</span>
                </label>`;
        }).join('')
        : '<div class="cr-empty-inline">No options provided</div>';

    return `
        <div class="cr-card permission-card permission-card-choice" data-id="${id}">
            <div class="permission-header">
                <div class="permission-icon">
                    <i class="bi bi-ui-radios" style="color:var(--cr-info)"></i>
                </div>
                <div class="flex-grow-1 min-width-0">
                    <div class="permission-agent">${agent}</div>
                    <div class="permission-tool">Choice</div>
                </div>
                <div class="permission-time">${created}</div>
            </div>
            <div class="permission-body">${prompt}</div>
            ${expiresIn ? `<div class="permission-expires"><i class="bi bi-clock"></i> Expires ${expiresIn}</div>` : ''}
            <div class="permission-choice-options" data-choice-group="${radioName}">${optionsHtml}</div>
            <div class="permission-actions">
                <button id="${submitId}" class="btn btn-primary flex-grow-1" onclick="submitChoice('${id}')" disabled>
                    <i class="bi bi-send"></i> Submit
                </button>
            </div>
        </div>`;
}

function onChoiceSelect(id) {
    const submit = document.getElementById(`choice-submit-${id}`);
    if (!submit) return;
    submit.disabled = !getSelectedChoice(id) || permissionsRequesting.has(id);
}

function getSelectedChoice(id) {
    const checked = document.querySelector(`input[name="choice-radio-${CSS.escape(id)}"]:checked`);
    return checked ? checked.value : null;
}

function submitChoice(id) {
    const value = getSelectedChoice(id);
    if (value == null) return;
    respondToPermission(id, { response: { value } });
}

function renderPlanApprovalCard(p) {
    const id = escapeHtml(p.id || '');
    const agent = escapeHtml(p.agent_name || p.agentName || 'Unknown');
    const prompt = (typeof p.prompt === 'string' && p.prompt) ? p.prompt : '(no plan)';
    const created = timeAgo(p.created_at || p.createdAt);
    const expires = p.expires_at || p.expiresAt;
    const expiresIn = expires ? formatExpiresIn(expires) : '';
    const busy = permissionsRequesting.has(p.id) ? 'disabled' : '';
    const planHtml = renderPlanMarkdown(prompt);

    return `
        <div class="cr-card permission-card permission-card-plan" data-id="${id}">
            <div class="permission-header">
                <div class="permission-icon">
                    <i class="bi bi-file-earmark-text-fill" style="color:var(--cr-primary)"></i>
                </div>
                <div class="flex-grow-1 min-width-0">
                    <div class="permission-agent">${agent}</div>
                    <div class="permission-tool">Plan approval</div>
                </div>
                <div class="permission-time">${created}</div>
            </div>
            <div class="permission-plan-body">${planHtml}</div>
            ${expiresIn ? `<div class="permission-expires"><i class="bi bi-clock"></i> Expires ${expiresIn}</div>` : ''}
            <div class="permission-revise" id="plan-revise-${id}" hidden>
                <textarea id="plan-comment-${id}" class="permission-input" rows="2"
                    placeholder="What should change about this plan?"
                    oninput="onPlanCommentInput('${id}')"></textarea>
                <div class="permission-actions">
                    <button class="btn btn-secondary flex-grow-1" onclick="cancelPlanRevise('${id}')" ${busy}>
                        <i class="bi bi-arrow-left"></i> Back
                    </button>
                    <button id="plan-revise-submit-${id}" class="btn btn-warning flex-grow-1"
                        onclick="submitPlanRevise('${id}')" disabled>
                        <i class="bi bi-send"></i> Send
                    </button>
                </div>
            </div>
            <div class="permission-actions" id="plan-actions-${id}">
                <button class="btn btn-warning flex-grow-1" onclick="showPlanRevise('${id}')" ${busy}>
                    <i class="bi bi-pencil"></i> Revise
                </button>
                <button class="btn btn-success flex-grow-1" onclick="submitPlanApprove('${id}')" ${busy}>
                    <i class="bi bi-check-lg"></i> Approve
                </button>
            </div>
        </div>`;
}

function showPlanRevise(id) {
    const revise = document.getElementById(`plan-revise-${id}`);
    const actions = document.getElementById(`plan-actions-${id}`);
    if (revise) revise.hidden = false;
    if (actions) actions.hidden = true;
    const input = document.getElementById(`plan-comment-${id}`);
    if (input) input.focus();
}

function cancelPlanRevise(id) {
    const revise = document.getElementById(`plan-revise-${id}`);
    const actions = document.getElementById(`plan-actions-${id}`);
    const input = document.getElementById(`plan-comment-${id}`);
    if (revise) revise.hidden = true;
    if (actions) actions.hidden = false;
    if (input) input.value = '';
    onPlanCommentInput(id);
}

function onPlanCommentInput(id) {
    const input = document.getElementById(`plan-comment-${id}`);
    const submit = document.getElementById(`plan-revise-submit-${id}`);
    if (!input || !submit) return;
    submit.disabled = input.value.trim().length === 0 || permissionsRequesting.has(id);
}

function submitPlanApprove(id) {
    respondToPermission(id, { response: { decision: 'approved' } });
}

function submitPlanRevise(id) {
    const input = document.getElementById(`plan-comment-${id}`);
    if (!input) return;
    const comment = input.value.trim();
    if (!comment) return; // Worker requires non-empty comment for 'revise'
    respondToPermission(id, { response: { decision: 'revise', comment } });
}

// Minimal markdown renderer for plan previews. Supports headers (#, ##, ###),
// fenced code blocks (```), bullets (-, *), blank lines as paragraph breaks,
// **bold**, `inline code`, and [text](url) links. Escapes HTML first so
// agent-supplied markdown can't inject script.
function renderPlanMarkdown(md) {
    const lines = String(md ?? '').split('\n');
    const out = [];
    let inCode = false;
    let listOpen = false;

    const closeList = () => { if (listOpen) { out.push('</ul>'); listOpen = false; } };

    for (const raw of lines) {
        if (raw.trim().startsWith('```')) {
            closeList();
            if (inCode) { out.push('</code></pre>'); inCode = false; }
            else        { out.push('<pre class="permission-plan-code"><code>'); inCode = true; }
            continue;
        }
        if (inCode) { out.push(escapeHtml(raw)); continue; }

        if (raw.startsWith('### ')) { closeList(); out.push(`<h5>${inlineMarkdown(raw.slice(4))}</h5>`); continue; }
        if (raw.startsWith('## '))  { closeList(); out.push(`<h4>${inlineMarkdown(raw.slice(3))}</h4>`); continue; }
        if (raw.startsWith('# '))   { closeList(); out.push(`<h3>${inlineMarkdown(raw.slice(2))}</h3>`); continue; }

        if (raw.startsWith('- ') || raw.startsWith('* ')) {
            if (!listOpen) { out.push('<ul>'); listOpen = true; }
            out.push(`<li>${inlineMarkdown(raw.slice(2))}</li>`);
            continue;
        }

        if (!raw.trim()) { closeList(); out.push(''); continue; }

        closeList();
        out.push(`<p>${inlineMarkdown(raw)}</p>`);
    }
    if (inCode) out.push('</code></pre>');
    closeList();
    return out.join('\n');
}

function inlineMarkdown(text) {
    let out = escapeHtml(text);
    out = out.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');
    out = out.replace(/`([^`]+)`/g, '<code>$1</code>');
    // Scheme whitelist — agent markdown can't produce javascript:/data: anchors,
    // and same-origin paths must start with exactly one slash so `//evil.com`
    // protocol-relative URLs don't sneak through. label and url were already
    // escapeHtml'd above so no re-escape here.
    out = out.replace(/\[([^\]]+)\]\(([^)]+)\)/g, (m, label, url) => {
        return /^(https?:\/\/|mailto:|\/[^\/]|#)/i.test(url.trim())
            ? `<a href="${url}" target="_blank" rel="noopener noreferrer">${label}</a>`
            : `[${label}](${url})`;
    });
    return out;
}

function renderNotificationCard(p) {
    const id = escapeHtml(p.id || '');
    const agent = escapeHtml(p.agent_name || p.agentName || 'Unknown');
    const description = escapeHtml(p.description || '(no details)');
    const created = timeAgo(p.created_at || p.createdAt);
    const expires = p.expires_at || p.expiresAt;
    const expiresIn = expires ? formatExpiresIn(expires) : '';
    const busy = permissionsRequesting.has(p.id) ? 'disabled' : '';

    return `
        <div class="cr-card permission-card permission-card-notification" data-id="${id}">
            <div class="permission-header">
                <div class="permission-icon">
                    <i class="bi bi-bell-fill" style="color:var(--cr-text-muted)"></i>
                </div>
                <div class="flex-grow-1 min-width-0">
                    <div class="permission-agent">${agent}</div>
                    <div class="permission-tool">Notification</div>
                </div>
                <div class="permission-time">${created}</div>
            </div>
            <div class="permission-body">${description}</div>
            ${expiresIn ? `<div class="permission-expires"><i class="bi bi-clock"></i> Expires ${expiresIn}</div>` : ''}
            <div class="permission-actions">
                <button class="btn btn-secondary flex-grow-1" onclick="dismissNotification('${id}')" ${busy}>
                    <i class="bi bi-check2"></i> Dismiss
                </button>
            </div>
        </div>`;
}

async function dismissNotification(id) {
    if (!id || permissionsRequesting.has(id)) return;
    permissionsRequesting.add(id);

    const card = document.querySelector(`.permission-card[data-id="${CSS.escape(id)}"]`);
    if (card) {
        card.querySelectorAll('button').forEach(b => b.disabled = true);
        card.classList.add('permission-responding');
    }

    // Direct fetch (not api()) — Worker DELETE returns 204 No Content; api()
    // calls res.json() which would throw on empty body and toggle the
    // connection status to offline. Idempotent on the Worker side: 204 for
    // both existing and already-gone rows. AbortSignal.timeout(30000) prevents
    // a hung request from permanently leaking the id into permissionsRequesting.
    let ok = false;
    let redirected = false;
    try {
        const res = await fetch(`/api/permissions/${encodeURIComponent(id)}`, {
            method: 'DELETE',
            signal: AbortSignal.timeout(30000)
        });
        if (res.status === 401) { redirected = true; window.location.href = '/login.html'; return; }
        ok = res.ok;
    } catch {
        ok = false;
    } finally {
        if (!redirected) {
            if (ok) {
                if (card) card.classList.add('permission-resolved');
                setTimeout(() => loadPermissions(), 400);
            } else {
                if (card) {
                    card.querySelectorAll('button').forEach(b => b.disabled = false);
                    card.classList.remove('permission-responding');
                }
                alert('Could not dismiss notification. Try again.');
            }
        }
        permissionsRequesting.delete(id);
    }
}

function renderUnsupportedCard(p, type) {
    const id = escapeHtml(p.id || '');
    const agent = escapeHtml(p.agent_name || p.agentName || 'Unknown');
    const displayType = escapeHtml(type || 'unknown');
    const created = timeAgo(p.created_at || p.createdAt);
    const expires = p.expires_at || p.expiresAt;
    const expiresIn = expires ? formatExpiresIn(expires) : '';
    return `
        <div class="cr-card permission-card" data-id="${id}">
            <div class="permission-header">
                <div class="permission-icon">
                    <i class="bi bi-question-circle-fill" style="color:var(--cr-text-muted)"></i>
                </div>
                <div class="flex-grow-1 min-width-0">
                    <div class="permission-agent">${agent}</div>
                    <div class="permission-tool">${displayType}</div>
                </div>
                <div class="permission-time">${created}</div>
            </div>
            <div class="permission-body">Unsupported request type. Update MultiRemote to handle it.</div>
            ${expiresIn ? `<div class="permission-expires"><i class="bi bi-clock"></i> Expires ${expiresIn}</div>` : ''}
        </div>`;
}

function formatExpiresIn(expiresAt) {
    const now = new Date();
    const expires = new Date(expiresAt);
    const seconds = Math.floor((expires - now) / 1000);
    if (seconds <= 0) return 'soon';
    if (seconds < 60) return `in ${seconds}s`;
    if (seconds < 3600) return `in ${Math.floor(seconds / 60)}m`;
    return `in ${Math.floor(seconds / 3600)}h`;
}

// body is the full response body to send — caller decides the shape:
//   tool_permission: { decision: 'approved'|'denied' }    (legacy)
//   other types:     { response: { text|value|decision, ... } }  (wrapped)
async function respondToPermission(id, body) {
    if (!id || permissionsRequesting.has(id)) return;
    permissionsRequesting.add(id);

    // Optimistic UI: disable buttons on this card
    const card = document.querySelector(`.permission-card[data-id="${CSS.escape(id)}"]`);
    if (card) {
        card.querySelectorAll('button').forEach(b => b.disabled = true);
        card.classList.add('permission-responding');
    }

    try {
        // 30s timeout prevents a hung fetch from permanently leaking the id
        // into permissionsRequesting, which would disable the card's buttons
        // across every subsequent poll until a full page reload.
        const res = await api(`/api/permissions/${encodeURIComponent(id)}/respond`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body),
            signal: AbortSignal.timeout(30000)
        });

        if (res) {
            if (card) card.classList.add('permission-resolved');
            setTimeout(() => loadPermissions(), 400);
        } else {
            if (card) {
                card.querySelectorAll('button').forEach(b => b.disabled = false);
                card.classList.remove('permission-responding');
            }
            alert('Could not submit response. Try again.');
        }
    } finally {
        permissionsRequesting.delete(id);
    }
}

function startPermissionsPolling() {
    stopPermissionsPolling();
    permissionsPollTimer = setInterval(() => {
        if (currentView === 'permissions') loadPermissions();
    }, 5000);
}

function stopPermissionsPolling() {
    if (permissionsPollTimer) {
        clearInterval(permissionsPollTimer);
        permissionsPollTimer = null;
    }
}
