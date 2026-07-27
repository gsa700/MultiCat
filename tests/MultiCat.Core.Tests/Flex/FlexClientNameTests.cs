using MultiCat.Core.Flex;

namespace MultiCat.Core.Tests.Flex;

public class FlexClientNameTests
{
    private readonly FlexRadioState _radio = new(new FlexIdentity
    {
        Serial = "8600-0000-0000-1234",
        AdvertiseIp = "10.0.1.50",
        Nickname = "Test Radio",
        Callsign = "AB0R",
    });

    [Fact]
    public void AnAmplifierIsNamedByTheModelItRegisters()
    {
        var session = new FlexSession(_radio);

        session.Receive("C0|amplifier create ip=10.0.1.100 port=9008 model=PowerGeniusXL serial_num=9-360");

        Assert.Equal("PowerGeniusXL", session.FriendlyName);
    }

    [Fact]
    public void AnAntennaSwitchIsNamedFromItsBanner_BecauseItNeverRegistersAsAnAmplifier()
    {
        // The Antenna Genius is not an amplifier, so it never sends "amplifier
        // create" — its greeting is the only thing that identifies it.
        var session = new FlexSession(_radio);

        session.Receive("V4.1.16 AG");

        Assert.Equal("AntennaGenius", session.FriendlyName);
        Assert.False(session.IsAmplifier);
    }

    [Theory]
    [InlineData("V4.1.16 AG", "AntennaGenius")]
    [InlineData("V1.2.3 SomethingElse", "SomethingElse")]
    public void BannerNamesAreRead(string banner, string expected)
    {
        Assert.Equal(expected, FlexSession.NameFromBanner(banner));
    }

    [Theory]
    [InlineData("V4.1.16")]     // version only, no name to take
    [InlineData("V")]
    [InlineData("")]
    [InlineData(null)]
    public void ABannerWithNoNameYieldsNothing(string? banner)
    {
        Assert.Null(FlexSession.NameFromBanner(banner));
    }

    [Fact]
    public void AModelWinsOverABanner()
    {
        var session = new FlexSession(_radio);

        session.Receive("V4.1.16 AG");
        session.Receive("C0|amplifier create model=TunerGeniusXL");

        Assert.Equal("TunerGeniusXL", session.FriendlyName);
    }

    [Fact]
    public void AnUnidentifiedClientHasNoNameOfItsOwn()
    {
        // The caller falls back to the address; it must not invent one here.
        Assert.Null(new FlexSession(_radio).FriendlyName);
    }
}
