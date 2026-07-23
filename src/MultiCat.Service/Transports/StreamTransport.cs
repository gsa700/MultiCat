using MultiCat.Core;

namespace MultiCat.Service.Transports;

/// <summary>ICatTransport over any duplex Stream (TCP socket, named pipe).</summary>
public sealed class StreamTransport : ICatTransport
{
    private readonly Stream _stream;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readLoop;

    public StreamTransport(Stream stream)
    {
        _stream = stream;
        _readLoop = Task.Run(ReadLoopAsync);
    }

    public event Action<byte[]>? DataReceived;

    /// <summary>Completes when the remote side closes the stream.</summary>
    public Task Closed => _readLoop;

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        await _stream.WriteAsync(data, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[4096];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var read = await _stream.ReadAsync(buffer, _cts.Token);
                if (read == 0)
                {
                    return;
                }

                DataReceived?.Invoke(buffer[..read]);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await _stream.DisposeAsync();
        try
        {
            await _readLoop;
        }
        catch (Exception)
        {
        }

        _cts.Dispose();
    }
}
