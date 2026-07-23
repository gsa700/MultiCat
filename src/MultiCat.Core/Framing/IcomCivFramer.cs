namespace MultiCat.Core.Framing;

/// <summary>
/// Framer for the Icom CI-V binary protocol: frames run from the 0xFE 0xFE preamble
/// to the 0xFD terminator. 0xFD cannot occur in frame data, so a simple scan is safe.
/// </summary>
public sealed class IcomCivFramer : ICatFramer
{
    private const byte Preamble = 0xFE;
    private const byte Terminator = 0xFD;
    private const int MaxFrameLength = 64;
    private readonly List<byte> _buffer = [];

    public IReadOnlyList<CatFrame> Push(ReadOnlySpan<byte> data)
    {
        List<CatFrame>? frames = null;
        foreach (var b in data)
        {
            if (_buffer.Count == 0 && b != Preamble)
            {
                continue;
            }

            _buffer.Add(b);
            if (b == Terminator)
            {
                (frames ??= []).Add(new CatFrame(_buffer.ToArray()));
                _buffer.Clear();
            }
            else if (_buffer.Count >= MaxFrameLength)
            {
                _buffer.Clear();
            }
        }

        return frames ?? (IReadOnlyList<CatFrame>)[];
    }

    public void Reset() => _buffer.Clear();
}
