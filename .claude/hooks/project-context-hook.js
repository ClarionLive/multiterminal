#!/usr/bin/env node
/**
 * project-context-hook.js
 *
 * SessionStart hook that reads MULTITERMINAL_PROJECT_ID, calls the project context
 * REST API, and injects relevant project information into the Claude session.
 *
 * Injected context includes:
 *   - Project name, type, description
 *   - Paths: source, deploy, build output
 *   - Commands: build, deploy, launch
 *   - Git configuration: repo URL, default branch, auto-commit
 *   - Team agents assigned to this project
 *   - MCP servers configured for this project
 *   - Skills enabled for this project
 *   - Specialist agents (verifier, devils-advocate, etc.)
 *
 * Gracefully no-ops if MULTITERMINAL_PROJECT_ID is not set, if the API is
 * unreachable, or if the project is not found. Never blocks Claude.
 */
const http = require('http');

const API_PORT = 5050;
const API_TIMEOUT = 4000; // Project context fetch should be fast

/**
 * Fetch the project context from the REST API.
 * Returns the parsed JSON body or null on any error.
 */
function fetchProjectContext(projectId) {
  return new Promise((resolve) => {
    const options = {
      hostname: 'localhost',
      port: API_PORT,
      path: `/api/projects/${encodeURIComponent(projectId)}/context`,
      method: 'GET',
      headers: { 'Accept': 'application/json' },
      timeout: API_TIMEOUT
    };

    const req = http.request(options, (res) => {
      let data = '';
      res.on('data', (chunk) => { data += chunk; });
      res.on('end', () => {
        if (res.statusCode === 200) {
          try {
            resolve(JSON.parse(data));
          } catch (e) {
            resolve(null);
          }
        } else {
          resolve(null);
        }
      });
    });

    req.on('error', () => resolve(null));
    req.on('timeout', () => { req.destroy(); resolve(null); });
    req.end();
  });
}

/**
 * Format the project context object into a human-readable string for Claude.
 */
