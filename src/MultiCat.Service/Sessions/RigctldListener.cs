using System.Net;
using System.Net.Sockets;
using MultiCat.Core.Rigctld;

namespace MultiCat.Service.Sessions;

/// <summary>
/// Serves the hamlib rigctld network protocol on localhost. Each connection gets
/// its own translator; all radio access flows through the session's arbiter, so
/// rigctld clients coexist with every other kind of client port.
/// </summary>
public sealed class RigctldListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly string _label;
    private readonly RadioSession _session;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private int _connectionCount;
    private int _nextConnectionId;

    public RigctldListener(string label, int port, RadioSession session)
    {
        _label = label;
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
        var translator = new RigctldTranslator(_session.Arbiter, clientId);

        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
            await using var writer = new StreamWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true) { AutoFlush = true };

            while (!_cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_cts.Token);
                if (line is null)
                {
                    return;
                }

                var reply = await translator.HandleLineAsync(line, _cts.Token);
                if (reply is null)
                {
                    return;
                }

                await writer.WriteAsync(reply);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
        finally
        {
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
