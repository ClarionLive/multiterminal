// MultiRemote — Terminals view with Launch functionality

let projectsList = [];

async function loadTerminals() {
    const container = document.getElementById('terminals-list');
    const data = await api('/api/terminals');

    if (!data) {
        container.innerHTML = `
            <div class="cr-empty">
                <i class="bi bi-wifi-off"></i>
                <p>Can't reach MultiTerminal</p>
            </div>`;
        return;
    }

    updateConnectionStatus(true);
    const terminals = Array.isArray(data) ? data : (data.terminals || []);

    if (terminals.length === 0) {
        container.innerHTML = `
            <div class="cr-empty">
                <i class="bi bi-terminal"></i>
                <p>No active terminals</p>
            </div>`;
        return;
    }

    let html = '';
    terminals.forEach(t => {
        const name = t.name || t.terminalName || 'Unknown';
        const initial = name.charAt(0).toUpperCase();
        const lastSeen = timeAgo(t.lastSeen || t.registeredAt);
        const isActive = isTerminalActive(t);
        const statusClass = isActive ? 'online' : 'offline';
        const project = t.projectName || t.docId || '';

        html += `
            <div class="cr-card">
                <div class="terminal-item">
                    <div class="terminal-avatar">${initial}</div>
                    <div class="terminal-info">
                        <div class="name">
                            <span class="status-dot ${statusClass}"></span>
                            ${escapeHtml(name)}
                        </div>
                        <div class="detail">${escapeHtml(project)} &middot; ${lastSeen}</div>
                    </div>
                </div>
            </div>`;
    });

    container.innerHTML = html;

    // Also load projects for the launch section
    loadProjects();
}

async function loadProjects() {
    const container = document.getElementById('launch-projects');
    if (!container) return;

    const data = await api('/api/projects');
    if (!data) {
        container.innerHTML = '<div class="small text-muted">Could not load projects</div>';
        return;
    }

    const projects = data.projects || [];
    projectsList = projects;

    if (projects.length === 0) {
        container.innerHTML = '<div class="small text-muted">No projects registered</div>';
        return;
    }

    // Sort: pinned first, then alphabetical
    projects.sort((a, b) => {
        if (a.isPinned && !b.isPinned) return -1;
        if (!a.isPinned && b.isPinned) return 1;
        return (a.name || '').localeCompare(b.name || '');
    });

    let html = '';
    projects.forEach(p => {
        const name = p.name || 'Unnamed';
        const type = p.projectType || '';
        const typeIcon = getProjectTypeIcon(type);
        const pinned = p.isPinned ? '<i class="bi bi-pin-fill text-warning"></i> ' : '';
        const desc = p.description ? p.description.substring(0, 60) + (p.description.length > 60 ? '...' : '') : '';

        html += `
            <div class="launch-project-card">
                <div class="launch-project-icon">${typeIcon}</div>
                <div class="launch-project-info">
                    <div class="launch-project-name">${pinned}${escapeHtml(name)}</div>
                    <div class="launch-project-desc">${escapeHtml(desc || type)}</div>
                </div>
                <button class="launch-project-go-btn" onclick="event.stopPropagation(); showLaunchSheet('${escapeAttr(p.id)}', '${escapeAttr(name)}')" title="Launch in ${escapeAttr(name)}">
                    <i class="bi bi-play-circle-fill"></i>
                </button>
            </div>`;
    });

    container.innerHTML = html;
}

function getProjectTypeIcon(type) {
    switch ((type || '').toLowerCase()) {
        case 'dotnet': return '<i class="bi bi-filetype-cs"></i>';
        case 'node': case 'nodejs': return '<i class="bi bi-filetype-js"></i>';
        case 'python': return '<i class="bi bi-filetype-py"></i>';
        case 'rust': return '<i class="bi bi-gear-fill"></i>';
        case 'go': return '<i class="bi bi-code-slash"></i>';
        default: return '<i class="bi bi-folder-fill"></i>';
    }
}

// Launch sheet state
let pendingLaunchProjectId = null;
let pendingLaunchProjectName = '';
let launchCounter = 1;
let launchSheetOpen = false;

