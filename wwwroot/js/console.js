// MultiRemote — Interactive Terminal (xterm.js)

let consoleTerminal = null;
let consoleWs = null;
let consoleFitAddon = null;
let consoleConnectedId = null;
let consoleConnectedName = null;
let consoleResizeTimeout = null;
let consoleReconnectTimer = null;
let consoleReconnectAttempts = 0;
let consoleUserDisconnected = false;
const CONSOLE_MAX_RECONNECT_ATTEMPTS = 5;
const CONSOLE_RECONNECT_BASE_DELAY = 2000;
const consoleDecoder = new TextDecoder();
const consoleEncoder = new TextEncoder();

function isMobile() {
    return /iPhone|iPad|iPod|Android/i.test(navigator.userAgent);
}

// Touch scroll addon for xterm.js — fixes broken iOS Safari touch scrolling
// Root cause: passive touchstart lets iOS gesture recognizer claim the touch,
// which stops delivering touchmove events to JS. Fix: non-passive touchstart
// with preventDefault() tells iOS we're handling this gesture.
// Also disables pointer-events on .xterm-screen during scroll to prevent
// xterm's own touch/gesture system from competing.
class TouchScrollAddon {
    activate(terminal) {
        this._terminal = terminal;
        const el = terminal.element;
        if (!el) return;

        const viewport = el.querySelector('.xterm-viewport');
        const screen = el.querySelector('.xterm-screen');
        if (!viewport || !screen) return;

        let startY = null;
        let startScrollTop = 0;
        let velocity = 0;
        let lastY = 0;
        let lastTime = 0;
        let momentumFrame = null;
        let isScrolling = false;
        const friction = 0.95;
        const minVelocity = 0.5;

        this._onTouchStart = (e) => {
            if (momentumFrame) {
                cancelAnimationFrame(momentumFrame);
                momentumFrame = null;
            }
            if (e.touches.length === 1) {
                e.preventDefault(); // CRITICAL: prevent iOS native gesture recognizer from claiming touch
                startY = e.touches[0].clientY;
                lastY = startY;
                startScrollTop = viewport.scrollTop;
                lastTime = Date.now();
                velocity = 0;
                isScrolling = false;
            }
        };

        this._onTouchMove = (e) => {
            if (startY !== null && e.touches.length === 1) {
                e.preventDefault();

                const y = e.touches[0].clientY;
                const totalDy = startY - y;

                // Start scroll mode after 6px of movement
                if (!isScrolling && Math.abs(totalDy) > 6) {
                    isScrolling = true;
                    // Disable pointer-events on screen to block xterm's touch handlers
                    screen.style.pointerEvents = 'none';
                }

                if (isScrolling) {
                    const now = Date.now();
                    const dt = now - lastTime;

                    if (dt > 0) {
                        const frameDy = lastY - y;
                        velocity = (frameDy / dt) * 16; // px per frame
                    }

                    viewport.scrollTop = startScrollTop + totalDy;

                    lastY = y;
                    lastTime = now;
                }
            }
        };

        this._onTouchEnd = () => {
            startY = null;

            // Restore pointer-events
            screen.style.pointerEvents = '';
            isScrolling = false;

            // Momentum scrolling
            let v = velocity;
            const momentum = () => {
                if (Math.abs(v) < minVelocity) return;
                viewport.scrollTop += v;
                v *= friction;
                momentumFrame = requestAnimationFrame(momentum);
            };

            if (Math.abs(v) > minVelocity) {
                momentumFrame = requestAnimationFrame(momentum);
            }
        };

        // NON-PASSIVE touchstart is the key iOS fix — prevents native scroll claim
        el.addEventListener('touchstart', this._onTouchStart, { passive: false });
        el.addEventListener('touchmove', this._onTouchMove, { passive: false });
        el.addEventListener('touchend', this._onTouchEnd, { passive: true });

        this._el = el;
    }

    dispose() {
        const el = this._el;
        if (!el) return;
        el.removeEventListener('touchstart', this._onTouchStart);
        el.removeEventListener('touchmove', this._onTouchMove);
        el.removeEventListener('touchend', this._onTouchEnd);
    }
}

