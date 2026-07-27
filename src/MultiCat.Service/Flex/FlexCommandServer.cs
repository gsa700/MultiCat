using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using MultiCat.Core.Flex;

namespace MultiCat.Service.Flex;

/// <summary>
/// Hosts the FlexRadio command/status port. Each 4O3A box connects here after it
/// has discovered the radio, and is served by a <see cref="FlexSession"/>.
/// <para>
/// Radio state changes arrive on the radio's own thread while several boxes may be
/// connected, so each connection owns an outbound queue and a single writer drains
/// it — no connection is ever written to from two threads at once.
/// </para>
/// </summary>
public sealed class FlexCommandServer : IAsyncDisposable
{
    private readonly FlexRadioState _radio;
    private readonly int _port;
    private readonly ILogger _logger;
    private readonly List<Connection> _clients = [];
    private readonly Lock _clientGate = new();
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;

    public FlexCommandServer(FlexRadioState radio, int port, ILogger logger)
    {
        _radio = radio;
        _port = port;
        _logger = logger;
        _radio.SliceLineReady += OnSliceLine;
        _radio.BroadcastLineReady += OnBroadcastLine;

        // A box recognises a key edge as its own by finding its connection handle
        // here, so this is read live from the connected clients — a box that drops
        // and reconnects must not leave its old handle behind.
        _radio.EngagedAmplifierHandles = () =>
        {
            lock (_clientGate)
            {
                return [.. _clients
                    .Where(c => c.Session.IsAmplifier)
                    .Select(c => $"0x{c.Session.Handle:X8}")];
            }
        };
    }

    /// <summary>The port actually bound — useful when the caller asked for 0.</summary>
    public int BoundPort { get; private set; }

    /// <summary>
    /// Whether connections are served. Cleared while the radio is absent: a box that
    /// remembers the address would otherwise reconnect and sit on a radio that has
    /// nothing to report, instead of falling back to its no-transceiver antenna.
    /// </summary>
    public bool Accepting { get; set; } = true;

    public int ClientCount
    {
        get
        {
            lock (_clientGate)
            {
                return _clients.Count;
            }
        }
    }

    /// <summary>One connected Genius box, for the signal-flow display.</summary>
    public readonly record struct ConnectedBox(string Name, string Address, int ConnectionId);

    /// <summary>
    /// The boxes currently connected, named by the model each reported when it
    /// registered. A box that has connected but not yet registered is still listed —
    /// it is on the wire, and hiding it would make the display lag reality.
    /// </summary>
    public IReadOnlyList<ConnectedBox> ConnectedBoxes()
    {
        lock (_clientGate)
        {
            return [.. _clients.Select(c => new ConnectedBox(
                c.Session.FriendlyName ?? c.PeerAddress ?? "box",
                c.PeerAddress ?? string.Empty,
                (int)c.Session.Handle))];
        }
    }

    /// <summary>Addresses of connected boxes, so discovery can keep reaching a live
    /// box even when its configured address has gone stale.</summary>
    public IEnumerable<string> ConnectedPeers()
    {
        lock (_clientGate)
        {
            return [.. _clients.Select(c => c.PeerAddress).Where(a => a is not null).Select(a => a!)];
        }
    }

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _logger.LogInformation("Flex command port listening on {Port}", BoundPort);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tcp = await _listener!.AcceptTcpClientAsync(ct);
                if (!Accepting)
                {
                    tcp.Close();
                    tcp.Dispose();
                    continue;
                }

