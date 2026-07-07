using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using MultiTerminal.Services;

namespace MultiTerminal.Terminal
{
    /// <summary>
    /// Manages a Windows Pseudo Console (ConPTY) for true terminal emulation.
    /// Requires Windows 10 version 1809 or later.
    /// </summary>
    public class ConPtyTerminal : IDisposable
    {
        private IntPtr _pseudoConsoleHandle;
        private IntPtr _processHandle;
        private IntPtr _threadHandle;
        private IntPtr _attributeList;
        private IntPtr _jobHandle;

        private SafeFileHandle _inputReadSide;
        private SafeFileHandle _inputWriteSide;
        private SafeFileHandle _outputReadSide;
        private SafeFileHandle _outputWriteSide;

        private FileStream _inputWriter;
        private FileStream _outputReader;
        private Thread _readThread;
        private Thread _processWaitThread;

        private bool _isDisposed;
        private bool _isRunning;

        // Output throttling to prevent UI thread overload with high-output terminals
        private readonly ConcurrentQueue<byte[]> _outputQueue = new ConcurrentQueue<byte[]>();
        private Timer _outputTimer;
        private const int OUTPUT_INTERVAL_MS = 16; // ~60 fps max update rate

        // Buffer for incomplete UTF-8 multi-byte sequences split across reads
        private byte[] _utf8Remainder = Array.Empty<byte>();

        private int _cols;
        private int _rows;

        /// <summary>
        /// Debug log sink, wired by the owning TerminalControl. Null until wired (e.g. in
        /// tests or before the control propagates it), so all call sites use the null-conditional.
        /// </summary>
        public DebugLogService DebugLogService { get; set; }

        /// <summary>
        /// Fired when output is received from the terminal (VT sequences).
        /// </summary>
        public event Action<byte[]> DataReceived;

        /// <summary>
        /// Fired when the shell process exits.
        /// </summary>
        public event EventHandler ProcessExited;

        /// <summary>
        /// Gets whether the terminal is currently running.
        /// </summary>
        public bool IsRunning => _isRunning && !_isDisposed;

        /// <summary>
        /// Gets the current column count.
        /// </summary>
        public int Columns => _cols;

        /// <summary>
        /// Gets the current row count.
        /// </summary>
        public int Rows => _rows;

