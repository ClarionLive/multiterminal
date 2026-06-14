// MultiRemote — Chat view

let phoneTerminalId = null;
let inboxInterval = null;
let chatHistory = JSON.parse(localStorage.getItem('cr-chat') || '[]');
let currentChatTarget = null;
const _elicitSchemas = {}; // Schema storage for conditional form evaluation

function setupMessageForm() {
    const container = document.getElementById('message-form-container');
    container.innerHTML = `
        <div class="mb-2">
            <select class="form-select bg-dark text-light border-secondary" id="chat-target" onchange="switchChat()">
                <option value="" disabled selected>Loading terminals...</option>
            </select>
        </div>
        <div id="chat-window" class="chat-window"></div>
        <div class="chat-input-sticky">
            <div id="image-preview-strip" class="image-preview-strip" style="display:none"></div>
            <div class="chat-input-bar">
                <textarea class="form-control bg-dark text-light border-secondary" id="chat-input"
                          rows="1" placeholder="Type or tap mic..." onkeydown="chatKeydown(event)" style="height:auto;min-height:38px"></textarea>
                <button class="btn btn-outline-light" id="chat-image-btn" onclick="openImagePicker()" title="Send image">
                    <i class="bi bi-camera-fill"></i>
                </button>
                <button class="btn btn-outline-light" id="chat-mic-btn" onclick="toggleVoice()" title="Voice input">
                    <i class="bi bi-mic-fill"></i>
                </button>
                <button class="btn btn-primary" id="chat-send-btn" onclick="sendChat()">
                    <i class="bi bi-send-fill"></i>
                </button>
            </div>
        </div>
        <div id="image-lightbox" class="image-lightbox" style="display:none" onclick="closeLightbox()">
            <img id="lightbox-img" src="" alt="Preview">
        </div>
        <input type="file" id="image-file-input" accept="image/*" multiple style="display:none" onchange="onImagesSelected(event)">`;
    loadChatTerminalDropdown();
    renderChat();

    // Auto-grow textarea on input (works with iOS predictive text)
    const chatInput = document.getElementById('chat-input');
    if (chatInput) {
        ['input', 'compositionend', 'change'].forEach(evt => {
            chatInput.addEventListener(evt, () => autoGrowInput(chatInput));
        });
    }

    // Poll for new messages every 5 seconds
    if (inboxInterval) clearInterval(inboxInterval);
    inboxInterval = setInterval(pollMessages, 5000);
    pollMessages();
}

async function loadChatTerminalDropdown(selectAgent) {
    const select = document.getElementById('chat-target');
    const previousValue = select.value;
    const data = await api('/api/terminals');
    if (!data) {
        select.innerHTML = '<option value="" disabled selected>Could not load terminals</option>';
        return;
    }
    const terminals = Array.isArray(data) ? data : (data.terminals || []);
    let html = '<option value="" disabled selected>Choose a terminal...</option>';
    html += '<option value="all">Broadcast to All</option>';
    terminals
        .filter(t => t.name !== 'MultiRemote' && t.docId !== 'multi-remote')
        .forEach(t => {
            const name = t.name || 'Unknown';
            const status = isTerminalActive(t) ? '●' : '○';
            html += `<option value="${escapeHtml(name)}">${status} ${escapeHtml(name)}</option>`;
        });
    select.innerHTML = html;
    // Restore selection: deep-link agent takes priority, then previous selection
    if (selectAgent && select.querySelector(`option[value="${CSS.escape(selectAgent)}"]`)) {
        select.value = selectAgent;
    } else if (previousValue && select.querySelector(`option[value="${CSS.escape(previousValue)}"]`)) {
        select.value = previousValue;
    } else if (previousValue) {
        // Previously selected terminal disappeared from list — reset to placeholder
        // rather than silently selecting a different terminal
        select.value = '';
    }
    currentChatTarget = select.value;
    if (selectAgent) renderChat();
}

function switchChat() {
    currentChatTarget = document.getElementById('chat-target').value;
    renderChat();
}

