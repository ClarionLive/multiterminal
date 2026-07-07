using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MultiTerminal.Services.Startup
{
    /// <summary>
    /// A decoded row from the OS TCP table: which PID owns which local port, and in what state.
    /// Ports here are already decoded to host order so the pure filter below is trivially testable.
    /// </summary>
    public readonly struct TcpTableRow
    {
        /// <summary>TCP state (MIB_TCP_STATE); 2 == LISTEN.</summary>
        public int State { get; }

        /// <summary>Local port in host byte order.</summary>
        public int LocalPort { get; }

        /// <summary>Owning process id.</summary>
        public int Pid { get; }

        public TcpTableRow(int state, int localPort, int pid)
        {
            State = state;
            LocalPort = localPort;
            Pid = pid;
        }

        /// <summary>MIB_TCP_STATE value for a listening socket.</summary>
        public const int StateListen = 2;
    }

    /// <summary>
    /// Resolves which process is holding a TCP port via the Windows TCP table
    /// (task 4fec40e2, bind-failure classification).
    /// <para>
    /// .NET's fully-managed <c>IPGlobalProperties.GetActiveTcpListeners()</c> returns the
    /// listening endpoints but NOT the owning PID, which is exactly what the user needs
    /// to see ("port held by X (PID N)"). Windows only exposes the PID through
    /// <c>GetExtendedTcpTable(TCP_TABLE_OWNER_PID_ALL)</c>, so a small P/Invoke is
    /// justified here despite the general preference for managed APIs. The native fetch
    /// is isolated from <see cref="FindHolderPid"/> so the selection logic stays pure and
    /// unit-testable without touching the live TCP stack.
    /// </para>
    /// </summary>
    public static class TcpPortOwnerLookup
    {
        private const int AF_INET = 2;                 // IPv4 — Kestrel binds 127.0.0.1 (IPv4 loopback)
        private const int TCP_TABLE_OWNER_PID_ALL = 5; // TCP_TABLE_CLASS
        private const uint ERROR_INSUFFICIENT_BUFFER = 122; // table grew between size probe and fetch

        /// <summary>
        /// Pure selection: from a set of decoded TCP rows, pick the PID that owns
        /// <paramref name="port"/>. A LISTEN row wins over any other state (that's the
        /// binder); if no LISTEN row exists, any row on the port is used as a fallback.
        /// Returns null when nothing owns the port.
        /// </summary>
        public static int? FindHolderPid(IEnumerable<TcpTableRow> rows, int port)
        {
            if (rows == null)
            {
                return null;
            }

            int? fallbackPid = null;
            foreach (var row in rows)
            {
                if (row.LocalPort != port || row.Pid <= 0)
                {
                    continue;
                }

                if (row.State == TcpTableRow.StateListen)
                {
                    return row.Pid;
                }

                fallbackPid ??= row.Pid;
            }

            return fallbackPid;
        }

        /// <summary>
        /// Resolve the process holding <paramref name="port"/> on IPv4 loopback. Best-effort:
        /// any failure (P/Invoke error, race, access denied on process name) degrades to
        /// <see cref="PortHolderInfo.Unknown"/> so the caller can still show an actionable
        /// (if less specific) dialog.
        /// </summary>
        public static PortHolderInfo Lookup(int port)
        {
            try
            {
                var rows = ReadIpv4TcpTable();
                int? pid = FindHolderPid(rows, port);
                if (pid is null or <= 0)
                {
                    return PortHolderInfo.Unknown;
                }

                return new PortHolderInfo { Pid = pid.Value, ProcessName = TryGetProcessName(pid.Value) };
            }
            catch
            {
                return PortHolderInfo.Unknown;
            }
        }

        private static string TryGetProcessName(int pid)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                return proc.ProcessName;
            }
            catch
            {
                return null;
            }
        }

        private static List<TcpTableRow> ReadIpv4TcpTable()
        {
            var result = new List<TcpTableRow>();

            // The TCP table can grow between the size probe and the fetch, in which case the
            // second call returns ERROR_INSUFFICIENT_BUFFER. Retry a few times, re-reading
            // dwOutBufLen each attempt, so a busy machine doesn't silently degrade the holder
            // to Unknown (task 4fec40e2 debugger finding).
            for (int attempt = 0; attempt < 5; attempt++)
            {
                int bufferSize = 0;

                // First call sizes the buffer.
                _ = GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
                if (bufferSize <= 0)
                {
                    return result;
                }

                IntPtr tablePtr = Marshal.AllocHGlobal(bufferSize);
                try
                {
                    uint ret = GetExtendedTcpTable(tablePtr, ref bufferSize, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
                    if (ret == ERROR_INSUFFICIENT_BUFFER)
                    {
                        continue; // table grew — retry with a freshly-probed size
                    }

                    if (ret != 0)
                    {
                        return result;
                    }

                    int numEntries = Marshal.ReadInt32(tablePtr);
                    IntPtr rowPtr = IntPtr.Add(tablePtr, 4); // skip dwNumEntries
                    int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();

                    for (int i = 0; i < numEntries; i++)
                    {
                        var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                        result.Add(new TcpTableRow(
                            (int)row.state,
                            DecodePort(row.localPort),
                            (int)row.owningPid));
                        rowPtr = IntPtr.Add(rowPtr, rowSize);
                    }

                    return result;
                }
                finally
                {
                    Marshal.FreeHGlobal(tablePtr);
                }
            }

            return result;
        }

        /// <summary>
        /// The TCP table stores the local port in the low 16 bits of a DWORD in network
        /// (big-endian) byte order. Decode to host order.
        /// </summary>
        private static int DecodePort(uint netPort) => (int)(((netPort & 0xFF) << 8) | ((netPort >> 8) & 0xFF));

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(
            IntPtr pTcpTable,
            ref int dwOutBufLen,
            bool sort,
            int ipVersion,
            int tableClass,
            uint reserved);

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW_OWNER_PID
        {
            public uint state;
            public uint localAddr;
            public uint localPort;
            public uint remoteAddr;
            public uint remotePort;
            public uint owningPid;
        }
    }
}
