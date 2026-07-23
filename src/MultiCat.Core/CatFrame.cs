using System.Text;

namespace MultiCat.Core;

/// <summary>A single complete CAT protocol frame (command or response), terminator included.</summary>
public readonly record struct CatFrame(ReadOnlyMemory<byte> Data)
{
    public static CatFrame FromAscii(string text) => new(Encoding.ASCII.GetBytes(text));

    public static CatFrame FromBytes(params byte[] bytes) => new(bytes);

    public bool IsEmpty => Data.IsEmpty;

    public int Length => Data.Length;

    public string ToAscii() => Encoding.ASCII.GetString(Data.Span);

    public string ToHex() => Convert.ToHexString(Data.Span);

    public string CacheKey => ToHex();

    public bool ContentEquals(CatFrame other) => Data.Span.SequenceEqual(other.Data.Span);
}
