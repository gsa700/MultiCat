using MultiCat.Core;
using MultiCat.Core.Framing;
using MultiCat.Core.Protocol;
using MultiCat.Core.Rigctld;

namespace MultiCat.Core.Tests;

public class RigctldTranslatorTests
{
    private readonly ScriptedTransport _radio = new();
    private readonly RigctldTranslator _translator;

    public RigctldTranslatorTests()
    {
        _radio.Responder = cmd => cmd switch
        {
            "FA;" => "FA00014074000;",
            "MD;" => "MD2;",
            _ => null,
        };
        var arbiter = new TransactionArbiter(
            _radio, new KenwoodFramer(), new KenwoodRules(),
            new PollCache(new FakeTimeProvider(), TimeSpan.FromMilliseconds(300)), TimeProvider.System,
            TimeSpan.FromMilliseconds(200));
        _translator = new RigctldTranslator(arbiter, "rigctld");
    }

    [Fact]
    public async Task GetFreq_ReturnsHertzLine()
    {
        Assert.Equal("14074000\n", await _translator.HandleLineAsync("f"));
    }

    [Fact]
    public async Task SetFreq_SendsKenwoodSet_AndReportsOk()
    {
        var reply = await _translator.HandleLineAsync("F 7074000");

        Assert.Equal("RPRT 0\n", reply);
        Assert.Contains("FA00007074000;", _radio.Sent);
    }

    [Fact]
    public async Task SetFreq_AcceptsFloatFormat()
    {
        var reply = await _translator.HandleLineAsync("F 7074000.000000");

        Assert.Equal("RPRT 0\n", reply);
        Assert.Contains("FA00007074000;", _radio.Sent);
    }

    [Fact]
    public async Task GetMode_MapsKenwoodDigitToHamlibName()
    {
        Assert.Equal("USB\n2700\n", await _translator.HandleLineAsync("m"));
    }

    [Fact]
    public async Task SetMode_MapsDataToKenwood6()
    {
        var reply = await _translator.HandleLineAsync("M PKTUSB 2400");

        Assert.Equal("RPRT 0\n", reply);
        Assert.Contains("MD6;", _radio.Sent);
    }

    [Fact]
    public async Task Ptt_SetAndGet_TracksState()
    {
        Assert.Equal("0\n", await _translator.HandleLineAsync("t"));
        Assert.Equal("RPRT 0\n", await _translator.HandleLineAsync("T 1"));
        Assert.Contains("TX;", _radio.Sent);
        Assert.Equal("1\n", await _translator.HandleLineAsync("t"));
        Assert.Equal("RPRT 0\n", await _translator.HandleLineAsync("T 0"));
        Assert.Contains("RX;", _radio.Sent);
        Assert.Equal("0\n", await _translator.HandleLineAsync("t"));
    }

    [Fact]
    public async Task ChkVfo_AndDumpState_AnswerWithoutTouchingRadio()
    {
        Assert.Equal("CHKVFO 0\n", await _translator.HandleLineAsync("\\chk_vfo"));
        var dump = await _translator.HandleLineAsync("\\dump_state");
        Assert.NotNull(dump);
        Assert.StartsWith("0\n2\n2\n", dump);
        Assert.Empty(_radio.Sent);
    }

    [Fact]
    public async Task Quit_ReturnsNull_UnknownCommand_ReportsUnavailable()
    {
        Assert.Null(await _translator.HandleLineAsync("q"));
        Assert.Equal("RPRT -11\n", await _translator.HandleLineAsync("\\weird_thing"));
    }
}
