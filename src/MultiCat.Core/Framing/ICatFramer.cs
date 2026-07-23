namespace MultiCat.Core.Framing;

/// <summary>
/// Reassembles a byte stream into complete CAT frames. Stateful: partial frames are
/// buffered across calls. One framer instance per stream direction.
/// </summary>
public interface ICatFramer
{
    /// <summary>Feed received bytes; returns any frames completed by this chunk, in order.</summary>
    IReadOnlyList<CatFrame> Push(ReadOnlySpan<byte> data);

    void Reset();
}