function renderChat() {
    const window_ = document.getElementById('chat-window');
    if (!window_) return;

    const filtered = currentChatTarget === 'all'
        ? chatHistory
        : chatHistory.filter(m => m.from === currentChatTarget || m.to === currentChatTarget || m.to === 'all');

    if (filtered.length === 0) {
        window_.innerHTML = '<div class="chat-empty"><i class="bi bi-chat-dots"></i><p>No messages yet. Say hello!</p></div>';
        return;
    }

    let html = '';
    filtered.forEach(msg => {
        const isMe = msg.fromMe;
        const bubbleClass = isMe ? 'chat-bubble-me' : 'chat-bubble-them';
        const name = isMe ? 'You' : msg.from;
        const time = formatChatTime(msg.timestamp);
        let imagesHtml = '';
        if (msg.imageThumbs && msg.imageThumbs.length > 0) {
            imagesHtml = '<div class="chat-images">';
            msg.imageThumbs.forEach(img => {
                const src = `data:${img.mime};base64,${img.base64}`;
                imagesHtml += `<img class="chat-image" src="${src}" onclick="openChatImage(this.src)">`;
            });
            imagesHtml += '</div>';
        }
        // Strip image ref tags from display text
        const cleanMsg = (msg.message || '').replace(/\n*📎.*\[ref:[a-f0-9]+\]/, '').trim();

        // Check for waiting-for-input notification
        const waitMatch = cleanMsg.match(/^\[WAITING_FOR_INPUT:([^\]]+)\]\s*(.*)$/);
        // Check for elicitation request pattern
        const elicitMatch = cleanMsg.match(/^\[ELICITATION_REQUEST:([^:]+):([^\]]+)\]\n?([\s\S]*?)\n\[SCHEMA\]\n([\s\S]*)$/);
        // Check for permission request pattern
        const permMatch = cleanMsg.match(/^\[PERMISSION_REQUEST:([a-km-z]{5}):([^:]+):([^\]]+)\]\n?([\s\S]*)/i);

        if (waitMatch && !isMe) {
            const [, agentName, waitMsg] = waitMatch;
            html += `
                <div class="chat-row chat-row-them">
                    <div class="chat-bubble-them chat-waiting-bubble">
                        <div class="chat-sender">${escapeHtml(name)}</div>
                        <div class="chat-waiting-header"><i class="bi bi-hourglass-split"></i> Waiting for Input</div>
                        <div class="chat-text">${escapeHtml(waitMsg || agentName + ' is waiting for your response.')}</div>
                        <div class="chat-time">${time}</div>
                    </div>
                </div>`;
        } else if (elicitMatch && !isMe) {
            const [, rawElicitId, serverName, elicitMessage, schemaStr] = elicitMatch;
            const elicitId = (rawElicitId || '').replace(/[^a-zA-Z0-9_]/g, '');
            const answered = msg._elicitationAnswered;
            const elicitAction = msg._elicitationAction;
            let schema = {};
            try { schema = JSON.parse(schemaStr); } catch {}

            html += `
                <div class="chat-row chat-row-them">
                    <div class="chat-bubble-them chat-elicitation-bubble">
                        <div class="chat-sender">${escapeHtml(name)}</div>
                        <div class="chat-permission-header"><i class="bi bi-input-cursor-text"></i> Input Requested</div>
                        <div class="chat-permission-tool">${escapeHtml(serverName)}</div>
                        <div class="chat-text">${escapeHtml(elicitMessage.trim())}</div>
                        ${answered
                            ? `<div class="chat-permission-answered ${elicitAction === 'accept' ? 'approved' : 'denied'}">${elicitAction === 'accept' ? '✅ Submitted' : '❌ Declined'}</div>`
                            : renderElicitationForm(elicitId, rawElicitId, schema)
                        }
                        <div class="chat-time">${time}</div>
                    </div>
                </div>`;
        } else if (permMatch && !isMe) {
            const [, reqId, agentName, toolName, description] = permMatch;
            const answered = msg._permissionAnswered;
            const verdict = msg._permissionVerdict;
            html += `
                <div class="chat-row chat-row-them">
                    <div class="chat-bubble-them chat-permission-bubble">
                        <div class="chat-sender">${escapeHtml(name)}</div>
                        <div class="chat-permission-header"><i class="bi bi-shield-lock"></i> Permission Request</div>
                        <div class="chat-permission-tool">${escapeHtml(agentName)} &rarr; ${escapeHtml(toolName)}</div>
                        <div class="chat-text">${escapeHtml(description.replace(/^🔐.*\n/, '').trim())}</div>
                        ${answered
                            ? `<div class="chat-permission-answered ${verdict === 'deny' ? 'denied' : 'approved'}">${verdict === 'always' ? '✅ Always Allowed' : verdict === 'approve' ? '✅ Approved' : '❌ Denied'}</div>`
                            : `<div class="chat-permission-actions">
                                <button class="btn btn-success btn-sm chat-perm-btn" onclick="sendPermissionVerdict('${reqId}', 'approve', this, '${escapeHtml(agentName)}')"><i class="bi bi-check-lg"></i> Approve</button>
                                <button class="btn btn-outline-success btn-sm chat-perm-btn" onclick="sendPermissionVerdict('${reqId}', 'always', this, '${escapeHtml(agentName)}')"><i class="bi bi-check-all"></i> Always</button>
                                <button class="btn btn-danger btn-sm chat-perm-btn" onclick="sendPermissionVerdict('${reqId}', 'deny', this, '${escapeHtml(agentName)}')"><i class="bi bi-x-lg"></i> Deny</button>
                               </div>`
                        }
                        <div class="chat-time">${time}</div>
                    </div>
                </div>`;
        } else {
            const msgText = cleanMsg ? `<div class="chat-text">${escapeHtml(cleanMsg)}</div>` : '';
            html += `
                <div class="chat-row ${isMe ? 'chat-row-me' : 'chat-row-them'}">
                    <div class="${bubbleClass}">
                        ${!isMe ? `<div class="chat-sender">${escapeHtml(name)}</div>` : ''}
                        ${imagesHtml}
                        ${msgText}
                        <div class="chat-time">${time}</div>
                    </div>
                </div>`;
        }
    });
    window_.innerHTML = html;
    window_.scrollTop = window_.scrollHeight;
    initElicitConditions();
}

