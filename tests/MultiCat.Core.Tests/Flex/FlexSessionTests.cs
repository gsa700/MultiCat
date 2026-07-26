using MultiCat.Core.Flex;

namespace MultiCat.Core.Tests.Flex;

/// <summary>Records what the protocol asks of the radio, so the wire handling can be
/// exercised without slice or interlock modelling.</summary>
internal sealed class FakeRadioState : IFlexRadioState
{
    private uint _handle = 0x40000000;
    private int _meterId;

    public List<(uint Handle, IReadOnlyDictionary<string, string> Props)> Amplifiers { get; } = [];

    public List<(int Id, IReadOnlyDictionary<string, string> Props)> Meters { get; } = [];

    public List<IReadOnlyDictionary<string, string>> Interlocks { get; } = [];

    public uint AllocateHandle() => ++_handle;

    public int AllocateMeterId() => ++_meterId;

    public string RadioStatusLine() => "S40000001|radio slices=4";

    public string InterlockConfigLine() => "S40000001|interlock tx1_enabled=1";

    public string InterlockStatusLine() => "S40000001|interlock state=READY";

    public string TransmitStatusLine() => "S40000001|transmit freq=14.074";

    public IReadOnlyList<string> SliceStatusLines() => ["S40000001|slice 0 RF_frequency=14.074"];

    public void AddAmplifier(uint handle, IReadOnlyDictionary<string, string> properties) =>
        Amplifiers.Add((handle, properties));

    public void AddMeter(int meterId, IReadOnlyDictionary<string, string> properties) =>
        Meters.Add((meterId, properties));

    public int AddInterlock(IReadOnlyDictionary<string, string> properties)
    {
        Interlocks.Add(properties);
        return Interlocks.Count;
    }
}

public class FlexSessionTests
{
    private readonly FakeRadioState _radio = new();
    private readonly FlexSession _session;

    public FlexSessionTests() => _session = new FlexSession(_radio);

    [Fact]
    public void Greeting_LeadsWithVersionAndHandle_ThenAValidTransmitPath()
    {
        var greeting = _session.Greeting();

        Assert.Equal("V1.4.0.0", greeting[0]);
        Assert.Equal($"H{_session.Handle:X8}", greeting[1]);
        // The interlock lines matter on connect: without them a box has no valid
        // transmit path until something happens to change.
        Assert.Contains(greeting, l => l.Contains("interlock state="));
    }

    [Theory]
    [InlineData("C1|ping")]
    [InlineData("CD1|ping")]      // optional debug flag
    [InlineData("c1|ping")]       // lowercase tag
    public void CommandsAreAcknowledgedWithTheirSequence(string line)
    {
        Assert.Equal(["R1|0|"], _session.Receive(line));
    }

    [Fact]
    public void UnknownCommandsAreStillAcknowledged_SoABoxNeverStalls()
    {
        Assert.Equal(["R7|0|"], _session.Receive("C7|something_we_do_not_model x=1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("C1")]            // no sequence separator
    [InlineData("garbage")]
    public void MalformedOrUnknownLinesProduceNoReply(string line)
    {
        Assert.Empty(_session.Receive(line));
    }

    [Fact]
    public void ClientBannerIsRecordedButNotAnswered()
    {
        Assert.Empty(_session.Receive("V4.1.16 AG"));
        Assert.Equal("V4.1.16 AG", _session.ClientBanner);
    }

    [Fact]
    public void ClientRepliesAreNeverAnsweredBack()
    {
        // Answering a NAK would loop the two of us forever.
        Assert.Empty(_session.Receive("R0|1|"));
    }

    [Fact]
    public void AmplifierCreate_RegistersItAndReturnsTheNewHandle()
    {
        var reply = _session.Receive("C2|amplifier create ip=10.0.1.20 model=PGXL serial=1234");

        var (handle, props) = Assert.Single(_radio.Amplifiers);
        Assert.Equal($"R2|0|0x{handle:X8}", reply[0]);
        Assert.Equal("PGXL", props["model"]);
        Assert.Equal("10.0.1.20", props["ip"]);
        Assert.True(_session.IsAmplifier);
    }

    [Fact]
    public void MeterCreate_ReturnsTheAllocatedMeterId()
    {
        var first = _session.Receive("C3|meter create name=PATEMP type=AMPLIFIER");
        var second = _session.Receive("C4|meter create name=SWR type=AMPLIFIER");

        Assert.Equal("R3|0|1", first[0]);
        Assert.Equal("R4|0|2", second[0]);
        Assert.Equal(2, _radio.Meters.Count);
        Assert.Equal("PATEMP", _radio.Meters[0].Props["name"]);
    }

    [Fact]
    public void InterlockCreate_ReturnsTheAllocatedId()
    {
        var reply = _session.Receive("C5|interlock create timeout=1000");

        Assert.Equal("R5|0|1", reply[0]);
        Assert.Equal("1000", Assert.Single(_radio.Interlocks)["timeout"]);
    }

    [Fact]
    public void SubscribingToSlice_AcksThenDumpsCurrentState()
    {
        var reply = _session.Receive("C6|sub slice all");

        Assert.Equal("R6|0|", reply[0]);
        Assert.Contains(reply, l => l.Contains("slice 0"));
        Assert.Contains(reply, l => l.Contains("transmit"));
        Assert.True(_session.IsSubscribedTo("slice"));
    }

    [Fact]
    public void SubscribingToAnUnrelatedSubsystem_AcksWithoutASliceDump()
    {
        var reply = _session.Receive("C8|sub meter all");

        Assert.Equal(["R8|0|"], reply);
        Assert.True(_session.IsSubscribedTo("meter"));
        Assert.False(_session.IsSubscribedTo("slice"));
    }

    [Fact]
    public void SubscribingToAll_CoversEverySubsystem()
    {
        _session.Receive("C9|sub all");

        Assert.True(_session.IsSubscribedTo("slice"));
        Assert.True(_session.IsSubscribedTo("anything"));
    }

    [Fact]
    public void KeepaliveIsRecorded()
    {
        Assert.Equal(["R10|0|"], _session.Receive("C10|keepalive enable"));
        Assert.True(_session.KeepAlive);
    }

    [Fact]
    public void ParseKeyValues_TakesPairsAndIgnoresBareTokens()
    {
        var kv = FlexSession.ParseKeyValues(["create", "ip=10.0.1.20", "model=PGXL", "="]);

        Assert.Equal(2, kv.Count);
        Assert.Equal("10.0.1.20", kv["ip"]);
        Assert.Equal("PGXL", kv["model"]);
    }
}
