; MultiTerminal Inno Setup Script
; Requires Inno Setup 6.x+
; https://jrsoftware.org/isinfo.php

#define AppName "MultiTerminal"
#define AppVersion "1.0.0"
#define AppPublisher "MultiTerminal"
#define AppExeName "MultiTerminal.exe"
#define AppURL "https://github.com/peterparker57"

; Source directories - adjust these for your build machine
#define PublishDir "..\bin\Release\net8.0-windows\win-x64\publish"
#define McpServerDir GetEnv("APPDATA") + "\multiterminal\mcp"
#define HooksDir GetEnv("USERPROFILE") + "\.claude\hooks"
#define SkillsDir GetEnv("USERPROFILE") + "\.claude\skills"
#define ClaudeProjectDir "..\.claude"

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
SetupIconFile=
UninstallDisplayIcon={app}\{#AppExeName}
LicenseFile=..\LICENSE
DisableProgramGroupPage=yes
; Code signing (requires Sectigo EV dongle to be plugged in)
SignTool=sectigo
SignedUninstaller=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "full"; Description: "Full installation (app + Claude Code integration)"
Name: "app"; Description: "Application only"
Name: "custom"; Description: "Custom installation"; Flags: iscustom

[Components]
Name: "main"; Description: "MultiTerminal Application"; Types: full app custom; Flags: fixed
Name: "claude"; Description: "Claude Code Integration"; Types: full
Name: "claude\mcp"; Description: "MCP Server (agent tool interface)"; Types: full
Name: "claude\hooks"; Description: "Session hooks (activity tracking, status)"; Types: full
Name: "claude\skills"; Description: "Skills (/kanban-task, /multiterminal-addproject, /profile)"; Types: full

; ============================================================
; FILES
; ============================================================
[Files]
; --- Main Application ---
Source: "{#PublishDir}\*"; DestDir: "{app}"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs

; --- Project .claude folder ---
Source: "{#ClaudeProjectDir}\CLAUDE.md"; DestDir: "{app}\.claude"; Components: main; Flags: ignoreversion

; --- MCP Server (to %APPDATA%\multiterminal\mcp) ---
Source: "{#McpServerDir}\*"; DestDir: "{userappdata}\multiterminal\mcp"; Components: claude\mcp; Flags: ignoreversion recursesubdirs createallsubdirs

; --- Hook Scripts (to %USERPROFILE%\.claude\hooks) ---
Source: "{#HooksDir}\session-status-hook.js"; DestDir: "{%USERPROFILE}\.claude\hooks"; Components: claude\hooks; Flags: ignoreversion
Source: "{#HooksDir}\activity-hook.js"; DestDir: "{%USERPROFILE}\.claude\hooks"; Components: claude\hooks; Flags: ignoreversion
Source: "{#HooksDir}\pool-context.js"; DestDir: "{%USERPROFILE}\.claude\hooks"; Components: claude\hooks; Flags: ignoreversion
Source: "{#HooksDir}\profile-status-hook.js"; DestDir: "{%USERPROFILE}\.claude\hooks"; Components: claude\hooks; Flags: ignoreversion

; --- Skills (to %USERPROFILE%\.claude\skills) ---
Source: "{#SkillsDir}\kanban-task\*"; DestDir: "{%USERPROFILE}\.claude\skills\kanban-task"; Components: claude\skills; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SkillsDir}\multiterminal-addproject\*"; DestDir: "{%USERPROFILE}\.claude\skills\multiterminal-addproject"; Components: claude\skills; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SkillsDir}\profile\*"; DestDir: "{%USERPROFILE}\.claude\skills\profile"; Components: claude\skills; Flags: ignoreversion recursesubdirs createallsubdirs

; --- Post-install/uninstall scripts (temp) ---
Source: "post-install.js"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "post-uninstall.js"; DestDir: "{app}"; Flags: ignoreversion

; ============================================================
; SHORTCUTS
; ============================================================
[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"

; ============================================================
; POST-INSTALL: Configure Claude Code integration
; ============================================================
[Run]
; Run post-install script to merge hooks into settings.json and generate mcp.json
Filename: "node"; Parameters: """{tmp}\post-install.js"" ""{app}"" ""{userappdata}"" ""{%USERPROFILE}"""; \
  StatusMsg: "Configuring Claude Code integration..."; \
  Components: claude; Flags: runhidden waituntilterminated

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
; Clean up generated config files
Type: files; Name: "{app}\.claude\project.json"
Type: dirifempty; Name: "{app}\.claude"

; ============================================================
; PASCAL SCRIPT - Prerequisite checks & uninstall prompts
; ============================================================
[Code]
var
  DeleteUserData: Boolean;

function IsNodeInstalled: Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec('cmd', '/c node --version', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
            and (ResultCode = 0);
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
  NodeMsg, WV2Msg: String;
begin
  Result := True;

  // Check Node.js
  if not IsNodeInstalled then
  begin
    NodeMsg := 'Node.js was not detected on this system.' + #13#10 + #13#10 +
               'Node.js 18+ is required for:' + #13#10 +
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
        '  - Task database (tasks.db)' + #13#10 +
        '  - Session history (sessions.db)' + #13#10 +
        '  - Message queue (messages.db)' + #13#10 +
        '  - Settings and prompts' + #13#10 + #13#10 +
        'Location: ' + UserDataPath + #13#10 + #13#10 +
        'Click Yes to delete all user data, or No to keep it.',
        mbConfirmation, MB_YESNO) = IDYES;

      if DeleteUserData then
        DelTree(UserDataPath, True, True, True);
    end;
  end;
end;
