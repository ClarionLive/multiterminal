using System.Collections.Generic;
using MultiTerminal.Services.Startup;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Unit coverage for the pure port-owner selection (task 4fec40e2). The native TCP-table
    /// fetch (GetExtendedTcpTable P/Invoke) is not exercised here — it depends on the live OS
    /// stack — but the row-selection logic that turns a table into "the PID holding :5050" is
    /// pure and fully covered.
    /// </summary>
    public class TcpPortOwnerLookupTests
    {
        private const int OtherState = 5; // MIB_TCP_STATE ESTABLISHED — anything that isn't LISTEN

        private static TcpTableRow Listen(int port, int pid) => new TcpTableRow(TcpTableRow.StateListen, port, pid);

        private static TcpTableRow Other(int port, int pid) => new TcpTableRow(OtherState, port, pid);

        [Fact]
        public void FindHolderPid_returns_listening_pid_on_port()
        {
            var rows = new[] { Other(1234, 11), Listen(5050, 4321), Listen(80, 99) };
            Assert.Equal(4321, TcpPortOwnerLookup.FindHolderPid(rows, 5050));
        }

        [Fact]
        public void FindHolderPid_prefers_listen_over_other_state_on_same_port()
        {
            var rows = new[] { Other(5050, 111), Listen(5050, 222) };
            Assert.Equal(222, TcpPortOwnerLookup.FindHolderPid(rows, 5050));
        }

        [Fact]
        public void FindHolderPid_falls_back_to_non_listen_when_no_listener()
        {
            var rows = new[] { Other(5050, 777) };
            Assert.Equal(777, TcpPortOwnerLookup.FindHolderPid(rows, 5050));
        }

        [Fact]
        public void FindHolderPid_null_when_no_row_on_port()
        {
            var rows = new[] { Listen(80, 1), Listen(443, 2) };
            Assert.Null(TcpPortOwnerLookup.FindHolderPid(rows, 5050));
        }

        [Fact]
        public void FindHolderPid_null_for_empty()
        {
            Assert.Null(TcpPortOwnerLookup.FindHolderPid(new List<TcpTableRow>(), 5050));
        }

        [Fact]
        public void FindHolderPid_null_for_null_rows()
        {
            Assert.Null(TcpPortOwnerLookup.FindHolderPid(null, 5050));
        }

        [Fact]
        public void FindHolderPid_skips_invalid_pid()
        {
            var rows = new[] { Listen(5050, 0), Other(5050, -1) };
            Assert.Null(TcpPortOwnerLookup.FindHolderPid(rows, 5050));
        }
    }
}
