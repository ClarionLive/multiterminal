# Building and running MultiTerminal from source

This guide is for anyone who has **cloned or forked** this repository and wants to build MultiTerminal (MT) and run it on their own machine. If you just want to *use* MT, use the installer instead; see [DEPLOYMENT-GUIDE.md](DEPLOYMENT-GUIDE.md).

It was written by following these steps on a clean Windows 11 machine that had never run MT (`main` @ `d70ea08`, October 2026). Where something surprised us, it is called out.

## 1. Prerequisites

| Requirement | Notes |
|---|---|
| **Windows 10/11, x64** | MT is a WinForms/WebView2 app (`net8.0-windows`, `win-x64`). It does not build on Linux or macOS. |
| **.NET SDK 8 or later** | SDK 10 works (that is what CI uses). There is deliberately no `global.json`. You do **not** need a .NET 8 *runtime* to run the app: it builds self-contained. |
| **Node.js** | Needed by the build (it installs MT's MCP server with `npm ci`) and by the Claude Code plugin's hooks. **See the Node version note below.** |
| **Git** | Plus a GitHub SSH key if you want the plugin install in step 5 to work as written. |
| **Claude Code CLI** | Installed, on `PATH`, and signed in at least once (`claude` in a normal terminal). |
| **WebView2 Runtime** | Preinstalled on Windows 11. On Windows 10 you may need to install it. |

> ⚠️ **Node version.** The plugin's hooks store activity, profiles and session state in MT's SQLite database through a bundled native `better-sqlite3` binary built for **Node 24**. On any other Node major version that binary does not load, and those features switch off **silently** (for example, the Attention rail never clears a card after you answer a question). Until [multiterminal-marketplace#2](https://github.com/ClarionLive/multiterminal-marketplace/pull/2) is merged, use **Node 24**. Once it is, any Node from 22.13 up works (22.5 to 22.12 have the built-in SQLite only behind a flag the hooks do not use). Tracking issue: [multiterminal-marketplace#1](https://github.com/ClarionLive/multiterminal-marketplace/issues/1).

## 2. Clone

```powershell
git clone https://github.com/ClarionLive/multiterminal.git   # or your fork
cd multiterminal
```

## 3. Build

```powershell
dotnet build MultiTerminal.csproj -c Debug -p:SharedStagedPath=C:\path\to\some\scratch\folder
```

The app is built to:

```
bin\Debug\net8.0-windows\win-x64\MultiTerminal.exe
```

**Why `-p:SharedStagedPath=…`:** after every build, the project mirrors its output to a "staged" folder for the maintainers' own deploy flow. The default is a path on the maintainer's machine (`H:\DevLaptop\ClarionPowerShell\staged`). Point it anywhere you like instead, either with that property or once with an environment variable:

```powershell
setx MULTITERMINAL_STAGED_PATH C:\path\to\some\scratch\folder
```

You can ignore the staged folder afterwards; you run MT from `bin\Debug` (step 4). `deploy.ps1` and the `Deploy` folder belong to the maintainers' flow too and are not needed.

**What the first build does outside the repository** (expected, but worth knowing):

- **Downloads a ~17 MB embedding model** from huggingface.co (`SmartComponents.LocalEmbeddings`, used for local semantic search). So the first build needs internet access beyond NuGet.
- **Installs MT's MCP server** into `%APPDATA%\multiterminal\mcp` (copies `mcp\` and runs `npm ci`). Every build refreshes it. This is how terminals get MT's tools; you do not register anything by hand.

## 4. Run

```powershell
.\bin\Debug\net8.0-windows\win-x64\MultiTerminal.exe
```

On first launch:

1. **User Profile** asks for a full name and email. These are **not** credentials. They are stored in MT's own database and used for git commit attribution. They are not read from your `git config`, so type the same values. GitHub/Bitbucket sign-in is separate (Settings → Source Control Accounts).
2. If the Claude Code plugin is not installed yet, MT warns **"agent tools unavailable … The MultiTerminal Claude Code plugin was not found"**. The warning suggests re-running the installer; when building from source, do step 5 instead.

MT creates its data folder `%APPDATA%\multiterminal` (database, settings, logs) and writes `%APPDATA%\multiterminal\.mcp.json`, which it passes to every Claude terminal with `--mcp-config`. Nothing is added to your global `~/.claude.json`.

> **Rebuilding while MT is running:** MT runs from the build output folder, so `dotnet build` fails on locked files while it is open. Close MT first, or build somewhere else to check for errors (`dotnet build MultiTerminal.csproj -c Debug -o C:\some\other\folder -p:SharedStagedPath=…`).

## 5. Install the Claude Code plugin

The plugin (hooks, skills, agents, agent rules) lives in a separate repository and is installed into Claude Code once per machine:

```powershell
claude plugin marketplace add ClarionLive/multiterminal-marketplace
claude plugin install multiterminal@multiterminal-marketplace
```

MT looks for it at `%USERPROFILE%\.claude\plugins\marketplaces\multiterminal-marketplace\plugins\multiterminal`, which is where these commands put it. It is passed only to MT's terminals (`--plugin-dir`), so your other Claude Code sessions are unaffected.

On our test machine the marketplace was cloned **over SSH** (`git@github.com:…`), which needs a GitHub SSH key. If that fails, add it by its HTTPS URL instead: `claude plugin marketplace add https://github.com/ClarionLive/multiterminal-marketplace.git`.

Restart MT after installing the plugin; the warning from step 4 should not come back.

## 6. Check it works

Open a terminal in MT (**Just Claude**, pick an agent name and a working folder). Use a small project folder rather than your user folder, which would make Claude ask to trust your whole profile. In the new pane, ask Claude to run `list_terminals`. If it lists the terminals, the MCP server and plugin are wired up.

To check the Node note from step 1: after answering the first question the agent asks, its card on the Attention rail should stop pulsing on its own.

## 7. Tests and lint

These are the commands CI runs (`.github/workflows/ci.yml`), from the repository root:

```powershell
dotnet build MultiTerminal.csproj -c Debug
dotnet build MultiTerminal.csproj -c Release            # also stages the MCP server for the installer
dotnet build AgentProcessTest/AgentProcessTest.csproj -c Debug
dotnet test MultiTerminal.Tests/MultiTerminal.Tests.csproj -c Debug
npm ci; npm run lint
node --test mcp/test/*.test.mjs                          # Node 21+ for the glob
```

The test project is not self-contained, so it needs a **.NET 8 runtime**. If you only have a newer one installed (for example only .NET 10), let it roll forward:

```powershell
$env:DOTNET_ROLL_FORWARD = 'Major'
dotnet test MultiTerminal.Tests/MultiTerminal.Tests.csproj -c Debug
```

Add `-p:SharedStagedPath=…` to the build commands as in step 3 if you have not set `MULTITERMINAL_STAGED_PATH`.

## Where to go next

- Full documentation: [clarionlive.github.io/multiterminal](https://clarionlive.github.io/multiterminal/html/index.html) (also under `docs/html/`, and the ❓ button in the app).
- Working on MT's code: `.claude/CLAUDE.md` is the codebase reference (architecture, linting, CI details).
