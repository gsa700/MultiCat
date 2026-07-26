using System.Buffers.Binary;
using System.Text;

namespace MultiCat.Core.Flex;

/// <summary>
/// Minimal VITA-49 framing for FlexRadio discovery packets.
/// <para>
/// A FlexRadio announces itself with a VITA-49 "extension data with stream"
/// packet whose class ID carries the FlexRadio OUI and the discovery packet
/// class. Peripherals such as the 4O3A PowerGenius XL listen for these and match
/// the <c>serial=</c> token against their paired radio before connecting to the
/// TCP command port. Only discovery needs VITA framing — the command/status
/// channel is plain ASCII.
/// </para>
/// Constants verified against flexlib-go/vita and the SmartSDR API docs, and
/// carried over from a working implementation proven against real 4O3A gear.
/// </summary>
public static class Vita49
{
    /// <summary>Discovery carries a stream id, so it is "extension data with stream".</summary>
    private const uint PacketTypeExtDataWithStream = 0x3;

    private const uint FlexOui = 0x001C2D;              // FlexRadio Systems (00:1C:2D)
    private const uint DiscoveryStreamId = 0x00000800;
    private const uint DiscoveryInfoClass = 0x534C;     // ASCII "SL"
    private const uint DiscoveryPacketClass = 0xFFFF;   // SL_VITA_DISCOVERY_CLASS

    private const uint TsiNone = 0, TsiOther = 3;
    private const uint TsfNone = 0, TsfReal = 2;

    /// <summary>
    /// Wraps a space-separated ASCII <c>key=value</c> payload in a discovery packet.
    /// Spaces inside a value (the nickname) must already be encoded as underscores.
    /// With <paramref name="includeTimestamp"/> the header is the 28 bytes a real
    /// radio emits; without it, 16 — the TSI/TSF bits and packet size follow suit.
    /// </summary>
    public static byte[] BuildDiscoveryPacket(string payload, int packetCount = 0, bool includeTimestamp = true)
    {
        var body = Encoding.ASCII.GetBytes(payload);
        var padding = body.Length % 4 == 0 ? 0 : 4 - (body.Length % 4);

        var tsi = includeTimestamp ? TsiOther : TsiNone;
        var tsf = includeTimestamp ? TsfReal : TsfNone;

        // header + stream id + two class-id words, plus 3 words of timestamp.
        var headerWords = includeTimestamp ? 7 : 4;
        var totalWords = headerWords + ((body.Length + padding) / 4);

        var header =
            ((PacketTypeExtDataWithStream & 0xF) << 28)
            | (1u << 27)                        // C: class id present
            | (0u << 26)                        // T: no trailer
            | ((tsi & 0x3) << 24)
            | ((tsf & 0x3) << 22)
            | (((uint)packetCount & 0xF) << 16)
            | ((uint)totalWords & 0xFFFF);

        var packet = new byte[(headerWords * 4) + body.Length + padding];
        var span = packet.AsSpan();

        BinaryPrimitives.WriteUInt32BigEndian(span[..4], header);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..8], DiscoveryStreamId);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..12], FlexOui & 0x00FFFFFF);
        BinaryPrimitives.WriteUInt32BigEndian(span[12..16], (DiscoveryInfoClass << 16) | DiscoveryPacketClass);

        var offset = 16;
        if (includeTimestamp)
        {
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(offset, 4), 0);      // integer seconds
            BinaryPrimitives.WriteUInt64BigEndian(span.Slice(offset + 4, 8), 0);  // fractional
            offset += 12;
        }

        body.CopyTo(span[offset..]);            // trailing pad bytes stay zero
        return packet;
    }
}
