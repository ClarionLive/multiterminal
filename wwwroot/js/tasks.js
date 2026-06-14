// MultiRemote — Tasks view

let allTasks = [];
let projectMap = {};
let activeFilter = 'all';
let projectFilter = '';
let agentFilter = '';
let knownAgents = [];

async function loadTasks() {
    const container = document.getElementById('tasks-list');

    // Fetch tasks and projects in parallel
    const [data, projData] = await Promise.all([
        api('/api/tasks?status=all'),
        api('/api/projects')
    ]);

    if (!data) {
        container.innerHTML = `
            <div class="cr-empty">
                <i class="bi bi-wifi-off"></i>
                <p>Can't reach MultiTerminal</p>
            </div>`;
        return;
    }

    updateConnectionStatus(true);
    allTasks = Array.isArray(data) ? data : (data.tasks || []);

    // Collect known agents from task data
    const agentSet = new Set();
    allTasks.forEach(t => {
        if (t.assignee) agentSet.add(t.assignee);
        if (t.assignedTo) agentSet.add(t.assignedTo);
        if (t.createdBy) agentSet.add(t.createdBy);
    });
    knownAgents = [...agentSet].sort();

    // Build project ID → name map
    projectMap = {};
    if (projData) {
        const projects = projData.projects || [];
        projects.forEach(p => { projectMap[p.id] = p.name; });
    }

    if (allTasks.length === 0) {
        document.getElementById('task-filters').innerHTML = '';
        container.innerHTML = `
            <div class="cr-empty">
                <i class="bi bi-kanban"></i>
                <p>No tasks on the board</p>
            </div>`;
        return;
    }

    renderTaskFilters();
    renderFilteredTasks();
}

function getFilteredTasks() {
    let tasks = allTasks;
    if (activeFilter !== 'all') {
        tasks = tasks.filter(t => (t.status || 'todo') === activeFilter);
    }
    if (projectFilter) {
        tasks = tasks.filter(t => t.projectId === projectFilter);
    }
    if (agentFilter) {
        tasks = tasks.filter(t =>
            (t.assignee || '') === agentFilter ||
            (t.assignedTo || '') === agentFilter ||
            (t.createdBy || '') === agentFilter
        );
    }
    return tasks;
}

function renderTaskFilters() {
    const filtersEl = document.getElementById('task-filters');

    // Status counts (from all tasks, before project/agent filter)
    const counts = { all: allTasks.length, in_progress: 0, todo: 0, suggestion: 0, done: 0 };
    allTasks.forEach(t => {
        const s = t.status || 'todo';
        if (counts[s] !== undefined) counts[s]++;
        else counts.todo++;
    });

    const statusFilters = [
        { key: 'all', label: 'All' },
        { key: 'in_progress', label: 'Active' },
        { key: 'todo', label: 'Todo' },
        { key: 'suggestion', label: 'Ideas' },
        { key: 'done', label: 'Done' }
    ];

    // Collect unique projects and agents
    const projects = new Map();
    const agents = new Set();
    allTasks.forEach(t => {
        if (t.projectId && projectMap[t.projectId]) {
            projects.set(t.projectId, projectMap[t.projectId]);
        }
        if (t.assignee) agents.add(t.assignee);
        if (t.assignedTo) agents.add(t.assignedTo);
    });

    let html = '<div class="task-filter-row">';

    // Status pills
    html += statusFilters
        .filter(f => f.key === 'all' || counts[f.key] > 0)
        .map(f => `<button class="task-filter-btn${activeFilter === f.key ? ' active' : ''}" onclick="setTaskFilter('${f.key}')">${f.label} <span class="task-filter-count">${counts[f.key]}</span></button>`)
        .join('');
    html += '</div>';

    // Project and agent dropdowns
    if (projects.size > 0 || agents.size > 0) {
        html += '<div class="task-filter-dropdowns">';

        if (projects.size > 0) {
            html += `<select class="task-filter-select" onchange="setProjectFilter(this.value)">
                <option value="">All Projects</option>`;
            for (const [id, name] of projects) {
                html += `<option value="${id}"${projectFilter === id ? ' selected' : ''}>${escapeHtml(name)}</option>`;
            }
            html += '</select>';
        }

        if (agents.size > 0) {
            html += `<select class="task-filter-select" onchange="setAgentFilter(this.value)">
                <option value="">All Agents</option>`;
            for (const name of [...agents].sort()) {
                html += `<option value="${escapeHtml(name)}"${agentFilter === name ? ' selected' : ''}>${escapeHtml(name)}</option>`;
            }
            html += '</select>';
        }

        html += '</div>';
    }

    filtersEl.innerHTML = html;
}

