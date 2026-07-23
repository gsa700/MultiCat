using System.Threading.Channels;
using MultiCat.Core.Framing;

namespace MultiCat.Core;

/// <summary>
/// One client-facing port: frames the client's byte stream, runs each command
/// through the arbiter, and writes the response (if any) back to that client only.
/// Unsolicited radio traffic is delivered via <see cref="BroadcastAsync"/>.
/// Owns and disposes its client transport.
/// </summary>
public sealed class ClientPortEndpoint : IAsyncDisposable
{
    private readonly ICatTransport _client;
    private readonly ICatFramer _framer;
    private readonly TransactionArbiter _arbiter;
    private readonly Channel<CatFrame> _incoming = Channel.CreateUnbounded<CatFrame>();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processing;
    private readonly Action<byte[]> _onData;

    public ClientPortEndpoint(string clientId, ICatTransport client, ICatFramer framer, TransactionArbiter arbiter)
    {
        ClientId = clientId;
        _client = client;
        _framer = framer;
        _arbiter = arbiter;
        _onData = OnClientData;
        _client.DataReceived += _onData;
        _processing = Task.Run(ProcessAsync);
    }

    public string ClientId { get; }

    private void OnClientData(byte[] data)
    {
        foreach (var frame in _framer.Push(data))
        {
            _incoming.Writer.TryWrite(frame);
        }
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var command in _incoming.Reader.ReadAllAsync(_cts.Token))
            {
                var response = await _arbiter.ExecuteAsync(ClientId, command, _cts.Token);
                if (response is { } frame)
                {
                    await _client.SendAsync(frame.Data, _cts.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Deliver an unsolicited radio frame (auto-information, transceive) to this client.</summary>
    public async ValueTask BroadcastAsync(CatFrame frame)
    {
        try
        {
            await _client.SendAsync(frame.Data, _cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _client.DataReceived -= _onData;
        _incoming.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            await _processing;
        }
        catch (OperationCanceledException)
        {
        }

        await _client.DisposeAsync();
        _cts.Dispose();
    }
}
