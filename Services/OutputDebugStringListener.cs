using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Listens for OutputDebugString messages from all processes system-wide.
    /// Uses the DBWIN_BUFFER shared memory mechanism (same as DebugView).
    /// </summary>
    public class OutputDebugStringListener : IDisposable
    {
        private Thread _listenerThread;
        private bool _isRunning;
        private bool _isDisposed;

        // Event raised when a debug message is captured
        public event EventHandler<OutputDebugStringEventArgs> MessageReceived;

        /// <summary>
        /// Starts listening for OutputDebugString messages.
        /// </summary>
        public void Start()
        {
            if (_isRunning || _isDisposed)
                return;

            _isRunning = true;
            _listenerThread = new Thread(ListenerThreadProc)
            {
                Name = "OutputDebugString Listener",
                IsBackground = true
            };
            _listenerThread.Start();
        }

        /// <summary>
        /// Stops listening for OutputDebugString messages.
        /// </summary>
        public void Stop()
        {
            _isRunning = false;
            _listenerThread?.Join(1000); // Wait up to 1 second for clean shutdown
        }

        private void ListenerThreadProc()
        {
            IntPtr bufferReadyEvent = IntPtr.Zero;
            IntPtr dataReadyEvent = IntPtr.Zero;
            IntPtr sharedFile = IntPtr.Zero;
            IntPtr sharedMem = IntPtr.Zero;

            try
            {
                // Create the DBWIN_BUFFER_READY event
                bufferReadyEvent = OutputDebugStringNativeMethods.CreateEvent(
                    IntPtr.Zero,
                    OutputDebugStringNativeMethods.MANUAL_RESET,
                    OutputDebugStringNativeMethods.INITIAL_STATE,
                    OutputDebugStringNativeMethods.DBWIN_BUFFER_READY);

                if (bufferReadyEvent == IntPtr.Zero)
                {
                    RaiseError("Failed to create DBWIN_BUFFER_READY event");
                    return;
                }

                // Create the DBWIN_DATA_READY event
                dataReadyEvent = OutputDebugStringNativeMethods.CreateEvent(
                    IntPtr.Zero,
                    OutputDebugStringNativeMethods.MANUAL_RESET,
                    OutputDebugStringNativeMethods.INITIAL_STATE,
                    OutputDebugStringNativeMethods.DBWIN_DATA_READY);

                if (dataReadyEvent == IntPtr.Zero)
                {
                    RaiseError("Failed to create DBWIN_DATA_READY event");
                    return;
                }

                // Create the shared memory buffer
                sharedFile = OutputDebugStringNativeMethods.CreateFileMapping(
                    new IntPtr(-1), // INVALID_HANDLE_VALUE
                    IntPtr.Zero,
                    OutputDebugStringNativeMethods.PAGE_READWRITE,
                    0,
                    OutputDebugStringNativeMethods.DBWIN_BUFFER_SIZE,
                    OutputDebugStringNativeMethods.DBWIN_BUFFER);

                if (sharedFile == IntPtr.Zero)
                {
                    RaiseError("Failed to create DBWIN_BUFFER shared memory");
                    return;
                }

                // Map the shared memory into our address space
                sharedMem = OutputDebugStringNativeMethods.MapViewOfFile(
                    sharedFile,
                    OutputDebugStringNativeMethods.FILE_MAP_READ,
                    0, 0, 0);

                if (sharedMem == IntPtr.Zero)
                {
                    RaiseError("Failed to map DBWIN_BUFFER into memory");
                    return;
                }

                // Signal that the buffer is ready for the first message
                OutputDebugStringNativeMethods.SetEvent(bufferReadyEvent);

                // Main listening loop
                while (_isRunning)
                {
                    // Wait for a debug message to arrive (with timeout for clean shutdown)
                    uint waitResult = OutputDebugStringNativeMethods.WaitForSingleObject(
                        dataReadyEvent,
                        100); // 100ms timeout

                    if (waitResult == OutputDebugStringNativeMethods.WAIT_OBJECT_0)
                    {
                        // Read the process ID (first 4 bytes)
                        int processId = Marshal.ReadInt32(sharedMem);

                        // Read the message string (remaining bytes)
                        IntPtr messagePtr = IntPtr.Add(sharedMem, sizeof(int));
                        string message = Marshal.PtrToStringAnsi(messagePtr);

                        // Get process name
                        string processName = GetProcessName(processId);

                        // Raise the event with the captured message
                        if (!string.IsNullOrEmpty(message))
                        {
                            var args = new OutputDebugStringEventArgs
                            {
                                ProcessId = processId,
                                ProcessName = processName,
                                Message = message
                            };
                            MessageReceived?.Invoke(this, args);
                        }

                        // Signal that we're ready for the next message
                        OutputDebugStringNativeMethods.SetEvent(bufferReadyEvent);
                    }
                    else if (waitResult == OutputDebugStringNativeMethods.WAIT_TIMEOUT)
                    {
                        // Timeout - just continue loop (allows clean shutdown)
                        continue;
                    }
                    else
                    {
                        // Error or unexpected result
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                RaiseError($"Listener thread exception: {ex.Message}");
            }
            finally
            {
                // Cleanup resources
                if (sharedMem != IntPtr.Zero)
                    OutputDebugStringNativeMethods.UnmapViewOfFile(sharedMem);

                if (sharedFile != IntPtr.Zero)
                    OutputDebugStringNativeMethods.CloseHandle(sharedFile);

                if (dataReadyEvent != IntPtr.Zero)
                    OutputDebugStringNativeMethods.CloseHandle(dataReadyEvent);

                if (bufferReadyEvent != IntPtr.Zero)
                    OutputDebugStringNativeMethods.CloseHandle(bufferReadyEvent);
            }
        }

        /// <summary>
        /// Gets the process name for a given process ID.
        /// </summary>
        private string GetProcessName(int processId)
        {
            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    return process.ProcessName;
                }
            }
            catch
            {
                // Process may have already exited or we don't have permission
                return $"PID:{processId}";
            }
        }

        /// <summary>
        /// Raises an error event (captured as a debug message).
        /// </summary>
        private void RaiseError(string errorMessage)
        {
            var args = new OutputDebugStringEventArgs
            {
                ProcessId = Process.GetCurrentProcess().Id,
                ProcessName = "OutputDebugStringListener",
                Message = $"ERROR: {errorMessage}"
            };
            MessageReceived?.Invoke(this, args);
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            Stop();
        }
    }

    /// <summary>
    /// Event args for OutputDebugString messages.
    /// </summary>
    public class OutputDebugStringEventArgs : EventArgs
    {
        public int ProcessId { get; set; }
        public string ProcessName { get; set; }
        public string Message { get; set; }
    }
}