function setTaskFilter(filter) {
    activeFilter = filter;
    renderTaskFilters();
    renderFilteredTasks();
}

function setProjectFilter(id) {
    projectFilter = id;
    renderTaskFilters();
    renderFilteredTasks();
}

function setAgentFilter(name) {
    agentFilter = name;
    renderTaskFilters();
    renderFilteredTasks();
}

function renderFilteredTasks() {
    const container = document.getElementById('tasks-list');
    const filtered = getFilteredTasks();

    if (filtered.length === 0) {
        container.innerHTML = `
            <div class="cr-empty">
                <i class="bi bi-funnel"></i>
                <p>No tasks match these filters</p>
            </div>`;
        return;
    }

    // Group by status only when showing "all" status
    if (activeFilter === 'all') {
        const groups = { in_progress: [], todo: [], suggestion: [], done: [] };
        filtered.forEach(t => {
            const status = t.status || 'todo';
            if (groups[status]) groups[status].push(t);
            else groups.todo.push(t);
        });

        let html = '';
        for (const [status, items] of Object.entries(groups)) {
            if (items.length === 0) continue;
            html += `<div class="cr-section-header">${status.replace('_', ' ')} (${items.length})</div>`;
            items.forEach(task => { html += renderTaskCard(task); });
        }
        container.innerHTML = html;
    } else {
        let html = '';
        filtered.forEach(task => { html += renderTaskCard(task); });
        container.innerHTML = html;
    }
}

function renderTaskCard(task) {
    const assignee = task.assignedTo || task.assignee || 'Unassigned';
    const priority = task.priority || 'normal';
    const priorityIcon = priority === 'high' ? '<i class="bi bi-exclamation-triangle-fill text-warning"></i> ' : '';
    const updated = timeAgo(task.updatedAt || task.createdAt);
    const project = task.projectId && projectMap[task.projectId]
        ? `<span class="ms-2"><i class="bi bi-folder"></i> ${escapeHtml(projectMap[task.projectId])}</span>`
        : '';

    return `
        <div class="cr-card" onclick="showTaskDetail('${task.id}')">
            <div class="d-flex justify-content-between align-items-start">
                <div class="card-title">${priorityIcon}${escapeHtml(task.title || 'Untitled')}</div>
                ${statusBadge(task.status)}
            </div>
            <div class="card-meta">
                <i class="bi bi-person"></i> ${escapeHtml(assignee)}
                <span class="ms-2"><i class="bi bi-clock"></i> ${updated}</span>
                ${project}
            </div>
        </div>`;
}