                _ = Task.Run(() => ServeAsync(tcp, ct), CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Flex accept loop ended: {Message}", ex.Message);
        }
    }

    private async Task ServeAsync(TcpClient tcp, CancellationToken ct)
    {
        var connection = new Connection(tcp, new FlexSession(_radio));
        lock (_clientGate)
        {
            _clients.Add(connection);
        }

        _logger.LogInformation(
            "Flex client connected: {Peer} (handle 0x{Handle:X8})", connection.PeerAddress, connection.Session.Handle);

        try
        {
            // The radio speaks first; the box waits for this before issuing commands.
            foreach (var line in connection.Session.Greeting())
            {
                connection.Enqueue(line);
            }

            await Task.WhenAny(connection.WriteLoopAsync(ct), ReadLoopAsync(connection, ct));
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Flex client {Peer} ended: {Message}", connection.PeerAddress, ex.Message);
        }
        finally
        {
            lock (_clientGate)
            {
                _clients.Remove(connection);
            }

            connection.Dispose();
            _logger.LogInformation("Flex client disconnected: {Peer}", connection.PeerAddress);
        }
    }

    private async Task ReadLoopAsync(Connection connection, CancellationToken ct)
    {
        var stream = connection.Tcp.GetStream();
        var buffer = new byte[4096];
        var pending = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
            {
                return;     // peer closed
            }

            pending.Append(Encoding.ASCII.GetString(buffer, 0, read));

            // Commands are CR-terminated; tolerate CR, LF or both.
            var text = pending.ToString();
            var lines = text.Split('\r', '\n');
            pending.Clear();
            pending.Append(lines[^1]);      // trailing partial line

            for (var i = 0; i < lines.Length - 1; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                foreach (var reply in connection.Session.Receive(line))
                {
                    connection.Enqueue(reply);
                }
            }
        }
    }

    /// <summary>Raised with a box's name each time something is sent to it, so the
    /// signal-flow display can show the Genius links carrying traffic rather than
    /// sitting inert.</summary>
    public event Action<string>? BoxTraffic;

    // Slice traffic only reaches clients that asked for it...
    private void OnSliceLine(string line)
    {
        List<string> served = [];
        lock (_clientGate)
        {
            foreach (var client in _clients)
            {
                if (client.Session.IsSubscribedTo("slice"))
                {
                    client.Enqueue(line);
                    served.Add(client.Session.FriendlyName ?? client.PeerAddress ?? "box");
                }
            }
        }

        foreach (var name in served)
        {
            BoxTraffic?.Invoke(name);
        }
    }

    // ...whereas the interlock goes to everyone: a box must never miss a key edge.
    private void OnBroadcastLine(string line)
    {
        lock (_clientGate)
        {
            foreach (var client in _clients)
            {
                client.Enqueue(line);
            }
        }
    }

    /// <summary>
    /// Closes every stack connection and forgets its registrations. Used when the
    /// radio goes absent: the stack sees it vanish as a real Flex powering off would,
    /// and each box reverts to its no-transceiver antenna rather than keying into a
    /// stale band.
    /// </summary>
    public void DropAllClients()
    {
        List<Connection> clients;
        lock (_clientGate)
        {
            clients = [.. _clients];
            _clients.Clear();
        }

        foreach (var client in clients)
        {
            client.Dispose();
        }

        _radio.Reset();
        if (clients.Count > 0)
        {
            _logger.LogInformation("Flex: dropped {Count} stack connection(s); radio is absent", clients.Count);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _radio.SliceLineReady -= OnSliceLine;
        _radio.BroadcastLineReady -= OnBroadcastLine;
        await _cts.CancelAsync();
        _listener?.Stop();
        DropAllClients();

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop;
            }
            catch (Exception)
            {
            }
        }

        _cts.Dispose();
    }

    private sealed class Connection(TcpClient tcp, FlexSession session) : IDisposable
    {
        private readonly Channel<string> _outbound =
            Channel.CreateBounded<string>(new BoundedChannelOptions(512)
            {
                // Under a burst the newest state matters; stale lines may be dropped.
                FullMode = BoundedChannelFullMode.DropOldest,
            });

        public TcpClient Tcp { get; } = tcp;

        public FlexSession Session { get; } = session;

        public string? PeerAddress { get; } = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString();

        public void Enqueue(string line) => _outbound.Writer.TryWrite(line);

        public async Task WriteLoopAsync(CancellationToken ct)
        {
            var stream = Tcp.GetStream();
            await foreach (var line in _outbound.Reader.ReadAllAsync(ct))
            {
                var bytes = Encoding.ASCII.GetBytes(line + "\n");
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
            }
        }

        public void Dispose()
        {
            _outbound.Writer.TryComplete();
            try
            {
                Tcp.Close();
            }
            catch (Exception)
            {
            }

            Tcp.Dispose();
        }
    }
}
