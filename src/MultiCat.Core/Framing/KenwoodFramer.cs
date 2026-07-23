namespace MultiCat.Core.Framing;

/// <summary>
/// Framer for the Kenwood/Elecraft ASCII protocol: frames are terminated by ';'.
/// </summary>
public sealed class KenwoodFramer : ICatFramer
{
    private const int MaxFrameLength = 256;
    private readonly List<byte> _buffer = [];
    private bool _resyncing;

    public IReadOnlyList<CatFrame> Push(ReadOnlySpan<byte> data)
    {
        List<CatFrame>? frames = null;
        foreach (var b in data)
        {
            if (_resyncing)
            {
                // Framing was lost to an overflow; skip to the next terminator.
                if (b == (byte)';')
                {
                    _resyncing = false;
                }

                continue;
            }

            _buffer.Add(b);
            if (b == (byte)';')
            {
                (frames ??= []).Add(new CatFrame(_buffer.ToArray()));
                _buffer.Clear();
            }
            else if (_buffer.Count >= MaxFrameLength)
            {
                _buffer.Clear();
                _resyncing = true;
            }
        }

        return frames ?? (IReadOnlyList<CatFrame>)[];
    }

    public void Reset()
    {
        _buffer.Clear();
        _resyncing = false;
    }
}
