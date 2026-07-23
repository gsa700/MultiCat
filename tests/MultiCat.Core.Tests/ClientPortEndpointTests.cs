using MultiCat.Core;
using MultiCat.Core.Framing;
using MultiCat.Core.Protocol;

namespace MultiCat.Core.Tests;

public class ClientPortEndpointTests
{
    private readonly ScriptedTransport _radio = new();
    private readonly FakeTimeProvider _time = new();
    private readonly TransactionArbiter _arbiter;

    public ClientPortEndpointTests()
    {
        _radio.Responder = cmd => cmd switch
        {
            "FA;" => "FA00014074000;",
            "MD;" => "MD2;",
            _ => null,
        };
        _arbiter = new TransactionArbiter(
            _radio, new KenwoodFramer(), new KenwoodRules(),
            new PollCache(_time, TimeSpan.FromMilliseconds(300)), TimeProvider.System,
            TimeSpan.FromMilliseconds(200));
    }

    private static async Task<string> WaitForSent(ScriptedTransport client, int count)
    {
        for (var i = 0; i < 100 && client.Sent.Count < count; i++)
        {
            await Task.Delay(10);
        }

        Assert.True(client.Sent.Count >= count, $"expected {count} frames written to client, saw {client.Sent.Count}");
        return client.Sent[count - 1];
    }

    [Fact]
    public async Task ClientCommand_GetsResponseWrittenBack()
    {
        var clientSide = new ScriptedTransport();
        await using var endpoint = new ClientPortEndpoint("n1mm", clientSide, new KenwoodFramer(), _arbiter);

        clientSide.Inject("FA;");

        Assert.Equal("FA00014074000;", await WaitForSent(clientSide, 1));
    }

    [Fact]
    public async Task SetCommand_ProducesNoReply()
    {
        var clientSide = new ScriptedTransport();
        await using var endpoint = new ClientPortEndpoint("n1mm", clientSide, new KenwoodFramer(), _arbiter);

        clientSide.Inject("FA00007074000;");
        clientSide.Inject("MD;");

        Assert.Equal("MD2;", await WaitForSent(clientSide, 1));
        Assert.Single(clientSide.Sent);
    }

    [Fact]
    public async Task TwoClients_EachGetOnlyTheirOwnResponses()
    {
        var clientA = new ScriptedTransport();
        var clientB = new ScriptedTransport();
        await using var endpointA = new ClientPortEndpoint("a", clientA, new KenwoodFramer(), _arbiter);
        await using var endpointB = new ClientPortEndpoint("b", clientB, new KenwoodFramer(), _arbiter);

        clientA.Inject("FA;");
        clientB.Inject("MD;");

        Assert.Equal("FA00014074000;", await WaitForSent(clientA, 1));
        Assert.Equal("MD2;", await WaitForSent(clientB, 1));
        Assert.Single(clientA.Sent);
        Assert.Single(clientB.Sent);
    }

    [Fact]
    public async Task Broadcast_ReachesTheClient()
    {
        var clientSide = new ScriptedTransport();
        await using var endpoint = new ClientPortEndpoint("n1mm", clientSide, new KenwoodFramer(), _arbiter);

        await endpoint.BroadcastAsync(CatFrame.FromAscii("FA00014250000;"));

        Assert.Equal("FA00014250000;", await WaitForSent(clientSide, 1));
    }

    [Fact]
    public async Task PartialBytesFromClient_AreReassembledBeforeExecution()
    {
        var clientSide = new ScriptedTransport();
        await using var endpoint = new ClientPortEndpoint("n1mm", clientSide, new KenwoodFramer(), _arbiter);

        clientSide.Inject("FA");
        clientSide.Inject(";");

        Assert.Equal("FA00014074000;", await WaitForSent(clientSide, 1));
    }
}