async function showTaskDetail(id) {
    const task = await api(`/api/tasks/${id}`);
    if (!task) return;

    const description = task.description || '';
    const project = task.projectId && projectMap[task.projectId]
        ? escapeHtml(projectMap[task.projectId]) : '';

    // Parse checklist
    let checklist = [];
    if (task.checklistJson) {
        try { checklist = typeof task.checklistJson === 'string' ? JSON.parse(task.checklistJson) : task.checklistJson; } catch {}
    }

    // Checklist progress
    let checklistHtml = '';
    if (checklist.length > 0) {
        const done = checklist.filter(c => c.status === 'done' || c.done).length;
        const testing = checklist.filter(c => c.status === 'testing').length;
        const coding = checklist.filter(c => c.status === 'coding').length;
        const pct = Math.round((done / checklist.length) * 100);

        checklistHtml = `
            <div class="task-detail-section">
                <div class="task-detail-heading">Checklist <span class="task-detail-count">${done}/${checklist.length}</span></div>
                <div class="task-progress-bar"><div class="task-progress-fill" style="width:${pct}%"></div></div>
                <ul class="task-checklist">`;

        checklist.forEach(item => {
            const icon = item.status === 'done' || item.done ? 'check-circle-fill text-success' :
                         item.status === 'testing' ? 'bug text-warning' :
                         item.status === 'coding' ? 'code-slash text-info' : 'circle';
            const assignee = item.assignee ? `<span class="task-cl-assignee">${escapeHtml(item.assignee)}</span>` : '';
            const statusLabel = item.status && item.status !== 'pending' ? `<span class="task-cl-status badge-${item.status}">${item.status}</span>` : '';
            checklistHtml += `<li><i class="bi bi-${icon}"></i> <span class="task-cl-text">${escapeHtml(item.item || item.title || '')}</span> ${assignee} ${statusLabel}</li>`;
        });
        checklistHtml += '</ul></div>';
    }

    // Collapsible sections for notes
    let sectionsHtml = '';

    if (task.plan) {
        sectionsHtml += renderCollapsibleSection('Plan', 'map', task.plan);
    }
    if (task.continuationNotes) {
        sectionsHtml += renderCollapsibleSection('Status Notes', 'journal-text', task.continuationNotes);
    }
    if (task.implementationSummary) {
        sectionsHtml += renderCollapsibleSection('Implementation', 'code-slash', task.implementationSummary);
    }
    if (task.reviewNotes) {
        sectionsHtml += renderCollapsibleSection('Review Notes', 'chat-square-text', task.reviewNotes);
    }
    if (task.testResults) {
        sectionsHtml += renderCollapsibleSection('Test Results', 'clipboard-check', task.testResults);
    }

    const modal = document.createElement('div');
    modal.className = 'position-fixed top-0 start-0 w-100 h-100';
    modal.style.cssText = 'z-index:200; background:rgba(0,0,0,0.6);';
    modal.onclick = (e) => { if (e.target === modal) modal.remove(); };
    // Build status options (exclude current)
    const allStatuses = ['suggestion', 'todo', 'in_progress', 'done'];
    const currentStatus = task.status || 'todo';
    const statusOptions = allStatuses
        .filter(s => s !== currentStatus)
        .map(s => `<option value="${s}">${s.replace('_', ' ')}</option>`)
        .join('');

    // Build agent options
    const currentAssignee = task.assignedTo || task.assignee || '';
    const agentOptions = knownAgents
        .filter(a => a !== currentAssignee)
        .map(a => `<option value="${escapeHtml(a)}">${escapeHtml(a)}</option>`)
        .join('');

    modal.innerHTML = `
        <div class="task-detail-modal" id="task-detail-sheet">
            <div class="task-sheet-handle" id="task-sheet-handle"><div class="task-sheet-bar"></div></div>
            <div class="task-detail-header">
                <h5 class="fw-bold mb-0">${escapeHtml(task.title || 'Untitled')}</h5>
            </div>
            <div class="task-detail-meta">
                ${statusBadge(task.status)}
                <span><i class="bi bi-person"></i> ${escapeHtml(currentAssignee || 'Unassigned')}</span>
                <span><i class="bi bi-tag"></i> ${escapeHtml(task.priority || 'normal')}</span>
                ${project ? `<span><i class="bi bi-folder"></i> ${project}</span>` : ''}
            </div>
            <div class="task-actions">
                <div class="task-action-row">
                    <label class="task-action-label"><i class="bi bi-arrow-left-right"></i> Move to</label>
                    <select class="task-action-select" id="task-status-select" onchange="changeTaskStatus('${task.id}', this.value)">
                        <option value="">${currentStatus.replace('_', ' ')}</option>
                        ${statusOptions}
                    </select>
                </div>
                <div class="task-action-row">
                    <label class="task-action-label"><i class="bi bi-person-plus"></i> Assign to</label>
                    <select class="task-action-select" id="task-assign-select" onchange="assignTask('${task.id}', this.value)">
                        <option value="">${escapeHtml(currentAssignee || 'Unassigned')}</option>
                        ${currentAssignee ? '<option value="__unassign__">Unassign</option>' : ''}
                        ${agentOptions}
                    </select>
                </div>
            </div>
            ${description ? `<div class="task-detail-section"><div class="task-detail-heading">Description</div><p class="task-detail-text">${escapeHtml(description)}</p></div>` : ''}
            ${checklistHtml}
            ${sectionsHtml}
        </div>`;
    document.body.appendChild(modal);

    // Prevent background scrolling while modal is open
    document.body.style.overflow = 'hidden';
    modal.addEventListener('touchmove', (e) => {
        // Allow scrolling inside the sheet, block everything else
        const sheet = modal.querySelector('.task-detail-modal');
        if (!sheet.contains(e.target)) {
            e.preventDefault();
        }
    }, { passive: false });

    // Restore scrolling when modal is removed
    const observer = new MutationObserver(() => {
        if (!document.body.contains(modal)) {
            document.body.style.overflow = '';
            observer.disconnect();
        }
    });
    observer.observe(document.body, { childList: true });

    setupSheetDismiss(modal);
}

