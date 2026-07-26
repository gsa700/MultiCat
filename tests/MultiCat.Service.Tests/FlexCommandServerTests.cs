using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MultiCat.Core.Flex;
using MultiCat.Service.Flex;

namespace MultiCat.Service.Tests;

/// <summary>
/// Drives the command port over a real socket, the way a Genius box does: connect,
/// read the greeting, issue commands, then watch for asynchronous status.
/// </summary>
public class FlexCommandServerTests : IAsyncLifetime
{
    private readonly FlexRadioState _radio = new(new FlexIdentity
    {
        Serial = "8600-0000-0000-1234",
        AdvertiseIp = "127.0.0.1",
        Nickname = "Bench",
    });

    private FlexCommandServer _server = null!;

    public Task InitializeAsync()
    {
        _server = new FlexCommandServer(_radio, 0, NullLogger.Instance);
        _server.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private async Task<(TcpClient Tcp, StreamReader Reader, StreamWriter Writer)> ConnectAsync()
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", _server.BoundPort);
        var stream = tcp.GetStream();
        return (tcp, new StreamReader(stream, Encoding.ASCII),
                new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true });
    }

    private static async Task<string> ReadLineAsync(StreamReader reader) =>
        await reader.ReadLineAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token)
        ?? throw new IOException("connection closed");

    private static async Task<string> ReadUntilAsync(StreamReader reader, string contains)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var line = await reader.ReadLineAsync(cts.Token) ?? throw new IOException("closed");
            if (line.Contains(contains))
            {
                return line;
            }
        }
    }

    [Fact]
    public async Task TheRadioSpeaksFirst_WithVersionHandleAndAValidTransmitPath()
    {
        var (tcp, reader, _) = await ConnectAsync();
        using var _tcp = tcp;

        Assert.Equal("V1.4.0.0", await ReadLineAsync(reader));
        Assert.StartsWith("H", await ReadLineAsync(reader));
        Assert.Contains("radio", await ReadLineAsync(reader));
        Assert.Contains("interlock", await ReadLineAsync(reader));
        // The interlock state line means the box has a valid transmit path from
        // the outset rather than only after the first change.
        Assert.Contains("state=READY", await ReadLineAsync(reader));
    }

    [Fact]
    public async Task ACommandIsAcknowledgedWithItsSequence()
    {
        var (tcp, reader, writer) = await ConnectAsync();
        using var _tcp = tcp;
        for (var i = 0; i < 5; i++)
        {
            await ReadLineAsync(reader);   // greeting
        }

        await writer.WriteAsync("C12|ping\r");

        Assert.Equal("R12|0|", await ReadLineAsync(reader));
    }

    [Fact]
    public async Task AmplifierRegistrationIsAnsweredWithTheNewHandle()
    {
        var (tcp, reader, writer) = await ConnectAsync();
        using var _tcp = tcp;

        await writer.WriteAsync("C1|amplifier create model=PGXL ip=127.0.0.1\r");
        var reply = await ReadUntilAsync(reader, "R1|0|");

        Assert.Matches(@"^R1\|0\|0x[0-9A-F]{8}$", reply);
    }

    [Fact]
    public async Task SubscribingIsAnsweredWithTheCurrentSliceAndTransmitState()
    {
        var (tcp, reader, writer) = await ConnectAsync();
        using var _tcp = tcp;

        await writer.WriteAsync("C2|sub slice all\r");
        await ReadUntilAsync(reader, "R2|0|");

        Assert.Contains("slice 0", await ReadUntilAsync(reader, "slice 0"));
        Assert.Contains("transmit", await ReadUntilAsync(reader, "transmit"));
    }

    [Fact]
    public async Task ASubscriberIsToldWhenTheDialMoves()
    {
        var (tcp, reader, writer) = await ConnectAsync();
        using var _tcp = tcp;
        await writer.WriteAsync("C3|sub slice all\r");
        await ReadUntilAsync(reader, "transmit");

        _radio.UpdateSlice(0, frequencyHz: 21_074_000);
        _radio.EmitPending();

        // The amplifier reads its band from the transmit object, so that is the
        // line that matters here.
        Assert.Contains("freq=21.074000", await ReadUntilAsync(reader, "S0|transmit"));
    }

    [Fact]
    public async Task KeyingReachesAClientThatNeverSubscribed()
    {
        var (tcp, reader, _) = await ConnectAsync();
        using var _tcp = tcp;
        for (var i = 0; i < 5; i++)
        {
            await ReadLineAsync(reader);   // greeting, no subscription issued
        }

        _radio.SetTransmit(true);

        Assert.Contains("state=PTT_REQUESTED", await ReadUntilAsync(reader, "PTT_REQUESTED"));
        Assert.Contains("state=TRANSMITTING", await ReadUntilAsync(reader, "TRANSMITTING"));
    }

    [Fact]
    public async Task ConnectedPeersAreReported_SoDiscoveryCanKeepReachingALiveBox()
    {
        var (tcp, reader, _) = await ConnectAsync();
        using var _tcp = tcp;
        await ReadLineAsync(reader);

        Assert.Contains("127.0.0.1", _server.ConnectedPeers());
        Assert.Equal(1, _server.ClientCount);
    }

    [Fact]
    public async Task DroppingClients_ClosesTheConnectionSoTheStackSeesTheRadioVanish()
    {
        var (tcp, reader, _) = await ConnectAsync();
        using var _tcp = tcp;
        await ReadLineAsync(reader);

        _server.DropAllClients();

        // Reads drain whatever was buffered and then hit end-of-stream.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string? line;
        do
        {
            line = await reader.ReadLineAsync(cts.Token);
        }
        while (line is not null);

        Assert.Equal(0, _server.ClientCount);
    }

    [Fact]
    public async Task TwoBoxesAreServedIndependently()
    {
        var (tcpA, readerA, writerA) = await ConnectAsync();
        var (tcpB, readerB, _) = await ConnectAsync();
        using var _a = tcpA;
        using var _b = tcpB;

        await writerA.WriteAsync("C9|sub slice all\r");
        await ReadUntilAsync(readerA, "R9|0|");

        _radio.SetTransmit(true);

        // Both see the interlock; handles differ so each box is its own client.
        Assert.Contains("TRANSMITTING", await ReadUntilAsync(readerA, "TRANSMITTING"));
        Assert.Contains("TRANSMITTING", await ReadUntilAsync(readerB, "TRANSMITTING"));
        Assert.Equal(2, _server.ClientCount);
    }
}