function formatChatTime(ts) {
    if (!ts) return '';
    const d = new Date(ts);
    return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

async function getPhoneTerminalId() {
    if (phoneTerminalId) return phoneTerminalId;
    const data = await api('/api/terminals');
    if (!data) return null;
    const terminals = Array.isArray(data) ? data : (data.terminals || []);
    const phone = terminals.find(t =>
        t.name === 'MultiRemote' || t.name === 'Phone' || t.docId === 'multi-remote-phone'
    );
    if (phone) {
        phoneTerminalId = phone.id || phone.terminalId || phone.docId;
        return phoneTerminalId;
    }
    // Auto-register if not found
    try {
        const res = await fetch('/api/terminals/register', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ name: 'MultiRemote' })
        });
        if (res.ok) {
            const reg = await res.json();
            phoneTerminalId = reg.terminalId || reg.id || reg.docId;
            console.log('Auto-registered phone terminal:', phoneTerminalId);
        } else {
            console.error('Failed to register phone terminal:', res.status);
        }
    } catch (err) {
        console.error('Phone terminal registration error:', err);
    }
    return phoneTerminalId;
}

async function pollMessages() {
    const fromId = await getPhoneTerminalId();
    if (!fromId) return;

    const data = await api(`/api/messages/${fromId}`);
    if (!data) return;

    const messages = Array.isArray(data) ? data : (data.messages || []);
    if (messages.length === 0) return;

    let newCount = 0;
    messages.forEach(msg => {
        const from = msg.from || msg.fromName || msg.fromTerminalId || 'Unknown';
        const messageText = msg.message || msg.content || '';
        const entry = {
            from,
            to: 'MultiRemote',
            message: messageText,
            timestamp: msg.timestamp || msg.sentAt || new Date().toISOString(),
            fromMe: false
        };
        // Extract image batch reference if present
        const refMatch = messageText.match(/\[ref:([a-f0-9]+)\]/);
        if (refMatch) {
            entry.imageBatchId = refMatch[1];
            entry.message = messageText.replace(/\n*📎.*\[ref:[a-f0-9]+\]/, '').trim();
        }
        // Deduplicate by timestamp + message text (null separator prevents key collision)
        const key = entry.timestamp + '\0' + messageText;
        if (!chatHistory.some(h => (h.timestamp + '\0' + (h._rawMessage || h.message)) === key)) {
            entry._rawMessage = messageText;
            chatHistory.push(entry);
            newCount++;
            // Fetch images async if batch reference found
            if (entry.imageBatchId) {
                fetchBatchImages(entry);
            }

            // Commentary detection — speak messages from Commentator
            if (from === 'Commentator' && typeof handleCommentary === 'function') {
                handleCommentary(messageText, from);
            }

            // Update badge (push notification handles the alert)
            if (currentView !== 'messages') {
                unreadCount++;
                updateMessageBadge();
            }
        }
    });

    if (newCount > 0) {
        saveChat();
        renderChat();
    }
}

async function fetchBatchImages(entry) {
    try {
        const data = await api(`/api/messages/images/${entry.imageBatchId}`);
        if (!data) return;
        const images = Array.isArray(data) ? data : [];
        if (images.length === 0) return;
        entry.imageThumbs = images.map(img => ({
            mime: img.mimeType || img.MimeType || 'image/jpeg',
            base64: img.base64Data || img.Base64Data
        }));
        saveChat();
        renderChat();
    } catch (err) {
        console.error('Failed to fetch batch images:', err);
    }
}

function saveChat() {
    // Keep last 200 messages
    if (chatHistory.length > 200) chatHistory = chatHistory.slice(-200);
    try {
        localStorage.setItem('cr-chat', JSON.stringify(chatHistory));
    } catch (e) {
        // Quota exceeded — trim aggressively and retry
        console.warn('Chat storage quota exceeded, trimming to 50 messages');
        chatHistory = chatHistory.slice(-50);
        try {
            localStorage.setItem('cr-chat', JSON.stringify(chatHistory));
        } catch (e2) {
            // Still failing — clear chat storage entirely
            console.error('Chat storage still failing, clearing cache');
            localStorage.removeItem('cr-chat');
        }
    }
}

function autoGrowInput(el) {
    // Reset to single row to get accurate scrollHeight on iOS
    el.style.height = 'auto';
    var newHeight = Math.min(el.scrollHeight, 120);
    el.style.height = newHeight + 'px';
    // Hide scrollbar unless at max height
    el.style.overflowY = el.scrollHeight > 120 ? 'auto' : 'hidden';
}

