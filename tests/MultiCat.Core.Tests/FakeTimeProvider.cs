namespace MultiCat.Core.Tests;

/// <summary>Manually-advanced clock for cache TTL and timeout tests.</summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private long _timestamp;

    public override long GetTimestamp() => _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by) => _timestamp += by.Ticks;
}
