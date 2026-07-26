using MultiCat.Core;

namespace MultiCat.Core.Tests;

public class ModeNamesTests
{
    [Theory]
    [InlineData("PKTUSB", "DATA")]
    [InlineData("PKTLSB", "DATA-R")]
    [InlineData("CWR", "CW-R")]
    [InlineData("RTTYR", "RTTY-R")]
    [InlineData("FMN", "FM-N")]
    [InlineData("USB", "USB")]
    [InlineData("LSB", "LSB")]
    public void HamlibNames_MapToHamDisplayNames(string hamlib, string expected) =>
        Assert.Equal(expected, ModeNames.ToDisplay(hamlib));

    [Theory]
    [InlineData("DATA")]
    [InlineData("DATA-R")]
    [InlineData("CW-R")]
    public void TrackerNames_AreAlreadyCanonical(string tracker) =>
        Assert.Equal(tracker, ModeNames.ToDisplay(tracker));

    [Fact]
    public void CaseAndWhitespace_AreTolerated()
    {
        Assert.Equal("DATA", ModeNames.ToDisplay(" pktusb "));
        Assert.Equal("CW-R", ModeNames.ToDisplay("cwr"));
    }

    [Fact]
    public void UnknownModes_PassThroughUnchanged() =>
        Assert.Equal("D-STAR", ModeNames.ToDisplay("D-STAR"));
}
