using System.Net;
using System.Net.Sockets;
using MultiCat.Core;
using MultiCat.Core.Framing;
using MultiCat.Service.Transports;

namespace MultiCat.Service.Sessions;

/// <summary>
/// Raw CAT over TCP on localhost: each connection becomes a client endpoint that
/// speaks the radio's native protocol, arbitrated like any other client.
/// </summary>
public sealed class TcpRawListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly string _label;
    private readonly Func<ICatFramer> _framerFactory;
    private readonly RadioSession _session;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private int _connectionCount;
    private int _nextConnectionId;

    public TcpRawListener(string label, int port, Func<ICatFramer> framerFactory, RadioSession session)
    {
        _label = label;
        _framerFactory = framerFactory;
        _session = session;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int ConnectionCount => _connectionCount;

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => HandleConnectionAsync(client));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        var clientId = $"{_label}#{Interlocked.Increment(ref _nextConnectionId)}";
        Interlocked.Increment(ref _connectionCount);

        var transport = new StreamTransport(client.GetStream());
        var endpoint = new ClientPortEndpoint(clientId, transport, _framerFactory(), _session.Arbiter);
        _session.RegisterEndpoint(endpoint);
        try
        {
            await transport.Closed;
        }
        finally
        {
            _session.UnregisterEndpoint(endpoint);
            await endpoint.DisposeAsync();
            client.Dispose();
            Interlocked.Decrement(ref _connectionCount);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
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
