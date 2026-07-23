using MultiCat.Core;

namespace MultiCat.Core.Tests;

public class RadioStateTrackerTests
{
    private readonly RadioStateTracker _tracker = new();

    [Fact]
    public void FaResponse_RaisesFrequencyChanged()
    {
        long? seen = null;
        _tracker.FrequencyChanged += hz => seen = hz;

        _tracker.Observe(CatFrame.FromAscii("FA00014074000;"));

        Assert.Equal(14_074_000, seen);
        Assert.Equal(14_074_000, _tracker.FrequencyHz);
    }

    [Fact]
    public void RepeatedSameFrequency_RaisesOnlyOnce()
    {
        var count = 0;
        _tracker.FrequencyChanged += _ => count++;

        _tracker.Observe(CatFrame.FromAscii("FA00014074000;"));
        _tracker.Observe(CatFrame.FromAscii("FA00014074000;"));

        Assert.Equal(1, count);
    }

    [Fact]
    public void ModeResponse_RaisesModeChanged()
    {
        string? seen = null;
        _tracker.ModeChanged += m => seen = m;

        _tracker.Observe(CatFrame.FromAscii("MD2;"));

        Assert.Equal("USB", seen);
    }

    [Fact]
    public void UnrelatedFrame_ChangesNothing()
    {
        _tracker.Observe(CatFrame.FromAscii("PS1;"));

        Assert.Null(_tracker.FrequencyHz);
        Assert.Null(_tracker.Mode);
    }
}