function chatKeydown(e) {
    if (e.key === 'Enter' && !e.shiftKey) {
        e.preventDefault();
        sendChat();
    }
}

async function sendChat() {
    const input = document.getElementById('chat-input');
    let message = input.value.trim();
    const to = document.getElementById('chat-target').value;
    const hasImages = pendingImages.length > 0;

    if (!message && !hasImages) return;
    if (!to) { showChatError('Select a recipient first'); return; }

    const sendBtn = document.getElementById('chat-send-btn');
    sendBtn.disabled = true;

    // Show spinner on send button during upload
    const btnIcon = sendBtn.querySelector('i');
    if (hasImages && btnIcon) {
        btnIcon.className = 'bi bi-arrow-repeat spin';
    }

    const fromId = await getPhoneTerminalId();
    if (!fromId) {
        showChatError('Phone terminal not registered — try refreshing');
        sendBtn.disabled = false;
        if (btnIcon) btnIcon.className = 'bi bi-send-fill';
        return;
    }

    let sendOk = false;
    try {
        // Upload images first if any are pending
        let batchId = null;
        let imageCount = 0;
        if (hasImages) {
            imageCount = pendingImages.length;
            batchId = await uploadImageBatch();
            if (!batchId) {
                showChatError('Image upload failed');
                sendBtn.disabled = false;
                if (btnIcon) btnIcon.className = 'bi bi-send-fill';
                return;
            }
            // Append image reference to message
            const refTag = `\n\n📎 ${imageCount} image(s) attached [ref:${batchId}]`;
            message = message ? message + refTag : `📎 ${imageCount} image(s) attached [ref:${batchId}]`;
        }

        const endpoint = to === 'all' ? '/api/messages/broadcast' : '/api/messages/send';
        const payload = to === 'all'
            ? { fromTerminalId: fromId, message }
            : { fromTerminalId: fromId, to, message };

        const res = await fetch(endpoint, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });

        if (res.ok) {
            const result = await res.json().catch(() => null);
            if (result && result.success === false) {
                showChatError(`Send failed — ${result.error || 'unknown error'}`);
                return;
            }
            sendOk = true;
            // Show in local chat with display-friendly text
            const displayMsg = input.value.trim() || (hasImages ? `📎 Sent ${imageCount} image(s)` : '');
            const entry = {
                from: 'MultiRemote',
                to: to,
                message: displayMsg,
                timestamp: new Date().toISOString(),
                fromMe: true
            };
            if (batchId) {
                entry.imageBatchId = batchId;
                entry.imageThumbs = pendingImages.map(img => ({
                    mime: img.mime,
                    base64: img.base64
                }));
            }
            chatHistory.push(entry);
            saveChat();
            renderChat();
            clearPendingImages();
        } else {
            let detail = `Server error ${res.status}`;
            try { const j = await res.json(); detail = j.error || j.message || detail; } catch {}
            showChatError(`Send failed — ${detail}`);
        }
    } catch (err) {
        console.error('Failed to send message:', err);
        showChatError(`Send failed — ${err.message || 'check your connection'}`);
    } finally {
        if (sendOk) {
            input.value = '';
            input.style.height = 'auto';
            clearPendingImages();
        }
        input.focus();
        sendBtn.disabled = false;
        if (btnIcon) btnIcon.className = 'bi bi-send-fill';
    }
}

async function sendPermissionVerdict(requestId, action, btnEl, agentName) {
    // Disable both buttons immediately
    const actionsDiv = btnEl.closest('.chat-permission-actions');
    if (actionsDiv) {
        actionsDiv.querySelectorAll('button').forEach(b => b.disabled = true);
    }

    const verdictWord = action === 'approve' ? 'yes' : action === 'always' ? 'always' : 'no';
    const verdict = `${verdictWord} ${requestId}`;
    const to = agentName || document.getElementById('chat-target').value;
    const fromId = await getPhoneTerminalId();

    if (!fromId || !to) {
        showChatError('Cannot send verdict — no terminal connection');
        if (actionsDiv) actionsDiv.querySelectorAll('button').forEach(b => b.disabled = false);
        return;
    }

    try {
        const res = await fetch('/api/messages/send', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ fromTerminalId: fromId, to, message: verdict })
        });

        if (res.ok) {
            // Mark this message as answered in chat history
            const permMsg = chatHistory.find(m =>
                m.message && m.message.includes(`[PERMISSION_REQUEST:${requestId}:`)
            );
            if (permMsg) {
                permMsg._permissionAnswered = true;
                permMsg._permissionVerdict = action;
                saveChat();
            }
            renderChat();
        } else {
            showChatError('Failed to send verdict');
            if (actionsDiv) actionsDiv.querySelectorAll('button').forEach(b => b.disabled = false);
        }
    } catch (err) {
        showChatError(`Verdict failed: ${err.message}`);
        if (actionsDiv) actionsDiv.querySelectorAll('button').forEach(b => b.disabled = false);
    }
}

