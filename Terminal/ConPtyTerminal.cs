using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

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

        private SafeFileHandle _inputReadSide;
        private SafeFileHandle _inputWriteSide;
        private SafeFileHandle _outputReadSide;
        private SafeFileHandle _outputWriteSide;

        private FileStream _inputWriter;
        private FileStream _outputReader;
        private Thread _readThread;

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
        public void Start(int cols, int rows, string shellPath = null, string workingDirectory = null, string docId = null, string terminalName = null, string autoRunCommand = null, string spawnerName = null, string projectId = null)
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
                // Set console code page to UTF-8 for proper Unicode support
                NativeMethods.SetConsoleCP(NativeMethods.CP_UTF8);
                NativeMethods.SetConsoleOutputCP(NativeMethods.CP_UTF8);

                // Create pipes for I/O
                CreatePipes();

                // Create the pseudo console
                CreatePseudoConsole(cols, rows);

                // Start the shell process
                StartProcess(shellPath, workingDirectory, docId, terminalName, autoRunCommand, spawnerName, projectId);

                // Set running flag BEFORE starting timer/reader (prevents race condition)
                _isRunning = true;

                // Start output flush timer (throttles events to ~60fps max)
                _outputTimer = new Timer(FlushOutputQueue, null, 0, OUTPUT_INTERVAL_MS);

                // Start reading output
                StartReading();
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

        private void StartProcess(string shellPath, string workingDirectory, string docId, string terminalName = null, string autoRunCommand = null, string spawnerName = null, string projectId = null)
        {
            System.Diagnostics.Trace.WriteLine($"[ConPtyTerminal.StartProcess] ===== START =====");
            System.Diagnostics.Trace.WriteLine($"[ConPtyTerminal.StartProcess] shellPath: '{shellPath}'");
            System.Diagnostics.Trace.WriteLine($"[ConPtyTerminal.StartProcess] workingDirectory: '{workingDirectory}'");
            System.Diagnostics.Trace.WriteLine($"[ConPtyTerminal.StartProcess] docId: '{docId ?? "null"}'");
            System.Diagnostics.Trace.WriteLine($"[ConPtyTerminal.StartProcess] terminalName: '{terminalName ?? "null"}'");
            System.Diagnostics.Trace.WriteLine($"[ConPtyTerminal.StartProcess] autoRunCommand: '{autoRunCommand ?? "null"}'");

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
            System.Diagnostics.Trace.WriteLine($"[ConPtyTerminal.StartProcess] Building environment setup...");
            // Force UTF-8 encoding in the child PowerShell process for correct international character handling
            string envSetup = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; [Console]::InputEncoding = [System.Text.Encoding]::UTF8; ";

            // Escape single quotes in all values before interpolating into the PowerShell -Command string.
            // In PowerShell single-quoted strings, a literal single quote is represented as ''.
            // Without escaping, a value like abc'; Start-Process calc; $x=' would break out of the string.
            if (!string.IsNullOrEmpty(docId))
            {
                string safeDocId = docId.Replace("'", "''");
                envSetup += $"$env:MULTITERMINAL_DOC_ID = '{safeDocId}'; ";
                System.Diagnostics.Trace.WriteLine($"[ConPtyTerminal.StartProcess] Setting MULTITERMINAL_DOC_ID = '{docId}'");
            }
            else
            {
                System.Diagnostics.Trace.WriteLine($"[ConPtyTerminal.StartProcess] NO docId - MULTITERMINAL_DOC_ID will not be set!");
            }

            if (!string.IsNullOrEmpty(terminalName))
            {
                string safeTerminalName = terminalName.Replace("'", "''");
                envSetup += $"$env:MULTITERMINAL_NAME = '{safeTerminalName}'; ";
                System.Diagnostics.Trace.WriteLine($"[ConPtyTerminal.StartProcess] Setting MULTITERMINAL_NAME = '{terminalName}'");
            }
            else
            {
                System.Diagnostics.Trace.WriteLine($"[ConPtyTerminal.StartProcess] NO terminalName - MULTITERMINAL_NAME will not be set!");
            }

            if (!string.IsNullOrEmpty(spawnerName))
            {
                string safeSpawnerName = spawnerName.Replace("'", "''");
                envSetup += $"$env:MULTITERMINAL_SPAWNER = '{safeSpawnerName}'; ";
                System.Diagnostics.Trace.WriteLine($"[ConPtyTerminal.StartProcess] Setting MULTITERMINAL_SPAWNER = '{spawnerName}'");
            }

            if (!string.IsNullOrEmpty(projectId))
            {
                string safeProjectId = projectId.Replace("'", "''");
                envSetup += $"$env:MULTITERMINAL_PROJECT_ID = '{safeProjectId}'; ";
                System.Diagnostics.Trace.WriteLine($"[ConPtyTerminal.StartProcess] Setting MULTITERMINAL_PROJECT_ID = '{projectId}'");
            }

            string promptFunc = "function prompt { $Host.UI.RawUI.WindowTitle = $PWD.Path; return \\\"PS $($PWD.Path)> \\\" }";

            // If autoRunCommand is specified, append it after the prompt function setup
            // IMPORTANT: Add cd command before autoRunCommand so Claude Code runs from correct directory
            string autoRun = "";
            if (!string.IsNullOrEmpty(autoRunCommand))
            {
                // Escape single quotes in workingDirectory to prevent PowerShell injection via path names
                string safeWorkDir = (workingDirectory ?? "").Replace("'", "''");
                autoRun = $"; cd '{safeWorkDir}'; {autoRunCommand}";
            }
            else
            {
                System.Diagnostics.Trace.WriteLine($"[ConPtyTerminal.StartProcess] NO autoRunCommand - terminal will start with prompt only");
            }

            string commandLine = $"\"{shellPath}\" -NoLogo -ExecutionPolicy Bypass -NoExit -Command \"{envSetup}{promptFunc}{autoRun}\"";
            System.Diagnostics.Trace.WriteLine($"[ConPtyTerminal.StartProcess] Final command line:");
            System.Diagnostics.Trace.WriteLine($"[ConPtyTerminal.StartProcess]   {commandLine}");

            // Create process
            System.Diagnostics.Trace.WriteLine($"[ConPtyTerminal.StartProcess] Calling CreateProcess...");
            if (!NativeMethods.CreateProcess(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                NativeMethods.EXTENDED_STARTUPINFO_PRESENT,
                IntPtr.Zero,
                workingDirectory,
                ref startupInfo,
                out var processInfo))
            {
                System.Diagnostics.Trace.WriteLine($"[ConPtyTerminal.StartProcess] CreateProcess FAILED: {Marshal.GetLastWin32Error()}");
                throw new InvalidOperationException("Failed to create process: " + Marshal.GetLastWin32Error());
            }

            System.Diagnostics.Trace.WriteLine($"[ConPtyTerminal.StartProcess] CreateProcess succeeded!");
            System.Diagnostics.Trace.WriteLine($"[ConPtyTerminal.StartProcess] Process handle: {processInfo.hProcess}");
            System.Diagnostics.Trace.WriteLine($"[ConPtyTerminal.StartProcess] Thread handle: {processInfo.hThread}");

            _processHandle = processInfo.hProcess;
            _threadHandle = processInfo.hThread;

            // Create streams for I/O
            _inputWriter = new FileStream(_inputWriteSide, FileAccess.Write);
            _outputReader = new FileStream(_outputReadSide, FileAccess.Read);

            System.Diagnostics.Trace.WriteLine($"[ConPtyTerminal.StartProcess] ===== COMPLETE =====");
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
                        System.Diagnostics.Debug.WriteLine($"[ConPtyTerminal] ProcessExited handler error: {ex.Message}");
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
                        System.Diagnostics.Debug.WriteLine($"[ConPtyTerminal] DataReceived handler error: {ex.Message}");
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

            // Close pseudo console
            if (_pseudoConsoleHandle != IntPtr.Zero)
            {
                NativeMethods.ClosePseudoConsole(_pseudoConsoleHandle);
                _pseudoConsoleHandle = IntPtr.Zero;
            }

            // Terminate process if still running
            if (_processHandle != IntPtr.Zero)
            {
                uint exitCode;
                if (NativeMethods.GetExitCodeProcess(_processHandle, out exitCode) && exitCode == NativeMethods.STILL_ACTIVE)
                {
                    NativeMethods.TerminateProcess(_processHandle, 0);
                }
                NativeMethods.CloseHandle(_processHandle);
                _processHandle = IntPtr.Zero;
            }

            if (_threadHandle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(_threadHandle);
                _threadHandle = IntPtr.Zero;
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

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            Cleanup();
        }
    }
}
