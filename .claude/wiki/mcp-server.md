# MCP Server

> Node.js MCP tool definitions that bridge Claude Code agents to MultiTerminal's REST API. Lives outside the repo at %APPDATA%/multiterminal/mcp but managed by McpConfigService.

**Tags:** `mcp`, `tools`, `external`

## Key Files

- `Services/GatewayIntegrationService.cs` (519 LOC)
- `Services/McpConfigService.cs` (187 LOC)
  - Manages MCP configuration lifecycle including import, generation, and global/project-level syncing
- `API/Controllers/GatewayController.cs` (113 LOC)
- `API/Controllers/ToolsController.cs` (62 LOC)
  - Self-documenting API endpoint that lists all available REST endpoints with HTTP methods and descriptions.

## Key Classes

- **McpConfigService** (class) — `Services/McpConfigService.cs:15`
- **GatewayServerDto** (class) — `Services/GatewayIntegrationService.cs:16`
- **GatewayToolDto** (class) — `Services/GatewayIntegrationService.cs:32`
- **GatewayIntegrationService** (class) — `Services/GatewayIntegrationService.cs:46`
- **ToolsController** (class) — `API/Controllers/ToolsController.cs:5`
- **GatewayController** (class) — `API/Controllers/GatewayController.cs:11`

## Key Methods

- `McpConfigService.GenerateSimpleMcpJson` — `Services/McpConfigService.cs:52`
- `McpConfigService.WriteMcpJsonToProject` — `Services/McpConfigService.cs:66`
- `McpConfigService.EnsureMcpConfigsForProject` — `Services/McpConfigService.cs:122`
- `McpConfigService.EnsureMcpConfigsForProjectWithGateway` — `Services/McpConfigService.cs:138`
- `McpConfigService.RegenerateAllMcpConfigs` — `Services/McpConfigService.cs:147`
- `GatewayIntegrationService.IsGatewayInstalled` — `Services/GatewayIntegrationService.cs:71`
- `GatewayIntegrationService.GetAllGatewayServers` — `Services/GatewayIntegrationService.cs:80`
- `GatewayIntegrationService.GetToolsForServer` — `Services/GatewayIntegrationService.cs:124`
- `GatewayIntegrationService.GetAllDiscoveredTools` — `Services/GatewayIntegrationService.cs:154`
- `GatewayIntegrationService.EnsureGatewayRegistered` — `Services/GatewayIntegrationService.cs:215`
- `GatewayIntegrationService.SyncProjectProfile` — `Services/GatewayIntegrationService.cs:309`
- `GatewayIntegrationService.GetGatewayProfileName` — `Services/GatewayIntegrationService.cs:385`
- `ToolsController.ListTools` — `API/Controllers/ToolsController.cs:12`
- `GatewayController.GetServers` — `API/Controllers/GatewayController.cs:26`
- `GatewayController.GetTools` — `API/Controllers/GatewayController.cs:50`
- `GatewayController.GetToolSchema` — `API/Controllers/GatewayController.cs:90`

## Routes

- `GET` `/api/tools` — `API/Controllers/ToolsController.cs:12`
- `GET` `/api/gateway/servers` — `API/Controllers/GatewayController.cs:26`
- `GET` `/api/gateway/tools` — `API/Controllers/GatewayController.cs:50`
- `GET` `/api/gateway/tools/{namespacedName}` — `API/Controllers/GatewayController.cs:90`

## External Callers

> Code outside this subsystem that calls into it.

- `MainForm.OnMcpJsonWriteRequested` — `MainForm.cs:5159`
- `MainForm.OnStartScreenNewProject` — `MainForm.cs:2542`
- `MainForm.OnStartScreenProjectLaunched` — `MainForm.cs:2357`
- `MainForm.OnProjectLaunchRequested` — `MainForm.cs:5207`
- `ProjectPanelDocument.SyncGatewayProfile` — `Docking/ProjectPanelDocument.cs:724`
- `ProjectPanelDocument.SendAvailableMcpServers` — `Docking/ProjectPanelDocument.cs:428`

## Gotchas

- Secret keywords detected in env var names (key, token, password, etc) are replaced with ${ENV_VAR} placeholders on import; global servers synced via CLI using 'cmd.exe /c claude mcp add'
- Returns baseUrl as http://localhost:5050, must update endpoint docs when adding new routes

---
_Generated 2026-06-10T17:01:21.3226631Z · [Back to index](./index.md)_
