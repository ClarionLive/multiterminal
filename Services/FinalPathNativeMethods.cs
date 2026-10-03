using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Resolves an existing folder to its canonical on-disk path (task 9f95ab0c, Run 2): junctions and
    /// symlinks followed (8.3 short names are already expanded by Path.GetFullPath on .NET; GetLongPathName
    /// is only the fallback when no handle can be opened). Two spellings of one folder must compare equal, or
    /// <see cref="ExistingProjectDetector"/> reports a folder's own project.json as "a copy".
    /// </summary>
    internal static class FinalPathNativeMethods
    {
        private const uint FileReadAttributes = 0x80;
        private const uint FileShareAll = 0x1 | 0x2 | 0x4;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000; // required to open a directory handle

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW")]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle hFile, [Out] char[] lpszFilePath, uint cchFilePath, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetLongPathNameW")]
        private static extern uint GetLongPathName(string lpszShortPath, [Out] char[] lpszLongPath, uint cchBuffer);

        /// <summary>
        /// The final path of an existing folder, or null when it cannot be resolved (missing, access
        /// denied, not Windows). Tries GetFinalPathNameByHandle (follows links), then GetLongPathName
        /// (expands 8.3 names only).
        /// </summary>
        internal static string TryResolveFinalPath(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath) || !OperatingSystem.IsWindows())
                return null;

            try
            {
                using (var handle = CreateFile(fullPath, FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero))
                {
                    if (!handle.IsInvalid)
                    {
                        var buffer = new char[1024];
                        uint len = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
                        if (len > 0 && len < buffer.Length)
                            return StripDevicePrefix(new string(buffer, 0, (int)len));
                    }
                }

                var longBuffer = new char[1024];
                uint longLen = GetLongPathName(fullPath, longBuffer, (uint)longBuffer.Length);
                if (longLen > 0 && longLen < longBuffer.Length)
                    return new string(longBuffer, 0, (int)longLen);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is IOException)
            {
                // Fall through: the caller keeps the unresolved path.
            }
            return null;
        }

        // GetFinalPathNameByHandle answers "\\?\C:\x" or "\\?\UNC\server\share\x".
        private static string StripDevicePrefix(string path)
        {
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                return @"\\" + path.Substring(8);
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
                return path.Substring(4);
            return path;
        }
    }
}
