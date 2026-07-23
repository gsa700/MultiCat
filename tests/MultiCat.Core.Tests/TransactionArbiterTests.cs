using MultiCat.Core;
using MultiCat.Core.Framing;
using MultiCat.Core.Protocol;

namespace MultiCat.Core.Tests;

public class TransactionArbiterTests
{
    private readonly ScriptedTransport _transport = new();
    private readonly FakeTimeProvider _time = new();

    private TransactionArbiter CreateArbiter(TimeSpan? timeout = null, PollCache? cache = null) =>
        new(_transport, new KenwoodFramer(), new KenwoodRules(),
            cache ?? new PollCache(_time, TimeSpan.FromMilliseconds(300)),
            TimeProvider.System, timeout);

    [Fact]
    public async Task ReadCommand_ResponseIsReturnedToCaller()
    {
        _transport.Responder = cmd => cmd == "FA;" ? "FA00014074000;" : null;
        await using var arbiter = CreateArbiter();

        var response = await arbiter.ExecuteAsync("n1mm", CatFrame.FromAscii("FA;"));

        Assert.NotNull(response);
        Assert.Equal("FA00014074000;", response.Value.ToAscii());
    }

    [Fact]
    public async Task SecondPollWithinTtl_IsServedFromCache_RadioPolledOnce()
    {
        _transport.Responder = cmd => cmd == "FA;" ? "FA00014074000;" : null;
        var activities = new List<ArbiterActivity>();
        await using var arbiter = CreateArbiter();
        arbiter.Activity += activities.Add;

        var first = await arbiter.ExecuteAsync("n1mm", CatFrame.FromAscii("FA;"));
        var second = await arbiter.ExecuteAsync("wsjtx", CatFrame.FromAscii("FA;"));

        Assert.Single(_transport.Sent);
        Assert.True(first!.Value.ContentEquals(second!.Value));
        Assert.Contains(activities, a => a.Kind == ArbiterActivityKind.CacheHit && a.ClientId == "wsjtx");
    }

    [Fact]
    public async Task SetCommand_ReturnsNull_AndInvalidatesCache()
    {
        _transport.Responder = cmd => cmd == "FA;" ? "FA00014074000;" : null;
        await using var arbiter = CreateArbiter();

        await arbiter.ExecuteAsync("n1mm", CatFrame.FromAscii("FA;"));
        var setResult = await arbiter.ExecuteAsync("wsjtx", CatFrame.FromAscii("FA00007074000;"));
        await arbiter.ExecuteAsync("n1mm", CatFrame.FromAscii("FA;"));

        Assert.Null(setResult);
        Assert.Equal(["FA;", "FA00007074000;", "FA;"], _transport.Sent);
    }

    [Fact]
    public async Task UnsolicitedFrame_IsBroadcast_NotSwallowed()
    {
        await using var arbiter = CreateArbiter();
        var unsolicited = new List<CatFrame>();
        arbiter.UnsolicitedReceived += unsolicited.Add;

        _transport.Inject("FA00014250000;");

        Assert.Single(unsolicited);
        Assert.Equal("FA00014250000;", unsolicited[0].ToAscii());
    }

    [Fact]
    public async Task InterleavedUnsolicitedFrame_DoesNotStealTheResponseSlot()
    {
        _transport.Responder = cmd => cmd == "FA;" ? "MD2;FA00014074000;" : null;
        await using var arbiter = CreateArbiter();
        var unsolicited = new List<CatFrame>();
        arbiter.UnsolicitedReceived += unsolicited.Add;

        var response = await arbiter.ExecuteAsync("n1mm", CatFrame.FromAscii("FA;"));

        Assert.Equal("FA00014074000;", response!.Value.ToAscii());
        Assert.Single(unsolicited);
        Assert.Equal("MD2;", unsolicited[0].ToAscii());
    }

    [Fact]
    public async Task SilentRadio_TimesOut_ReturnsNull()
    {
        await using var arbiter = CreateArbiter(TimeSpan.FromMilliseconds(50));
        var activities = new List<ArbiterActivity>();
        arbiter.Activity += activities.Add;

        var response = await arbiter.ExecuteAsync("n1mm", CatFrame.FromAscii("FA;"));

        Assert.Null(response);
        Assert.Contains(activities, a => a.Kind == ArbiterActivityKind.Timeout);
    }
}
