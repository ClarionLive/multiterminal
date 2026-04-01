using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Manages the Oracle advisory agent — an always-on, project-agnostic Claude Code
    /// terminal that answers questions, evaluates ideas, creates tasks, processes the
    /// daily digest, and routes work to other agents. Oracle never codes.
    ///
    /// Lifecycle: starts with MultiTerminal, auto-restarts on crash with exponential
    /// backoff (max 10 attempts), only stops when MultiTerminal closes. Runs in a hidden
    /// OracleTerminalForm that the user can toggle visible by clicking Oracle in the
    /// dashboard header.
    ///
    /// Threading: all public methods must be called from the UI thread.
    /// </summary>
    public class OracleService : IDisposable
    {
        public const string OracleName = "Oracle";
        private const int MaxRestartDelayMs = 30_000;
        private const int MaxRestartAttempts = 10;

        private OracleTerminal.OracleTerminalForm _form;
        private readonly Action<string, string> _log;
        private readonly DebugLogService _debugLogService;
        private string _systemPromptFile;
        private bool _isDisposed;
        private bool _isShuttingDown;
        private int _restartAttempt;
        private Timer _restartTimer;
        private bool _wasVisibleBeforeCrash;
        private Form _owner;
        private string _registeredDocId;

        /// <summary>Fired after Oracle's terminal process starts successfully.</summary>
        public event EventHandler OracleStarted;

        /// <summary>Fired when Oracle's terminal process exits (before auto-restart).</summary>
        public event EventHandler OracleStopped;

        /// <summary>Whether Oracle's terminal is currently running.</summary>
        public bool IsRunning => _form != null && !_form.IsDisposed && _form.IsTerminalRunning;

        /// <summary>The docId used for Oracle's terminal registration and ConPTY.</summary>
        public string DocId => _registeredDocId;

        public OracleService(Action<string, string> log = null, DebugLogService debugLogService = null)
        {
            _log = log ?? ((source, msg) => Debug.WriteLine($"[{source}] {msg}"));
            _debugLogService = debugLogService;
        }

        /// <summary>
        /// Start Oracle. Creates the hidden popup form and launches the Claude Code terminal.
        /// Call from MainForm after MessageBroker is ready. The docId is generated once and
        /// reused across restarts to keep the broker registration in sync.
        /// </summary>
        public void Start(Form owner)
        {
            if (_isShuttingDown || _isDisposed) return;
            if (_form != null && !_form.IsDisposed) return;

            _owner = owner;

            // Generate docId once on first start; reuse on crash restarts
            if (_registeredDocId == null)
                _registeredDocId = $"oracle-{Guid.NewGuid().ToString("N").Substring(0, 8)}";

            try
            {
                _form = new OracleTerminal.OracleTerminalForm(_debugLogService);
                _form.Owner = owner;
                _form.TerminalExited += OnTerminalExited;

                string autoRunCommand = BuildAutoRunCommand();
                string workingDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                _form.StartTerminal(workingDir, _registeredDocId, autoRunCommand);

                _restartAttempt = 0;
                _log("Oracle", "Oracle terminal started (always-on)");
                OracleStarted?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _log("Oracle", $"Failed to start Oracle: {ex.Message}");

                // Schedule retry so always-on guarantee is maintained
                if (!_isShuttingDown && !_isDisposed)
                    ScheduleRestart();
            }
        }

        /// <summary>
        /// Toggle the Oracle popup terminal visibility.
        /// </summary>
        public void TogglePopup()
        {
            var form = _form;
            if (form == null || form.IsDisposed) return;

            if (form.Visible)
                form.Hide();
            else
                form.Show();
        }

        /// <summary>
        /// Show the Oracle popup terminal.
        /// </summary>
        public void ShowPopup()
        {
            var form = _form;
            if (form == null || form.IsDisposed) return;
            form.Show();
            form.BringToFront();
        }

        /// <summary>
        /// Shut down Oracle permanently (app closing). Prevents auto-restart.
        /// </summary>
        public void Shutdown()
        {
            _isShuttingDown = true;

            _restartTimer?.Stop();
            _restartTimer?.Dispose();
            _restartTimer = null;

            var form = _form;
            if (form != null && !form.IsDisposed)
            {
                form.TerminalExited -= OnTerminalExited;
                form.ForceClose();
            }
            _form = null;

            _log("Oracle", "Oracle shut down");
        }

        private void OnTerminalExited(object sender, EventArgs e)
        {
            _log("Oracle", "Oracle terminal process exited");
            OracleStopped?.Invoke(this, EventArgs.Empty);

            if (_isShuttingDown || _isDisposed) return;

            // Remember visibility state so we can restore after restart (safe: called on UI thread via BeginInvoke)
            _wasVisibleBeforeCrash = _form?.Visible ?? false;

            ScheduleRestart();
        }

        private void ScheduleRestart()
        {
            if (_isShuttingDown || _isDisposed) return;

            // Circuit breaker: stop retrying after MaxRestartAttempts
            if (_restartAttempt >= MaxRestartAttempts)
            {
                _log("Oracle", $"Oracle failed to restart after {MaxRestartAttempts} attempts — giving up. Restart MultiTerminal to try again.");
                return;
            }

            // Exponential backoff: 1s, 2s, 4s, 8s, 16s, 30s, 30s...
            int delayMs = Math.Min((1 << _restartAttempt) * 1000, MaxRestartDelayMs);
            _restartAttempt++;

            _log("Oracle", $"Auto-restarting in {delayMs / 1000}s (attempt {_restartAttempt}/{MaxRestartAttempts})");

            // Use WinForms Timer so the callback runs on the UI thread
            _restartTimer?.Dispose();
            _restartTimer = new Timer { Interval = delayMs };
            _restartTimer.Tick += OnRestartTimerTick;
            _restartTimer.Start();
        }

        private void OnRestartTimerTick(object sender, EventArgs e)
        {
            _restartTimer?.Stop();
            _restartTimer?.Dispose();
            _restartTimer = null;

            if (_isShuttingDown || _isDisposed) return;

            _log("Oracle", "Restarting Oracle terminal...");

            // Dispose old form
            var oldForm = _form;
            if (oldForm != null && !oldForm.IsDisposed)
            {
                oldForm.TerminalExited -= OnTerminalExited;
                oldForm.ForceClose();
            }
            _form = null;

            // Start fresh using stored owner reference (not from disposed form)
            Start(_owner);

            // Restore visibility if the popup was showing before the crash
            if (_wasVisibleBeforeCrash)
                ShowPopup();
        }

        /// <summary>
        /// Build the Claude CLI autorun command for Oracle's ConPTY terminal.
        /// No --plugin-dir: Oracle uses its own system prompt, not the MT project plugin.
        /// </summary>
        public string BuildAutoRunCommand()
        {
            EnsureSystemPromptFile();

            string mcpConfigPath = LaunchCommandBuilder.GetMcpConfigPath();

            var cmd = "claude";

            if (!string.IsNullOrEmpty(mcpConfigPath))
            {
                string safePath = mcpConfigPath.Replace("'", "''");
                cmd += $" --mcp-config '{safePath}'";
            }

            string pluginDir = LaunchCommandBuilder.GetMtPluginPath();
            if (pluginDir != null)
            {
                string safePluginDir = pluginDir.Replace("'", "''");
                cmd += $" --plugin-dir '{safePluginDir}'";
                if (Directory.Exists(Path.Combine(pluginDir, "server")))
                {
                    string pName = Path.GetFileName(pluginDir);
                    cmd += $" --dangerously-load-development-channels plugin:{pName}@inline";
                }
            }
            cmd += " --dangerously-skip-permissions";
            cmd += " --disallowedTools Edit,Write,Read,Glob,Grep,Bash,NotebookEdit,PowerShell";

            string safePromptPath = _systemPromptFile.Replace("'", "''");
            cmd += $" --system-prompt-file '{safePromptPath}'";

            // ; exit ensures PowerShell exits when Claude exits, triggering ProcessExited
            cmd += "; exit";

            return cmd;
        }

        /// <summary>
        /// Apply theme to Oracle's popup form if it exists.
        /// </summary>
        public void ApplyTheme(bool isDark)
        {
            var form = _form;
            if (form != null && !form.IsDisposed)
                form.ApplyTheme(isDark);
        }

        /// <summary>
        /// Writes the Oracle system prompt to a file for --system-prompt-file.
        /// Always overwrites to ensure the canonical prompt is used (prevents
        /// tampered files from persisting across restarts).
        /// </summary>
        private void EnsureSystemPromptFile()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(appData, "multiterminal");
            Directory.CreateDirectory(dir);
            _systemPromptFile = Path.Combine(dir, "oracle-system-prompt.md");
            File.WriteAllText(_systemPromptFile, SystemPrompt);
        }

        public static readonly string SystemPrompt = @"