function formatProjectContext(ctx) {
  const project = ctx.project || ctx.Project;
  if (!project) return null;

  const lines = [];

  // Header
  const projectName = project.name || project.Name || 'Unknown Project';
  const projectType = project.projectType || project.ProjectType || '';
  const description = project.description || project.Description || '';

  lines.push(`## Project Context: ${projectName}${projectType ? ` (${projectType})` : ''}`);
  if (description) {
    lines.push(description);
  }
  lines.push('');

  // Paths
  const sourcePath = project.sourcePath || project.SourcePath || project.path || project.Path;
  const deployPath = project.deployPath || project.DeployPath;
  const buildOutputPath = project.buildOutputPath || project.BuildOutputPath;

  const hasPaths = sourcePath || deployPath || buildOutputPath;
  const extraPaths = (ctx.paths || ctx.Paths || []);

  if (hasPaths || extraPaths.length > 0) {
    lines.push('### Paths');
    if (sourcePath) lines.push(`- Source: ${sourcePath}`);
    if (deployPath) lines.push(`- Deploy: ${deployPath}`);
    if (buildOutputPath) lines.push(`- Build Output: ${buildOutputPath}`);
    for (const p of extraPaths) {
      const pName = p.pathName || p.PathName || p.name || p.Name || 'Path';
      const pValue = p.pathValue || p.PathValue || p.value || p.Value || '';
      if (pValue) lines.push(`- ${pName}: ${pValue}`);
    }
    lines.push('');
  }

  // Commands
  const buildCommand = project.buildCommand || project.BuildCommand;
  const deployCommand = project.deployCommand || project.DeployCommand;
  const launchCommand = project.launchCommand || project.LaunchCommand;

  if (buildCommand || deployCommand || launchCommand) {
    lines.push('### Commands');
    if (buildCommand) lines.push(`- Build: \`${buildCommand}\``);
    if (deployCommand) lines.push(`- Deploy: \`${deployCommand}\``);
    if (launchCommand) lines.push(`- Launch: \`${launchCommand}\``);
    lines.push('');
  }

  // Git configuration
  const gitRepoUrl = project.gitRepoUrl || project.GitRepoUrl;
  const gitDefaultBranch = project.gitDefaultBranch || project.GitDefaultBranch;
  const gitAutoCommit = project.gitAutoCommit || project.GitAutoCommit;

  if (gitRepoUrl || gitDefaultBranch) {
    lines.push('### Git');
    if (gitRepoUrl) lines.push(`- Repo: ${gitRepoUrl}`);
    if (gitDefaultBranch) lines.push(`- Default Branch: ${gitDefaultBranch}`);
    if (gitAutoCommit) lines.push(`- Auto-commit: enabled`);
    lines.push('');
  }

  // Team agents
  const agents = ctx.agents || ctx.Agents || [];
  if (agents.length > 0) {
    lines.push('### Team Agents');
    for (const agent of agents) {
      const agentName = agent.agentName || agent.AgentName || agent.name || agent.Name || 'Agent';
      const agentRole = agent.role || agent.Role || '';
      const agentModel = agent.preferredModel || agent.PreferredModel || agent.model || agent.Model || '';
      let agentLine = `- ${agentName}`;
      if (agentRole) agentLine += ` (${agentRole})`;
      if (agentModel) agentLine += ` [${agentModel}]`;
      lines.push(agentLine);
    }
    lines.push('');
  }

  // MCP servers
  const mcpServers = ctx.mcpServers || ctx.McpServers || [];
  if (mcpServers.length > 0) {
    lines.push('### MCP Servers');
    for (const mcp of mcpServers) {
      const mcpName = mcp.serverName || mcp.ServerName || mcp.name || mcp.Name || 'MCP';
      const mcpUrl = mcp.serverUrl || mcp.ServerUrl || mcp.url || mcp.Url || '';
      let mcpLine = `- ${mcpName}`;
      if (mcpUrl) mcpLine += `: ${mcpUrl}`;
      lines.push(mcpLine);
    }
    lines.push('');
  }

  // Skills
  const skills = ctx.skills || ctx.Skills || [];
  if (skills.length > 0) {
    const skillNames = skills.map(s => s.skillName || s.SkillName || s.name || s.Name || '').filter(Boolean);
    if (skillNames.length > 0) {
      lines.push('### Skills');
      lines.push(skillNames.map(n => `- ${n}`).join('\n'));
      lines.push('');
    }
  }

  // Specialist agents
  const specialists = ctx.specialistAgents || ctx.SpecialistAgents || [];
  if (specialists.length > 0) {
    lines.push('### Specialist Agents');
    for (const spec of specialists) {
      const specType = spec.agentType || spec.AgentType || spec.type || spec.Type || 'Specialist';
      const specModel = spec.preferredModel || spec.PreferredModel || spec.model || spec.Model || '';
      let specLine = `- ${specType}`;
      if (specModel) specLine += ` [${specModel}]`;
      lines.push(specLine);
    }
    lines.push('');
  }

  // Version and changelog
  const currentVersion = project.currentVersion || project.CurrentVersion;
  if (currentVersion && currentVersion !== '0.1.0') {
    lines.push(`Current Version: ${currentVersion}`);
    lines.push('');
  }

  return lines.join('\n').trim();
}

async function main() {
  // Read hook input from stdin
  let input = '';
  for await (const chunk of process.stdin) {
    input += chunk;
  }

  // Parse hook type
  let hookData;
  try {
    hookData = JSON.parse(input);
  } catch (e) {
    // Malformed input - no-op
    process.exit(0);
    return;
  }

  const hookType = hookData.hook_type || hookData.type;

  // Only act on SessionStart
  if (hookType !== 'SessionStart') {
    process.exit(0);
    return;
  }

  // Check if a project ID is set in the environment
  const projectId = process.env.MULTITERMINAL_PROJECT_ID;
  if (!projectId) {
    // No project context for this session
    process.exit(0);
    return;
  }

  // Fetch context from REST API
  const ctx = await fetchProjectContext(projectId);
  if (!ctx) {
    // API unreachable or project not found - continue silently
    process.exit(0);
    return;
  }

  // Format and output context for Claude
  const formatted = formatProjectContext(ctx);
  if (formatted) {
    console.log(formatted);
  }

  process.exit(0);
}

main().catch(() => {
  // Never block Claude on errors
  process.exit(0);
});
