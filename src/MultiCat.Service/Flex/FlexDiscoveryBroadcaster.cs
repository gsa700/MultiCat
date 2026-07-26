using System.Net;
using System.Net.Sockets;
using MultiCat.Core.Flex;

namespace MultiCat.Service.Flex;

public sealed record FlexDiscoveryOptions
{
    /// <summary>Subnet-directed broadcast address; the host must share the stack's subnet.</summary>
    public string BroadcastAddress { get; init; } = "255.255.255.255";

    public int DiscoveryPort { get; init; } = 4992;

    public double IntervalSeconds { get; init; } = 1.0;

    /// <summary>
    /// When non-empty, discovery is unicast ONLY to these addresses and the virtual
    /// radio disappears from every other picker on the LAN. Pair new boxes in
    /// broadcast mode first, then pin their addresses here.
    /// </summary>
    public IReadOnlyList<string> UnicastTargets { get; init; } = [];
}

/// <summary>
/// Announces the virtual radio so a 4O3A Genius stack can find it and match its
/// serial. The stack will not open a command connection to a radio it has not
/// discovered, so this runs for as long as the radio is meant to be visible.
/// </summary>
public sealed class FlexDiscoveryBroadcaster(
    FlexIdentity identity,
    FlexDiscoveryOptions options,
    ILogger logger,
    Func<IEnumerable<string>>? connectedPeers = null) : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private int _packetCount;

    /// <summary>Number of discovery packets sent, for the GUI and for tests.</summary>
    public long PacketsSent { get; private set; }

    /// <summary>
    /// Where this cycle's packet goes. Unicast mode augments the configured list
    /// with currently-connected clients, so a live box keeps being refreshed even
    /// if the configured list has gone stale.
    /// </summary>
    public IReadOnlyList<string> Targets()
    {
        if (options.UnicastTargets.Count == 0)
        {
            return [options.BroadcastAddress];
        }

        var addresses = new SortedSet<string>(options.UnicastTargets, StringComparer.OrdinalIgnoreCase);
        foreach (var peer in connectedPeers?.Invoke() ?? [])
        {
            if (!string.IsNullOrWhiteSpace(peer))
            {
                addresses.Add(peer);
            }
        }

        return [.. addresses];
    }

    public void Start() => _loop = Task.Run(() => RunAsync(_cts.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.EnableBroadcast = true;

        logger.LogInformation(
            options.UnicastTargets.Count > 0
                ? "Flex discovery: unicasting to {Targets} :{Port} every {Interval}s (serial {Serial}) — invisible to other pickers"
                : "Flex discovery: broadcasting to {Targets}:{Port} every {Interval}s (serial {Serial})",
            string.Join(",", options.UnicastTargets.Count > 0 ? options.UnicastTargets : [options.BroadcastAddress]),
            options.DiscoveryPort,
            options.IntervalSeconds,
            identity.Serial);

        var payload = DiscoveryPayload.Build(identity);
        while (!ct.IsCancellationRequested)
        {
            var packet = Vita49.BuildDiscoveryPacket(payload, _packetCount);
            foreach (var target in Targets())
            {
                try
                {
                    var endpoint = new IPEndPoint(IPAddress.Parse(target), options.DiscoveryPort);
                    await socket.SendToAsync(packet, SocketFlags.None, endpoint, ct);
                    PacketsSent++;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Flex discovery send to {Target} failed: {Message}", target, ex.Message);
                }
            }

            _packetCount = (_packetCount + 1) & 0xF;   // the header carries four bits
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.IntervalSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// This host's LAN address, as advertised to the stack. Picking a route to the
    /// target tells us which local interface the stack will actually see us on,
    /// which a plain hostname lookup on a multi-homed machine does not. No traffic
    /// is sent — a datagram socket only records the route.
    /// </summary>
    public static string? DetectLocalIp(string towards = "8.8.8.8")
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(towards, 65530);
            return (probe.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (Exception)
            {
            }
        }

        _cts.Dispose();
    }
}