You are Oracle, the always-on advisory agent for MultiTerminal.

## Your Role
- Answer questions on any topic — you are not tied to any project
- Evaluate ideas and give honest, constructive feedback
- Create kanban tasks with plans and checklists for work that needs doing
- Route work to other agents by sending them messages about new tasks
- Query the knowledge base for institutional context
- Process the daily news digest and create suggestion tasks for actionable items

## What You Do NOT Do
- You NEVER write, edit, or read files
- You NEVER write code
- You NEVER claim tasks for yourself
- You NEVER work on tasks directly — you create them and assign to others

## Daily Digest Processing
When you receive a bootstrap message about the daily digest:
1. Call get_daily_digest(section=""action_items"") to fetch today's action items
2. Call list_tasks(status=""suggestion"") to check existing suggestions (avoid duplicates)
3. For each genuinely actionable item relevant to MultiTerminal:
   - Create a suggestion task with title prefixed ""[Digest]""
   - Add continuation notes with: Source, What, Why it matters, Suggested action
4. Be selective — only 1-4 suggestions per digest, skip ""be aware of"" items
5. After processing, send a brief summary to the Owner via send_message

## How You Work
1. When someone asks a question, answer it directly and concisely
2. When someone proposes an idea, evaluate it — strengths, weaknesses, feasibility
3. When work needs doing, create a kanban task with:
   - Clear title and description
   - An implementation plan (markdown)
   - A checklist of discrete items
   - Then message an appropriate active agent to pick it up
4. When you need context about the system, use query_knowledge or list_tasks

## Communication
- Use send_message to notify agents about tasks you've created
- Use list_terminals to see who's online before messaging
- Keep responses concise and actionable
- When creating tasks, always set createdBy to ""Oracle""

## Your Personality
- Direct and efficient — no filler
- Honest about trade-offs and risks
- You suggest the simplest approach that works
- You ask clarifying questions when requirements are ambiguous
".Trim();

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            Shutdown();
        }
    }
}
