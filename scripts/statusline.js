#!/usr/bin/env node
// Claude Code statusLine script for MultiTerminal
// Reads JSON from stdin, enriches with git data, writes to temp file
// for the MultiTerminal status bar to pick up.

const fs = require('fs');
const path = require('path');
const os = require('os');
const { execSync } = require('child_process');

let input = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', (chunk) => { input += chunk; });
process.stdin.on('end', () => {
    try {
        const data = JSON.parse(input);
        const terminalName = process.env.MULTITERMINAL_NAME;

        // Graceful no-op when running outside MultiTerminal
        if (!terminalName) {
            process.exit(0);
        }

        const cwd = data.workspace?.current_dir || '';
        const model = normalizeModel(data.model);
        const contextPct = data.context_window?.used_percentage ?? null;

        // Query git status from the working directory
        const git = getGitInfo(cwd);

        const output = {
            terminalName,
            model,
            folder: cwd,
            folderName: cwd ? path.basename(cwd) : '',
            contextPct,
            gitBranch: git.branch,
            gitStatus: git.status,
            gitDirty: git.dirty,
            timestamp: Date.now()
        };

        // Write to temp file keyed by terminal name
        const outPath = path.join(os.tmpdir(), `mt-statusline-${terminalName}.json`);
        fs.writeFileSync(outPath, JSON.stringify(output), 'utf8');
    } catch (e) {
        // Silently fail — don't break Claude Code
    }
});

function normalizeModel(model) {
    if (!model) return 'claude';
    let name = typeof model === 'object' ? (model.id || model.name || 'claude') : String(model);
    // Remove claude- prefix and date suffix, truncate
    name = name.replace(/^claude-/, '').replace(/-\d{8}$/, '');
    if (name.length > 12) name = name.substring(0, 12);
    return name;
}

function getGitInfo(cwd) {
    if (!cwd) return { branch: '', status: '', dirty: false };
    try {
        const branch = execSync('git branch --show-current', { cwd, encoding: 'utf8', timeout: 3000 }).trim()
            || execSync('git rev-parse --short HEAD', { cwd, encoding: 'utf8', timeout: 3000 }).trim();

        const porcelain = execSync('git status --porcelain', { cwd, encoding: 'utf8', timeout: 3000 });
        const lines = porcelain.split('\n').filter(l => l.length > 0);

        let staged = 0, modified = 0;
        for (const line of lines) {
            if (/^[MADRC]/.test(line)) staged++;
            if (/^.[MD]/.test(line)) modified++;
        }

        let ahead = 0, behind = 0;
        try {
            ahead = parseInt(execSync('git rev-list --count @{u}..HEAD', { cwd, encoding: 'utf8', timeout: 3000 }).trim()) || 0;
            behind = parseInt(execSync('git rev-list --count HEAD..@{u}', { cwd, encoding: 'utf8', timeout: 3000 }).trim()) || 0;
        } catch (e) { /* no upstream */ }

        let status = '';
        if (ahead > 0) status += `\u21e1${ahead}`;
        if (behind > 0) status += `\u21e3${behind}`;
        if (staged > 0) status += `+${staged}`;
        if (modified > 0) status += `!${modified}`;

        return { branch, status, dirty: lines.length > 0 };
    } catch (e) {
        return { branch: '', status: '', dirty: false };
    }
}
