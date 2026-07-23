namespace MultiCat.Core;

/// <summary>Byte-level connection to the radio: a serial port, TCP socket, or a simulator.</summary>
public interface ICatTransport : IAsyncDisposable
{
    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    event Action<byte[]>? DataReceived;
}
