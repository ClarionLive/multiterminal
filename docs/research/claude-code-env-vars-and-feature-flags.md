# Claude Code Environment Variables & Feature Flags Catalog

Internal research document. Compiled from the March 31 2026 npm source map leak (v2.1.88),
community analysis (ccleaks.com, GitHub gists, blog posts), and the official docs at
code.claude.com/docs/en/env-vars.

---

## Table of Contents

1. [Overview](#overview)
2. [Officially Documented Env Vars](#officially-documented-env-vars)
3. [Undocumented / Leaked Env Vars](#undocumented--leaked-env-vars)
4. [The 32 Compile-Time Build Flags](#the-32-compile-time-build-flags)
5. [GrowthBook Runtime Feature Flags (tengu_*)](#growthbook-runtime-feature-flags-tengu_)
6. [USER_TYPE=ant -- Anthropic Employee Mode](#user_typeant----anthropic-employee-mode)
7. [GrowthBook SDK Integration](#growthbook-sdk-integration)
8. [CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC -- Why It Breaks Things](#claude_code_disable_nonessential_traffic----why-it-breaks-things)
9. [Env Vars We Care About (Hooks, MCP, Plugins, Permissions, Context, Compaction)](#env-vars-we-care-about)
10. [Env Vars That Suppress Warnings or Change Defaults](#env-vars-that-suppress-warnings-or-change-defaults)
11. [Internal Codenames](#internal-codenames)
12. [Anti-Distillation Mechanisms](#anti-distillation-mechanisms)
13. [Sources](#sources)

---

## Overview

The leaked Claude Code source (512K lines of TypeScript across 1,884 files) revealed:
- **120+ environment variables** (official docs cover ~80; the rest are undocumented)
- **32 compile-time build flags** (dead-code-eliminated from public builds)
- **15+ runtime feature flags** (tengu_* namespace, controlled via GrowthBook)
- **26 hidden slash commands**
- **12 secret CLI flags**
- **8 unreleased features** (KAIROS, BUDDY, ULTRAPLAN, Coordinator Mode, etc.)

---

## Officially Documented Env Vars

These are from code.claude.com/docs/en/env-vars. They are safe to use.

### Authentication & API

| Variable | Purpose |
|----------|---------|
| `ANTHROPIC_API_KEY` | API key for authentication |
| `ANTHROPIC_AUTH_TOKEN` | Custom Bearer token |
| `ANTHROPIC_BASE_URL` | Override API endpoint (for proxies/gateways) |
| `CLAUDE_CODE_USE_BEDROCK` | Route API calls through AWS Bedrock |
| `CLAUDE_CODE_USE_VERTEX` | Route API calls through Google Vertex AI |
| `CLAUDE_CODE_USE_FOUNDRY` | Route API calls through Microsoft Foundry |
| `CLAUDE_CODE_SKIP_BEDROCK_AUTH` | Skip AWS authentication for Bedrock |
| `CLAUDE_CODE_SKIP_VERTEX_AUTH` | Skip Google authentication for Vertex |
| `CLAUDE_CODE_SKIP_FOUNDRY_AUTH` | Skip Azure authentication for Foundry |
| `ANTHROPIC_BEDROCK_BASE_URL` | Override Bedrock endpoint |
| `ANTHROPIC_VERTEX_BASE_URL` | Override Vertex AI endpoint |
| `ANTHROPIC_FOUNDRY_BASE_URL` | Full base URL for Foundry |
| `ANTHROPIC_FOUNDRY_RESOURCE` | Foundry resource name |
| `ANTHROPIC_FOUNDRY_API_KEY` | API key for Foundry |
| `ANTHROPIC_VERTEX_PROJECT_ID` | GCP project ID for Vertex |
| `AWS_BEARER_TOKEN_BEDROCK` | Bedrock bearer token auth |
| `CLAUDE_CODE_OAUTH_TOKEN` | OAuth access token |
| `CLAUDE_CODE_OAUTH_REFRESH_TOKEN` | OAuth refresh token (skips browser flow) |
| `CLAUDE_CODE_OAUTH_SCOPES` | Space-separated OAuth scopes |
| `CLAUDE_CODE_API_KEY_HELPER_TTL_MS` | Credential refresh interval |
| `CLAUDE_CODE_API_KEY_FILE_DESCRIPTOR` | File descriptor for API key |
| `CLAUDE_CODE_SUBPROCESS_ENV_SCRUB` | Strip credentials from subprocess environments |

### Model Configuration

| Variable | Purpose |
|----------|---------|
| `ANTHROPIC_MODEL` | Default model override |
| `CLAUDE_CODE_SUBAGENT_MODEL` | Model for subagents/workers |
| `CLAUDE_CODE_MAX_OUTPUT_TOKENS` | Maximum output tokens (64000 doubles default) |
| `MAX_THINKING_TOKENS` | Thinking budget control |
| `CLAUDE_CODE_EFFORT_LEVEL` | Effort level: low, medium, high, max, auto |
| `ANTHROPIC_CUSTOM_MODEL_OPTION` | Custom model ID for /model picker |
| `ANTHROPIC_CUSTOM_MODEL_OPTION_NAME` | Display name for custom model |
| `ANTHROPIC_CUSTOM_MODEL_OPTION_DESCRIPTION` | Description for custom model |
| `ANTHROPIC_DEFAULT_OPUS_MODEL` | Override default Opus model ID |
| `ANTHROPIC_DEFAULT_SONNET_MODEL` | Override default Sonnet model ID |
| `ANTHROPIC_DEFAULT_HAIKU_MODEL` | Override default Haiku model ID |
| `ANTHROPIC_SMALL_FAST_MODEL` | [DEPRECATED] Haiku-class model for background tasks |
| `ANTHROPIC_BETAS` | Comma-separated beta header values |
| `ANTHROPIC_CUSTOM_HEADERS` | Custom headers for API requests |
| `API_TIMEOUT_MS` | API request timeout (default: 600000) |
| `CLAUDE_CODE_MAX_RETRIES` | Retry count for failed requests (default: 10) |

### Bash & Shell

| Variable | Purpose |
|----------|---------|
| `BASH_DEFAULT_TIMEOUT_MS` | Default bash command timeout |
| `BASH_MAX_TIMEOUT_MS` | Maximum bash command timeout |
| `BASH_MAX_OUTPUT_LENGTH` | Max chars before bash output truncation |
| `CLAUDE_CODE_SHELL` | Override automatic shell detection |
| `CLAUDE_CODE_SHELL_PREFIX` | Prefix command wrapping all bash commands |
| `CLAUDE_CODE_GIT_BASH_PATH` | Windows: path to Git Bash |
| `CLAUDE_CODE_USE_POWERSHELL_TOOL` | Enable PowerShell tool on Windows |
| `CLAUDE_CODE_BASH_MAINTAIN_PROJECT_WORKING_DIR` | Return to original cwd after bash |
| `CLAUDECODE` | Set to 1 in shell environments spawned by Claude Code |
| `CLAUDE_ENV_FILE` | Path to shell script sourced before each bash command |

### Memory & Context

| Variable | Purpose |
|----------|---------|
| `CLAUDE_CODE_DISABLE_AUTO_MEMORY` | Disable automatic memory |
| `CLAUDE_CODE_DISABLE_CLAUDE_MDS` | Prevent loading CLAUDE.md files |
| `CLAUDE_CODE_ADDITIONAL_DIRECTORIES_CLAUDE_MD` | Load CLAUDE.md from additional dirs |
| `DISABLE_AUTO_COMPACT` | Disable automatic context compaction |
| `DISABLE_COMPACT` | Disable ALL compaction (auto + manual) |
| `CLAUDE_AUTOCOMPACT_PCT_OVERRIDE` | Trigger compaction at N% (default ~80-90%) |
| `CLAUDE_CODE_AUTO_COMPACT_WINDOW` | Context capacity in tokens for compaction calculations |

### Feature Toggles (Documented)

| Variable | Purpose |
|----------|---------|
| `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC` | Kill telemetry + analytics + GrowthBook (WARNING: see section 8) |
| `CLAUDE_CODE_DISABLE_ATTACHMENTS` | Disable file attachments |
| `CLAUDE_CODE_DISABLE_BACKGROUND_TASKS` | Disable background tasks |
| `CLAUDE_CODE_DISABLE_CRON` | Disable scheduled tasks |
| `CLAUDE_CODE_DISABLE_FAST_MODE` | Disable fast mode |
| `CLAUDE_CODE_DISABLE_FILE_CHECKPOINTING` | Disable file checkpointing |
| `CLAUDE_CODE_DISABLE_GIT_INSTRUCTIONS` | Remove git instructions from system prompt |
| `CLAUDE_CODE_DISABLE_TERMINAL_TITLE` | Disable terminal title updates |
| `CLAUDE_CODE_DISABLE_THINKING` | Force-disable extended thinking |
| `CLAUDE_CODE_DISABLE_ADAPTIVE_THINKING` | Disable adaptive reasoning |
| `CLAUDE_CODE_DISABLE_1M_CONTEXT` | Disable 1M context window |
| `CLAUDE_CODE_DISABLE_LEGACY_MODEL_REMAP` | Prevent remapping Opus 4.0/4.1 |
| `CLAUDE_CODE_DISABLE_EXPERIMENTAL_BETAS` | Strip Anthropic-specific beta headers |
| `DISABLE_INTERLEAVED_THINKING` | Prevent interleaved-thinking beta header |
| `DISABLE_PROMPT_CACHING` | Disable prompt caching (all models) |
| `DISABLE_PROMPT_CACHING_HAIKU` | Disable caching for Haiku only |
| `DISABLE_PROMPT_CACHING_OPUS` | Disable caching for Opus only |
| `DISABLE_PROMPT_CACHING_SONNET` | Disable caching for Sonnet only |
| `ENABLE_PROMPT_CACHING_1H_BEDROCK` | 1-hour cache TTL for Bedrock |
| `CLAUDE_CODE_ENABLE_PROMPT_SUGGESTION` | Enable/disable prompt suggestions |
| `CLAUDE_CODE_ENABLE_TASKS` | Enable task tracking in non-interactive mode |
| `ENABLE_TOOL_SEARCH` | Control MCP tool search/deferred loading |
| `ENABLE_CLAUDEAI_MCP_SERVERS` | Control MCP servers from claude.ai |
| `FALLBACK_FOR_ALL_PRIMARY_MODELS` | Enable fallback after overload errors |

### Telemetry & Updates

| Variable | Purpose |
|----------|---------|
| `CLAUDE_CODE_ENABLE_TELEMETRY` | Enable OpenTelemetry collection |
| `DISABLE_TELEMETRY` | Opt out of Statsig telemetry |
| `DISABLE_ERROR_REPORTING` | Opt out of Sentry error reporting |
| `DISABLE_AUTOUPDATER` | Disable automatic updates |
| `DISABLE_COST_WARNINGS` | Disable cost warning messages |
| `DISABLE_FEEDBACK_SURVEY` | Disable session quality surveys |

### Tool & File Configuration

| Variable | Purpose |
|----------|---------|
| `CLAUDE_CODE_FILE_READ_MAX_OUTPUT_TOKENS` | Override token limit for file reads |
| `CLAUDE_CODE_GLOB_HIDDEN` | Include dotfiles in Glob results |
| `CLAUDE_CODE_GLOB_NO_IGNORE` | Ignore .gitignore in globs |
| `CLAUDE_CODE_GLOB_TIMEOUT_SECONDS` | Glob timeout (default: 20-60s) |
| `CLAUDE_CODE_MAX_TOOL_USE_CONCURRENCY` | Max parallel tools/subagents (default: 10) |

### IDE Integration

| Variable | Purpose |
|----------|---------|
| `CLAUDE_CODE_AUTO_CONNECT_IDE` | Override auto IDE connection |
| `CLAUDE_CODE_IDE_HOST_OVERRIDE` | Override IDE host address |
| `CLAUDE_CODE_IDE_SKIP_AUTO_INSTALL` | Skip IDE extension auto-install |
| `CLAUDE_CODE_IDE_SKIP_VALID_CHECK` | Skip IDE lockfile validation |

### Plugins

| Variable | Purpose |
|----------|---------|
| `CLAUDE_CODE_PLUGIN_CACHE_DIR` | Override plugins root directory |
| `CLAUDE_CODE_PLUGIN_GIT_TIMEOUT_MS` | Git timeout for plugin install (default: 120000) |
| `CLAUDE_CODE_PLUGIN_SEED_DIR` | Path to read-only plugin seed directories |
| `CLAUDE_CODE_DISABLE_OFFICIAL_MARKETPLACE_AUTOINSTALL` | Skip official marketplace auto-add |
| `CLAUDE_CODE_SYNC_PLUGIN_INSTALL` | Wait for plugin install before first query |
| `CLAUDE_CODE_SYNC_PLUGIN_INSTALL_TIMEOUT_MS` | Timeout for sync plugin install |
| `FORCE_AUTOUPDATE_PLUGINS` | Force plugin auto-update |

### MCP & Network

| Variable | Purpose |
|----------|---------|
| `MCP_TIMEOUT` | MCP server connection timeout |
| `MCP_TOOL_TIMEOUT` | MCP tool execution timeout |
| `MAX_MCP_OUTPUT_TOKENS` | Maximum output tokens for MCP results |
| `CLAUDE_AGENT_SDK_MCP_NO_PREFIX` | Skip mcp__server__ prefix on tool names |
| `CLAUDE_CODE_PROXY_RESOLVES_HOSTS` | Allow proxy to do DNS resolution |
| `HTTP_PROXY` / `HTTPS_PROXY` / `NO_PROXY` | Proxy configuration |
| `CLAUDE_ENABLE_STREAM_WATCHDOG` | Abort stalled streams (90s default) |
| `CLAUDE_STREAM_IDLE_TIMEOUT_MS` | Stall timeout (default: 90000) |
| `CLAUDE_CODE_CLIENT_CERT` | mTLS client certificate path |
| `CLAUDE_CODE_CLIENT_KEY` | mTLS client private key path |
| `CLAUDE_CODE_CLIENT_KEY_PASSPHRASE` | mTLS key passphrase |

### Agent & Team Configuration

| Variable | Purpose |
|----------|---------|
| `CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS` | Enable agent teams (experimental) |
| `CLAUDE_CODE_TEAM_NAME` | Agent team name |
| `CLAUDE_AGENT_SDK_DISABLE_BUILTIN_AGENTS` | Disable built-in subagent types (SDK) |
| `CLAUDE_CODE_TASK_LIST_ID` | Share task list across sessions |
| `CLAUDE_CODE_AUTO_BACKGROUND_TASKS` | Force auto-background long tasks |
| `CLAUDE_CODE_RESUME_INTERRUPTED_TURN` | Auto-resume mid-turn sessions |
| `CLAUDE_CODE_NEW_INIT` | New /init interactive setup flow |

### OpenTelemetry

| Variable | Purpose |
|----------|---------|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | OTLP exporter endpoint |
| `OTEL_EXPORTER_OTLP_HEADERS` | OTLP request headers |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | Transport protocol |
| `OTEL_TRACES_EXPORTER` | Traces exporter type |
| `OTEL_METRICS_EXPORTER` | Metrics exporter type |
| `OTEL_LOGS_EXPORTER` | Logs exporter type |
| `CLAUDE_CODE_OTEL_FLUSH_TIMEOUT_MS` | Flush timeout (default: 5000) |
| `CLAUDE_CODE_OTEL_SHUTDOWN_TIMEOUT_MS` | Shutdown timeout (default: 2000) |

### Command Visibility

| Variable | Purpose |
|----------|---------|
| `DISABLE_DOCTOR_COMMAND` | Hide /doctor |
| `DISABLE_FEEDBACK_COMMAND` | Disable /feedback |
| `DISABLE_BUG_COMMAND` | Disable /bug |
| `DISABLE_LOGIN_COMMAND` | Hide /login |
| `DISABLE_LOGOUT_COMMAND` | Hide /logout |
| `DISABLE_UPGRADE_COMMAND` | Hide /upgrade |
| `DISABLE_EXTRA_USAGE_COMMAND` | Hide /extra-usage |
| `DISABLE_INSTALL_GITHUB_APP_COMMAND` | Hide /install-github-app |
| `DISABLE_INSTALLATION_CHECKS` | Disable installation warnings |

### Debugging

| Variable | Purpose |
|----------|---------|
| `CLAUDE_CODE_DEBUG_LOGS_DIR` | Override debug log path |
| `CLAUDE_CODE_DEBUG_LOG_LEVEL` | Log level: verbose, debug, info, warn, error |
| `CLAUDE_CODE_DIAGNOSTICS_FILE` | Diagnostics output path |

### Accessibility

| Variable | Purpose |
|----------|---------|
| `CLAUDE_CODE_ACCESSIBILITY` | Keep native terminal cursor visible |
| `CLAUDE_CODE_SCROLL_SPEED` | Mouse wheel scroll multiplier (1-20) |
| `CLAUDE_CODE_SYNTAX_HIGHLIGHT` | Disable syntax highlighting in diffs |
| `CLAUDE_CODE_DISABLE_MOUSE` | Disable mouse tracking |
| `CLAUDE_CODE_NO_FLICKER` | Fullscreen rendering (research preview) |

### Session & Hooks

| Variable | Purpose |
|----------|---------|
| `CLAUDE_CODE_SESSIONEND_HOOKS_TIMEOUT_MS` | Max time for SessionEnd hooks (default: 1500) |
| `CLAUDE_CODE_EXIT_AFTER_STOP_DELAY` | Auto-exit delay in SDK mode |
| `CLAUDE_CODE_SIMPLE` | Minimal system prompt + basic tools |
| `CLAUDE_CODE_TMPDIR` | Override temp directory |
| `CLAUDE_CONFIG_DIR` | Override config directory (default: ~/.claude) |

---

## Undocumented / Leaked Env Vars

These are NOT in the official docs but were found in the source code. Use at your own risk.

### Authentication (Undocumented)

| Variable | Purpose |
|----------|---------|
| `CLAUDE_CODE_OAUTH_CLIENT_ID` | Custom OAuth client ID |
| `CLAUDE_CODE_OAUTH_TOKEN_FILE_DESCRIPTOR` | File descriptor for OAuth token |
| `CLAUDE_CODE_CUSTOM_OAUTH_URL` | Custom OAuth URL endpoint |
| `CLAUDE_CODE_SESSION_ACCESS_TOKEN` | Session-specific access token |
| `CLAUDE_CODE_WEBSOCKET_AUTH_FILE_DESCRIPTOR` | File descriptor for WebSocket auth |
| `USE_LOCAL_OAUTH` | Use local OAuth endpoint |
| `USE_STAGING_OAUTH` | Use staging OAuth endpoint |
| `CLAUDE_CODE_ACCOUNT_UUID` | Pre-set account UUID |
| `CLAUDE_CODE_USER_EMAIL` | Pre-set user email |
| `CLAUDE_CODE_ORGANIZATION_UUID` | Pre-set organization UUID |
| `CLAUDE_CODE_ACCOUNT_TAGGED_ID` | Tagged account ID for OTEL |
| `ANTHROPIC_UNIX_SOCKET` | Unix socket path for API connections |
| `ANTHROPIC_LOG` | Anthropic SDK internal logging level |

### Model (Undocumented)

| Variable | Purpose |
|----------|---------|
| `CLAUDE_CODE_ALWAYS_ENABLE_EFFORT` | Force effort level on all models |
| `CLAUDE_CODE_EXTRA_BODY` | Extra JSON body params for API requests |
| `CLAUDE_CODE_EXTRA_METADATA` | Extra metadata for API requests |
| `ANTHROPIC_SMALL_FAST_MODEL_AWS_REGION` | AWS region for small fast model on Bedrock |

### Remote / Bridge / CCR

| Variable | Purpose |
|----------|---------|
| `CLAUDE_CODE_REMOTE` | Marks remote/headless runtime |
| `CLAUDE_CODE_REMOTE_SESSION_ID` | Remote session identifier |
| `CLAUDE_CODE_REMOTE_ENVIRONMENT_TYPE` | Type of remote environment |
| `CLAUDE_CODE_REMOTE_MEMORY_DIR` | Remote memory storage directory |
| `CLAUDE_CODE_REMOTE_SEND_KEEPALIVES` | Send keepalive messages in remote |
| `CLAUDE_CODE_CONTAINER_ID` | Container ID for remote sessions |
| `CLAUDE_CODE_USE_CCR_V2` | Switch to CCR v2 transport |
| `CLAUDE_CODE_POST_FOR_SESSION_INGRESS_V2` | POST for session-ingress v2 |
| `CLAUDE_CODE_CCR_MIRROR` | Enable mirror mode |
| `CLAUDE_CODE_WORKER_EPOCH` | Worker epoch for CCR client state |
| `CLAUDE_CODE_ENVIRONMENT_KIND` | Environment runner kind |
| `CLAUDE_CODE_ENVIRONMENT_RUNNER_VERSION` | Runner version reporting |
| `CLAUDE_BRIDGE_OAUTH_TOKEN` | Bridge auth token |
| `CLAUDE_BRIDGE_BASE_URL` | Bridge base URL |
| `CLAUDE_BRIDGE_USE_CCR_V2` | Bridge-side CCR v2 override |
| `CLAUDE_BRIDGE_SESSION_INGRESS_URL` | Custom session ingress URL |
| `CCR_ENABLE_BUNDLE` | Enable code bundle uploads |
| `CCR_FORCE_BUNDLE` | Force code bundle uploads |
| `SESSION_INGRESS_URL` | URL for session ingress |
| `LOCAL_BRIDGE` | Enable local bridge mode |
| `CLAUDE_REPL_MODE` | Enable REPL mode |

### Cowork Mode

| Variable | Purpose |
|----------|---------|
| `CLAUDE_CODE_IS_COWORK` | Indicates cowork mode |
| `CLAUDE_COWORK_MEMORY_PATH_OVERRIDE` | Override cowork memory directory |
| `CLAUDE_COWORK_MEMORY_EXTRA_GUIDELINES` | Extra cowork memory guidelines |
| `CLAUDE_CODE_WORKSPACE_HOST_PATHS` | Host paths for workspace mapping |
| `CLAUDE_CODE_USE_COWORK_PLUGINS` | Enable cowork-mode plugins |

### Agent Teams / Swarm (Undocumented)

| Variable | Purpose |
|----------|---------|
| `CLAUDE_CODE_TEAMMATE_COMMAND` | Override teammate executable |
| `CLAUDE_CODE_AGENT_COLOR` | Color for spawned teammates |
| `CLAUDE_CODE_PLAN_MODE_REQUIRED` | Force plan mode requirement |
| `CLAUDE_CODE_PLAN_MODE_INTERVIEW_PHASE` | Control interview phase |
| `CLAUDE_CODE_PLAN_V2_AGENT_COUNT` | Number of agents in plan v2 |
| `CLAUDE_CODE_PLAN_V2_EXPLORE_AGENT_COUNT` | Number of explore agents in plan v2 |
| `CLAUDE_AUTO_BACKGROUND_TASKS` | Auto background task spawning |
| `TEAM_MEMORY_SYNC_URL` | URL for team memory sync |

### Profiling & Debug (Undocumented)

| Variable | Purpose |
|----------|---------|
| `CLAUDE_CODE_PROFILE_STARTUP` | Enable startup profiling |
| `CLAUDE_CODE_PERFETTO_TRACE` | Enable Perfetto trace collection |
| `CLAUDE_CODE_FRAME_TIMING_LOG` | Path for frame timing logs |
| `CLAUDE_CODE_SLOW_OPERATION_THRESHOLD_MS` | Threshold for slow operation warnings |
| `CLAUDE_CODE_STALL_TIMEOUT_MS_FOR_TESTING` | Stall timeout for testing |
| `CLAUDE_CODE_VCR_RECORD` | VCR recording mode for test replay |
| `CLAUDE_CODE_DEBUG_REPAINTS` | Debug UI repaints |
| `CLAUDE_CODE_EXIT_AFTER_FIRST_RENDER` | Exit after first UI render (debug) |
| `CLAUDE_CODE_SKIP_FAST_MODE_NETWORK_ERRORS` | Skip network errors in fast mode |
| `CLAUDE_DEBUG` | Enable debug mode |

### Tool Configuration (Undocumented)

| Variable | Purpose |
|----------|---------|
| `CLAUDE_CODE_EMIT_TOOL_USE_SUMMARIES` | Emit tool use summaries |
| `CLAUDE_CODE_INCLUDE_PARTIAL_MESSAGES` | Include partial messages |
| `USE_BUILTIN_RIPGREP` | Use built-in ripgrep binary |
| `CLAUDE_CODE_BASH_SANDBOX_SHOW_INDICATOR` | Show 'SandboxedBash' label |
| `ANALYTICS_LOG_TOOL_DETAILS` | Log detailed tool analytics |
| `USE_API_CONTEXT_MANAGEMENT` | Use API-side context management |
| `TASK_MAX_OUTPUT_LENGTH` | Max task result output length |
| `SLASH_COMMAND_TOOL_CHAR_BUDGET` | Char budget for slash command output |
| `EMBEDDED_SEARCH_TOOLS` | Enable embedded search tools |
| `MCP_SERVER_CONNECTION_BATCH_SIZE` | Simultaneous MCP connections |
| `MCP_REMOTE_SERVER_CONNECTION_BATCH_SIZE` | Simultaneous remote MCP connections |
| `CLAUDE_CODE_MCP_INSTR_DELTA` | Delta for MCP instruction processing |

### MCP OAuth (Undocumented)

| Variable | Purpose |
|----------|---------|
| `MCP_CLIENT_SECRET` | Client secret for MCP OAuth |
| `MCP_OAUTH_CALLBACK_PORT` | Port for MCP OAuth callback |
| `MCP_OAUTH_CLIENT_METADATA_URL` | URL for MCP OAuth client metadata |
| `CLAUDE_CODE_MCP_SERVER_NAME` | MCP server name for headersHelper |
| `CLAUDE_CODE_MCP_SERVER_URL` | MCP server URL for headersHelper |

### Context & Compaction (Undocumented)

| Variable | Purpose |
|----------|---------|
| `CLAUDE_CODE_BLOCKING_LIMIT_OVERRIDE` | Override blocking context limit |
| `CLAUDE_AFTER_LAST_COMPACT` | Fetch messages after last compaction |
| `CLAUDE_CODE_DISABLE_PRECOMPACT_SKIP` | Disable pre-compaction optimization |
| `ENABLE_CLAUDE_CODE_SM_COMPACT` | Enable smart compaction |
| `DISABLE_CLAUDE_CODE_SM_COMPACT` | Disable smart compaction |
| `CLAUDE_CODE_MAX_CONTEXT_TOKENS` | Override max context tokens |

### Misc Enable/Disable (Undocumented)

| Variable | Purpose |
|----------|---------|
| `CLAUDE_CODE_ENABLE_CFC` | Enable "Claude For Code" feature |
| `CLAUDE_CODE_ENABLE_FINE_GRAINED_TOOL_STREAMING` | Fine-grained tool streaming |
| `CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING` | SDK file checkpointing |
| `CLAUDE_CODE_ENABLE_TOKEN_USAGE_ATTACHMENT` | Attach token usage info |
| `ENABLE_BETA_TRACING_DETAILED` | Detailed beta tracing |
| `ENABLE_MCP_LARGE_OUTPUT_FILES` | Large output file support for MCP |
| `CLAUDE_CODE_ENHANCED_TELEMETRY_BETA` | Enhanced telemetry beta |
| `CLAUDE_CODE_DISABLE_COMMAND_INJECTION_CHECK` | Disable command injection checks |
| `CLAUDE_CODE_DISABLE_VIRTUAL_SCROLL` | Disable virtual scrolling |
| `DISABLE_AUTO_MIGRATE_TO_NATIVE` | Disable native migration |
| `CLAUDE_CODE_DISABLE_FEEDBACK_SURVEY` | Disable feedback surveys |
| `CLAUDE_CODE_PROACTIVE` | Enable proactive behavior |
| `CLAUDE_CODE_STREAMLINED_OUTPUT` | Alternate streamlined output mode |
| `CLAUDE_CODE_VERIFY_PLAN` | Load VerifyPlanExecutionTool |
| `CLAUDE_CODE_ABLATION_BASELINE` | Bootstrap simplification toggles |
| `CLAUDE_CODE_DONT_INHERIT_ENV` | Don't inherit environment variables |
| `CLAUDE_CODE_SKIP_PROMPT_HISTORY` | Skip saving prompts to history |
| `CLAUDE_CODE_SAVE_HOOK_ADDITIONAL_CONTEXT` | Save extra context during hooks |

### Internal / Anthropic-Only

| Variable | Purpose |
|----------|---------|
| `USER_TYPE` | "ant" for Anthropic employees (unlocks internal features) |
| `CLAUDE_INTERNAL_FC_OVERRIDES` | Override feature flags internally |
| `CLAUDE_MORERIGHT` | Internal layout override |
| `CLAUDE_CODE_UNDERCOVER` | Force undercover mode on |
| `CLAUBBIT` | Bypasses trust dialog |
| `ALLOW_ANT_COMPUTER_USE_MCP` | Bypass computer use gating |
| `IS_DEMO` | Demo mode (affects onboarding) |
| `IS_SANDBOX` | Sandbox security checks |

### Platform Detection (read, not set)

| Variable | Purpose |
|----------|---------|
| `GITHUB_ACTIONS` | Detect GitHub Actions |
| `GITLAB_CI` | Detect GitLab CI |
| `CIRCLECI` | Detect CircleCI |
| `BUILDKITE` | Detect Buildkite |
| `TMUX` | Detect tmux |
| `WT_SESSION` | Detect Windows Terminal |
| `TERM_PROGRAM` | Terminal program name |
| `CODESPACES` | Detect GitHub Codespaces |
| `GITPOD_WORKSPACE_ID` | Detect Gitpod |
| `WSL_DISTRO_NAME` | Detect WSL |

---

## The 32 Compile-Time Build Flags

These are baked into the build via Bun's dead-code elimination. External builds have them
all set to false, so the code paths are literally removed from the shipped binary.

| Flag | Purpose |
|------|---------|
| `KAIROS` | Always-on autonomous agent daemon mode |
| `PROACTIVE` | Proactive behavior (KAIROS prerequisite) |
| `COORDINATOR_MODE` | Multi-agent task orchestration |
| `BRIDGE_MODE` | Remote control mode (now released) |
| `DAEMON` | Background daemon sessions |
| `BG_SESSIONS` | Background session management |
| `ULTRAPLAN` | 30-minute remote planning sessions |
| `BUDDY` | Tamagotchi-style terminal pet (18 species, rarity tiers) |
| `TORCH` | Unknown |
| `WORKFLOW_SCRIPTS` | Workflow scripting |
| `VOICE_MODE` | Push-to-talk voice interface |
| `TEMPLATES` | Template system |
| `CHICAGO_MCP` | Computer use feature ("Chicago" codename) |
| `UDS_INBOX` | Cross-session inter-process communication |
| `REACTIVE_COMPACT` | Reactive context compaction |
| `CONTEXT_COLLAPSE` | Context window inspection tools |
| `HISTORY_SNIP` | History snipping |
| `CACHED_MICROCOMPACT` | Cached micro-compaction |
| `TOKEN_BUDGET` | Token budget management |
| `EXTRACT_MEMORIES` | Memory extraction |
| `OVERFLOW_TEST` | Overflow testing |
| `TERMINAL_PANEL` | Terminal capture capabilities |
| `WEB_BROWSER` | Built-in web browser tool |
| `FORK_SUBAGENT` | Fork-based subagent spawning |
| `DUMP_SYS_PROMPT` | System prompt dumping (internal debug) |
| `ABLATION_BASE` | Ablation testing baseline |
| `BYOC_RUNNER` | Bring-your-own-compute runner |
| `SELF_HOSTED` | Self-hosted deployment mode |
| `MONITOR_TOOL` | Monitoring tool |
| `CCR_AUTO` | Automatic CCR mode |
| `MEM_SHAPE_TEL` | Memory shape telemetry |
| `SKILL_SEARCH` | Skill search system |
| `ANTI_DISTILLATION_CC` | Anti-distillation fake tool injection |

---

## GrowthBook Runtime Feature Flags (tengu_*)

"Tengu" is Claude Code's internal project codename. These flags are controlled remotely
by Anthropic via the GrowthBook SDK. They can be toggled per-user, per-region, or by
percentage without pushing a new release.

| Flag | Controls |
|------|----------|
| `tengu_anti_distill_fake_tool_injection` | Server-side decoy tool injection |
| `tengu_attribution_header` | Killswitch for attribution/attestation header |
| `tengu_penguins_off` | Kill-switch for Fast Mode ("Penguin Mode") |
| `tengu_amber_quartz_disabled` | Emergency off-switch for voice mode |
| `tengu_amber_flint` | Agent Teams/Swarm feature gate |
| `tengu_malort_pedway` | Computer use / full GUI automation |
| `tengu_onyx_plover` | Auto-dream memory consolidation |
| `tengu_kairos` | KAIROS assistant mode gate |
| `tengu_ultraplan_model` | Remote 30-minute planning session model |
| `tengu_cobalt_raccoon` | Auto-compact behavior |
| `tengu_portal_quail` | Memory extraction |
| `tengu_harbor` | MCP allowlist |
| `tengu_scratch` | Worker scratch dirs / shared scratchpad |
| `tengu_herring_clock` | Team memory |
| `tengu_chomp_inflection` | Prompt suggestions |
| `tengu_turtle_carbon` | UltraThink |
| `tengu_pewter_ledger` | Plan file A/B test |
| `tengu_marble_sandcastle` | Fast mode binary requirement |
| `tengu_speculation` | Speculative execution / prompt suggestion |

---

## USER_TYPE=ant -- Anthropic Employee Mode

Setting `USER_TYPE=ant` identifies the session as an Anthropic employee. This unlocks:

1. **Staging API access** -- connects to `claude-ai.staging.ant.dev` instead of production
2. **Internal beta headers** -- additional API capabilities not available externally
3. **Undercover Mode** -- auto-activates in public repos; strips all Anthropic internals,
   codenames, and AI attribution. System prompt: "Do not blow your cover"
4. **ConfigTool** -- internal configuration tool
5. **TungstenTool** -- internal debugging/testing tool
6. **/security-review command** -- internal security review workflow
7. **Debug prompt dumping** -- dump full system prompts for debugging
8. **CLAUDE_INTERNAL_FC_OVERRIDES** -- override any feature flag value
9. **ALLOW_ANT_COMPUTER_USE_MCP** -- bypass computer use gating

The undercover mode is a "one-way door" -- external users cannot enable it, and it is
dead-code-eliminated from external builds. When active, it prevents mentioning codenames
like "Capybara," "Tengu," "Fennec," or "Numbat" in git commits.

Note: Setting USER_TYPE=ant on an external build will NOT grant these features because
the code paths are compiled out. It may trigger unexpected behavior.

---

## GrowthBook SDK Integration

### How It Works

1. **Initialization**: During Claude Code boot, GrowthBook is initialized asynchronously
   as part of the feature flag evaluation step
2. **Aggressive caching**: Feature flag values are cached aggressively to minimize
   network calls
3. **Dead-code interaction**: Compile-time flags use `bun:bundle` for dead-code
   elimination. Runtime flags (tengu_*) are evaluated by GrowthBook at runtime
4. **Remote control**: Anthropic can flip flags on/off for specific users, regions,
   or percentage rollouts without pushing a new npm version
5. **Integration point**: `services/analytics/growthbook.ts` is the main integration file
6. **Telemetry events**: `types/generated/events_mono/growthbook/v1/growthbook_experiment_event.ts`
   tracks experiment exposure events

### Why This Matters

GrowthBook is how Anthropic rolls out new features gradually. If you disable non-essential
traffic (CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC), GrowthBook cannot fetch flags, and
ALL gated features silently disappear. See section 8.

---

## CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC -- Why It Breaks Things

### What It Does

When set to `1` or `true`, it is equivalent to simultaneously setting:
- `DISABLE_AUTOUPDATER=1`
- `DISABLE_FEEDBACK_SURVEY=1`
- `DISABLE_ERROR_REPORTING=1`
- `DISABLE_TELEMETRY=1`

### The Hidden Problem

Feature entitlements and telemetry opt-out share the same kill switch. When this variable
is set, GrowthBook feature flag evaluation is ALSO disabled. This means:

1. **Channels breaks** -- `--channels` flag is silently ignored with the message
   "(Channels are not currently available)"
2. **Remote control breaks** -- `/remote-control`, `/rc` commands disappear
3. **Premium model access breaks** -- Users on Max/Team/Enterprise plans lose Opus 4.6
   1M context as their default model
4. **All gated features disappear** -- Any feature behind a GrowthBook flag becomes
   invisible with no error message
5. **/btw and other gated commands** -- silently hidden

### The Root Cause

Telemetry opt-out and feature flag evaluation are coupled in the code. Disabling telemetry
for privacy also disables the mechanism that checks what features your account is entitled to.

### Workaround

Remove the variable entirely. If you need to minimize traffic but keep features working,
use the more granular options instead:
```
DISABLE_AUTOUPDATER=1
DISABLE_ERROR_REPORTING=1
DISABLE_FEEDBACK_SURVEY=1
# Do NOT set DISABLE_TELEMETRY if you want feature flags to work
```

### Relevant GitHub Issues
- anthropics/claude-code#38450 -- "Telemetry opt-out should not disable Channels feature flag"
- anthropics/claude-code#34178 -- "DISABLE_TELEMETRY silently disables Opus 4.6 1M model"
- anthropics/claude-code#32955 -- "Claude.ai MCP servers not loading"

---

## Env Vars We Care About

### Hooks

| Variable | Purpose | Notes |
|----------|---------|-------|
| `CLAUDE_CODE_SESSIONEND_HOOKS_TIMEOUT_MS` | Max time for SessionEnd hooks | Default: 1500ms. Increase to 30000 for heavy hooks |
| `CLAUDE_CODE_SAVE_HOOK_ADDITIONAL_CONTEXT` | Save extra context during hooks | Undocumented |
| `CLAUDE_ENV_FILE` | Script sourced before each bash command | Good for `.envrc` loading |

### MCP

| Variable | Purpose | Notes |
|----------|---------|-------|
| `MCP_TIMEOUT` | MCP server connection timeout | Important for slow MCP servers |
| `MCP_TOOL_TIMEOUT` | MCP tool execution timeout | Per-tool timeout |
| `MAX_MCP_OUTPUT_TOKENS` | Max output from MCP tools | Prevent oversized responses |
| `MCP_SERVER_CONNECTION_BATCH_SIZE` | Simultaneous MCP connections | Tune for performance |
| `ENABLE_TOOL_SEARCH` | Deferred tool loading | Reduces prompt bloat |
| `ENABLE_CLAUDEAI_MCP_SERVERS` | Control claude.ai MCP servers | Set false to disable |
| `CLAUDE_AGENT_SDK_MCP_NO_PREFIX` | Skip mcp__server__ prefix | Cleaner tool names |
| `CLAUDE_CODE_MCP_SERVER_NAME` | Server name for headersHelper | Multi-server helpers |
| `CLAUDE_CODE_MCP_SERVER_URL` | Server URL for headersHelper | Multi-server helpers |

### Plugins

| Variable | Purpose | Notes |
|----------|---------|-------|
| `CLAUDE_CODE_PLUGIN_SEED_DIR` | Read-only seed plugins | Pre-load plugins |
| `CLAUDE_CODE_SYNC_PLUGIN_INSTALL` | Sync plugin install | Wait for install before query |
| `CLAUDE_CODE_DISABLE_OFFICIAL_MARKETPLACE_AUTOINSTALL` | Skip marketplace | Reduce startup time |

### Permissions

| Variable | Purpose | Notes |
|----------|---------|-------|
| `CLAUDE_CODE_SIMPLE` | Minimal system prompt + basic tools | Simplified mode |
| `CLAUDE_CODE_DISABLE_COMMAND_INJECTION_CHECK` | Disable injection checks | DANGEROUS |
| `CLAUDE_CODE_SUBPROCESS_ENV_SCRUB` | Strip credentials from subprocesses | Security hardening |
| `CLAUDE_CODE_BUBBLEWRAP` | Running inside Bubblewrap sandbox | Security context |
| `CLAUDE_CODE_ADDITIONAL_PROTECTION` | Additional protection header | Security |

### Context & Compaction

| Variable | Purpose | Notes |
|----------|---------|-------|
| `CLAUDE_AUTOCOMPACT_PCT_OVERRIDE` | Trigger compaction at N% | Default ~80-90%. Set 80 for earlier |
| `CLAUDE_CODE_AUTO_COMPACT_WINDOW` | Context capacity in tokens | Override compaction window |
| `DISABLE_AUTO_COMPACT` | Disable auto-compaction | Manual control only |
| `CLAUDE_CODE_DISABLE_1M_CONTEXT` | Force 200K context | Healthcare compliance use case |
| `CLAUDE_CODE_MAX_CONTEXT_TOKENS` | Override max context tokens | Undocumented |
| `CLAUDE_CODE_BLOCKING_LIMIT_OVERRIDE` | Override blocking limit | Undocumented |
| `ENABLE_CLAUDE_CODE_SM_COMPACT` | Smart compaction | Undocumented |

### Model & Behavior

| Variable | Purpose | Notes |
|----------|---------|-------|
| `ANTHROPIC_MODEL` | Override model | Use specific model ID |
| `CLAUDE_CODE_SUBAGENT_MODEL` | Subagent model | Cheaper model for workers |
| `CLAUDE_CODE_MAX_OUTPUT_TOKENS` | Max output tokens | 64000 doubles default |
| `MAX_THINKING_TOKENS` | Thinking budget | Control reasoning depth |
| `CLAUDE_CODE_EFFORT_LEVEL` | Effort level | low/medium/high/max/auto |
| `CLAUDE_CODE_DISABLE_THINKING` | Kill thinking entirely | |
| `CLAUDE_CODE_DISABLE_ADAPTIVE_THINKING` | Disable adaptive thinking | |

---

## Env Vars That Suppress Warnings or Change Defaults

| Variable | What It Suppresses/Changes |
|----------|---------------------------|
| `DISABLE_COST_WARNINGS` | Suppresses cost warning notifications |
| `DISABLE_INSTALLATION_CHECKS` | Suppresses installation verification warnings |
| `DISABLE_AUTOUPDATER` | Suppresses update prompts |
| `DISABLE_FEEDBACK_SURVEY` | Suppresses session quality surveys |
| `CLAUDE_CODE_DISABLE_FEEDBACK_SURVEY` | Same as above (alternate name) |
| `CLAUDE_CODE_DISABLE_TERMINAL_TITLE` | Stops Claude from setting terminal title |
| `CLAUDE_CODE_DISABLE_GIT_INSTRUCTIONS` | Removes git instructions from system prompt |
| `CLAUDE_CODE_DISABLE_LEGACY_MODEL_REMAP` | Stops remapping Opus 4.0/4.1 to newer |
| `CLAUDE_CODE_DISABLE_EXPERIMENTAL_BETAS` | Strips beta API headers |
| `DISABLE_PROMPT_CACHING` | Disables prompt caching (increases cost) |
| `CLAUDE_CODE_DISABLE_OFFICIAL_MARKETPLACE_AUTOINSTALL` | Stops marketplace auto-install |
| `CLAUDE_CODE_IDE_SKIP_AUTO_INSTALL` | Stops IDE extension auto-install |
| `CLAUDE_CODE_DISABLE_CLAUDE_MDS` | Prevents loading CLAUDE.md files |
| `CLAUDE_CODE_DONT_INHERIT_ENV` | Don't inherit parent env vars |
| `CLAUDE_CODE_SKIP_PROMPT_HISTORY` | Don't save prompts to history |

---

## Internal Codenames

| Codename | Meaning |
|----------|---------|
| **Tengu** | Claude Code project codename (feature flag prefix) |
| **Capybara** | Claude 4.6 variant (internal model codename) |
| **Fennec** | Opus 4.6 (internal model codename) |
| **Numbat** | Unreleased model (still in testing) |
| **Chicago** | Computer use feature codename |
| **Penguin Mode** | Fast Mode (internal name) |

---

## Anti-Distillation Mechanisms

Claude Code includes two anti-distillation systems to prevent competitors from training
on recorded API traffic:

### 1. Fake Tool Injection
When enabled (requires ALL four conditions):
- `ANTI_DISTILLATION_CC` compile-time flag = true
- CLI entrypoint (not SDK)
- First-party API provider (not Bedrock/Vertex)
- `tengu_anti_distill_fake_tool_injection` GrowthBook flag = true

The API request includes `anti_distillation: ['fake_tools']`, which tells the server to
silently inject decoy tool definitions into the system prompt. Anyone recording the traffic
gets poisoned training data.

### 2. Connector-Text Summarization
Anthropic-internal only (USER_TYPE=ant). Uses server-side summarization with cryptographic
signatures so adversaries only capture summaries, not full reasoning chains.

---

## Sources

- [ccleaks -- Claude Code Hidden Features](https://www.ccleaks.com/)
- [ccleaks -- Architecture](https://www.ccleaks.com/architecture)
- [Alex Kim -- The Claude Code Source Leak](https://alex000kim.com/posts/2026-03-31-claude-code-source-leak/)
- [jedisct1 -- Claude Code environment variables full list (GitHub Gist)](https://gist.github.com/jedisct1/9627644cda1c3929affe9b1ce8eaf714)
- [unkn0wncode -- Claude Code CLI Environment Variables (GitHub Gist)](https://gist.github.com/unkn0wncode/f87295d055dd0f0e8082358a0b5cc467)
- [Kuberwastaken -- Claude Code source analysis (GitHub)](https://github.com/Kuberwastaken/claude-code)
- [Official Claude Code env vars docs](https://code.claude.com/docs/en/env-vars)
- [GitHub Issue #38450 -- Telemetry opt-out disabling Channels](https://github.com/anthropics/claude-code/issues/38450)
- [GitHub Issue #34178 -- DISABLE_TELEMETRY disabling Opus 4.6 1M](https://github.com/anthropics/claude-code/issues/34178)
- [VentureBeat -- Claude Code leak coverage](https://venturebeat.com/technology/claude-codes-source-code-appears-to-have-leaked-heres-what-we-know)
- [CyberSecurityNews -- Claude Code leak coverage](https://cybersecuritynews.com/claude-code-source-code-leaked/)
- [Matt Paige -- What Anthropic Was Hiding](https://mattpaige68.substack.com/p/claude-codes-entire-source-code-just)
- [DEV.to -- 512,000 Lines Analysis](https://dev.to/vibehackers/i-analyzed-all-512000-lines-of-claude-codes-leaked-source-heres-what-anthropic-was-hiding-4gg8)