function renderElicitField(elicitId, key, prop, isRequired) {
    const label = prop.title || key;
    const reqMark = isRequired ? '<span class="text-danger">*</span>' : '';
    const fieldId = `elicit-${elicitId}-${key}`;

    if (prop.type === 'array' && prop.items && prop.items.enum) {
        // Multi-select: checkboxes for array of enum values
        let checksHtml = '';
        prop.items.enum.forEach(val => {
            checksHtml += `
                <label class="elicit-radio-label">
                    <input type="checkbox" name="${fieldId}" value="${escapeHtml(val)}">
                    <span>${escapeHtml(val)}</span>
                </label>`;
        });
        return `<div class="elicit-field"><label>${escapeHtml(label)} ${reqMark}</label><div class="elicit-radios">${checksHtml}</div></div>`;
    } else if (prop.enum) {
        if (prop.enum.length <= 6) {
            let radiosHtml = '';
            prop.enum.forEach((val, i) => {
                radiosHtml += `
                    <label class="elicit-radio-label">
                        <input type="radio" name="${fieldId}" value="${escapeHtml(val)}" ${i === 0 ? 'checked' : ''}>
                        <span>${escapeHtml(val)}</span>
                    </label>`;
            });
            return `<div class="elicit-field"><label>${escapeHtml(label)} ${reqMark}</label><div class="elicit-radios">${radiosHtml}</div></div>`;
        } else {
            let optsHtml = prop.enum.map(v => `<option value="${escapeHtml(v)}">${escapeHtml(v)}</option>`).join('');
            return `<div class="elicit-field"><label>${escapeHtml(label)} ${reqMark}</label><select id="${fieldId}" class="form-select bg-dark text-light border-secondary">${optsHtml}</select></div>`;
        }
    } else if (prop.type === 'boolean') {
        return `
            <div class="elicit-field elicit-toggle-row">
                <label>${escapeHtml(label)} ${reqMark}</label>
                <div class="form-check form-switch">
                    <input class="form-check-input" type="checkbox" id="${fieldId}">
                </div>
            </div>`;
    } else if (prop.type === 'number' || prop.type === 'integer') {
        return `<div class="elicit-field"><label>${escapeHtml(label)} ${reqMark}</label><input type="number" id="${fieldId}" class="form-control bg-dark text-light border-secondary" placeholder="${escapeHtml(prop.description || '')}"></div>`;
    } else {
        const inputType = prop.format === 'password' ? 'password' : 'text';
        return `<div class="elicit-field"><label>${escapeHtml(label)} ${reqMark}</label><input type="${inputType}" id="${fieldId}" class="form-control bg-dark text-light border-secondary" placeholder="${escapeHtml(prop.description || '')}"></div>`;
    }
}

function renderElicitationForm(elicitId, originalElicitId, schema) {
    const props = schema.properties || {};
    const required = schema.required || [];

    // Store schema for conditional evaluation
    _elicitSchemas[elicitId] = schema;

    let fieldsHtml = '';

    // Render main properties
    for (const [key, prop] of Object.entries(props)) {
        fieldsHtml += renderElicitField(elicitId, key, prop, required.includes(key));
    }

    // Collect conditional blocks from if/then/else (root + allOf)
    const condBlocks = [];
    if (schema.if && (schema.then || schema.else)) {
        condBlocks.push({ if: schema.if, then: schema.then, else: schema.else });
    }
    if (schema.allOf) {
        schema.allOf.forEach(sub => {
            if (sub.if && (sub.then || sub.else)) {
                condBlocks.push({ if: sub.if, then: sub.then, else: sub.else });
            }
        });
    }

    // Render conditional properties (hidden initially)
    condBlocks.forEach((block, idx) => {
        const thenProps = (block.then && block.then.properties) || {};
        const thenReq = (block.then && block.then.required) || [];
        let thenHtml = '';
        for (const [key, prop] of Object.entries(thenProps)) {
            if (!props[key]) thenHtml += renderElicitField(elicitId, key, prop, thenReq.includes(key));
        }
        if (thenHtml) {
            fieldsHtml += `<div class="elicit-conditional" data-cond-idx="${idx}" data-cond-branch="then" style="display:none">${thenHtml}</div>`;
        }

        const elseProps = (block.else && block.else.properties) || {};
        const elseReq = (block.else && block.else.required) || [];
        let elseHtml = '';
        for (const [key, prop] of Object.entries(elseProps)) {
            if (!props[key]) elseHtml += renderElicitField(elicitId, key, prop, elseReq.includes(key));
        }
        if (elseHtml) {
            fieldsHtml += `<div class="elicit-conditional" data-cond-idx="${idx}" data-cond-branch="else" style="display:none">${elseHtml}</div>`;
        }
    });

    // Render dependentSchemas properties (hidden until trigger has value)
    if (schema.dependentSchemas) {
        for (const [trigger, depSchema] of Object.entries(schema.dependentSchemas)) {
            const depProps = depSchema.properties || {};
            const depReq = depSchema.required || [];
            let depHtml = '';
            for (const [key, prop] of Object.entries(depProps)) {
                if (!props[key]) depHtml += renderElicitField(elicitId, key, prop, depReq.includes(key));
            }
            if (depHtml) {
                fieldsHtml += `<div class="elicit-conditional" data-depends-on="${escapeHtml(trigger)}" style="display:none">${depHtml}</div>`;
            }
        }
    }

    const hasConditionals = condBlocks.length > 0 || schema.dependentSchemas;
    const changeHandler = hasConditionals
        ? `oninput="evaluateElicitConditions('${elicitId}')" onchange="evaluateElicitConditions('${elicitId}')"`
        : '';

    return `
        <form class="elicit-form" id="elicit-form-${elicitId}" data-original-id="${escapeHtml(originalElicitId)}" onsubmit="submitElicitation('${elicitId}', this, event)" ${changeHandler}>
            ${fieldsHtml}
            <div class="chat-permission-actions">
                <button type="submit" class="btn btn-success btn-sm chat-perm-btn"><i class="bi bi-check-lg"></i> Submit</button>
                <button type="button" class="btn btn-secondary btn-sm chat-perm-btn" onclick="declineElicitation('${elicitId}', this)"><i class="bi bi-x-lg"></i> Cancel</button>
            </div>
        </form>`;
}

