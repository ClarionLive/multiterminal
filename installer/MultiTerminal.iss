; MultiTerminal Inno Setup Script
; Requires Inno Setup 6.1+ (for Excludes support)
; https://jrsoftware.org/isinfo.php

#define AppName "MultiTerminal"
#define AppVersion "3.0.0"
#define AppPublisher "MultiTerminal"
#define AppExeName "MultiTerminal.exe"
#define AppURL "https://github.com/peterparker57"

; Source directories - adjust these for your build machine
#define PublishDir "..\bin\Release\net8.0-windows\win-x64\publish"
; MCP server ships from the git-canonical repo copy staged by the build's
; StageMcpForInstaller target (mcp-dist = repo mcp/ + freshly `npm install`ed
; deps), NOT the build machine's %APPDATA% profile. This removes the silent
; dependency on whatever happened to be hand-edited into the build machine's
; APPDATA\multiterminal\mcp (ticket ec97c446) and ships only the real declared
; deps instead of accumulated profile cruft. Run a Release build before compiling
; this installer so installer\mcp-dist exists and is current.
#define McpServerDir "mcp-dist"
; publish-installer (not bin\publish) — live gateways run from bin\publish and lock
; its DLLs, so the release publish targets this dedicated staging dir (task df1f521f).
#define McpGatewayPublishDir "..\..\McpGateway\bin\publish-installer\win-x64"
; Claude Code plugin marketplace (hooks, agents, skills, CLAUDE.md, channel MCP)
#define PluginMarketplaceDir GetEnv("USERPROFILE") + "\.claude\plugins\marketplaces\multiterminal-marketplace"
#define ToolsDir "..\tools"
#define DocsDir "..\docs\html"

