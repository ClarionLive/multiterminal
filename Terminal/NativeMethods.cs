using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MultiTerminal.Terminal
{
    /// <summary>
    /// Win32 API declarations for ConPTY (Pseudo Console) support.
    /// Requires Windows 10 version 1809 or later.
    /// </summary>
    internal static class NativeMethods
    {
        // Process creation flags
        internal const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

        // Pseudo console flags
        internal const uint PSEUDOCONSOLE_INHERIT_CURSOR = 1;

        // Attribute for proc thread attribute list
        internal static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;

        // COORD structure for console size
        [StructLayout(LayoutKind.Sequential)]
        internal struct COORD
        {
            public short X;
            public short Y;

            public COORD(short x, short y)
            {
                X = x;
                Y = y;
            }
        }

        // SECURITY_ATTRIBUTES structure
        [StructLayout(LayoutKind.Sequential)]
        internal struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public bool bInheritHandle;
        }

        // STARTUPINFO structure
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        // STARTUPINFOEX structure (extended with attribute list)
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        // PROCESS_INFORMATION structure
        [StructLayout(LayoutKind.Sequential)]
        internal struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        // CreatePipe - Create anonymous pipe for I/O
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CreatePipe(
            out SafeFileHandle hReadPipe,
            out SafeFileHandle hWritePipe,
            ref SECURITY_ATTRIBUTES lpPipeAttributes,
            uint nSize);

        // CreatePseudoConsole - Create the pseudo console (Windows 10 1809+)
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern int CreatePseudoConsole(
            COORD size,
            SafeFileHandle hInput,
            SafeFileHandle hOutput,
            uint dwFlags,
            out IntPtr phPC);

        // ResizePseudoConsole - Change console size
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern int ResizePseudoConsole(
            IntPtr hPC,
            COORD size);

        // ClosePseudoConsole - Clean up pseudo console
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern void ClosePseudoConsole(IntPtr hPC);

        // InitializeProcThreadAttributeList - Initialize attribute list
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool InitializeProcThreadAttributeList(
            IntPtr lpAttributeList,
            int dwAttributeCount,
            int dwFlags,
            ref IntPtr lpSize);

        // UpdateProcThreadAttribute - Set pseudo console attribute
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool UpdateProcThreadAttribute(
            IntPtr lpAttributeList,
            uint dwFlags,
            IntPtr Attribute,
            IntPtr lpValue,
            IntPtr cbSize,
            IntPtr lpPreviousValue,
            IntPtr lpReturnSize);

        // DeleteProcThreadAttributeList - Clean up attribute list
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        // CreateProcess - Start process with pseudo console
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool CreateProcess(
            string lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFOEX lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        // CloseHandle - Close handle
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(IntPtr hObject);

        // GetExitCodeProcess - Check if process exited
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        // TerminateProcess - Force terminate process
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        // WaitForSingleObject - Wait for process to exit
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        // Constants for WaitForSingleObject
        internal const uint WAIT_OBJECT_0 = 0x00000000;
        internal const uint WAIT_TIMEOUT = 0x00000102;
        internal const uint INFINITE = 0xFFFFFFFF;
        internal const uint STILL_ACTIVE = 259;

        // SetHandleInformation - Set handle flags
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetHandleInformation(
            SafeFileHandle hObject,
            uint dwMask,
            uint dwFlags);

        internal const uint HANDLE_FLAG_INHERIT = 0x00000001;

        // Console code page functions for UTF-8 support
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetConsoleCP(uint wCodePageID);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetConsoleOutputCP(uint wCodePageID);

        internal const uint CP_UTF8 = 65001;

        #region Job Object API for process tree management

        // Job Object — used to kill entire process tree (shell + Claude Code + grandchildren)
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

        // Job Object info class for SetInformationJobObject
        internal const int JobObjectExtendedLimitInformation = 9;

        // Limit flag: kill all processes when the job handle is closed
        internal const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

        [StructLayout(LayoutKind.Sequential)]
        internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        #endregion

        #region SendInput API for keyboard simulation

        private const int INPUT_KEYBOARD = 1;
        private const ushort VK_RETURN = 0x0D;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public int type;
            public INPUTUNION u;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)]
            public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        #endregion

        /// <summary>
        /// Sends an Enter key press using Windows SendInput API.
        /// This simulates an actual keyboard press at the OS level.
        /// </summary>
        /// <returns>True if the input was successfully sent.</returns>
        public static bool SendEnterKey()
        {
            var inputs = new INPUT[2];

            // Key down
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].u.ki.wVk = VK_RETURN;
            inputs[0].u.ki.dwFlags = 0;
            inputs[0].u.ki.time = 0;
            inputs[0].u.ki.dwExtraInfo = IntPtr.Zero;

            // Key up
            inputs[1].type = INPUT_KEYBOARD;
            inputs[1].u.ki.wVk = VK_RETURN;
            inputs[1].u.ki.dwFlags = KEYEVENTF_KEYUP;
            inputs[1].u.ki.time = 0;
            inputs[1].u.ki.dwExtraInfo = IntPtr.Zero;

            uint result = SendInput(2, inputs, Marshal.SizeOf<INPUT>());
            return result == 2;
        }

        /// <summary>
        /// Brings a window to the foreground and ensures it receives keyboard input.
        /// Uses thread input attachment for more reliable focus.
        /// </summary>
        /// <param name="windowHandle">Handle of the window to focus.</param>
        /// <returns>True if focus was successfully set.</returns>
        public static bool FocusWindow(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
                return false;

            IntPtr foregroundWindow = GetForegroundWindow();
            if (foregroundWindow == windowHandle)
                return true;

            // Get thread IDs
            uint foregroundThreadId = GetWindowThreadProcessId(foregroundWindow, out _);
            uint currentThreadId = GetCurrentThreadId();

            // If we're not the foreground thread, attach to it
            bool attached = false;
            if (foregroundThreadId != currentThreadId)
            {
                attached = AttachThreadInput(currentThreadId, foregroundThreadId, true);
            }

            try
            {
                return SetForegroundWindow(windowHandle);
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(currentThreadId, foregroundThreadId, false);
                }
            }
        }

        /// <summary>
        /// Gets the current foreground window handle.
        /// </summary>
        public static IntPtr GetCurrentForegroundWindow()
        {
            return GetForegroundWindow();
        }
    }
}