function initElicitConditions() {
    for (const elicitId in _elicitSchemas) {
        if (document.getElementById(`elicit-form-${elicitId}`)) {
            evaluateElicitConditions(elicitId);
        }
    }
}

function evaluateElicitConditions(elicitId) {
    const schema = _elicitSchemas[elicitId];
    const form = document.getElementById(`elicit-form-${elicitId}`);
    if (!schema || !form) return;

    const values = getElicitFormValues(form);

    // Evaluate if/then/else blocks
    const condBlocks = [];
    if (schema.if && (schema.then || schema.else)) {
        condBlocks.push({ if: schema.if, then: schema.then, else: schema.else });
    }
    if (schema.allOf) {
        schema.allOf.forEach(sub => {
            if (sub.if && (sub.then || sub.else)) {
                condBlocks.push({ if: sub.if, then: sub.then, else: sub.else });
            }
        });
    }

    condBlocks.forEach((block, idx) => {
        const match = checkIfCondition(block.if, values);
        const thenEl = form.querySelector(`[data-cond-idx="${idx}"][data-cond-branch="then"]`);
        const elseEl = form.querySelector(`[data-cond-idx="${idx}"][data-cond-branch="else"]`);
        if (thenEl) thenEl.style.display = match ? '' : 'none';
        if (elseEl) elseEl.style.display = match ? 'none' : '';
    });

    // Evaluate dependentSchemas
    if (schema.dependentSchemas) {
        for (const trigger of Object.keys(schema.dependentSchemas)) {
            const depEl = form.querySelector(`[data-depends-on="${trigger}"]`);
            if (depEl) {
                const val = values[trigger];
                const hasValue = val !== undefined && val !== '' && val !== null && val !== false;
                depEl.style.display = hasValue ? '' : 'none';
            }
        }
    }
}

function getElicitFormValues(form) {
    const values = {};
    form.querySelectorAll('input, select').forEach(el => {
        const id = el.id || '';
        const keyMatch = id.match(/^elicit-[^-]+-(.+)$/);

        if (el.type === 'radio') {
            if (el.checked) {
                const radioKey = el.name.match(/^elicit-[^-]+-(.+)$/);
                if (radioKey) values[radioKey[1]] = el.value;
            }
        } else if (keyMatch) {
            if (el.type === 'checkbox') {
                values[keyMatch[1]] = el.checked;
            } else if (el.type === 'number') {
                values[keyMatch[1]] = el.value ? Number(el.value) : null;
            } else {
                values[keyMatch[1]] = el.value;
            }
        }
    });
    return values;
}

function checkIfCondition(ifSchema, values) {
    if (!ifSchema) return false;

    // Check property constraints (AND logic — all must match)
    if (ifSchema.properties) {
        for (const [key, constraint] of Object.entries(ifSchema.properties)) {
            const val = values[key];
            if ('const' in constraint) {
                // Exact match (loose string comparison for radio values)
                if (val !== constraint.const && String(val) !== String(constraint.const)) return false;
            } else if (constraint.enum) {
                if (!constraint.enum.includes(val) && !constraint.enum.map(String).includes(String(val))) return false;
            }
        }
    }

    // Check required — all listed properties must have a non-empty value
    if (ifSchema.required) {
        for (const key of ifSchema.required) {
            const val = values[key];
            if (val === undefined || val === '' || val === null) return false;
        }
    }

    return true;
}

