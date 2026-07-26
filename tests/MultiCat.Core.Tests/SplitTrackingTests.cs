using MultiCat.Core;

namespace MultiCat.Core.Tests;

/// <summary>
/// Split is normal SO2V operation, and anything that band-follows the radio keys off
/// the transmit frequency. Getting this wrong points an amplifier, tuner and antenna
/// switch at the receive band while the operator transmits on another one.
/// </summary>
public class SplitTrackingTests
{
    private readonly RadioStateTracker _tracker = new();

    private void Observe(string frame) => _tracker.Observe(CatFrame.FromAscii(frame));

    [Fact]
    public void Simplex_TransmitFrequencyIsTheDial()
    {
        Observe("FA00014074000;");

        Assert.False(_tracker.Split);
        Assert.Equal(14_074_000, _tracker.TransmitFrequencyHz);
    }

    [Fact]
    public void Split_TransmitFrequencyFollowsVfoB()
    {
        Observe("FA00014074000;");
        Observe("FB00014200000;");
        Observe("FT1;");

        Assert.True(_tracker.Split);
        Assert.Equal(14_074_000, _tracker.FrequencyHz);       // still listening here
        Assert.Equal(14_200_000, _tracker.TransmitFrequencyHz);
    }

    [Fact]
    public void LeavingSplit_TransmitFrequencyReturnsToTheDial()
    {
        Observe("FA00014074000;");
        Observe("FB00014200000;");
        Observe("FT1;");
        Observe("FT0;");

        Assert.False(_tracker.Split);
        Assert.Equal(14_074_000, _tracker.TransmitFrequencyHz);
    }

    [Fact]
    public void SwitchingIntoSplitRaisesTheTransmitFrequencyChange()
    {
        var reported = new List<long>();
        _tracker.TransmitFrequencyChanged += reported.Add;

        Observe("FA00014074000;");
        Observe("FB00014200000;");
        Observe("FT1;");

        // The band-following gear must be told, even though the dial never moved.
        Assert.Equal([14_074_000, 14_200_000], reported);
    }

    [Fact]
    public void MovingVfoBWhileSplit_ReportsTheNewTransmitFrequency()
    {
        Observe("FA00014074000;");
        Observe("FT1;");
        var reported = new List<long>();
        _tracker.TransmitFrequencyChanged += reported.Add;

        Observe("FB00021300000;");

        Assert.Equal([21_300_000], reported);
        Assert.Equal(21_300_000, _tracker.TransmitFrequencyHz);
    }

    [Fact]
    public void MovingVfoBWhileSimplex_ChangesNothingForTheAmplifier()
    {
        Observe("FA00014074000;");
        var reported = new List<long>();
        _tracker.TransmitFrequencyChanged += reported.Add;

        Observe("FB00021300000;");

        Assert.Empty(reported);
        Assert.Equal(14_074_000, _tracker.TransmitFrequencyHz);
        Assert.Equal(21_300_000, _tracker.VfoBHz);
    }

    [Fact]
    public void SplitBeforeVfoBIsKnown_FallsBackToTheDialRatherThanNothing()
    {
        Observe("FA00014074000;");
        Observe("FT1;");

        // Better to follow the receive VFO briefly than to report no frequency.
        Assert.Equal(14_074_000, _tracker.TransmitFrequencyHz);
    }

    [Fact]
    public void SplitStateIsReported()
    {
        var states = new List<bool>();
        _tracker.SplitChanged += states.Add;

        Observe("FT1;");
        Observe("FT1;");   // unchanged, no repeat
        Observe("FT0;");

        Assert.Equal([true, false], states);
    }

    [Fact]
    public void TheDialEventStillOnlyTracksVfoA()
    {
        var dial = new List<long>();
        _tracker.FrequencyChanged += dial.Add;

        Observe("FA00014074000;");
        Observe("FB00014200000;");

        Assert.Equal([14_074_000], dial);
    }
}