// Initialize xterm.js terminal instance (once)
function initConsoleTerminal() {
    if (consoleTerminal) return;

    const container = document.getElementById('console-terminal');
    if (!container) return;

    consoleTerminal = new Terminal({
        cursorBlink: true,
        fontSize: 14,
        fontFamily: "'Cascadia Code', 'Fira Code', 'Consolas', monospace",
        theme: {
            background: '#0f172a',
            foreground: '#f1f5f9',
            cursor: '#6366f1',
            selectionBackground: '#334155',
            black: '#1e293b',
            red: '#ef4444',
            green: '#22c55e',
            yellow: '#f59e0b',
            blue: '#6366f1',
            magenta: '#a855f7',
            cyan: '#06b6d4',
            white: '#f1f5f9',
            brightBlack: '#475569',
            brightRed: '#f87171',
            brightGreen: '#4ade80',
            brightYellow: '#fbbf24',
            brightBlue: '#818cf8',
            brightMagenta: '#c084fc',
            brightCyan: '#22d3ee',
            brightWhite: '#ffffff',
        },
        allowProposedApi: true,
        scrollback: 5000,
        convertEol: true,
    });

    // Fit addon — auto-sizes terminal to container
    consoleFitAddon = new FitAddon.FitAddon();
    consoleTerminal.loadAddon(consoleFitAddon);

    // Web links addon — clickable URLs
    const webLinksAddon = new WebLinksAddon.WebLinksAddon();
    consoleTerminal.loadAddon(webLinksAddon);

    consoleTerminal.open(container);
    consoleFitAddon.fit();

    // Mobile: load touch scroll addon
    if (isMobile()) {
        consoleTerminal.loadAddon(new TouchScrollAddon());
    }

    // Handle user input → send to WebSocket
    consoleTerminal.onData(data => {
        if (consoleWs && consoleWs.readyState === WebSocket.OPEN) {
            consoleWs.send(consoleEncoder.encode(data));
        }
    });

    // Handle resize
    consoleTerminal.onResize(({ cols, rows }) => {
        sendResizeMessage(cols, rows);
    });

    // Window resize → refit terminal
    window.addEventListener('resize', () => {
        clearTimeout(consoleResizeTimeout);
        consoleResizeTimeout = setTimeout(() => {
            if (consoleFitAddon && document.getElementById('view-console')?.classList.contains('active')) {
                consoleFitAddon.fit();
            }
        }, 150);
    });

    // Mobile keyboard support: only focus on tap, not swipe
    if (isMobile()) {
        let touchStartY = 0;
        let touchStartTime = 0;
        container.addEventListener('touchstart', (e) => {
            touchStartY = e.touches[0].clientY;
            touchStartTime = Date.now();
        }, { passive: true });
        container.addEventListener('touchend', (e) => {
            const dy = Math.abs(e.changedTouches[0].clientY - touchStartY);
            const dt = Date.now() - touchStartTime;
            if (dy < 10 && dt < 300) {
                const input = document.getElementById('console-mobile-input');
                if (input) input.focus();
            }
        }, { passive: true });
    }

    // Hidden textarea → relay input to terminal
    const mobileInput = document.getElementById('console-mobile-input');
    if (mobileInput) {
        mobileInput.addEventListener('input', (e) => {
            const data = e.target.value;
            if (data && consoleWs && consoleWs.readyState === WebSocket.OPEN) {
                consoleWs.send(consoleEncoder.encode(data));
            }
            e.target.value = '';
        });
        mobileInput.addEventListener('keydown', (e) => {
            let data = null;
            switch (e.key) {
                case 'Enter': data = '\r'; break;
                case 'Backspace': data = '\x7f'; break;
                case 'ArrowUp': data = '\x1b[A'; break;
                case 'ArrowDown': data = '\x1b[B'; break;
                case 'ArrowRight': data = '\x1b[C'; break;
                case 'ArrowLeft': data = '\x1b[D'; break;
                case 'Tab': data = '\t'; break;
                case 'Escape': data = '\x1b'; break;
                default: return;
            }
            e.preventDefault();
            e.target.value = '';
            if (data && consoleWs && consoleWs.readyState === WebSocket.OPEN) {
                consoleWs.send(consoleEncoder.encode(data));
            }
        });
    }

    consoleTerminal.writeln('\x1b[36mMultiRemote Console\x1b[0m');
    consoleTerminal.writeln('Select a terminal and tap Connect to begin.\r\n');
}

function sendResizeMessage(cols, rows) {
    if (consoleWs && consoleWs.readyState === WebSocket.OPEN) {
        consoleWs.send(JSON.stringify({ type: 'resize', cols, rows }));
    }
}

