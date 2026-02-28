using System;
using System.Runtime.InteropServices;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Win32 API declarations for capturing OutputDebugString messages system-wide.
    /// Uses the DBWIN_BUFFER shared memory mechanism (same as DebugView).
    /// </summary>
    internal static class OutputDebugStringNativeMethods
    {
        // Shared memory and event names used by OutputDebugString
        internal const string DBWIN_BUFFER = "DBWIN_BUFFER";
        internal const string DBWIN_BUFFER_READY = "DBWIN_BUFFER_READY";
        internal const string DBWIN_DATA_READY = "DBWIN_DATA_READY";

        // Size constants
        internal const int DBWIN_BUFFER_SIZE = 4096;
        internal const int DBWIN_DATA_SIZE = 4096 - sizeof(int); // Total size minus PID field

        // Memory protection constants
        internal const uint PAGE_READWRITE = 0x04;

        // File mapping constants
        internal const uint FILE_MAP_READ = 0x0004;
        internal const uint FILE_MAP_WRITE = 0x0002;

        // Wait constants
        internal const uint WAIT_OBJECT_0 = 0x00000000;
        internal const uint WAIT_TIMEOUT = 0x00000102;
        internal const uint WAIT_FAILED = 0xFFFFFFFF;
        internal const uint INFINITE = 0xFFFFFFFF;

        // Event constants
        internal const bool MANUAL_RESET = false;
        internal const bool INITIAL_STATE = false;

        // SECURITY_ATTRIBUTES for shared memory
        [StructLayout(LayoutKind.Sequential)]
        internal struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public bool bInheritHandle;
        }

        // Security descriptor constants
        internal const int SECURITY_DESCRIPTOR_MIN_LENGTH = 20;
        internal const uint SECURITY_DESCRIPTOR_REVISION = 1;

        // CreateFileMapping - Create named shared memory
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr CreateFileMapping(
            IntPtr hFile,
            ref SECURITY_ATTRIBUTES lpFileMappingAttributes,
            uint flProtect,
            uint dwMaximumSizeHigh,
            uint dwMaximumSizeLow,
            string lpName);

        // CreateFileMapping overload without security attributes
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr CreateFileMapping(
            IntPtr hFile,
            IntPtr lpFileMappingAttributes,
            uint flProtect,
            uint dwMaximumSizeHigh,
            uint dwMaximumSizeLow,
            string lpName);

        // MapViewOfFile - Map shared memory into process address space
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr MapViewOfFile(
            IntPtr hFileMappingObject,
            uint dwDesiredAccess,
            uint dwFileOffsetHigh,
            uint dwFileOffsetLow,
            uint dwNumberOfBytesToMap);

        // UnmapViewOfFile - Unmap shared memory
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

        // CreateEvent - Create named synchronization event
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr CreateEvent(
            ref SECURITY_ATTRIBUTES lpEventAttributes,
            bool bManualReset,
            bool bInitialState,
            string lpName);

        // CreateEvent overload without security attributes
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr CreateEvent(
            IntPtr lpEventAttributes,
            bool bManualReset,
            bool bInitialState,
            string lpName);

        // SetEvent - Signal an event
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetEvent(IntPtr hEvent);

        // WaitForSingleObject - Wait for event to be signaled
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        // CloseHandle - Close handle
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(IntPtr hObject);

        // GetProcessById - Get process by ID
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(
            uint dwDesiredAccess,
            bool bInheritHandle,
            int dwProcessId);

        // Process access rights
        internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        // InitializeSecurityDescriptor
        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool InitializeSecurityDescriptor(
            IntPtr pSecurityDescriptor,
            uint dwRevision);

        // SetSecurityDescriptorDacl
        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool SetSecurityDescriptorDacl(
            IntPtr pSecurityDescriptor,
            bool bDaclPresent,
            IntPtr pDacl,
            bool bDaclDefaulted);

        // Invalid handle value
        internal static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
    }
}
