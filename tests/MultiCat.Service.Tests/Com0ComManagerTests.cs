using MultiCat.Service.VirtualPorts;

namespace MultiCat.Service.Tests;

public class Com0ComManagerTests
{
    private const string TypicalListOutput = """
               CNCA0 PortName=COM11,EmuBR=yes
               CNCB0 PortName=COM21
               CNCA1 PortName=COM12
               CNCB1 PortName=COM22
        """;

    [Fact]
    public void ParseList_ReadsPairsWithPortNames()
    {
        var pairs = Com0ComManager.ParseList(TypicalListOutput);

        Assert.Equal(2, pairs.Count);
        Assert.Equal(new PortPair(0, "COM11", "COM21"), pairs[0]);
        Assert.Equal(new PortPair(1, "COM12", "COM22"), pairs[1]);
    }

    [Fact]
    public void ParseList_EmptyOutput_YieldsNoPairs()
    {
        Assert.Empty(Com0ComManager.ParseList(string.Empty));
        Assert.Empty(Com0ComManager.ParseList("command> no pairs here"));
    }

    [Fact]
    public void ParseList_HalfPair_StillReported()
    {
        var pairs = Com0ComManager.ParseList("CNCA3 PortName=COM30");
        Assert.Single(pairs);
        Assert.Equal(new PortPair(3, "COM30", null), pairs[0]);
    }

    [Fact]
    public void PickFreePair_SkipsRealAndConfiguredPorts()
    {
        var (app, mux) = Com0ComManager.PickFreePair(["COM1", "COM7", "COM11", "COM21", "COM22"]);

        // COM11 taken; COM12 free but its mux side COM22 is taken; next candidate wins.
        Assert.Equal("COM13", app);
        Assert.Equal("COM23", mux);
    }

    [Fact]
    public void PickFreePair_DefaultsToCom11()
    {
        var (app, mux) = Com0ComManager.PickFreePair([]);
        Assert.Equal("COM11", app);
        Assert.Equal("COM21", mux);
    }
}

public class ComNameArbiterTests
{
    [Fact]
    public void ParseComDb_ReadsBitsLsbFirst()
    {
        // 0b0000_0101 = COM1 and COM3; second byte 0b0000_0100 = COM11.
        var names = ComNameArbiter.ParseComDb([0b0000_0101, 0b0000_0100]);
        Assert.Equal(["COM1", "COM3", "COM11"], names);
    }

    [Fact]
    public void ParseComDb_EmptyBitmap_YieldsNothing()
    {
        Assert.Empty(ComNameArbiter.ParseComDb([]));
        Assert.Empty(ComNameArbiter.ParseComDb([0, 0, 0, 0]));
    }
}