function setupSheetDismiss(modal) {
    const sheet = modal.querySelector('.task-detail-modal');
    const handle = modal.querySelector('.task-sheet-handle');
    let startY = 0, currentY = 0, dragging = false;

    handle.addEventListener('touchstart', (e) => {
        startY = e.touches[0].clientY;
        currentY = startY;
        dragging = true;
        sheet.style.transition = 'none';
    }, { passive: true });

    handle.addEventListener('touchmove', (e) => {
        if (!dragging) return;
        currentY = e.touches[0].clientY;
        const dy = Math.max(0, currentY - startY);
        sheet.style.transform = `translateY(${dy}px)`;
        // Fade backdrop
        const opacity = Math.max(0, 0.6 - (dy / 600));
        modal.style.background = `rgba(0,0,0,${opacity})`;
    }, { passive: true });

    handle.addEventListener('touchend', () => {
        if (!dragging) return;
        dragging = false;
        const dy = currentY - startY;
        sheet.style.transition = 'transform 0.3s ease';
        if (dy > 100) {
            // Dismiss
            sheet.style.transform = 'translateY(100%)';
            modal.style.transition = 'background 0.3s';
            modal.style.background = 'rgba(0,0,0,0)';
            setTimeout(() => modal.remove(), 300);
        } else {
            // Snap back
            sheet.style.transform = 'translateY(0)';
            modal.style.background = 'rgba(0,0,0,0.6)';
        }
    }, { passive: true });
}

function renderCollapsibleSection(title, icon, content) {
    const id = 'section-' + Math.random().toString(36).substr(2, 6);
    return `
        <div class="task-detail-section">
            <div class="task-detail-heading task-collapsible" onclick="toggleSection('${id}')">
                <i class="bi bi-${icon}"></i> ${title}
                <i class="bi bi-chevron-down task-chevron" id="${id}-chevron"></i>
            </div>
            <div class="task-collapsible-body" id="${id}" style="display:none">
                <pre class="task-detail-pre">${escapeHtml(content)}</pre>
            </div>
        </div>`;
}

function toggleSection(id) {
    const el = document.getElementById(id);
    const chevron = document.getElementById(id + '-chevron');
    if (el.style.display === 'none') {
        el.style.display = 'block';
        chevron.classList.replace('bi-chevron-down', 'bi-chevron-up');
    } else {
        el.style.display = 'none';
        chevron.classList.replace('bi-chevron-up', 'bi-chevron-down');
    }
}

async function changeTaskStatus(taskId, newStatus) {
    if (!newStatus) return;
    const res = await api(`/api/tasks/${taskId}/status`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ status: newStatus })
    });
    if (res && res.success !== false) {
        dismissTaskModal();
        await loadTasks();
    } else {
        alert('Failed to update status');
        document.getElementById('task-status-select').selectedIndex = 0;
    }
}

async function assignTask(taskId, assignee) {
    if (!assignee) return;
    if (assignee === '__unassign__') assignee = '';
    const res = await api(`/api/tasks/${taskId}/assign`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ assignee })
    });
    if (res && res.success !== false) {
        dismissTaskModal();
        await loadTasks();
    } else {
        alert('Failed to assign task');
        document.getElementById('task-assign-select').selectedIndex = 0;
    }
}

function dismissTaskModal() {
    const modal = document.querySelector('.position-fixed');
    if (modal) modal.remove();
    document.body.style.overflow = '';
}

// escapeHtml is defined in app.js (loaded first)
