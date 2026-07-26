using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MultiCat.Core.Flex;
using MultiCat.Service.Flex;

namespace MultiCat.Service.Tests;

public class FlexDiscoveryBroadcasterTests
{
    private static readonly FlexIdentity Identity = new()
    {
        Serial = "8600-0000-0000-4242",
        AdvertiseIp = "127.0.0.1",
        Nickname = "Test Radio",
    };

    private static FlexDiscoveryBroadcaster Create(
        FlexDiscoveryOptions options, Func<IEnumerable<string>>? peers = null) =>
        new(Identity, options, NullLogger.Instance, peers);

    [Fact]
    public void WithNoUnicastTargets_SendsToTheBroadcastAddress()
    {
        var broadcaster = Create(new FlexDiscoveryOptions { BroadcastAddress = "10.0.1.255" });

        Assert.Equal(["10.0.1.255"], broadcaster.Targets());
    }

    [Fact]
    public void WithUnicastTargets_TheBroadcastAddressIsNotUsed()
    {
        var broadcaster = Create(new FlexDiscoveryOptions
        {
            BroadcastAddress = "10.0.1.255",
            UnicastTargets = ["10.0.1.20", "10.0.1.21"],
        });

        Assert.Equal(["10.0.1.20", "10.0.1.21"], broadcaster.Targets());
    }

    [Fact]
    public void UnicastTargets_AreAugmentedWithConnectedPeers_SoAStaleListStillReaches()
    {
        var broadcaster = Create(
            new FlexDiscoveryOptions { UnicastTargets = ["10.0.1.20"] },
            peers: () => ["10.0.1.99"]);

        Assert.Equal(["10.0.1.20", "10.0.1.99"], broadcaster.Targets());
    }

    [Fact]
    public void AConnectedPeerAlreadyConfigured_IsNotDuplicated()
    {
        var broadcaster = Create(
            new FlexDiscoveryOptions { UnicastTargets = ["10.0.1.20"] },
            peers: () => ["10.0.1.20", "  "]);

        Assert.Equal(["10.0.1.20"], broadcaster.Targets());
    }

    [Fact]
    public void ConnectedPeers_AreIgnoredWhileBroadcasting()
    {
        // Broadcast already reaches everyone; adding peers would send duplicates.
        var broadcaster = Create(
            new FlexDiscoveryOptions { BroadcastAddress = "10.0.1.255" },
            peers: () => ["10.0.1.99"]);

        Assert.Equal(["10.0.1.255"], broadcaster.Targets());
    }

    [Fact]
    public async Task SendsARealDiscoveryPacketAClientCanParse()
    {
        // Unicast to loopback only: exercises the whole send path without putting a
        // radio advertisement on the real network.
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        await using var broadcaster = Create(new FlexDiscoveryOptions
        {
            DiscoveryPort = port,
            IntervalSeconds = 0.05,
            UnicastTargets = ["127.0.0.1"],
        });
        broadcaster.Start();

        var receive = await listener.ReceiveAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        var packet = receive.Buffer;

        Assert.True(packet.Length > 28, "expected a VITA header plus payload");
        Assert.Equal(0, packet.Length % 4);

        var card = Encoding.ASCII.GetString(packet, 28, packet.Length - 28).TrimEnd('\0');
        Assert.Contains("serial=8600-0000-0000-4242", card);
        Assert.Contains("nickname=Test_Radio", card);
        Assert.Contains("port=4992", card);
    }

    [Fact]
    public async Task KeepsAnnouncing_SoALateListenerStillFindsTheRadio()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        await using var broadcaster = Create(new FlexDiscoveryOptions
        {
            DiscoveryPort = port,
            IntervalSeconds = 0.05,
            UnicastTargets = ["127.0.0.1"],
        });
        broadcaster.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await listener.ReceiveAsync(cts.Token);
        await listener.ReceiveAsync(cts.Token);   // a second announcement follows

        Assert.True(broadcaster.PacketsSent >= 2);
    }

    [Fact]
    public async Task StopsSendingOnceDisposed()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        var broadcaster = Create(new FlexDiscoveryOptions
        {
            DiscoveryPort = port,
            IntervalSeconds = 0.05,
            UnicastTargets = ["127.0.0.1"],
        });
        broadcaster.Start();
        await listener.ReceiveAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        await broadcaster.DisposeAsync();

        var sent = broadcaster.PacketsSent;
        await Task.Delay(200);

        Assert.Equal(sent, broadcaster.PacketsSent);
    }

    [Fact]
    public void DetectLocalIp_ReturnsARoutableAddress()
    {
        var ip = FlexDiscoveryBroadcaster.DetectLocalIp();

        Assert.NotNull(ip);
        Assert.True(IPAddress.TryParse(ip, out var parsed));
        Assert.Equal(AddressFamily.InterNetwork, parsed!.AddressFamily);
    }
}