async function showLaunchSheet(projectId, projectName) {
    if (launchSheetOpen) return;
    launchSheetOpen = true;
    pendingLaunchProjectId = projectId;
    pendingLaunchProjectName = projectName || 'Just Claude';

    const sheet = document.getElementById('launch-sheet');
    const title = document.getElementById('launch-sheet-title');
    const subtitle = document.getElementById('launch-sheet-project');
    const agentList = document.getElementById('launch-sheet-agent-list');
    const customRow = document.getElementById('launch-sheet-custom-row');

    title.textContent = projectId ? `Launch in ${projectName}` : 'Launch Terminal';
    subtitle.textContent = 'Loading agents...';
    agentList.innerHTML = '';
    customRow.classList.add('d-none');

    sheet.classList.remove('d-none');
    document.body.style.overflow = 'hidden';

    // Get active terminals to know which agents are already running
    const terminalsData = await api('/api/terminals');
    const activeTerminals = terminalsData ? (Array.isArray(terminalsData) ? terminalsData : (terminalsData.terminals || [])) : [];
    const activeNames = new Set(activeTerminals.map(t => (t.name || t.terminalName || '').toLowerCase()));

    if (projectId) {
        // Fetch project context with agents
        const ctx = await api(`/api/projects/${projectId}/context`);
        const projectAgents = ctx?.agents || [];
        const teamLead = ctx?.project?.teamLead || ctx?.teamLead || '';
        const projectPath = ctx?.project?.path || '';

        // Fetch ALL team member profiles (not just project-assigned ones)
        const profilesData = await api('/api/team/profiles');
        const allProfiles = profilesData?.profiles || [];

        // Build agent list from all profiles, enriched with project context
        const allAgents = [];
        const seenNames = new Set();
        const projectAgentNames = new Set(projectAgents.map(a => a.agentName.toLowerCase()));

        allProfiles.forEach(p => {
            const name = p.name;
            if (!name) return;
            seenNames.add(name.toLowerCase());
            allAgents.push({
                name: name,
                isLead: p.isTeamLead || name === teamLead,
                isOnline: p.isOnline || activeNames.has(name.toLowerCase()),
                role: p.role || '',
                isProjectAgent: projectAgentNames.has(name.toLowerCase())
            });
        });

        // Add any project-assigned agents not in profiles
        projectAgents.forEach(a => {
            if (!seenNames.has(a.agentName.toLowerCase())) {
                seenNames.add(a.agentName.toLowerCase());
                allAgents.push({
                    name: a.agentName,
                    isLead: a.agentName === teamLead,
                    isOnline: activeNames.has(a.agentName.toLowerCase()),
                    role: a.role || '',
                    isProjectAgent: true
                });
            }
        });

        if (teamLead && !seenNames.has(teamLead.toLowerCase())) {
            allAgents.unshift({
                name: teamLead, isLead: true,
                isOnline: activeNames.has(teamLead.toLowerCase()), role: '',
                isProjectAgent: true
            });
        }

        // Sort: available (not online) first, then alphabetical; team lead stays at top if available
        allAgents.sort((a, b) => {
            if (a.isLead && !a.isOnline && !(b.isLead && !b.isOnline)) return -1;
            if (b.isLead && !b.isOnline && !(a.isLead && !a.isOnline)) return 1;
            if (!a.isOnline && b.isOnline) return -1;
            if (a.isOnline && !b.isOnline) return 1;
            return a.name.localeCompare(b.name);
        });

        const leadBusy = teamLead && activeNames.has(teamLead.toLowerCase());

        // Find the project's assigned agent — team lead first, then first project-specific agent
        const assignedAgent = teamLead
            || (projectAgents.length > 0 ? projectAgents[0].agentName : '')
            || (allAgents.length > 0 ? allAgents[0].name : '');
        const assignedBusy = assignedAgent && activeNames.has(assignedAgent.toLowerCase());

        // Auto-launch the assigned agent if they're not busy
        if (assignedAgent && !assignedBusy) {
            closeLaunchSheet();
            await doLaunch(assignedAgent);
            return;
        }

        // Show picker — assigned agent is busy, offer alternatives
        subtitle.innerHTML = `<i class="bi bi-exclamation-circle text-warning"></i> <strong>${escapeHtml(assignedAgent)}</strong> is busy. Choose another agent:`;

        // Build tappable agent list — only show available (not busy) agents
        let html = '';
        allAgents.forEach(a => {
            if (a.isOnline) return; // skip busy agents entirely
            const lead = a.isLead ? ' <span class="text-warning">★</span>' : '';
            const role = a.role ? `<span class="text-muted"> — ${escapeHtml(a.role)}</span>` : '';

            html += `
                <div class="launch-agent-item" style="cursor: pointer;"
                     onclick="pickAndLaunch('${escapeAttr(a.name)}')">
                    <div class="terminal-avatar" style="width:36px;height:36px;font-size:1rem;">${a.name.charAt(0).toUpperCase()}</div>
                    <div style="flex:1;min-width:0;">
                        <div class="fw-semibold">${escapeHtml(a.name)}${lead}</div>
                        <div class="small text-muted">${role}</div>
                    </div>
                    <span class="badge bg-success ms-auto">available</span>
                </div>`;
        });

        // If no available agents were listed, show a note
        if (!html) {
            html += `<div class="small text-muted p-3">All agents are busy.</div>`;
        }

        // "Other..." option at the bottom
        html += `
            <div class="launch-agent-item" style="cursor:pointer;"
                 onclick="showCustomNameInput()">
                <div class="terminal-avatar" style="width:36px;height:36px;font-size:1rem;background:#555;">?</div>
                <div style="flex:1;min-width:0;">
                    <div class="fw-semibold">Other...</div>
                    <div class="small text-muted">Enter a custom name</div>
                </div>
            </div>`;

        agentList.innerHTML = html;
    } else {
        // Just Claude — no project, show simple name entry
        subtitle.textContent = 'A new Claude Code terminal will open.';
        customRow.classList.remove('d-none');
        document.getElementById('launch-sheet-name').value = 'Agent' + launchCounter;
        document.getElementById('launch-sheet-name')?.focus();
    }
}

