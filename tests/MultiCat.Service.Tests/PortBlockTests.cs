using MultiCat.Service.Sessions;

namespace MultiCat.Service.Tests;

public class PortBlockTests
{
    [Theory]
    [InlineData(0, 4532)]   // first radio keeps the familiar rigctld port
    [InlineData(1, 4542)]
    [InlineData(2, 4552)]
    public void EachRadioGetsItsOwnBlock(int radioIndex, int expected)
    {
        Assert.Equal(expected, SessionManager.BlockBase(4532, radioIndex));
    }

    [Fact]
    public void FlexBlocksStepTheSameWay()
    {
        Assert.Equal(4992, SessionManager.BlockBase(4992, 0));
        Assert.Equal(5002, SessionManager.BlockBase(4992, 1));
    }

    [Fact]
    public void BlocksAreWideEnoughToNotOverlapTheNextRadio()
    {
        var first = SessionManager.BlockBase(4532, 0);
        var second = SessionManager.BlockBase(4532, 1);

        Assert.Equal(SessionManager.PortBlockSize, second - first);
        Assert.True(second - first > 1, "a block must hold more than one endpoint");
    }

    [Fact]
    public void HamlibsOwnDaemonPortsAreNeverHandedOut()
    {
        // Both sit inside the first radio's block, so they must be skipped by number
        // rather than by staying below the base: a rotator or amplifier daemon has
        // every right to be running alongside us.
        Assert.True(SessionManager.IsReservedPort(4531));   // ampctld
        Assert.True(SessionManager.IsReservedPort(4533));   // rotctld
        Assert.False(SessionManager.IsReservedPort(4532));  // rigctld — ours to use
        Assert.False(SessionManager.IsReservedPort(4542));
    }

    [Fact]
    public void TheFirstRadiosBlockContainsAReservedPort_WhichIsWhySkippingMatters()
    {
        var start = SessionManager.BlockBase(4532, 0);
        var block = Enumerable.Range(start, SessionManager.PortBlockSize).ToList();

        Assert.Contains(4533, block);
        Assert.Contains(block, SessionManager.IsReservedPort);
    }

    [Fact]
    public void ASecondRadiosBlockIsClearOfReservedPorts()
    {
        var start = SessionManager.BlockBase(4532, 1);
        var block = Enumerable.Range(start, SessionManager.PortBlockSize);

        Assert.DoesNotContain(block, SessionManager.IsReservedPort);
    }

    [Fact]
    public void AnUnknownRadioFallsBackToTheFirstBlock()
    {
        Assert.Equal(4532, SessionManager.BlockBase(4532, -1));
    }
}
