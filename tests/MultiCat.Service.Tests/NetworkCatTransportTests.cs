using System.Net;
using System.Net.Sockets;
using System.Text;
using MultiCat.Service.Transports;

namespace MultiCat.Service.Tests;

public class NetworkCatTransportTests
{
    private static async Task<string> WaitFor(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(20);
        }

        return condition() ? "ok" : "timeout";
    }

    [Fact]
    public async Task Connects_ReceivesData_AndReportsConnected()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var received = new List<byte>();
        await using var transport = new NetworkCatTransport("127.0.0.1", port);
        transport.DataReceived += bytes => received.AddRange(bytes);

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var buffer = Encoding.ASCII.GetBytes("FA00014074000;");
            await client.GetStream().WriteAsync(buffer);
            await Task.Delay(200);
        });

        transport.Open();

        Assert.Equal("ok", await WaitFor(() => transport.IsConnected));
        Assert.Equal("ok", await WaitFor(() => Encoding.ASCII.GetString([.. received]).Contains("FA00014074000;")));
        listener.Stop();
    }

    [Fact]
    public async Task UnreachableHost_DoesNotThrow_StaysDisconnected()
    {
        // Port 1 on loopback: nothing listens, connect fails fast.
        await using var transport = new NetworkCatTransport("127.0.0.1", 1);
        transport.Open();

        await Task.Delay(300);

        Assert.False(transport.IsConnected);
        // Sending while disconnected must be a no-op, not a throw.
        await transport.SendAsync(Encoding.ASCII.GetBytes("FA;"));
    }

    [Fact]
    public async Task ConnectedEvent_FiresOnConnect()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var connectedCount = 0;
        await using var transport = new NetworkCatTransport("127.0.0.1", port);
        transport.Connected += () => Interlocked.Increment(ref connectedCount);

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await Task.Delay(200);
        });

        transport.Open();

        Assert.Equal("ok", await WaitFor(() => connectedCount >= 1));
        listener.Stop();
    }
}
