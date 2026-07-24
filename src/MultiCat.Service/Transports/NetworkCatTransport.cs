using System.Net.Sockets;
using MultiCat.Core;

namespace MultiCat.Service.Transports;

/// <summary>
/// CAT over a TCP socket for networked radios (Elecraft K4 on port 9200, remote
/// rigs, rigctld chaining). Maintains the connection in the background: if the rig
/// is off or unreachable at startup, or the link drops, it keeps retrying so the
/// radio comes online on its own once reachable. Sends while disconnected are
/// dropped (the pending command simply times out).
/// </summary>
public sealed class NetworkCatTransport(string host, int port) : ICatTransport
{
    // Bound each connect: a powered-off host makes the OS retransmit SYN for ~2 min
    // otherwise, stalling recovery (learned in virtual-flex against a real K4D).
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _cts = new();
    private volatile TcpClient? _client;
    private volatile NetworkStream? _stream;
    private Task? _maintain;

    public event Action<byte[]>? DataReceived;

    /// <summary>Fired after each successful (re)connect, so the session can re-arm push mode.</summary>
    public event Action? Connected;

    public bool IsConnected => _stream is not null;

    public void Open() => _maintain ??= Task.Run(MaintainAsync);

    private async Task MaintainAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var client = new TcpClient { NoDelay = true };
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                connectCts.CancelAfter(ConnectTimeout);
                await client.ConnectAsync(host, port, connectCts.Token);

                _client = client;
                _stream = client.GetStream();
                Connected?.Invoke();
                await ReadLoopAsync(_stream);
            }
            catch (Exception) when (!_cts.IsCancellationRequested)
            {
                // Unreachable or dropped; fall through to retry.
            }
            finally
            {
                _stream = null;
                _client?.Dispose();
                _client = null;
            }

            if (!_cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(RetryDelay, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    private async Task ReadLoopAsync(NetworkStream stream)
    {
        var buffer = new byte[4096];
        while (!_cts.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer, _cts.Token);
            if (read == 0)
            {
                return; // remote closed
            }

            DataReceived?.Invoke(buffer[..read]);
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var stream = _stream;
        if (stream is null)
        {
            return; // not connected; let the transaction time out
        }

        try
        {
            await stream.WriteAsync(data, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Link dropped mid-write; the maintain loop will reconnect.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _client?.Dispose();
        if (_maintain is not null)
        {
            try
            {
                await _maintain;
            }
            catch (Exception)
            {
            }
        }

        _cts.Dispose();
    }
}