async function submitElicitation(elicitId, formEl, event) {
    if (event) event.preventDefault();

    // Use original (unsanitized) ID for API calls and chatHistory lookups
    const originalId = formEl.dataset.originalId || elicitId;

    // Disable buttons
    formEl.querySelectorAll('button').forEach(b => b.disabled = true);

    // Collect form values — skip inputs inside hidden conditional containers
    const content = {};
    formEl.querySelectorAll('input, select').forEach(el => {
        // Skip fields in hidden conditional sections
        const condParent = el.closest('.elicit-conditional');
        if (condParent && condParent.style.display === 'none') return;

        const id = el.id || '';
        const keyMatch = id.match(/^elicit-[^-]+-(.+)$/);
        if (!keyMatch && el.type !== 'radio' && el.type !== 'checkbox') return;

        if (el.type === 'radio') {
            if (el.checked) {
                const radioKey = el.name.match(/^elicit-[^-]+-(.+)$/);
                if (radioKey) content[radioKey[1]] = el.value;
            }
        } else if (el.type === 'checkbox' && !el.classList.contains('form-check-input')) {
            // Multi-select checkbox (not a boolean toggle)
            const checkKey = el.name.match(/^elicit-[^-]+-(.+)$/);
            if (checkKey) {
                if (!content[checkKey[1]]) content[checkKey[1]] = [];
                if (el.checked) content[checkKey[1]].push(el.value);
            }
        } else if (el.type === 'checkbox') {
            content[keyMatch[1]] = el.checked;
        } else if (el.type === 'number') {
            content[keyMatch[1]] = el.value ? Number(el.value) : null;
        } else {
            content[keyMatch[1]] = el.value;
        }
    });

    try {
        const res = await fetch(`/api/messages/elicitations/${originalId}/respond`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ action: 'accept', contentJson: JSON.stringify(content) })
        });

        if (res.ok) {
            const msg = chatHistory.find(m => m.message && m.message.includes(`[ELICITATION_REQUEST:${originalId}:`));
            if (msg) {
                msg._elicitationAnswered = true;
                msg._elicitationAction = 'accept';
                saveChat();
            }
            renderChat();
        } else {
            showChatError('Failed to submit form');
            formEl.querySelectorAll('button').forEach(b => b.disabled = false);
        }
    } catch (err) {
        showChatError(`Submit failed: ${err.message}`);
        formEl.querySelectorAll('button').forEach(b => b.disabled = false);
    }
}

async function declineElicitation(elicitId, btnEl) {
    const formEl = btnEl.closest('.elicit-form');
    const originalId = formEl ? (formEl.dataset.originalId || elicitId) : elicitId;
    if (formEl) formEl.querySelectorAll('button').forEach(b => b.disabled = true);

    try {
        const res = await fetch(`/api/messages/elicitations/${originalId}/respond`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ action: 'decline', contentJson: '{}' })
        });

        if (res.ok) {
            const msg = chatHistory.find(m => m.message && m.message.includes(`[ELICITATION_REQUEST:${originalId}:`));
            if (msg) {
                msg._elicitationAnswered = true;
                msg._elicitationAction = 'decline';
                saveChat();
            }
            renderChat();
        } else {
            showChatError('Failed to decline');
            if (formEl) formEl.querySelectorAll('button').forEach(b => b.disabled = false);
        }
    } catch (err) {
        showChatError(`Decline failed: ${err.message}`);
        if (formEl) formEl.querySelectorAll('button').forEach(b => b.disabled = false);
    }
}

function showChatError(msg) {
    const chatArea = document.getElementById('chat-window');
    if (!chatArea) return;
    const errDiv = document.createElement('div');
    errDiv.className = 'chat-error';
    errDiv.textContent = msg;
    chatArea.appendChild(errDiv);
    chatArea.scrollTop = chatArea.scrollHeight;
    setTimeout(() => errDiv.remove(), 5000);
}

// Voice input using Web Speech API
let recognition = null;
let isListening = false;

function toggleVoice() {
    if (isListening) {
        stopVoice();
        return;
    }

    const SpeechRecognition = window.SpeechRecognition || window.webkitSpeechRecognition;
    if (!SpeechRecognition) {
        alert('Speech recognition is not supported on this browser.');
        return;
    }

    recognition = new SpeechRecognition();
    recognition.lang = 'en-US';
    recognition.interimResults = true;
    recognition.continuous = true;

    const input = document.getElementById('chat-input');
    const micBtn = document.getElementById('chat-mic-btn');
    const startText = input.value;

    recognition.onstart = () => {
        isListening = true;
        micBtn.classList.remove('btn-outline-light');
        micBtn.classList.add('btn-danger');
        input.placeholder = 'Listening...';
    };

    recognition.onresult = (event) => {
        let transcript = '';
        for (let i = 0; i < event.results.length; i++) {
            transcript += event.results[i][0].transcript;
        }
        input.value = startText + (startText ? ' ' : '') + transcript;
        autoGrowInput(input);
    };

    recognition.onerror = (event) => {
        if (event.error !== 'no-speech') {
            console.error('Speech error:', event.error);
        }
        stopVoice();
    };

    recognition.onend = () => {
        stopVoice();
    };

    recognition.start();
}

