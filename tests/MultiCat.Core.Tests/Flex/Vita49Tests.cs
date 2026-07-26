using System.Buffers.Binary;
using System.Text;
using MultiCat.Core.Flex;

namespace MultiCat.Core.Tests.Flex;

public class Vita49Tests
{
    // "abcd" is already word-aligned, so it exercises the no-padding path.
    private static readonly string Aligned = "abcd";

    [Fact]
    public void Header_CarriesTypeClassIdAndTimestampBits()
    {
        var packet = Vita49.BuildDiscoveryPacket(Aligned);
        var header = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(0, 4));

        Assert.Equal(0x3u, (header >> 28) & 0xF);   // extension data with stream
        Assert.Equal(1u, (header >> 27) & 0x1);     // class id present
        Assert.Equal(0u, (header >> 26) & 0x1);     // no trailer
        Assert.Equal(3u, (header >> 24) & 0x3);     // TSI = other
        Assert.Equal(2u, (header >> 22) & 0x3);     // TSF = real
    }

    [Fact]
    public void Header_CarriesFlexClassIdAndStreamId()
    {
        var packet = Vita49.BuildDiscoveryPacket(Aligned);

        Assert.Equal(0x00000800u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4, 4)));
        Assert.Equal(0x001C2Du, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8, 4)));
        Assert.Equal(0x534CFFFFu, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(12, 4)));
    }

    [Fact]
    public void PacketSizeField_CountsWholePacketInWords()
    {
        var packet = Vita49.BuildDiscoveryPacket(Aligned);
        var header = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(0, 4));

        Assert.Equal((uint)(packet.Length / 4), header & 0xFFFF);
        Assert.Equal(28 + 4, packet.Length); // 28-byte header + "abcd"
    }

    [Theory]
    [InlineData("a", 3)]
    [InlineData("ab", 2)]
    [InlineData("abc", 1)]
    [InlineData("abcd", 0)]
    public void Payload_IsNulPaddedToAWordBoundary(string payload, int expectedPadding)
    {
        var packet = Vita49.BuildDiscoveryPacket(payload);

        Assert.Equal(0, packet.Length % 4);
        Assert.Equal(28 + payload.Length + expectedPadding, packet.Length);
        Assert.Equal(payload, Encoding.ASCII.GetString(packet, 28, payload.Length));
        for (var i = 0; i < expectedPadding; i++)
        {
            Assert.Equal(0, packet[28 + payload.Length + i]);
        }
    }

    [Fact]
    public void PacketCount_LandsInItsNibble_AndWraps()
    {
        var header = (int count) =>
            BinaryPrimitives.ReadUInt32BigEndian(Vita49.BuildDiscoveryPacket(Aligned, count).AsSpan(0, 4));

        Assert.Equal(5u, (header(5) >> 16) & 0xF);
        Assert.Equal(0u, (header(16) >> 16) & 0xF); // only four bits are carried
    }

    [Fact]
    public void WithoutTimestamp_HeaderIs16Bytes_AndTimeBitsClear()
    {
        var packet = Vita49.BuildDiscoveryPacket(Aligned, includeTimestamp: false);
        var header = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(0, 4));

        Assert.Equal(16 + 4, packet.Length);
        Assert.Equal(0u, (header >> 24) & 0x3);
        Assert.Equal(0u, (header >> 22) & 0x3);
        Assert.Equal((uint)(packet.Length / 4), header & 0xFFFF);
    }
}

public class DiscoveryPayloadTests
{
    private static readonly FlexIdentity Identity = new()
    {
        Serial = "8600-0000-0000-1234",
        AdvertiseIp = "10.0.1.50",
        Nickname = "Station One",
        Name = "Multi CAT",
        Callsign = "AB0R",
    };

    [Fact]
    public void CarriesTheFieldsTheStackPairsOn()
    {
        var payload = DiscoveryPayload.Build(Identity);

        Assert.Contains("serial=8600-0000-0000-1234", payload);
        Assert.Contains("model=FLEX-8600", payload);
        Assert.Contains("ip=10.0.1.50", payload);
        Assert.Contains("port=4992", payload);
        Assert.Contains("callsign=AB0R", payload);
    }

    [Fact]
    public void SpacesInNamesBecomeUnderscores()
    {
        var payload = DiscoveryPayload.Build(Identity);

        Assert.Contains("nickname=Station_One", payload);
        Assert.Contains("name=Multi_CAT", payload);
        Assert.DoesNotContain("Station One", payload);
    }

    [Fact]
    public void EveryFieldIsAKeyValueToken_SoTheCardParses()
    {
        var payload = DiscoveryPayload.Build(Identity);
        var tokens = payload.Split(' ');

        Assert.Equal(32, tokens.Length);
        Assert.All(tokens, t => Assert.Contains('=', t));
    }

    [Fact]
    public void DerivedSerial_IsStableAndDistinguishesRadios()
    {
        var a = FlexIdentity.DeriveSerial("K4D");
        var b = FlexIdentity.DeriveSerial("IC-7610");

        Assert.Equal(a, FlexIdentity.DeriveSerial("K4D"));
        Assert.NotEqual(a, b);
        Assert.StartsWith("8600-0000-0000-", a);
    }
}
