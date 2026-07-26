using MultiCat.Core.Flex;

namespace MultiCat.Core.Tests.Flex;

/// <summary>
/// Byte-for-byte cross-check against the reference implementation that has been
/// validated against real 4O3A PowerGenius/TunerGenius/AntennaGenius hardware.
/// Vectors were generated from it directly, so a change that alters the wire
/// format fails here rather than on the air.
/// </summary>
public class ReferencePacketTests
{
    private const string Payload =
        "model=FLEX-8600 serial=8600-0000-0000-1234 ip=10.0.1.50 port=4992";

    [Theory]
    [InlineData(0, true,
        "3b8000180000080000001c2d534cffff0000000000000000000000006d6f64656c3d464c45582d38363030" +
        "2073657269616c3d383630302d303030302d303030302d313233342069703d31302e302e312e353020706f" +
        "72743d34393932000000")]
    [InlineData(0, false,
        "380000150000080000001c2d534cffff6d6f64656c3d464c45582d383630302073657269616c3d383630302d" +
        "303030302d303030302d313233342069703d31302e302e312e353020706f72743d34393932000000")]
    [InlineData(5, true,
        "3b8500180000080000001c2d534cffff0000000000000000000000006d6f64656c3d464c45582d38363030" +
        "2073657269616c3d383630302d303030302d303030302d313233342069703d31302e302e312e353020706f" +
        "72743d34393932000000")]
    [InlineData(5, false,
        "380500150000080000001c2d534cffff6d6f64656c3d464c45582d383630302073657269616c3d383630302d" +
        "303030302d303030302d313233342069703d31302e302e312e353020706f72743d34393932000000")]
    public void MatchesTheReferenceImplementationExactly(int packetCount, bool includeTimestamp, string expectedHex)
    {
        var packet = Vita49.BuildDiscoveryPacket(Payload, packetCount, includeTimestamp);

        Assert.Equal(expectedHex, Convert.ToHexString(packet).ToLowerInvariant());
    }
}