; Optional MCP server source directories
#define McpMssqlDir "H:\Dev\MCP\mssql-mcp-server"
#define McpSqliteDir "H:\Dev\MCP\custom-sqlite-mcp"
#define McpBuildRunnerDir "H:\Dev\MCP\windows-build-runner"
#define McpSnapItDir "H:\Dev\MCP\WindowsSnapIt-MCP"
#define McpEverythingDir "H:\Dev\MCP\everything-mcp-server"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
AllowNoIcons=yes
OutputDir=Output
OutputBaseFilename=MultiTerminalSetup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UsedUserAreasWarning=no
WizardStyle=modern
SetupIconFile=..\Assets\MultiTerminal.ico
UninstallDisplayIcon={app}\{#AppExeName}
; Broadcast SHCNE_ASSOCCHANGED at the end of setup so Explorer re-reads icons. An upgrade from a
; build without an exe icon otherwise leaves existing pins/shortcuts on the cached generic icon (GH #36).
ChangesAssociations=yes
LicenseFile=..\LICENSE
DisableProgramGroupPage=yes
; Code signing (requires Sectigo EV dongle to be plugged in)
SignTool=sectigo
SignedUninstaller=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "full"; Description: "Full installation (app + Claude Code integration + documentation)"
Name: "app"; Description: "Application + Claude Code integration (no documentation)"
Name: "custom"; Description: "Custom installation"; Flags: iscustom

[Components]
Name: "main"; Description: "MultiTerminal Application"; Types: full app custom; Flags: fixed
Name: "tools"; Description: "Bundled tools (ripgrep)"; Types: full app custom; Flags: fixed
Name: "docs"; Description: "HTML Documentation"; Types: full
; Task cb4883b6 (GitHub #20): the Claude Code integration is in EVERY type, "app" included. Without
; the plugin and the MCP servers, MT's agents have no tools and nothing says why — "Application only"
; used to be exactly that install. Only "custom" can leave them out, as a deliberate choice.
Name: "claude"; Description: "Claude Code Integration"; Types: full app
Name: "claude\mcp"; Description: "MCP Servers (agent tools + gateway)"; Types: full app
Name: "claude\plugin"; Description: "MultiTerminal Claude Code plugin (hooks, skills, agents, CLAUDE.md)"; Types: full app
; GH#2: global registration is OPT-IN (no Types: = unchecked by default in every install type).
; MT-spawned terminals never need it — they get MCP servers via --mcp-config and the plugin via
; --plugin-dir at launch (LaunchCommandBuilder). Check this only if you want Claude Code sessions
; started OUTSIDE MultiTerminal (plain `claude` in a shell) to see the multiterminal + mcp-gateway
; tools via ~/.claude.json.
Name: "claude\globalreg"; Description: "Register MCP servers globally (~/.claude.json) for Claude Code sessions outside MultiTerminal"

; ============================================================
; FILES
; ============================================================
[Files]

; ============================================================
; --- Main Application: Non-DLL files (always install) ---
; ============================================================
Source: "{#PublishDir}\MultiTerminal.exe"; DestDir: "{app}"; Components: main; Flags: ignoreversion
Source: "{#PublishDir}\MultiTerminal.deps.json"; DestDir: "{app}"; Components: main; Flags: ignoreversion
Source: "{#PublishDir}\MultiTerminal.runtimeconfig.json"; DestDir: "{app}"; Components: main; Flags: ignoreversion
Source: "{#PublishDir}\MultiTerminal.dll.config"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#PublishDir}\*.xml"; DestDir: "{app}"; Components: main; Flags: ignoreversion

; ============================================================
; --- App-specific DLLs (always install — NuGet packages) ---
; ============================================================
Source: "{#PublishDir}\MultiTerminal.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion
Source: "{#PublishDir}\WeifenLuo.WinFormsUI.Docking.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion
Source: "{#PublishDir}\WeifenLuo.WinFormsUI.Docking.ThemeVS2015.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion
Source: "{#PublishDir}\Microsoft.Web.WebView2.Core.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion
Source: "{#PublishDir}\Microsoft.Web.WebView2.WinForms.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion
Source: "{#PublishDir}\Microsoft.Web.WebView2.Wpf.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion
Source: "{#PublishDir}\SQLite.Interop.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion
Source: "{#PublishDir}\System.Data.SQLite.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion
Source: "{#PublishDir}\WebView2Loader.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion
; LibGit2Sharp — git operations (GitRepoManager's static ctor loads it). Managed assembly
; PLUS its hash-named native git2-*.dll. Both were absent from this explicit file list, so a
; clean-machine install threw TypeInitializationException at MCP/chat init (task df1f521f).
Source: "{#PublishDir}\LibGit2Sharp.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion
Source: "{#PublishDir}\git2-*.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion
; More app NuGet deps that were absent from this list (found by publish-vs-iss audit, task
; df1f521f): MQTT presence messaging, web-push + its BouncyCastle VAPID crypto, and JSON.NET.
; Same failure class as LibGit2Sharp — they just load later, once their code path runs.
Source: "{#PublishDir}\MQTTnet.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion
Source: "{#PublishDir}\WebPush.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion
Source: "{#PublishDir}\BouncyCastle.Crypto.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion
Source: "{#PublishDir}\Newtonsoft.Json.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion
; Roslyn code graph indexer (v1.4.0+)
Source: "{#PublishDir}\Microsoft.CodeAnalysis.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion
Source: "{#PublishDir}\Microsoft.CodeAnalysis.CSharp.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion
Source: "{#PublishDir}\Microsoft.DiaSymReader.Native.amd64.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion
; SmartComponents local embeddings (v1.4.0+)
Source: "{#PublishDir}\SmartComponents.Inference.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion
Source: "{#PublishDir}\SmartComponents.LocalEmbeddings.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion
Source: "{#PublishDir}\FastBertTokenizer.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion
Source: "{#PublishDir}\Microsoft.ML.OnnxRuntime.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion
Source: "{#PublishDir}\onnxruntime.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion
Source: "{#PublishDir}\onnxruntime_providers_shared.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion

; ============================================================
; --- App content directories (HTML panels, Terminal) ---
; ============================================================
Source: "{#PublishDir}\ActivityPanel\*"; DestDir: "{app}\ActivityPanel"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\ChatPanel\*"; DestDir: "{app}\ChatPanel"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\Controls\*"; DestDir: "{app}\Controls"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\InboxPanel\*"; DestDir: "{app}\InboxPanel"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\OfficePanel\*"; DestDir: "{app}\OfficePanel"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\ProfilePanel\*"; DestDir: "{app}\ProfilePanel"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\ProjectSettings\*"; DestDir: "{app}\ProjectSettings"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\TaskLifecycleBoard\*"; DestDir: "{app}\TaskLifecycleBoard"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\TasksPanel\*"; DestDir: "{app}\TasksPanel"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\Terminal\*"; DestDir: "{app}\Terminal"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\AgentPanel\*"; DestDir: "{app}\AgentPanel"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\StartScreen\*"; DestDir: "{app}\StartScreen"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\FilePreviewPanel\*"; DestDir: "{app}\FilePreviewPanel"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\DashboardHeader\*"; DestDir: "{app}\DashboardHeader"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\shared\*"; DestDir: "{app}\shared"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\tools\*"; DestDir: "{app}\tools"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\LocalEmbeddingsModel\*"; DestDir: "{app}\LocalEmbeddingsModel"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs

; ============================================================
; --- .NET Runtime: Managed DLLs (skip if .NET 8 installed) ---
; ============================================================
Source: "{#PublishDir}\Microsoft.AspNetCore.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\Microsoft.AspNetCore.*.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\Microsoft.CSharp.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\Microsoft.Extensions.*.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\Microsoft.JSInterop*.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\Microsoft.Net.*.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\Microsoft.VisualBasic*.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\Microsoft.Win32.*.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\Microsoft.Bcl.*.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
; System.dll base assembly (wildcard below doesn't match it)
Source: "{#PublishDir}\System.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
; System.* DLLs EXCEPT our NuGet package System.Data.SQLite.dll
Source: "{#PublishDir}\System.*.dll"; Excludes: "System.Data.SQLite.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\mscorlib.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\netstandard.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\WindowsBase.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles

; ============================================================
; --- .NET Runtime: WPF/WinForms managed DLLs (skip if .NET 8) ---
; ============================================================
Source: "{#PublishDir}\Accessibility.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\DirectWriteForwarder.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\PresentationCore.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\PresentationFramework*.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\PresentationUI.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\ReachFramework.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\WindowsFormsIntegration.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\UIAutomation*.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles

; ============================================================
; --- .NET Runtime: Native binaries (skip if .NET 8) ---
; ============================================================
Source: "{#PublishDir}\coreclr.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\clrjit.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\clrgc.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\clretwrc.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\hostfxr.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\hostpolicy.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\createdump.exe"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\mscordaccore.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\mscordaccore_amd64_amd64_*.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\mscordbi.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\mscorrc.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\msquic.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\D3DCompiler_47_cor3.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\PenImc_cor3.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\PresentationNative_cor3.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\wpfgfx_cor3.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\aspnetcorev2_inprocess.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\vcruntime140_cor3.dll"; DestDir: "{app}"; Components: main; Flags: ignoreversion skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles

; ============================================================
; --- .NET Runtime: Locale resource directories (skip if .NET 8) ---
; ============================================================
Source: "{#PublishDir}\cs\*"; DestDir: "{app}\cs"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\de\*"; DestDir: "{app}\de"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\es\*"; DestDir: "{app}\es"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\fr\*"; DestDir: "{app}\fr"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\it\*"; DestDir: "{app}\it"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\ja\*"; DestDir: "{app}\ja"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\ko\*"; DestDir: "{app}\ko"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\pl\*"; DestDir: "{app}\pl"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\pt-BR\*"; DestDir: "{app}\pt-BR"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\ru\*"; DestDir: "{app}\ru"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\tr\*"; DestDir: "{app}\tr"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\zh-Hans\*"; DestDir: "{app}\zh-Hans"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles
Source: "{#PublishDir}\zh-Hant\*"; DestDir: "{app}\zh-Hant"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles

; ============================================================
; --- .NET Runtime: Native shims directory (skip if .NET 8) ---
; ============================================================
Source: "{#PublishDir}\runtimes\*"; DestDir: "{app}\runtimes"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist; Check: ShouldInstallRuntimeFiles

; ============================================================
; --- Bundled Tools (ripgrep for search_code MCP tool) ---
; ============================================================
Source: "{#ToolsDir}\rg.exe"; DestDir: "{app}\tools"; Components: tools; Flags: ignoreversion
Source: "{#ToolsDir}\rg-UNLICENSE.txt"; DestDir: "{app}\tools"; Components: tools; Flags: ignoreversion

; ============================================================
; --- HTML Documentation ---
; ============================================================
Source: "{#DocsDir}\*"; DestDir: "{app}\docs\html"; Components: docs; Flags: ignoreversion

; ============================================================
; --- Claude Code Plugin Marketplace ---
; --- Ships hooks, agents, skills, CLAUDE.md, and the
; --- multiterminal-channel MCP server (with node_modules).
; --- Claude Code auto-discovers marketplaces placed here.
; --- Destination is chosen on the "Plugin location" page (ticket
; --- c8ee0eec, GH#23): the default profile's
; --- %USERPROFILE%\.claude\plugins\marketplaces\multiterminal-marketplace
; --- (unchanged behaviour, the default choice) or a shared folder every
; --- Claude Code config profile can use. GetPluginDir returns exactly the
; --- old path for the default choice.
; ============================================================
Source: "{#PluginMarketplaceDir}\*"; DestDir: "{code:GetPluginDir}"; Components: claude\plugin; Flags: ignoreversion recursesubdirs createallsubdirs

; ============================================================
; --- MCP Server (to %APPDATA%\multiterminal\mcp) ---
; ============================================================
Source: "{#McpServerDir}\*"; DestDir: "{userappdata}\multiterminal\mcp"; Components: claude\mcp; Flags: ignoreversion recursesubdirs createallsubdirs

; ============================================================
; --- MCP Gateway (to {app}\mcp-gateway) ---
; ============================================================
Source: "{#McpGatewayPublishDir}\*"; DestDir: "{app}\mcp-gateway"; Components: claude\mcp; Flags: ignoreversion recursesubdirs createallsubdirs

; ============================================================
; --- Optional MCP Servers (to {app}\mcps\<name>) ---
; --- Selections controlled by custom wizard page ---
; ============================================================
Source: "{#McpMssqlDir}\dist\*"; DestDir: "{app}\mcps\mssql\dist"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: IsMcpSelected('mssql')
Source: "{#McpMssqlDir}\node_modules\*"; DestDir: "{app}\mcps\mssql\node_modules"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: IsMcpSelected('mssql')
Source: "{#McpMssqlDir}\package.json"; DestDir: "{app}\mcps\mssql"; Flags: ignoreversion; Check: IsMcpSelected('mssql')

Source: "{#McpSqliteDir}\custom-sqlite-mcp-server.js"; DestDir: "{app}\mcps\sqlite"; Flags: ignoreversion; Check: IsMcpSelected('sqlite')
Source: "{#McpSqliteDir}\node_modules\*"; DestDir: "{app}\mcps\sqlite\node_modules"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: IsMcpSelected('sqlite')
Source: "{#McpSqliteDir}\package.json"; DestDir: "{app}\mcps\sqlite"; Flags: ignoreversion; Check: IsMcpSelected('sqlite')

Source: "{#McpBuildRunnerDir}\build\*"; DestDir: "{app}\mcps\windows-build-runner\build"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: IsMcpSelected('windows-build-runner')
Source: "{#McpBuildRunnerDir}\node_modules\*"; DestDir: "{app}\mcps\windows-build-runner\node_modules"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: IsMcpSelected('windows-build-runner')
Source: "{#McpBuildRunnerDir}\package.json"; DestDir: "{app}\mcps\windows-build-runner"; Flags: ignoreversion; Check: IsMcpSelected('windows-build-runner')

Source: "{#McpSnapItDir}\index.js"; DestDir: "{app}\mcps\windowssnapit"; Flags: ignoreversion; Check: IsMcpSelected('windowssnapit')
Source: "{#McpSnapItDir}\Programs\*"; DestDir: "{app}\mcps\windowssnapit\Programs"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: IsMcpSelected('windowssnapit')
Source: "{#McpSnapItDir}\node_modules\*"; DestDir: "{app}\mcps\windowssnapit\node_modules"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: IsMcpSelected('windowssnapit')
Source: "{#McpSnapItDir}\package.json"; DestDir: "{app}\mcps\windowssnapit"; Flags: ignoreversion; Check: IsMcpSelected('windowssnapit')

Source: "{#McpEverythingDir}\build\*"; DestDir: "{app}\mcps\everything-search\build"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: IsMcpSelected('everything-search')
Source: "{#McpEverythingDir}\node_modules\*"; DestDir: "{app}\mcps\everything-search\node_modules"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: IsMcpSelected('everything-search')
Source: "{#McpEverythingDir}\package.json"; DestDir: "{app}\mcps\everything-search"; Flags: ignoreversion; Check: IsMcpSelected('everything-search')

; --- Post-install/uninstall scripts (temp) ---
Source: "post-install.js"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "post-uninstall.js"; DestDir: "{app}"; Flags: ignoreversion
; Shared-marketplace registration helpers, required by BOTH scripts above (ticket c8ee0eec)
Source: "plugin-registration.js"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "plugin-registration.js"; DestDir: "{app}"; Flags: ignoreversion

; ============================================================
; SHORTCUTS
; ============================================================
[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{#AppName} Documentation"; Filename: "{app}\docs\html\index.html"; Components: docs

; ============================================================
; POST-INSTALL: Configure Claude Code integration
; ============================================================
[Run]
; Run post-install script to generate configs (it does not write Claude Code's settings.json)
Filename: "node"; Parameters: """{tmp}\post-install.js"" ""{app}"" ""{userappdata}"" ""{%USERPROFILE}"" {code:GetDotNetFlag} {code:GetSelectedMcps} {code:GetGlobalRegFlag} {code:GetPluginModeArg} ""--plugin-root={code:GetPluginDir}"" ""--plugin-profiles={code:GetPluginProfilesArg}"""; \
  StatusMsg: "Configuring Claude Code integration..."; \
  Components: claude; Flags: runhidden waituntilterminated

; Refresh the icon cache for the user who pinned MT (runasoriginaluser: setup itself runs elevated,
; possibly as a different admin account). Complements ChangesAssociations above (GH #36).
Filename: "{sys}\ie4uinit.exe"; Parameters: "-show"; \
  StatusMsg: "Refreshing icons..."; \
  Flags: runhidden waituntilterminated runasoriginaluser skipifdoesntexist

; Offer to launch the app
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

; ============================================================
; UNINSTALL
; ============================================================
[UninstallRun]
; Run cleanup script before uninstalling files
Filename: "node"; Parameters: """{app}\post-uninstall.js"" ""{app}"" ""{userappdata}"" ""{%USERPROFILE}"""; \
  RunOnceId: "MultiTerminalCleanup"; Flags: runhidden waituntilterminated

[UninstallDelete]
; Note: MCP servers MAY be registered in ~/.claude.json (opt-in claude\globalreg component) —
;       post-uninstall.js removes the entries if present (no-op when the opt-in was off)
; Note: Plugin marketplace at %USERPROFILE%\.claude\plugins\... — cleaned up by post-uninstall.js
; Note: Shared plugin location (HKCU\Software\MultiTerminal\PluginMarketplacePath) - post-uninstall.js
;       de-registers it from every PluginRegisteredProfiles dir and deletes it only if
;       PluginDirCreatedByInstaller=1 AND its .mt-installer-owned marker matches
;       PluginDirOwnerToken AND it is not a reparse point. CurUninstallStepChanged(usPostUninstall)
;       also removes the four registry values, in case the script could not run.
Type: filesandordirs; Name: "{app}\mcp-gateway"
Type: filesandordirs; Name: "{app}\mcps"

; ============================================================
; PASCAL SCRIPT - .NET detection, prerequisite checks, uninstall
; ============================================================
[Code]
var
  DeleteUserData: Boolean;
  DotNet8Detected: Boolean;
  McpPage: TInputOptionWizardPage;
  // Plugin location (ticket c8ee0eec, GH#23)
  PluginLocPage: TInputOptionWizardPage;
  PluginDirPage: TInputDirWizardPage;
  PluginProfilesPage: TInputQueryWizardPage;
  PrevPluginPath: String;
  PrevPluginCreated: Cardinal;
  PrevPluginProfiles: String;
  PrevPluginToken: String;
  PluginDirExistedBefore: Boolean;

const
  // Shared with Services/LaunchCommandBuilder.cs and installer/plugin-registration.js
  PluginRegKey = 'Software\MultiTerminal';
  PluginRegPath = 'PluginMarketplacePath';
  PluginRegCreated = 'PluginDirCreatedByInstaller';
  PluginRegProfiles = 'PluginRegisteredProfiles';
  // Ownership proof for deleting the shared folder (Codex security review): HKCU is writable by
  // any same-user unelevated process, so uninstall (post-uninstall.js) deletes a folder only when
  // the marker file the installer wrote on CREATING that folder holds the same random token as
  // this value. An upgrade never deletes a shared folder.
  PluginRegToken = 'PluginDirOwnerToken';
  PluginOwnerMarker = '.mt-installer-owned';

type
  TMtGuid = record
    D1: LongWord;
    D2: Word;
    D3: Word;
    D4: array[0..7] of Byte;
  end;

function MtCoCreateGuid(var Guid: TMtGuid): Integer;
  external 'CoCreateGuid@ole32.dll stdcall';

// Check if .NET 8 Desktop Runtime is installed (includes WinForms + WPF + NETCore)
function IsDotNet8DesktopInstalled: Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetValueNames(HKLM,
    'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App',
    Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      if Pos('8.', Names[I]) = 1 then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
end;

// Check if ASP.NET Core 8 Runtime is installed
function IsDotNet8AspNetInstalled: Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetValueNames(HKLM,
    'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.AspNetCore.App',
    Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      if Pos('8.', Names[I]) = 1 then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
end;

// Returns True when runtime DLLs should be installed (i.e., .NET 8 is NOT fully present)
function ShouldInstallRuntimeFiles: Boolean;
begin
  Result := not (IsDotNet8DesktopInstalled and IsDotNet8AspNetInstalled);
end;

// Returns flag string for post-install.js to know which mode we're in
function GetDotNetFlag(Param: String): String;
begin
  if IsDotNet8DesktopInstalled and IsDotNet8AspNetInstalled then
    Result := '--framework-dependent'
  else
    Result := '--self-contained';
end;

// GH#2: tells post-install.js whether to register the multiterminal + mcp-gateway
// MCP servers globally in ~/.claude.json. Driven by the opt-in claude\globalreg
// component (unchecked by default) — MT-spawned terminals don't need global
// registration (they load MCP servers via --mcp-config at launch).
function GetGlobalRegFlag(Param: String): String;
begin
  if WizardIsComponentSelected('claude\globalreg') then
    Result := '--global-reg=yes'
  else
    Result := '--global-reg=no';
end;

// ============================================================
// Recommended MCP Servers - custom wizard page
// ============================================================

// Create the MCP recommendation page (called from InitializeWizard)
procedure CreateMcpPage;
begin
  McpPage := CreateInputOptionPage(wpSelectComponents,
    'Recommended MCP Servers',
    'We recommend the following MCP servers be installed alongside MultiTerminal.',
    'The servers checked below will be installed and registered with the MCP Gateway.' + #13#10 +
    'You can change these later via the MCP Gateway configuration.',
    False, False);

  // Index 0: MSSQL
  McpPage.Add('MSSQL — Microsoft SQL Server query and schema tools');
  McpPage.Values[0] := True;

  // Index 1: SQLite
  McpPage.Add('SQLite — Local SQLite database query and management tools');
  McpPage.Values[1] := True;

  // Index 2: Windows Build Runner
  McpPage.Add('Windows Build Runner — MSBuild/dotnet build execution tools');
  McpPage.Values[2] := True;

  // Index 3: Windows SnapIt
  McpPage.Add('Windows SnapIt — Window screenshot capture tools');
  McpPage.Values[3] := True;

  // Index 4: Everything Search
  McpPage.Add('Everything Search — Instant file search via voidtools Everything');
  McpPage.Values[4] := True;
end;

// ============================================================
// Plugin location page (ticket c8ee0eec, GH#23)
// ============================================================

// Today's location, byte-for-byte the old [Files] DestDir.
function DefaultProfilePluginDir: String;
begin
  Result := ExpandConstant('{%USERPROFILE}\.claude\plugins\marketplaces\multiterminal-marketplace');
end;

function SuggestedSharedPluginDir: String;
begin
  Result := ExpandConstant('{localappdata}\ClarionLive\claude-plugins\marketplaces\multiterminal-marketplace');
end;

function IsAbsoluteDir(Dir: String): Boolean;
begin
  Result := ((Length(Dir) >= 3) and (Dir[2] = ':') and (Dir[3] = '\')) or (Copy(Dir, 1, 2) = '\\');
end;

// Trimmed, unquoted, '/' folded to '\', an absolute path normalized ('.' and '..' segments
// resolved by ExpandFileName), no trailing backslash (a trailing backslash before a closing quote
// would swallow the quote when the path is passed on post-install.js's command line). A bare
// drive root stays "C:\". Same "same folder" definition as dirKey in plugin-registration.js
// (path.win32.normalize). Only the JS side of that case is pinned (installer/test); no test
// executes this Pascal function.
function CleanPluginPath(Value: String): String;
begin
  Result := Trim(Value);
  StringChangeEx(Result, '"', '', True);
  Result := Trim(Result);
  StringChangeEx(Result, '/', '\', True);
  if IsAbsoluteDir(Result) then
    Result := ExpandFileName(Result);
  while (Length(Result) > 3) and (Result[Length(Result)] = '\') do
    Result := Copy(Result, 1, Length(Result) - 1);
end;

function IsPluginComponentSelected: Boolean;
begin
  Result := WizardIsComponentSelected('claude\plugin');
end;

function IsSharedPluginLocation: Boolean;
begin
  Result := (PluginLocPage <> nil) and PluginLocPage.Values[1];
end;

// [Files] DestDir for the marketplace, and --plugin-root for post-install.js.
function GetPluginDir(Param: String): String;
begin
  if IsSharedPluginLocation then
    Result := CleanPluginPath(PluginDirPage.Values[0])
  else
    Result := DefaultProfilePluginDir;
end;

function GetPluginModeArg(Param: String): String;
begin
  if not IsPluginComponentSelected then
    Result := '--plugin-mode=skip'
  else if IsSharedPluginLocation then
    Result := '--plugin-mode=shared'
  else
    Result := '--plugin-mode=default';
end;

// ';'-separated extra config dirs (shared choice only). Never ends in a backslash, for the same
// quoting reason as CleanPluginPath; post-install.js ignores empty entries.
function GetPluginProfilesArg(Param: String): String;
begin
  Result := '';
  if IsSharedPluginLocation and (PluginProfilesPage <> nil) then
  begin
    Result := Trim(PluginProfilesPage.Values[0]);
    StringChangeEx(Result, '"', '', True);
    // A trailing '\' would escape the closing quote on the [Run] command line; a ';' after it is
    // an empty list entry, which post-install.js ignores.
    if (Length(Result) > 0) and (Result[Length(Result)] = '\') then
      Result := Result + ';';
  end;
end;

// True when one path is the other or contains it.
function PathsOverlap(A, B: String): Boolean;
begin
  A := Lowercase(CleanPluginPath(A));
  B := Lowercase(CleanPluginPath(B));
  Result := (A = B) or (Pos(A + '\', B) = 1) or (Pos(B + '\', A) = 1);
end;

procedure CreatePluginLocationPages(AfterID: Integer);
begin
  PluginLocPage := CreateInputOptionPage(AfterID,
    'Plugin location',
    'Where should the MultiTerminal Claude Code plugin be installed?',
    'The default installs it into your default Claude Code profile, as earlier versions did.' + #13#10 + #13#10 +
    'Choose the shared location if you use more than one Claude Code config profile ' +
    '(CLAUDE_CONFIG_DIR). It installs one copy outside any profile, and MultiTerminal terminals ' +
    'use it whichever profile they run under.',
    True, False);
  PluginLocPage.Add('Default Claude Code profile (%USERPROFILE%\.claude)');
  PluginLocPage.Add('Shared location, usable by every Claude Code config profile');

  PluginDirPage := CreateInputDirPage(PluginLocPage.ID,
    'Shared plugin location',
    'Choose the folder for the shared plugin marketplace.',
    'The MultiTerminal plugin marketplace will be installed into this folder. Choose a folder ' +
    'outside every .claude profile directory.',
    False, '');
  PluginDirPage.Add('');

  PluginProfilesPage := CreateInputQueryPage(PluginDirPage.ID,
    'Register in other Claude Code profiles (optional)',
    'Make the plugin marketplace known to other Claude Code config profiles.',
    'MultiTerminal terminals do not need this. Fill it in only if you want plain "claude" ' +
    'sessions in other profiles to see the MultiTerminal marketplace. Each listed profile gets ' +
    'the marketplace added, not the plugin installed: run "/plugin install" in that profile if ' +
    'you want it enabled there.' + #13#10 + #13#10 +
    'Separate profile directories with a semicolon, e.g. %USERPROFILE%\.claude-work. ' +
    'Directories that do not exist are skipped, and so is the default profile ' +
    '(%USERPROFILE%\.claude): only extra profiles are registered here.');
  PluginProfilesPage.Add('Claude Code config directories:', False);

  // Upgrade: pre-select the previous choice so an upgrade (including a silent one) never
  // quietly moves the plugin back into the default profile.
  if not RegQueryStringValue(HKCU, PluginRegKey, PluginRegPath, PrevPluginPath) then
    PrevPluginPath := '';
  PrevPluginPath := CleanPluginPath(PrevPluginPath);
  if not RegQueryDWordValue(HKCU, PluginRegKey, PluginRegCreated, PrevPluginCreated) then
    PrevPluginCreated := 0;
  if not RegQueryStringValue(HKCU, PluginRegKey, PluginRegProfiles, PrevPluginProfiles) then
    PrevPluginProfiles := '';
  if not RegQueryStringValue(HKCU, PluginRegKey, PluginRegToken, PrevPluginToken) then
    PrevPluginToken := '';

  if PrevPluginPath <> '' then
  begin
    PluginLocPage.Values[1] := True;
    PluginDirPage.Values[0] := PrevPluginPath;
    Log('Plugin location: previous install used the shared location ' + PrevPluginPath);
  end
  else
  begin
    PluginLocPage.Values[0] := True;
    PluginDirPage.Values[0] := SuggestedSharedPluginDir;
  end;
  PluginProfilesPage.Values[0] := PrevPluginProfiles;
end;

procedure InitializeWizard;
begin
  CreateMcpPage;
  CreatePluginLocationPages(McpPage.ID);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if (PluginLocPage <> nil) and (PageID = PluginLocPage.ID) then
    Result := not IsPluginComponentSelected
  else if ((PluginDirPage <> nil) and (PageID = PluginDirPage.ID)) or
          ((PluginProfilesPage <> nil) and (PageID = PluginProfilesPage.ID)) then
    Result := not (IsPluginComponentSelected and IsSharedPluginLocation);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Dir: String;
begin
  Result := True;
  if (PluginDirPage <> nil) and (CurPageID = PluginDirPage.ID) then
  begin
    Dir := CleanPluginPath(PluginDirPage.Values[0]);
    if (not IsAbsoluteDir(Dir)) or (Length(Dir) <= 3) then
    begin
      MsgBox('Please enter a full folder path (for example C:\...\multiterminal-marketplace), not a drive root.', mbError, MB_OK);
      Result := False;
    end
    else if Pos('%', Dir) > 0 then
    begin
      MsgBox('The folder path cannot contain a % character.', mbError, MB_OK);
      Result := False;
    end
    else if PathsOverlap(Dir, DefaultProfilePluginDir) then
    begin
      MsgBox('That is the default profile''s location. Go back and choose "Default Claude Code profile" instead.', mbError, MB_OK);
      Result := False;
    end
    else
      PluginDirPage.Values[0] := Dir;
  end;
end;

// A fresh random token for the ownership marker, as a braced GUID; '' if COM could not make one
// (then no marker is written and the folder is simply never deletable).
// Upper-case hex, exactly Digits wide (Inno's Pascal Script has no IntToHex).
function MtHex(Value: LongWord; Digits: Integer): String;
var
  I: Integer;
begin
  Result := '';
  for I := 1 to Digits do
  begin
    Result := Copy('0123456789ABCDEF', (Value and $F) + 1, 1) + Result;
    Value := Value shr 4;
  end;
end;

function NewPluginOwnerToken: String;
var
  G: TMtGuid;
  I: Integer;
begin
  Result := '';
  if MtCoCreateGuid(G) <> 0 then Exit;
  Result := '{' + MtHex(G.D1, 8) + '-' + MtHex(G.D2, 4) + '-' + MtHex(G.D3, 4) + '-' +
            MtHex(G.D4[0], 2) + MtHex(G.D4[1], 2) + '-';
  for I := 2 to 7 do
    Result := Result + MtHex(G.D4[I], 2);
  Result := Result + '}';
end;

// Trimmed content of <Dir>\.mt-installer-owned, or '' when absent/unreadable.
function ReadPluginOwnerMarker(Dir: String): String;
var
  S: AnsiString;
begin
  Result := '';
  if LoadStringFromFile(AddBackslash(Dir) + PluginOwnerMarker, S) then
    Result := Trim(String(S));
end;

// Persists the choice for the runtime lookup (LaunchCommandBuilder), upgrades and uninstall.
// PluginRegisteredProfiles is written by post-install.js, the only party that knows which
// registrations succeeded.
procedure SavePluginLocation;
var
  NewDir, Token: String;
  Created: Boolean;
begin
  if IsSharedPluginLocation then
  begin
    NewDir := GetPluginDir('');
    // Created if it was absent before this install, OR a previous install created this same
    // folder (an upgrade finds it present, and must not forget that it owns it).
    Created := (not PluginDirExistedBefore) or
               ((PrevPluginCreated = 1) and (CompareText(PrevPluginPath, NewDir) = 0));
    RegWriteStringValue(HKCU, PluginRegKey, PluginRegPath, NewDir);
    if Created then
      RegWriteDWordValue(HKCU, PluginRegKey, PluginRegCreated, 1)
    else
      RegWriteDWordValue(HKCU, PluginRegKey, PluginRegCreated, 0);
    Log('Plugin location: shared ' + NewDir + ' (created by installer: ' + IntToStr(Ord(Created)) + ')');

    // Ownership marker. A new token is minted ONLY when this run created the folder: HKCU alone
    // (forgeable) must never turn a pre-existing folder into a deletable one. An upgrade into the
    // same folder keeps the existing token when its marker still matches; it is never rotated.
    // A pre-existing folder without a valid marker (including one from the Phase-1 build) gets
    // no token and is therefore never deleted by uninstall (an upgrade deletes nothing).
    Token := '';
    if not PluginDirExistedBefore then
    begin
      Token := NewPluginOwnerToken;
      if (Token <> '') and not SaveStringToFile(AddBackslash(NewDir) + PluginOwnerMarker, Token, False) then
        Token := '';
    end
    else if (PrevPluginCreated = 1) and (CompareText(PrevPluginPath, NewDir) = 0) and
            (Trim(PrevPluginToken) <> '') and (ReadPluginOwnerMarker(NewDir) = Trim(PrevPluginToken)) then
      Token := Trim(PrevPluginToken);

    if Token <> '' then
    begin
      RegWriteStringValue(HKCU, PluginRegKey, PluginRegToken, Token);
      Log('Plugin location: ownership marker in place; uninstall may remove this folder');
    end
    else
    begin
      RegDeleteValue(HKCU, PluginRegKey, PluginRegToken);
      Log('Plugin location: no ownership marker; this folder will be kept by uninstall');
    end;
  end
  else
  begin
    NewDir := DefaultProfilePluginDir;
    // Default choice: no stale pointer may survive an upgrade that switched back.
    RegDeleteValue(HKCU, PluginRegKey, PluginRegPath);
    RegDeleteValue(HKCU, PluginRegKey, PluginRegCreated);
    RegDeleteValue(HKCU, PluginRegKey, PluginRegToken);
    Log('Plugin location: default profile ' + NewDir);
  end;

  // Moving away from a previous shared folder (including back to Default) never deletes it
  // (Owner decision, c8ee0eec Run 2). The folder and its marker file stay where they are. HKCU
  // now describes only the new location (a token only for a folder this run created, otherwise
  // none), so uninstall can never act on the old folder either.
  if (PrevPluginPath <> '') and (CompareText(PrevPluginPath, NewDir) <> 0) then
    Log('Plugin location: previous shared folder kept, not removed: ' + PrevPluginPath +
        ' (no longer used by MultiTerminal; delete it by hand if it is not wanted)');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if not IsPluginComponentSelected then Exit;
  if CurStep = ssInstall then
    PluginDirExistedBefore := DirExists(GetPluginDir(''))
  else if CurStep = ssPostInstall then
    SavePluginLocation;
end;

// Check function used by [Files] entries to conditionally install MCP files
function IsMcpSelected(McpName: String): Boolean;
begin
  Result := False;
  if McpPage = nil then Exit;

  if McpName = 'mssql' then
    Result := McpPage.Values[0]
  else if McpName = 'sqlite' then
    Result := McpPage.Values[1]
  else if McpName = 'windows-build-runner' then
    Result := McpPage.Values[2]
  else if McpName = 'windowssnapit' then
    Result := McpPage.Values[3]
  else if McpName = 'everything-search' then
    Result := McpPage.Values[4];
end;

// Returns comma-separated list of selected MCPs for post-install.js
function GetSelectedMcps(Param: String): String;
begin
  Result := '--mcps=';
  if (McpPage <> nil) then
  begin
    if McpPage.Values[0] then Result := Result + 'mssql,';
    if McpPage.Values[1] then Result := Result + 'sqlite,';
    if McpPage.Values[2] then Result := Result + 'windows-build-runner,';
    if McpPage.Values[3] then Result := Result + 'windowssnapit,';
    if McpPage.Values[4] then Result := Result + 'everything-search,';
  end;
  // Remove trailing comma
  if Result[Length(Result)] = ',' then
    Result := Copy(Result, 1, Length(Result) - 1);
  // If nothing selected, return empty flag
  if Result = '--mcps=' then
    Result := '--mcps=none';
end;

// Install Claude Code via winget, then run 'claude --version' to initialize config
function InstallClaudeCode: Boolean;
var
  ResultCode: Integer;
  ClaudeJsonPath: String;
begin
  Result := False;

  // Run winget install
  if not Exec('cmd', '/c winget install -e --id Anthropic.ClaudeCode --accept-source-agreements --accept-package-agreements', '',
              SW_SHOW, ewWaitUntilTerminated, ResultCode) then
    Exit;

  if ResultCode <> 0 then
    Exit;

  // Run 'claude --version' to initialize ~/.claude.json
  Exec('cmd', '/c claude --version', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // Check if config file now exists
  ClaudeJsonPath := ExpandConstant('{%USERPROFILE}\.claude.json');
  Result := FileExists(ClaudeJsonPath);
end;

// Check if Claude Code CLI is installed (required for MCP integration)
function IsClaudeCodeInstalled: Boolean;
var
  ClaudeJsonPath: String;
begin
  // Claude Code stores its config in %USERPROFILE%\.claude.json
  // If this file exists, Claude Code has been run at least once
  ClaudeJsonPath := ExpandConstant('{%USERPROFILE}\.claude.json');
  Result := FileExists(ClaudeJsonPath);
end;

function IsNodeInstalled: Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec('cmd', '/c node --version', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
            and (ResultCode = 0);
end;

// Major version of the node on PATH ('v24.1.0' -> 24), or 0 when it cannot be read.
// Exec cannot capture output, so 'node --version' is redirected to a temp file.
function GetNodeMajorVersion: Integer;
var
  ResultCode, I: Integer;
  OutFile: String;
  Output: AnsiString;
  Digits: String;
begin
  Result := 0;
  OutFile := ExpandConstant('{tmp}\node-version.txt');
  if not Exec('cmd', '/c node --version > "' + OutFile + '" 2>&1', '', SW_HIDE,
              ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
    Exit;
  if not LoadStringFromFile(OutFile, Output) then
    Exit;
  Output := Trim(Output);
  if (Length(Output) < 2) or (Output[1] <> 'v') then
    Exit;
  Digits := '';
  for I := 2 to Length(Output) do
  begin
    if (Output[I] < '0') or (Output[I] > '9') then
      Break;
    Digits := Digits + Output[I];
  end;
  Result := StrToIntDef(Digits, 0);
end;

function IsWebView2Installed: Boolean;
var
  Version: String;
begin
  // Check both HKLM and HKCU for WebView2 Evergreen Runtime
  Result := RegQueryStringValue(HKLM,
    'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
    'pv', Version);
  if not Result then
    Result := RegQueryStringValue(HKCU,
      'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
      'pv', Version);
  // Also check the per-machine key without WOW6432Node
  if not Result then
    Result := RegQueryStringValue(HKLM,
      'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
      'pv', Version);
end;

function InitializeSetup: Boolean;
var
  NodeMsg, WV2Msg, DotNetMsg: String;
  NodeMajor: Integer;
begin
  Result := True;

  // Detect .NET 8 and cache the result
  DotNet8Detected := IsDotNet8DesktopInstalled and IsDotNet8AspNetInstalled;

  // Show .NET detection status
  if DotNet8Detected then
    Log('.NET 8 Desktop + ASP.NET Core Runtime detected. Runtime DLLs will be skipped (~380 files).')
  else
  begin
    DotNetMsg := '.NET 8 Desktop Runtime was not detected on this system.' + #13#10 + #13#10 +
                 'The installer will include the full .NET runtime (~380 additional files).' + #13#10 +
                 'To reduce install size, install the .NET 8 Desktop Runtime first:' + #13#10 +
                 '  https://dotnet.microsoft.com/download/dotnet/8.0' + #13#10 + #13#10 +
                 'Continue with full (self-contained) installation?';
    if MsgBox(DotNetMsg, mbConfirmation, MB_YESNO) = IDNO then
    begin
      Result := False;
      Exit;
    end;
  end;

  // Check Claude Code (required - MCP servers need it)
  if not IsClaudeCodeInstalled then
  begin
    if MsgBox('Claude Code was not detected on this system.' + #13#10 + #13#10 +
              'MultiTerminal requires Claude Code for MCP servers and hooks.' + #13#10 + #13#10 +
              'Would you like to install Claude Code now using winget?' + #13#10 +
              '(This requires an internet connection and may take a minute)',
              mbConfirmation, MB_YESNO) = IDYES then
    begin
      // Install Claude Code via winget
      if not InstallClaudeCode then
      begin
        MsgBox('Claude Code installation did not complete successfully.' + #13#10 + #13#10 +
               'You can install it manually later:' + #13#10 +
               '  winget install -e --id Anthropic.ClaudeCode' + #13#10 + #13#10 +
               'Then run "claude" once to initialize.' + #13#10 + #13#10 +
               'Installation will continue, but Claude Code integration' + #13#10 +
               'will not work until Claude Code is installed and run once.',
               mbInformation, MB_OK);
      end;
    end
    else
    begin
      if MsgBox('Continue without Claude Code?' + #13#10 + #13#10 +
                'MultiTerminal will install, but Claude Code integration' + #13#10 +
                '(MCP servers, hooks, skills) will not work until you install' + #13#10 +
                'Claude Code and run it once.' + #13#10 + #13#10 +
                'To install later:  winget install -e --id Anthropic.ClaudeCode',
                mbConfirmation, MB_YESNO) = IDNO then
      begin
        Result := False;
        Exit;
      end;
    end;
  end;

  // Check Node.js
  if not IsNodeInstalled then
  begin
    NodeMsg := 'Node.js was not detected on this system.' + #13#10 + #13#10 +
               'Node.js 24 is required for:' + #13#10 +
               '  - MCP Server (agent tool interface)' + #13#10 +
               '  - Claude Code hooks (activity tracking)' + #13#10 + #13#10 +
               'You can install Node.js from https://nodejs.org' + #13#10 + #13#10 +
               'Continue installation without Node.js?' + #13#10 +
               '(Claude Code integration will not work until Node.js is installed)';
    if MsgBox(NodeMsg, mbConfirmation, MB_YESNO) = IDNO then
    begin
      Result := False;
      Exit;
    end;
  end
  else
  begin
    // GH #50: the plugin's hooks use the bundled better-sqlite3, a native binary built for Node 24,
    // which loads on no other major version (older OR newer). The MCP server still works, but the
    // hooks that record activity cannot open the database. 0 means the version could not be read;
    // say nothing rather than guess.
    NodeMajor := GetNodeMajorVersion;
    if (NodeMajor > 0) and (NodeMajor <> 24) then
    begin
      NodeMsg := 'Node.js ' + IntToStr(NodeMajor) + ' was detected. MultiTerminal needs Node.js 24.' + #13#10 + #13#10 +
                 'With another Node.js version the agent tools still work, but the Claude Code hooks' + #13#10 +
                 'cannot open MultiTerminal''s database, so these stop working:' + #13#10 +
                 '  - the Activity feed' + #13#10 +
                 '  - clearing Attention cards when an agent moves on' + #13#10 +
                 '  - session state (what each terminal is doing)' + #13#10 + #13#10 +
                 'You can install Node.js 24 from https://nodejs.org' + #13#10 + #13#10 +
                 'Continue installation anyway?';
      if MsgBox(NodeMsg, mbConfirmation, MB_YESNO) = IDNO then
      begin
        Result := False;
        Exit;
      end;
    end;
  end;

  // Check WebView2
  if not IsWebView2Installed then
  begin
    WV2Msg := 'Microsoft WebView2 Runtime was not detected.' + #13#10 + #13#10 +
              'WebView2 is required for MultiTerminal''s UI panels.' + #13#10 +
              'It is pre-installed on Windows 11 but may need to be' + #13#10 +
              'installed separately on Windows 10.' + #13#10 + #13#10 +
              'Download from: https://developer.microsoft.com/microsoft-edge/webview2/' + #13#10 + #13#10 +
              'Continue installation without WebView2?' + #13#10 +
              '(The application will not display correctly without it)';
    if MsgBox(WV2Msg, mbConfirmation, MB_YESNO) = IDNO then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  UserDataPath: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    UserDataPath := ExpandConstant('{userappdata}\multiterminal');
    if DirExists(UserDataPath) then
    begin
      DeleteUserData := MsgBox(
        'Do you want to delete your MultiTerminal user data?' + #13#10 + #13#10 +
        'This includes:' + #13#10 +
        '  - Task database (multiterminal.db)' + #13#10 +
        '  - Session history (sessions.db)' + #13#10 +
        '  - Message queue (messages.db)' + #13#10 +
        '  - Settings and prompts' + #13#10 + #13#10 +
        'Location: ' + UserDataPath + #13#10 + #13#10 +
        'Click Yes to delete all user data, or No to keep it.',
        mbConfirmation, MB_YESNO) = IDYES;

      if DeleteUserData then
        DelTree(UserDataPath, True, True, True);
    end;
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    // post-uninstall.js normally removes these after using them; this covers a machine where it
    // could not run (no Node.js), so no stale pointer outlives the app.
    RegDeleteValue(HKCU, PluginRegKey, PluginRegPath);
    RegDeleteValue(HKCU, PluginRegKey, PluginRegCreated);
    RegDeleteValue(HKCU, PluginRegKey, PluginRegProfiles);
    RegDeleteValue(HKCU, PluginRegKey, PluginRegToken);
    RegDeleteKeyIfEmpty(HKCU, PluginRegKey);
  end;
end;
