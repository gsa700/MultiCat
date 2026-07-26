using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MultiCat.Service.Rigctld;

/// <summary>
/// Transparent TCP relay in front of a supervised rigctld: clients connect to the
/// public port, rigctld listens on an internal one, and every byte passes through
/// MultiCAT — so client commands can be attributed, visualized, and logged without
/// altering the protocol. Also the source of truth for who is connected.
/// </summary>
public sealed class RigctldRelay : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly int _publicPort;
    private readonly int _upstreamPort;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly ConcurrentDictionary<int, (int Pid, string Process)> _connections = new();

    public RigctldRelay(int publicPort, int upstreamPort, ILogger logger)
    {
        _publicPort = publicPort;
        _upstreamPort = upstreamPort;
        _logger = logger;
        _listener = new TcpListener(IPAddress.Loopback, publicPort);
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>(clientId "process#connId", text, isCommand). Commands are single
    /// protocol lines; responses are coalesced per read and carry empty text.</summary>
    public event Action<string, string, bool>? Traffic;

    public int ConnectionCount => _connections.Count;

    public IReadOnlyList<(int Pid, string Process, int ConnectionId)> Connections =>
        [.. _connections.Select(kv => (kv.Value.Pid, kv.Value.Process, kv.Key))];

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => HandleAsync(client));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        var connId = ((IPEndPoint)client.Client.RemoteEndPoint!).Port;
        var (pid, process) = TcpConnections.OwnerOfClientPort(connId, _publicPort);
        var clientId = $"{process}#{connId}";
        _connections[connId] = (pid, process);
        _logger.LogInformation("relay {Port}: {Client} connected", _publicPort, clientId);

        try
        {
            using var upstream = new TcpClient();
            await upstream.ConnectAsync(IPAddress.Loopback, _upstreamPort, _cts.Token);
            var clientStream = client.GetStream();
            var upstreamStream = upstream.GetStream();

            var toRadio = PumpAsync(clientStream, upstreamStream, line => Traffic?.Invoke(clientId, line, true), parseLines: true);
            var toClient = PumpAsync(upstreamStream, clientStream, _ => Traffic?.Invoke(clientId, string.Empty, false), parseLines: false);
            await Task.WhenAny(toRadio, toClient);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug("relay {Port}: {Client} error: {Message}", _publicPort, clientId, ex.Message);
        }
        finally
        {
            _connections.TryRemove(connId, out _);
            client.Dispose();
            _logger.LogInformation("relay {Port}: {Client} disconnected", _publicPort, clientId);
        }
    }

    // Forwards bytes one direction. For the client→rigctld direction each protocol
    // line is reported (commands are short, newline-terminated); for the return
    // direction one coalesced notification per read is enough for a pulse.
    private async Task PumpAsync(NetworkStream from, NetworkStream to, Action<string> report, bool parseLines)
    {
        var buffer = new byte[4096];
        var lineBuf = new StringBuilder();
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer, _cts.Token)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read), _cts.Token);
                await to.FlushAsync(_cts.Token);

                if (!parseLines)
                {
                    report(string.Empty);
                    continue;
                }

                foreach (var ch in Encoding.ASCII.GetString(buffer, 0, read))
                {
                    if (ch == '\n')
                    {
                        var line = lineBuf.ToString().Trim();
                        lineBuf.Clear();
                        if (line.Length > 0)
                        {
                            report(line);
                        }
                    }
                    else if (lineBuf.Length < 200)
                    {
                        lineBuf.Append(ch);
                    }
                }
            }
        }
        catch (Exception)
        {
            // Connection torn down; HandleAsync cleans up.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try
        {
            await _acceptLoop;
        }
        catch (Exception)
        {
        }

        _cts.Dispose();
    }
}