// Send a special keystroke sequence to the connected terminal
function sendHotkey(seq) {
    if (consoleWs && consoleWs.readyState === WebSocket.OPEN) {
        consoleWs.send(consoleEncoder.encode(seq));
    }
}

// Shared WebSocket event handler setup
function setupWebSocketHandlers(ws, terminalId, terminalName, isReconnect) {
    ws.onopen = () => {
        consoleConnectedId = terminalId;
        consoleConnectedName = terminalName;
        consoleReconnectAttempts = 0;

        if (isReconnect) {
            consoleTerminal.writeln(`\x1b[32m[Reconnected]\x1b[0m\r\n`);
        }

        updateConsoleStatus('connected', terminalName);
        updateConsoleButtons(true);

        if (consoleFitAddon) {
            consoleFitAddon.fit();
            const dims = consoleFitAddon.proposeDimensions();
            if (dims) sendResizeMessage(dims.cols, dims.rows);
        }

        consoleTerminal.focus();
    };

    ws.onmessage = (event) => {
        if (event.data instanceof ArrayBuffer) {
            consoleTerminal.write(consoleDecoder.decode(event.data));
            consoleTerminal.scrollToBottom();
        } else {
            try {
                const msg = JSON.parse(event.data);
                if (msg.type === 'connected' && msg.cols && msg.rows) {
                    consoleTerminal.resize(msg.cols, msg.rows);
                } else if (msg.type === 'exited') {
                    consoleTerminal.writeln('\r\n\x1b[31m[Terminal process exited]\x1b[0m');
                } else if (msg.type === 'error') {
                    consoleTerminal.writeln(`\r\n\x1b[31mError: ${msg.message}\x1b[0m`);
                }
            } catch {
                consoleTerminal.write(event.data);
                consoleTerminal.scrollToBottom();
            }
        }
    };

    ws.onclose = (event) => {
        if (consoleUserDisconnected) return;

        const wasConnected = consoleConnectedId;
        const reason = event.reason || 'Connection closed';

        if (event.code === 1000) {
            consoleTerminal.writeln(`\r\n\x1b[31m[Disconnected: ${reason}]\x1b[0m`);
            consoleConnectedId = null;
            consoleConnectedName = null;
            updateConsoleStatus('disconnected');
            updateConsoleButtons(false);
            return;
        }

        if (wasConnected && consoleReconnectAttempts < CONSOLE_MAX_RECONNECT_ATTEMPTS) {
            consoleConnectedId = null;
            const delay = CONSOLE_RECONNECT_BASE_DELAY * Math.pow(1.5, consoleReconnectAttempts);
            consoleReconnectAttempts++;
            consoleTerminal.writeln(`\r\n\x1b[33m[Connection lost. Reconnecting in ${Math.round(delay/1000)}s... (attempt ${consoleReconnectAttempts}/${CONSOLE_MAX_RECONNECT_ATTEMPTS})]\x1b[0m`);
            updateConsoleStatus('reconnecting');
            consoleReconnectTimer = setTimeout(() => {
                openWebSocket(terminalId, terminalName, true);
            }, delay);
        } else {
            consoleTerminal.writeln(`\r\n\x1b[31m[Disconnected: ${reason}]\x1b[0m`);
            if (consoleReconnectAttempts >= CONSOLE_MAX_RECONNECT_ATTEMPTS) {
                consoleTerminal.writeln('\x1b[31m[Max reconnect attempts reached. Tap Connect to try again.]\x1b[0m');
            }
            consoleConnectedId = null;
            consoleConnectedName = null;
            updateConsoleStatus('disconnected');
            updateConsoleButtons(false);
        }
    };

    ws.onerror = () => {};
}

function openWebSocket(terminalId, terminalName, isReconnect) {
    if (consoleConnectedId) {
        console.warn('openWebSocket: already connected, ignoring call');
        return;
    }
    consoleUserDisconnected = false;

    if (isReconnect) {
        consoleTerminal.writeln(`\x1b[33m[Reconnecting to ${terminalName}...]\x1b[0m`);
    }

    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const wsUrl = `${proto}//${location.host}/api/terminal/${terminalId}/ws`;

    consoleWs = new WebSocket(wsUrl);
    consoleWs.binaryType = 'arraybuffer';
    setupWebSocketHandlers(consoleWs, terminalId, terminalName, isReconnect);
}