function closeLaunchSheet() {
    const sheet = document.getElementById('launch-sheet');
    sheet.classList.add('d-none');
    document.body.style.overflow = '';
    launchSheetOpen = false;
}

async function doLaunch(agentName) {
    showLaunchStatus('Launching ' + agentName + ' in ' + pendingLaunchProjectName + '...', 'info');

    try {
        const body = { agentName: agentName };
        if (pendingLaunchProjectId) body.projectId = pendingLaunchProjectId;

        const res = await fetch('/api/spawn', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body)
        });

        const data = await res.json();

        if (res.ok && data.success) {
            launchCounter++;
            showLaunchStatus(agentName + ' launched in ' + pendingLaunchProjectName + '!', 'success');
            setTimeout(function() {
                loadTerminals();
                if (typeof refreshConsoleTerminals === 'function') refreshConsoleTerminals();
            }, 2000);
            return true;
        } else {
            showLaunchStatus(data.error || 'Launch failed', 'danger');
            return false;
        }
    } catch (err) {
        showLaunchStatus('Could not reach server', 'danger');
        return false;
    }
}

async function pickAndLaunch(agentName) {
    // Disable the list to prevent double-taps
    const list = document.getElementById('launch-sheet-agent-list');
    list.style.pointerEvents = 'none';
    list.style.opacity = '0.5';

    document.getElementById('launch-sheet-project').innerHTML =
        `<span class="spinner-border spinner-border-sm"></span> Launching ${escapeHtml(agentName)}...`;

    const success = await doLaunch(agentName);
    if (success) {
        closeLaunchSheet();
    } else {
        list.style.pointerEvents = '';
        list.style.opacity = '';
    }
}

function showCustomNameInput() {
    document.getElementById('launch-sheet-agent-list').classList.add('d-none');
    document.getElementById('launch-sheet-custom-row').classList.remove('d-none');
    document.getElementById('launch-sheet-name').value = '';
    document.getElementById('launch-sheet-name')?.focus();
}

async function confirmCustomLaunch() {
    const nameInput = document.getElementById('launch-sheet-name');
    const btn = document.getElementById('launch-sheet-btn');
    const agentName = (nameInput?.value || '').trim();

    if (!agentName) { nameInput?.focus(); return; }

    btn.disabled = true;
    btn.innerHTML = '<span class="spinner-border spinner-border-sm"></span> Launching...';

    const success = await doLaunch(agentName);
    if (success) {
        closeLaunchSheet();
    } else {
        btn.innerHTML = '<i class="bi bi-play-fill"></i> Launch';
        btn.disabled = false;
    }
}

function showLaunchStatus(message, type) {
    const el = document.getElementById('launch-status');
    if (!el) return;
    el.className = `launch-status alert alert-${type}`;
    el.textContent = message;
    el.classList.remove('d-none');

    if (type === 'success' || type === 'warning') {
        setTimeout(() => el.classList.add('d-none'), 4000);
    }
}

function isTerminalActive(terminal) {
    if (!terminal.lastSeen) return true;
    const lastSeen = new Date(terminal.lastSeen);
    const now = new Date();
    return (now - lastSeen) < 5 * 60 * 1000;
}

function escapeAttr(text) {
    return text.replace(/&/g, '&amp;').replace(/'/g, '&#39;').replace(/"/g, '&quot;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}