function stopVoice() {
    isListening = false;
    if (recognition) {
        recognition.stop();
        recognition = null;
    }
    const micBtn = document.getElementById('chat-mic-btn');
    if (micBtn) {
        micBtn.classList.remove('btn-danger');
        micBtn.classList.add('btn-outline-light');
    }
    const input = document.getElementById('chat-input');
    if (input) input.placeholder = 'Type or tap mic...';
}

// New message alerts — badge + sound + vibrate
let unreadCount = 0;

function updateMessageBadge() {
    const msgTab = document.querySelector('[data-view="messages"]');
    if (!msgTab) return;
    let badge = msgTab.querySelector('.msg-badge');
    if (unreadCount > 0) {
        if (!badge) {
            badge = document.createElement('span');
            badge.className = 'msg-badge';
            msgTab.appendChild(badge);
        }
        badge.textContent = unreadCount;
    } else if (badge) {
        badge.remove();
    }
}

function clearMessageBadge() {
    unreadCount = 0;
    updateMessageBadge();
}

// ============ iMessage-style Image Sending ============

let pendingImages = []; // Array of { name, mime, base64 }

function openImagePicker() {
    document.getElementById('image-file-input').click();
}

function onImagesSelected(event) {
    const files = Array.from(event.target.files);
    if (!files.length) return;

    const imageFiles = files.filter(f => f.type.startsWith('image/'));
    if (imageFiles.length === 0) {
        showChatError('Only image files are supported');
        event.target.value = '';
        return;
    }

    if (pendingImages.length + imageFiles.length > 10) {
        showChatError('Maximum 10 images at a time');
        event.target.value = '';
        return;
    }

    imageFiles.forEach(file => {
        const reader = new FileReader();
        reader.onload = async (e) => {
            const dataUrl = e.target.result;
            let base64;
            if (file.size > 5 * 1024 * 1024) {
                base64 = await resizeImage(dataUrl, 1920);
            } else {
                base64 = dataUrl.split(',')[1];
            }
            pendingImages.push({ name: file.name, mime: file.type, base64 });
            renderImagePreviews();
        };
        reader.readAsDataURL(file);
    });
    event.target.value = '';
}

async function resizeImage(dataUrl, maxDim) {
    return new Promise((resolve) => {
        const img = new Image();
        img.onload = () => {
            let w = img.width, h = img.height;
            if (w > maxDim || h > maxDim) {
                const ratio = Math.min(maxDim / w, maxDim / h);
                w = Math.round(w * ratio);
                h = Math.round(h * ratio);
            }
            const canvas = document.createElement('canvas');
            canvas.width = w;
            canvas.height = h;
            canvas.getContext('2d').drawImage(img, 0, 0, w, h);
            const resized = canvas.toDataURL('image/jpeg', 0.85);
            resolve(resized.split(',')[1]);
        };
        img.src = dataUrl;
    });
}

function renderImagePreviews() {
    const strip = document.getElementById('image-preview-strip');
    if (!strip) return;

    if (pendingImages.length === 0) {
        strip.style.display = 'none';
        strip.innerHTML = '';
        return;
    }

    strip.style.display = 'flex';
    let html = '';
    pendingImages.forEach((img, idx) => {
        const dataUrl = `data:${img.mime};base64,${img.base64}`;
        html += `<div class="image-preview-thumb" onclick="previewImage(${idx})">
            <img src="${dataUrl}" alt="${escapeHtml(img.name)}">
            <button class="image-preview-remove" onclick="event.stopPropagation(); removeImage(${idx})">✕</button>
        </div>`;
    });
    html += `<button class="image-preview-add" onclick="openImagePicker()" title="Add more">
        <i class="bi bi-plus-lg"></i>
    </button>`;
    strip.innerHTML = html;
}

function previewImage(index) {
    const img = pendingImages[index];
    if (!img) return;
    const lightbox = document.getElementById('image-lightbox');
    const lbImg = document.getElementById('lightbox-img');
    lbImg.src = `data:${img.mime};base64,${img.base64}`;
    lightbox.style.display = 'flex';
}

function closeLightbox() {
    document.getElementById('image-lightbox').style.display = 'none';
}

function openChatImage(src) {
    const lightbox = document.getElementById('image-lightbox');
    const lbImg = document.getElementById('lightbox-img');
    lbImg.src = src;
    lightbox.style.display = 'flex';
}

function removeImage(index) {
    pendingImages.splice(index, 1);
    renderImagePreviews();
}

function clearPendingImages() {
    pendingImages = [];
    renderImagePreviews();
}

async function uploadImageBatch() {
    if (pendingImages.length === 0) return null;

    const payload = {
        images: pendingImages.map(img => ({
            fileName: img.name,
            mimeType: img.mime,
            base64Data: img.base64
        }))
    };

    const res = await fetch('/api/messages/images', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
    });

    if (!res.ok) {
        let detail = `Upload failed (${res.status})`;
        try { const j = await res.json(); detail = j.error || detail; } catch {}
        throw new Error(detail);
    }

    const result = await res.json();
    return result.batchId;
}