        /// <summary>
        /// Starts the pseudo console with the specified shell.
        /// </summary>
        /// <param name="cols">Number of columns</param>
        /// <param name="rows">Number of rows</param>
        /// <param name="shellPath">Path to shell executable (default: powershell.exe)</param>
        /// <param name="workingDirectory">Initial working directory</param>
        /// <param name="docId">Document ID for MCP push notifications (sets MULTITERMINAL_DOC_ID env var)</param>
        /// <param name="terminalName">Pre-registered terminal name (sets MULTITERMINAL_NAME env var)</param>
        /// <param name="autoRunCommand">Command to run automatically after shell starts (e.g., "claude -r session_id")</param>
        /// <param name="spawnerName">Name of the terminal that spawned this one (sets MULTITERMINAL_SPAWNER env var)</param>
        /// <param name="projectId">Project ID for context injection (sets MULTITERMINAL_PROJECT_ID env var)</param>
        /// <param name="isTeamLead">Whether this terminal is a team lead (sets MULTITERMINAL_TEAM_LEAD env var)</param>
        /// <param name="gatewayProfile">MCP Gateway profile name (sets MCP_GATEWAY_PROFILE env var)</param>
        /// <param name="taskWorktreePath">Per-task worktree path resolved from the active task (sets MULTITERMINAL_TASK_WORKTREE env var). Empty when no task worktree is in play.</param>
        public void Start(int cols, int rows, string shellPath = null, string workingDirectory = null, string docId = null, string terminalName = null, string autoRunCommand = null, string spawnerName = null, string projectId = null, bool isTeamLead = false, string gatewayProfile = null, string taskWorktreePath = null)
        {
            if (_isRunning)
                throw new InvalidOperationException("Terminal is already running");

            _cols = cols;
            _rows = rows;

            if (string.IsNullOrEmpty(shellPath))
            {
                shellPath = FindPowerShell();
            }

            if (string.IsNullOrEmpty(workingDirectory))
            {
                workingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            try
            {
                // Note: UTF-8 encoding is set inside the child PowerShell process via -Command string
                // (see envSetup below). We do NOT call SetConsoleCP/SetConsoleOutputCP here because
                // those APIs mutate the parent process's console state, which can corrupt
                // the console subsystem when multiple ConPTY instances are created/destroyed.

                // Create pipes for I/O
                CreatePipes();

                // Create the pseudo console
                CreatePseudoConsole(cols, rows);

                // Start the shell process
                StartProcess(shellPath, workingDirectory, docId, terminalName, autoRunCommand, spawnerName, projectId, isTeamLead, gatewayProfile, taskWorktreePath);

                // Close the pipe ends that belong to the pseudo console.
                // CreatePseudoConsole() duplicates these handles internally, so our
                // originals are no longer needed.  Keeping them open is the root cause
                // of the "stuck terminal" bug: when ClosePseudoConsole() runs later,
                // it releases ConPTY's copies, but if _outputWriteSide is still open
                // there is still a writer on the pipe and _outputReader.Read() blocks
                // forever instead of returning 0 (EOF).
                _inputReadSide?.Dispose();
                _inputReadSide = null;
                _outputWriteSide?.Dispose();
                _outputWriteSide = null;

                // Set running flag BEFORE starting timer/reader (prevents race condition)
                _isRunning = true;

                // Start output flush timer (throttles events to ~60fps max)
                _outputTimer = new Timer(FlushOutputQueue, null, 0, OUTPUT_INTERVAL_MS);

                // Start reading output
                StartReading();

                // Monitor the process for exit — when the process ends, close the
                // pseudoconsole so the output pipe breaks and ReadLoop can detect EOF.
                // ConPTY keeps the pipe open even after the attached process exits,
                // so without this the ReadLoop blocks forever on Read().
                StartProcessWait();
            }
            catch
            {
                Cleanup();
                throw;
            }
        }

        /// <summary>
        /// Writes data to the terminal input.
        /// </summary>
        public void Write(byte[] data)
        {
            if (!_isRunning || _inputWriter == null)
                return;

            try
            {
                _inputWriter.Write(data, 0, data.Length);
                _inputWriter.Flush();
            }
            catch (IOException)
            {
                // Pipe broken - process likely exited
            }
        }

        /// <summary>
        /// Writes a string to the terminal input.
        /// </summary>
        public void Write(string text)
        {
            Write(Encoding.UTF8.GetBytes(text));
        }

        /// <summary>
        /// Sends a single key to the terminal.
        /// </summary>
        public void SendKey(char key)
        {
            Write(Encoding.UTF8.GetBytes(new char[] { key }));
        }

        /// <summary>
        /// Sends Ctrl+C to interrupt the current process.
        /// </summary>
        public void SendCtrlC()
        {
            Write(new byte[] { 0x03 }); // ETX - Ctrl+C
        }

        /// <summary>
        /// Resizes the terminal.
        /// </summary>
        public void Resize(int cols, int rows)
        {
            if (!_isRunning || _pseudoConsoleHandle == IntPtr.Zero)
                return;

            _cols = cols;
            _rows = rows;

            var size = new NativeMethods.COORD((short)cols, (short)rows);
            NativeMethods.ResizePseudoConsole(_pseudoConsoleHandle, size);
        }

        private string FindPowerShell()
        {
            // Check for PowerShell 7+ (pwsh) first
            string[] pwshPaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7-preview", "pwsh.exe"),
            };

            foreach (string path in pwshPaths)
            {
                if (File.Exists(path)) return path;
            }

            // Check PATH for pwsh
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in pathEnv.Split(';'))
            {
                if (!string.IsNullOrEmpty(dir))
                {
                    string pwshPath = Path.Combine(dir.Trim(), "pwsh.exe");
                    if (File.Exists(pwshPath)) return pwshPath;
                }
            }

            // Fall back to Windows PowerShell
            return "powershell.exe";
        }

        private void CreatePipes()
        {
            var security = new NativeMethods.SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf(typeof(NativeMethods.SECURITY_ATTRIBUTES)),
                bInheritHandle = true
            };