// Load terminal list into the picker dropdown
async function refreshConsoleTerminals() {
    const select = document.getElementById('console-terminal-select');
    if (!select) return;

    const data = await api('/api/terminals');
    const terminals = data ? (Array.isArray(data) ? data : (data.terminals || [])) : [];

    const currentVal = select.value;
    select.innerHTML = '<option value="">Select terminal...</option>';

    terminals
        .filter(t => {
            const name = (t.name || t.terminalName || '').toLowerCase();
            return name !== 'multiremote';
        })
        .forEach(t => {
            const name = t.name || t.terminalName || 'Unknown';
            const id = t.id || t.terminalId || '';
            const isActive = isTerminalActive(t);
            const dot = isActive ? '\u25CF' : '\u25CB';
            const opt = document.createElement('option');
            opt.value = id;
            opt.textContent = `${dot} ${name}`;
            opt.dataset.name = name;
            select.appendChild(opt);
        });

    if (currentVal) select.value = currentVal;
    updateConnectButton();
}

function updateConnectButton() {
    const select = document.getElementById('console-terminal-select');
    const connectBtn = document.getElementById('console-connect-btn');
    if (connectBtn) {
        connectBtn.disabled = !select?.value || !!consoleConnectedId;
    }
}

function connectTerminal() {
    const select = document.getElementById('console-terminal-select');
    const terminalId = select?.value;
    if (!terminalId) return;

    const terminalName = select.selectedOptions[0]?.dataset.name || 'terminal';

    initConsoleTerminal();
    consoleTerminal.clear();
    consoleTerminal.writeln(`\x1b[33mConnecting to ${terminalName}...\x1b[0m\r\n`);

    openWebSocket(terminalId, terminalName, false);
}

function disconnectTerminal() {
    clearTimeout(consoleReconnectTimer);
    consoleReconnectAttempts = 0;
    consoleUserDisconnected = true;
    if (consoleWs) {
        consoleWs.close(1000, 'User disconnected');
        consoleWs = null;
    }
    consoleConnectedId = null;
    consoleConnectedName = null;
    updateConsoleStatus('disconnected');
    updateConsoleButtons(false);
}

function updateConsoleStatus(state, terminalName) {
    const statusEl = document.getElementById('console-status');
    if (!statusEl) return;
    if (state === 'connected') {
        statusEl.innerHTML = `<span class="status-dot online"></span> Connected to ${escapeHtml(terminalName)}`;
    } else if (state === 'reconnecting') {
        statusEl.innerHTML = `<span class="status-dot" style="background:var(--cr-warning)"></span> Reconnecting...`;
    } else {
        statusEl.innerHTML = `<span class="status-dot offline"></span> Not connected`;
    }
}

function updateConsoleButtons(connected) {
    const connectBtn = document.getElementById('console-connect-btn');
    const disconnectBtn = document.getElementById('console-disconnect-btn');
    const select = document.getElementById('console-terminal-select');
    const hotkeys = document.getElementById('console-hotkeys');
    if (connected) {
        connectBtn?.classList.add('d-none');
        disconnectBtn?.classList.remove('d-none');
        hotkeys?.classList.remove('d-none');
        if (select) select.disabled = true;
    } else {
        connectBtn?.classList.remove('d-none');
        disconnectBtn?.classList.add('d-none');
        hotkeys?.classList.add('d-none');
        if (select) select.disabled = false;
        updateConnectButton();
    }
}

// Listen for terminal select changes
document.getElementById('console-terminal-select')?.addEventListener('change', updateConnectButton);

// Clean up WebSocket on page unload
window.addEventListener('beforeunload', () => {
    clearTimeout(consoleReconnectTimer);
    if (consoleWs && consoleWs.readyState === WebSocket.OPEN) {
        consoleWs.close(1000, 'Page closing');
    }
});

// Handle page visibility — pause/resume reconnect when app is backgrounded
document.addEventListener('visibilitychange', () => {
    if (document.hidden) {
        clearTimeout(consoleReconnectTimer);
    } else {
        if (consoleConnectedName && !consoleConnectedId && consoleWs?.readyState !== WebSocket.OPEN) {
            const select = document.getElementById('console-terminal-select');
            if (select?.value) {
                consoleReconnectAttempts = 0;
                openWebSocket(select.value, consoleConnectedName, true);
            }
        }
        if (consoleFitAddon && document.getElementById('view-console')?.classList.contains('active')) {
            setTimeout(() => consoleFitAddon.fit(), 150);
        }
    }
});
