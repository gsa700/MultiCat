using MultiCat.Core;

namespace MultiCat.Core.Tests;

public class PollCacheTests
{
    private readonly FakeTimeProvider _time = new();
    private static readonly CatFrame Query = CatFrame.FromAscii("FA;");
    private static readonly CatFrame Response = CatFrame.FromAscii("FA00014074000;");

    [Fact]
    public void FreshEntry_IsReturned()
    {
        var cache = new PollCache(_time, TimeSpan.FromMilliseconds(300));
        cache.Set(Query, Response);
        Assert.True(cache.TryGet(Query, out var hit));
        Assert.True(hit.ContentEquals(Response));
    }

    [Fact]
    public void ExpiredEntry_IsNotReturned()
    {
        var cache = new PollCache(_time, TimeSpan.FromMilliseconds(300));
        cache.Set(Query, Response);
        _time.Advance(TimeSpan.FromMilliseconds(301));
        Assert.False(cache.TryGet(Query, out _));
    }

    [Fact]
    public void Invalidate_ClearsEverything()
    {
        var cache = new PollCache(_time, TimeSpan.FromMilliseconds(300));
        cache.Set(Query, Response);
        cache.Invalidate();
        Assert.False(cache.TryGet(Query, out _));
    }

    [Fact]
    public void DifferentCommands_DoNotCollide()
    {
        var cache = new PollCache(_time, TimeSpan.FromMilliseconds(300));
        cache.Set(Query, Response);
        Assert.False(cache.TryGet(CatFrame.FromAscii("FB;"), out _));
    }
}