            // Create input pipe (we write, console reads)
            if (!NativeMethods.CreatePipe(out _inputReadSide, out _inputWriteSide, ref security, 0))
                throw new InvalidOperationException("Failed to create input pipe: " + Marshal.GetLastWin32Error());

            // Create output pipe (console writes, we read)
            if (!NativeMethods.CreatePipe(out _outputReadSide, out _outputWriteSide, ref security, 0))
                throw new InvalidOperationException("Failed to create output pipe: " + Marshal.GetLastWin32Error());

            // Don't inherit our ends of the pipes
            NativeMethods.SetHandleInformation(_inputWriteSide, NativeMethods.HANDLE_FLAG_INHERIT, 0);
            NativeMethods.SetHandleInformation(_outputReadSide, NativeMethods.HANDLE_FLAG_INHERIT, 0);
        }

        private void CreatePseudoConsole(int cols, int rows)
        {
            var size = new NativeMethods.COORD((short)cols, (short)rows);

            int result = NativeMethods.CreatePseudoConsole(
                size,
                _inputReadSide,
                _outputWriteSide,
                0,
                out _pseudoConsoleHandle);

            if (result != 0)
                throw new InvalidOperationException("Failed to create pseudo console. Error: " + result +
                    ". Make sure you're running Windows 10 version 1809 or later.");
        }

        private void StartProcess(string shellPath, string workingDirectory, string docId, string terminalName = null, string autoRunCommand = null, string spawnerName = null, string projectId = null, bool isTeamLead = false, string gatewayProfile = null, string taskWorktreePath = null)
        {
            DebugLogService?.Info("ConPtyTerminal", $"StartProcess ===== START =====");
            DebugLogService?.Trace("ConPtyTerminal", $"shellPath: '{shellPath}'");
            DebugLogService?.Trace("ConPtyTerminal", $"workingDirectory: '{workingDirectory}'");
            DebugLogService?.Trace("ConPtyTerminal", $"docId: '{docId ?? "null"}'");
            DebugLogService?.Trace("ConPtyTerminal", $"terminalName: '{terminalName ?? "null"}'");
            DebugLogService?.Trace("ConPtyTerminal", $"autoRunCommand: '{autoRunCommand ?? "null"}'");

            // Initialize attribute list
            IntPtr attrListSize = IntPtr.Zero;
            NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrListSize);

            _attributeList = Marshal.AllocHGlobal(attrListSize);

            if (!NativeMethods.InitializeProcThreadAttributeList(_attributeList, 1, 0, ref attrListSize))
                throw new InvalidOperationException("Failed to initialize attribute list: " + Marshal.GetLastWin32Error());

            // Set pseudo console attribute
            if (!NativeMethods.UpdateProcThreadAttribute(
                _attributeList,
                0,
                NativeMethods.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                _pseudoConsoleHandle,
                (IntPtr)IntPtr.Size,
                IntPtr.Zero,
                IntPtr.Zero))
            {
                throw new InvalidOperationException("Failed to update attribute list: " + Marshal.GetLastWin32Error());
            }

            // Create startup info
            var startupInfo = new NativeMethods.STARTUPINFOEX
            {
                StartupInfo = new NativeMethods.STARTUPINFO
                {
                    cb = Marshal.SizeOf(typeof(NativeMethods.STARTUPINFOEX))
                },
                lpAttributeList = _attributeList
            };

            // Build command line with execution policy bypass
            // Include prompt function that sets window title to current directory (for recent folders tracking)
            // Set MULTITERMINAL_DOC_ID and MULTITERMINAL_NAME environment variables for MCP
            DebugLogService?.Trace("ConPtyTerminal", $"Building environment setup...");
            // Force UTF-8 encoding in the child PowerShell process for correct international character handling
            string envSetup = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; [Console]::InputEncoding = [System.Text.Encoding]::UTF8; ";

            // Escape single quotes in all values before interpolating into the PowerShell -Command string.
            // In PowerShell single-quoted strings, a literal single quote is represented as ''.
            // Without escaping, a value like abc'; Start-Process calc; $x=' would break out of the string.
            // Never launch a terminal MCP-invisible: a missing docId means our
            // statusline.js early-exits (it requires MULTITERMINAL_DOC_ID) and the HUD
            // has no per-terminal file to read. Auto-generate a fallback docId rather than
            // silently launching without one (task 1ba59334 defense-in-depth). All current
            // interactive callers pass a populated docId, so this only guards future/edge
            // callers — but it makes "unidentified terminal" structurally impossible.
            if (string.IsNullOrEmpty(docId))
            {
                docId = Guid.NewGuid().ToString("N").Substring(0, 8);
                DebugLogService?.Warning("ConPtyTerminal", $"NO docId supplied - generated fallback MULTITERMINAL_DOC_ID = '{docId}'");
            }
            string safeDocId = docId.Replace("'", "''");
            envSetup += $"$env:MULTITERMINAL_DOC_ID = '{safeDocId}'; ";
            DebugLogService?.Trace("ConPtyTerminal", $"Setting MULTITERMINAL_DOC_ID = '{docId}'");

            if (!string.IsNullOrEmpty(terminalName))
            {
                string safeTerminalName = terminalName.Replace("'", "''");
                envSetup += $"$env:MULTITERMINAL_NAME = '{safeTerminalName}'; ";
                DebugLogService?.Trace("ConPtyTerminal", $"Setting MULTITERMINAL_NAME = '{terminalName}'");
            }
            else
            {
                DebugLogService?.Trace("ConPtyTerminal", $"NO terminalName - MULTITERMINAL_NAME will not be set!");
            }

            if (!string.IsNullOrEmpty(spawnerName))
            {
                string safeSpawnerName = spawnerName.Replace("'", "''");
                envSetup += $"$env:MULTITERMINAL_SPAWNER = '{safeSpawnerName}'; ";
                DebugLogService?.Trace("ConPtyTerminal", $"Setting MULTITERMINAL_SPAWNER = '{spawnerName}'");
            }

            if (!string.IsNullOrEmpty(projectId))
            {
                string safeProjectId = projectId.Replace("'", "''");
                envSetup += $"$env:MULTITERMINAL_PROJECT_ID = '{safeProjectId}'; ";
                DebugLogService?.Trace("ConPtyTerminal", $"Setting MULTITERMINAL_PROJECT_ID = '{projectId}'");
            }

            if (isTeamLead)
            {
                envSetup += "$env:MULTITERMINAL_TEAM_LEAD = 'true'; ";
                DebugLogService?.Trace("ConPtyTerminal", $"Setting MULTITERMINAL_TEAM_LEAD = 'true'");
            }

            if (!string.IsNullOrEmpty(gatewayProfile))
            {
                string safeGatewayProfile = gatewayProfile.Replace("'", "''");
                envSetup += $"$env:MCP_GATEWAY_PROFILE = '{safeGatewayProfile}'; ";
                DebugLogService?.Trace("ConPtyTerminal", $"Setting MCP_GATEWAY_PROFILE = '{gatewayProfile}'");
            }

            if (!string.IsNullOrEmpty(taskWorktreePath))
            {
                string safeWorktreePath = taskWorktreePath.Replace("'", "''");
                envSetup += $"$env:MULTITERMINAL_TASK_WORKTREE = '{safeWorktreePath}'; ";
                DebugLogService?.Trace("ConPtyTerminal", $"Setting MULTITERMINAL_TASK_WORKTREE = '{taskWorktreePath}'");
            }

            // Enable flicker-free alternate-screen renderer for Claude Code
            envSetup += "$env:CLAUDE_CODE_NO_FLICKER = '1'; ";

            string promptFunc = "function prompt { $Host.UI.RawUI.WindowTitle = $PWD.Path; return \\\"PS $($PWD.Path)> \\\" }";

            // If autoRunCommand is specified, append it after the prompt function setup
            // IMPORTANT: Add cd command before autoRunCommand so Claude Code runs from correct directory.
            //
            // Launch-at-root strategy (task 0134ec2f, supersedes AC7 c6ed236c):
            // we emit a SINGLE cd to repoRoot (the caller passes repo root as
            // workingDirectory) and DO NOT narrow into the task worktree here.
            // The old second `cd '<worktree>'` pinned the harness cwd to the
            // worktree — when the task completed and the worktree was pruned, the
            // shell was stranded inside a deleted dir and `git worktree remove`
            // could not remove its own cwd. Instead the session-start skill now
            // calls EnterWorktree(path=.claude/worktrees/<id>) to switch the
            // process into the worktree (CLI >= 2.1.157), and ExitWorktree(keep)
            // returns to repoRoot before prune. MULTITERMINAL_TASK_WORKTREE is
            // still exported above so the skill + MCP tools resolve the path.
            // CWD STRATEGY (ExitWorktree-compatibility experiment): do NOT pass a
            // native working directory to CreateProcess (lpCurrentDirectory=null below,
            // so the child inherits the host's cwd). Instead `cd` into the project
            // folder in the shell itself, then run the autoRunCommand. Passing an
            // explicit native working directory is suspected of making the Claude Code
            // harness classify the session as a cwd-overridden / isolated agent, which
            // makes ExitWorktree refuse to run and breaks the EnterWorktree/ExitWorktree
            // worktree lifecycle (task 0134ec2f follow-up). The shell `cd` lands the
            // session in the project folder the "normal interactive" way instead.
            string cdPrefix = "";
            if (!string.IsNullOrEmpty(workingDirectory))
            {
                // Escape single quotes to prevent PowerShell injection via path names.
                string safeWorkDir = workingDirectory.Replace("'", "''");
                cdPrefix = $"cd '{safeWorkDir}'; ";
            }

            string autoRun = "";
            if (!string.IsNullOrEmpty(autoRunCommand))
            {
                autoRun = $"; {autoRunCommand}";
            }
            else
            {
                DebugLogService?.Trace("ConPtyTerminal", $"NO autoRunCommand - terminal will start with prompt only");
            }

            // Use -NoExit only for plain shells (no autoRunCommand). When an autoRunCommand is
            // specified (e.g. Claude Code), PowerShell should exit after the command finishes so
            // that ProcessExited fires and the terminal returns to the Start Screen.
            string noExit = string.IsNullOrEmpty(autoRunCommand) ? "-NoExit " : "";
            string commandLine = $"\"{shellPath}\" -NoLogo -ExecutionPolicy Bypass {noExit}-Command \"{envSetup}{cdPrefix}{promptFunc}{autoRun}\"";
            DebugLogService?.Trace("ConPtyTerminal", $"Final command line:");
            DebugLogService?.Trace("ConPtyTerminal", $"{commandLine}");

            // Create process
            DebugLogService?.Trace("ConPtyTerminal", $"Calling CreateProcess...");
            if (!NativeMethods.CreateProcess(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                NativeMethods.EXTENDED_STARTUPINFO_PRESENT,
                IntPtr.Zero,
                null, // lpCurrentDirectory: NULL => inherit host cwd. The shell `cd`'s into
                      // the project folder (cdPrefix above) so the session is NOT launched
                      // with a native cwd override (ExitWorktree-compatibility experiment).
                ref startupInfo,
                out var processInfo))
            {
                DebugLogService?.Error("ConPtyTerminal", $"CreateProcess FAILED: {Marshal.GetLastWin32Error()}");
                throw new InvalidOperationException("Failed to create process: " + Marshal.GetLastWin32Error());
            }

            DebugLogService?.Trace("ConPtyTerminal", $"CreateProcess succeeded!");
            DebugLogService?.Trace("ConPtyTerminal", $"Process handle: {processInfo.hProcess}");
            DebugLogService?.Trace("ConPtyTerminal", $"Thread handle: {processInfo.hThread}");

            _processHandle = processInfo.hProcess;
            _threadHandle = processInfo.hThread;

            // Assign the process to a Job Object so that all child processes (Claude Code,
            // McpGateway, node.js MCP server) are terminated when we kill the job.
            // Without this, TerminateProcess only kills the immediate PowerShell child,
            // leaving orphaned grandchildren that hold ConPTY pipe handles open and can
            // corrupt the console driver state.
            CreateJobAndAssignProcess();

            // Create streams for I/O
            _inputWriter = new FileStream(_inputWriteSide, FileAccess.Write);
            _outputReader = new FileStream(_outputReadSide, FileAccess.Read);

            DebugLogService?.Info("ConPtyTerminal", $"StartProcess ===== COMPLETE =====");
        }

        /// <summary>
        /// Creates a Windows Job Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE and assigns
        /// the shell process to it. When the job handle is closed (during Cleanup), Windows
        /// terminates the entire process tree — shell, Claude Code, McpGateway, node.js, etc.
        /// </summary>
        private void CreateJobAndAssignProcess()
        {
            try
            {
                _jobHandle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
                if (_jobHandle == IntPtr.Zero)
                {
                    DebugLogService?.Error("ConPtyTerminal", $"CreateJobObject failed: {Marshal.GetLastWin32Error()}");
                    return;
                }

                // Configure the job to kill all processes when the job handle is closed
                var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    }
                };

                int infoSize = Marshal.SizeOf(typeof(NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                IntPtr infoPtr = Marshal.AllocHGlobal(infoSize);
                try
                {
                    Marshal.StructureToPtr(info, infoPtr, false);
                    if (!NativeMethods.SetInformationJobObject(_jobHandle, NativeMethods.JobObjectExtendedLimitInformation, infoPtr, (uint)infoSize))
                    {
                        DebugLogService?.Error("ConPtyTerminal", $"SetInformationJobObject failed: {Marshal.GetLastWin32Error()}");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(infoPtr);
                }

                if (!NativeMethods.AssignProcessToJobObject(_jobHandle, _processHandle))
                {
                    DebugLogService?.Error("ConPtyTerminal", $"AssignProcessToJobObject failed: {Marshal.GetLastWin32Error()}");
                }
                else
                {
                    DebugLogService?.Info("ConPtyTerminal", "Process assigned to job object — entire tree will be killed on cleanup");
                }
            }
            catch (Exception ex)
            {
                DebugLogService?.Error("ConPtyTerminal", $"Job object setup failed: {ex.Message}");
            }
        }

        private void StartReading()
        {
            _readThread = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = "ConPTY Read Thread"
            };
            _readThread.Start();
        }

        /// <summary>
        /// Waits for the shell process to exit, then closes the pseudoconsole.
        /// ConPTY keeps its output pipe open independently of the process lifetime,
        /// so ReadLoop would block on Read() forever without this.
        /// Closing the pseudoconsole breaks the pipe, allowing ReadLoop to detect
        /// EOF and fire the ProcessExited event.
        /// </summary>
        private void StartProcessWait()
        {
            _processWaitThread = new Thread(() =>
            {
                try
                {
                    NativeMethods.WaitForSingleObject(_processHandle, NativeMethods.INFINITE);
                    DebugLogService?.Info("ConPtyTerminal", "Process exited, closing pseudoconsole to unblock ReadLoop");

                    if (!_isDisposed && _pseudoConsoleHandle != IntPtr.Zero)
                    {
                        NativeMethods.ClosePseudoConsole(_pseudoConsoleHandle);
                        _pseudoConsoleHandle = IntPtr.Zero;
                    }
                }
                catch (Exception ex)
                {
                    DebugLogService?.Error("ConPtyTerminal", $"ProcessWait error: {ex.Message}");
                }
            })
            {
                IsBackground = true,
                Name = "ConPTY Process Wait"
            };
            _processWaitThread.Start();
        }

        private void ReadLoop()
        {
            byte[] buffer = new byte[4096];

            try
            {
                while (!_isDisposed && _isRunning)
                {
                    int bytesRead = _outputReader.Read(buffer, 0, buffer.Length);
                    if (bytesRead > 0)
                    {
                        byte[] data = new byte[bytesRead];
                        Array.Copy(buffer, data, bytesRead);
                        // Queue data instead of firing event directly (throttling)
                        _outputQueue.Enqueue(data);
                    }
                    else
                    {
                        // End of stream - process exited
                        break;
                    }
                }
            }
            catch (IOException)
            {
                // Pipe closed
            }
            catch (ObjectDisposedException)
            {
                // Already disposed
            }
            finally
            {
                if (_isRunning)
                {
                    _isRunning = false;
                    try
                    {
                        ProcessExited?.Invoke(this, EventArgs.Empty);
                    }
                    catch (Exception ex)
                    {
                        DebugLogService?.Error("ConPtyTerminal", $"ProcessExited handler error: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Timer callback that flushes queued output data.
        /// Batches multiple reads into a single event to reduce UI thread load.
        /// Handles UTF-8 multi-byte sequences that may be split across reads by
        /// holding trailing incomplete bytes until the next flush.
        /// </summary>
        private void FlushOutputQueue(object state)
        {
            if (_isDisposed || !_isRunning) return;

            // Dequeue all pending data and combine into a single batch
            var allData = new List<byte>();

            // Prepend any incomplete UTF-8 sequence held from the previous flush
            if (_utf8Remainder.Length > 0)
            {
                allData.AddRange(_utf8Remainder);
                _utf8Remainder = Array.Empty<byte>();
            }

            while (_outputQueue.TryDequeue(out byte[] data))
            {
                allData.AddRange(data);
            }

            if (allData.Count > 0)
            {
                // Detect trailing incomplete UTF-8 sequence and hold it back
                byte[] complete = StripIncompleteUtf8Tail(allData.ToArray(), out byte[] remainder);
                _utf8Remainder = remainder;

                if (complete.Length > 0)
                {
                    try
                    {
                        DataReceived?.Invoke(complete);
                    }
                    catch (Exception ex)
                    {
                        DebugLogService?.Error("ConPtyTerminal", $"DataReceived handler error: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Splits a byte array at the boundary of any incomplete trailing UTF-8 sequence.
        /// Returns the complete portion and sets remainder to the incomplete tail bytes.
        /// </summary>
        private static byte[] StripIncompleteUtf8Tail(byte[] data, out byte[] remainder)
        {
            int splitAt = data.Length;

            // Walk backwards from end to find start of last potential multi-byte sequence
            for (int i = data.Length - 1; i >= 0 && i >= data.Length - 4; i--)
            {
                byte b = data[i];

                if (b >= 0xF0) // 4-byte sequence leader: needs 3 more bytes
                {
                    int needed = 4;
                    if (data.Length - i < needed) { splitAt = i; }
                    break;
                }
                else if (b >= 0xE0) // 3-byte sequence leader: needs 2 more bytes
                {
                    int needed = 3;
                    if (data.Length - i < needed) { splitAt = i; }
                    break;
                }
                else if (b >= 0xC0) // 2-byte sequence leader: needs 1 more byte
                {
                    int needed = 2;
                    if (data.Length - i < needed) { splitAt = i; }
                    break;
                }
                else if (b < 0x80)
                {
                    // ASCII byte — sequence boundary is clear, nothing incomplete
                    break;
                }
                // 0x80–0xBF: continuation byte — keep scanning backwards
            }

            if (splitAt == data.Length)
            {
                remainder = Array.Empty<byte>();
                return data;
            }

            remainder = new byte[data.Length - splitAt];
            Array.Copy(data, splitAt, remainder, 0, remainder.Length);

            byte[] complete = new byte[splitAt];
            Array.Copy(data, 0, complete, 0, splitAt);
            return complete;
        }

        private void Cleanup()
        {
            _isRunning = false;

            // Stop output timer
            try { _outputTimer?.Dispose(); } catch { }
            _outputTimer = null;

            // === CRITICAL: Terminate the process tree FIRST ===
            // This must happen before closing handles, because _processWaitThread is blocked
            // on WaitForSingleObject(_processHandle). Killing the process unblocks the wait,
            // which allows us to safely join _processWaitThread before closing handles.

            // Use the Job Object to kill the entire process tree (shell + Claude Code +
            // McpGateway + node.js). This prevents orphaned grandchildren that hold ConPTY
            // pipe handles open and can corrupt the console driver state.
            if (_jobHandle != IntPtr.Zero)
            {
                NativeMethods.TerminateJobObject(_jobHandle, 0);
                DebugLogService?.Info("ConPtyTerminal", "Terminated job object (entire process tree)");
            }
            else if (_processHandle != IntPtr.Zero)
            {
                // Fallback: no job object, terminate just the immediate child
                uint exitCode;
                if (NativeMethods.GetExitCodeProcess(_processHandle, out exitCode) && exitCode == NativeMethods.STILL_ACTIVE)
                {
                    NativeMethods.TerminateProcess(_processHandle, 0);
                }
            }

            // === Join _processWaitThread BEFORE closing handles ===
            // Now that the process is dead, WaitForSingleObject will return and the thread
            // can exit. We MUST join it before closing _processHandle / _pseudoConsoleHandle
            // to prevent handle-use-after-close (which can corrupt the console driver).
            if (_processWaitThread != null && _processWaitThread.IsAlive)
            {
                _processWaitThread.Join(2000);
            }
            _processWaitThread = null;

            // Close pseudo console (safe now — _processWaitThread is done)
            if (_pseudoConsoleHandle != IntPtr.Zero)
            {
                NativeMethods.ClosePseudoConsole(_pseudoConsoleHandle);
                _pseudoConsoleHandle = IntPtr.Zero;
            }

            // Close streams
            try { _inputWriter?.Dispose(); } catch { }
            try { _outputReader?.Dispose(); } catch { }
            _inputWriter = null;
            _outputReader = null;

            // Close pipe handles
            try { _inputReadSide?.Dispose(); } catch { }
            try { _inputWriteSide?.Dispose(); } catch { }
            try { _outputReadSide?.Dispose(); } catch { }
            try { _outputWriteSide?.Dispose(); } catch { }
            _inputReadSide = null;
            _inputWriteSide = null;
            _outputReadSide = null;
            _outputWriteSide = null;

            // Close process and thread handles (safe now — all threads are done)
            if (_processHandle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(_processHandle);
                _processHandle = IntPtr.Zero;
            }

            if (_threadHandle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(_threadHandle);
                _threadHandle = IntPtr.Zero;
            }

            // Close job handle
            if (_jobHandle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(_jobHandle);
                _jobHandle = IntPtr.Zero;
            }

            // Clean up attribute list
            if (_attributeList != IntPtr.Zero)
            {
                NativeMethods.DeleteProcThreadAttributeList(_attributeList);
                Marshal.FreeHGlobal(_attributeList);
                _attributeList = IntPtr.Zero;
            }

            // Wait for read thread
            if (_readThread != null && _readThread.IsAlive)
            {
                _readThread.Join(1000);
            }
            _readThread = null;
        }

        /// <summary>
        /// Stops the running terminal process and cleans up resources, but keeps the terminal
        /// reusable (Start() can be called again). Use this for "return to Home" scenarios
        /// where the tab stays open but the shell session ends.
        /// </summary>
        public void Stop()
        {
            if (_isDisposed || !_isRunning) return;
            Cleanup();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            if (disposing)
            {
                // Full teardown — safe to touch managed disposables + join threads.
                Cleanup();
            }
            else
            {
                // Finalizer path — only release unmanaged OS handles. Managed disposables
                // and threads are finalized separately by the GC; touching them here is
                // undefined behavior.
                CloseUnmanagedHandles();
            }
        }

        // Unmanaged-only handle release. Safe from both Dispose(true) via Cleanup and
        // Dispose(false) from the finalizer. Idempotent — each handle is nulled after close.
        private void CloseUnmanagedHandles()
        {
            if (_pseudoConsoleHandle != IntPtr.Zero)
            {
                NativeMethods.ClosePseudoConsole(_pseudoConsoleHandle);
                _pseudoConsoleHandle = IntPtr.Zero;
            }
            if (_processHandle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(_processHandle);
                _processHandle = IntPtr.Zero;
            }
            if (_threadHandle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(_threadHandle);
                _threadHandle = IntPtr.Zero;
            }
            if (_jobHandle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(_jobHandle);
                _jobHandle = IntPtr.Zero;
            }
            if (_attributeList != IntPtr.Zero)
            {
                NativeMethods.DeleteProcThreadAttributeList(_attributeList);
                Marshal.FreeHGlobal(_attributeList);
                _attributeList = IntPtr.Zero;
            }
        }

        // Required by CA2216: owning unmanaged handles obliges us to release them even if
        // Dispose() was never called. Finalizer path only runs CloseUnmanagedHandles().
        ~ConPtyTerminal()
        {
            Dispose(false);
        }
    }
}
