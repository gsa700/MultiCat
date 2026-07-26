using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MultiCat.Service.Rigctld;

/// <summary>
/// Enumerates the loopback TCP clients connected to a rigctld listen port, so
/// MultiCAT can show which apps (Log4OM, WSJT-X, …) are sharing a radio. rigctld
/// doesn't expose its clients, so we read them from the OS connection table.
/// </summary>
internal static class TcpConnections
{
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tblClass, int reserved);

    private const int AfInet = 2;
    private const int TcpTableOwnerPidAll = 5;
    private const uint MibTcpStateEstab = 5;
    private const uint Loopback = 0x0100007F; // 127.0.0.1 as stored in the table

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    /// <summary>Clients established to 127.0.0.1:<paramref name="listenPort"/>. Each
    /// returned entry is one connection: its owning process and a stable connection
    /// id (the client's ephemeral port). <paramref name="excludePid"/> hides our own
    /// poller, which connects to the same port.</summary>
    public static List<(int Pid, string Process, int ConnectionId)> ClientsOnLoopbackPort(int listenPort, int excludePid)
    {
        var clients = new List<(int, string, int)>();
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
        if (size == 0)
        {
            return clients;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidAll, 0) != 0)
            {
                return clients;
            }

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<TcpRow>();
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<TcpRow>(buffer + 4 + (i * rowSize));
                if (row.State != MibTcpStateEstab || row.RemoteAddr != Loopback || row.LocalAddr != Loopback)
                {
                    continue;
                }

                // Client rows point AT the listen port; skip rigctld's own server rows.
                if (PortOf(row.RemotePort) != listenPort)
                {
                    continue;
                }

                var pid = (int)row.OwningPid;
                if (pid == excludePid)
                {
                    continue;
                }

                clients.Add((pid, ProcessName(pid), PortOf(row.LocalPort)));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return clients;
    }

    private static int PortOf(uint netPort) => ((int)(netPort & 0xFF) << 8) | (int)((netPort >> 8) & 0xFF);

    private static string ProcessName(int pid)
    {
        try
        {
            return Process.GetProcessById(pid).ProcessName;
        }
        catch (Exception)
        {
            return $"pid {pid}";
        }
    }
}
