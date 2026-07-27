using System.Globalization;
using MultiCat.Core.Flex;

namespace MultiCat.Core.Tests.Flex;

public class FlexRadioStateTests
{
    private readonly FlexRadioState _radio = new(new FlexIdentity
    {
        Serial = "8600-0000-0000-1234",
        AdvertiseIp = "10.0.1.50",
        Nickname = "Test Radio",
        Callsign = "AB0R",
    });

    private readonly List<string> _sliceLines = [];
    private readonly List<string> _broadcastLines = [];

    public FlexRadioStateTests()
    {
        _radio.SliceLineReady += _sliceLines.Add;
        _radio.BroadcastLineReady += _broadcastLines.Add;
    }

    // --- what the amplifier actually reads ------------------------------------
    [Fact]
    public void TransmitObjectCarriesTheFrequency_BecauseThatIsWhereTheAmpReadsItsBand()
    {
        _radio.UpdateSlice(0, frequencyHz: 21_074_000);
        _radio.EmitPending();

        // A slice-only update would leave the PowerGenius showing "N/A".
        Assert.Contains(_sliceLines, l => l.StartsWith("S0|transmit") && l.Contains("freq=21.074000"));
    }

    [Fact]
    public void FrequenciesAreFormattedInvariantly_SoACommaLocaleCannotBreakTheParser()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            _radio.UpdateSlice(0, frequencyHz: 21_074_000);
            _radio.EmitPending();

            Assert.Contains(_sliceLines, l => l.Contains("21.074000"));
            Assert.DoesNotContain(_sliceLines, l => l.Contains("21,074000"));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    // --- delta pacing ----------------------------------------------------------
    [Fact]
    public void RuntimeDeltasCarryOnlyWhatChanged_NotAFullDump()
    {
        _radio.UpdateSlice(0, frequencyHz: 14_100_000);
        _radio.EmitPending();

        var sliceDelta = _sliceLines.First(l => l.StartsWith("S0|slice"));
        Assert.Contains("RF_frequency=14.100000", sliceDelta);
        Assert.DoesNotContain("mode_list", sliceDelta);   // a full status line has this
        Assert.DoesNotContain("mode=", sliceDelta);       // mode did not change
    }

    [Fact]
    public void SupersededValuesAreSkipped_OnlyTheLatestIsSent()
    {
        _radio.UpdateSlice(0, frequencyHz: 14_100_000);
        _radio.UpdateSlice(0, frequencyHz: 14_200_000);
        _radio.UpdateSlice(0, frequencyHz: 14_300_000);
        _radio.EmitPending();

        var deltas = _sliceLines.Where(l => l.StartsWith("S0|slice")).ToList();
        Assert.Single(deltas);
        Assert.Contains("14.300000", deltas[0]);
    }

    [Fact]
    public void AnUnchangedValueProducesNothing()
    {
        _radio.UpdateSlice(0, frequencyHz: 14_074_000, mode: "USB");  // already the defaults
        _radio.EmitPending();

        Assert.Empty(_sliceLines);
    }

    [Fact]
    public void MovingTheTransmitDesignationResendsTheFullPicture()
    {
        _radio.UpdateSlice(0, isTransmitSlice: false);

        // Structural, so it goes out immediately and in full rather than as a delta.
        Assert.Contains(_sliceLines, l => l.Contains("mode_list"));
        Assert.False(_radio.HasPendingUpdates);
    }

    // --- mode normalisation ----------------------------------------------------
    [Theory]
    [InlineData("PKTUSB", "DIGU")]
    [InlineData("PKTLSB", "DIGL")]
    [InlineData("DATA", "DIGU")]
    [InlineData("CWR", "CW")]
    [InlineData("RTTYR", "RTTY")]
    [InlineData("usb", "USB")]
    public void IncomingModesAreNormalisedToFlexSliceModes(string input, string expected)
    {
        // Start from a mode none of these map to, so every case is a real change.
        _radio.UpdateSlice(0, mode: "FM");
        _radio.EmitPending();
        _sliceLines.Clear();

        _radio.UpdateSlice(0, mode: input);
        _radio.EmitPending();

        Assert.Equal(expected, _radio.Slices[0].Mode);
        Assert.Contains(_sliceLines, l => l.Contains($"tx_slice_mode={expected}"));
    }

    // --- the interlock, i.e. what keys the amplifier ---------------------------
    [Fact]
    public void KeyingRunsTheFullRequestedThenTransmittingSequence()
    {
        _radio.SetTransmit(true);

        Assert.Equal(2, _broadcastLines.Count);
        Assert.Contains("state=PTT_REQUESTED", _broadcastLines[0]);
        Assert.Contains("state=TRANSMITTING", _broadcastLines[1]);
    }

    [Fact]
    public void UnkeyingRunsUnkeyRequestedThenReady()
    {
        _radio.SetTransmit(true);
        _broadcastLines.Clear();

        _radio.SetTransmit(false);

        Assert.Equal(2, _broadcastLines.Count);
        Assert.Contains("state=UNKEY_REQUESTED", _broadcastLines[0]);
        Assert.Contains("state=READY", _broadcastLines[1]);
    }

    [Fact]
    public void RepeatingTheSameKeyStateChangesNothing()
    {
        _radio.SetTransmit(true);
        _broadcastLines.Clear();

        _radio.SetTransmit(true);

        Assert.Empty(_broadcastLines);
    }

    [Fact]
    public void WhileTransmitting_TheEngagedAmplifiersAreNamed_SoTheAmpKnowsToKey()
    {
        _radio.EngagedAmplifierHandles = () => ["0x41000000"];

        _radio.SetTransmit(true);

        var transmitting = _broadcastLines.First(l => l.Contains("state=TRANSMITTING"));
        Assert.Contains("amplifier=0x41000000", transmitting);
        Assert.Contains("source=SW", transmitting);
    }

    [Fact]
    public void WhileIdle_NoAmplifierIsEngagedAndNoClientHoldsTransmit()
    {
        _radio.EngagedAmplifierHandles = () => ["0x41000000"];

        var idle = _radio.InterlockStatusLine();

        Assert.Contains("state=READY", idle);
        Assert.Contains("amplifier=", idle);
        Assert.DoesNotContain("amplifier=0x41000000", idle);
        Assert.Contains("tx_client_handle=0x00000000", idle);
    }

    [Fact]
    public void InterlockAlwaysAllowsTransmit_TheStackEnforcesItsOwnProtection()
    {
        Assert.Contains("tx_allowed=1", _radio.InterlockStatusLine());
    }

    // --- object identity -------------------------------------------------------
    [Fact]
    public void HandlesStartHighAndStayDistinct_LikeSmartSdr()
    {
        var first = _radio.AllocateHandle();
        var second = _radio.AllocateHandle();

        Assert.Equal(0x40000000u, first);
        Assert.Equal(0x41000000u, second);
    }

    [Fact]
    public void MeterIdsCountFromOne()
    {
        Assert.Equal(1, _radio.AllocateMeterId());
        Assert.Equal(2, _radio.AllocateMeterId());
    }

    [Fact]
    public void ResetForgetsRegistrations_SoTheStackSeesTheRadioVanish()
    {
        _radio.AddAmplifier(0x41000000, new Dictionary<string, string>());
        _radio.AllocateMeterId();

        _radio.Reset();

        Assert.Equal(1, _radio.AllocateMeterId());
    }

    [Fact]
    public void KeyingListsTheAmplifiersConnectionHandles_NotItsObjectHandles()
    {
        // A box finds its own CONNECTION handle here to know a key edge is for it;
        // publishing the handle returned by "amplifier create" instead leaves every
        // box unable to recognise itself, so none of them key.
        _radio.AddAmplifier(0x41000000, new Dictionary<string, string>());
        _radio.EngagedAmplifierHandles = () => ["0x40000000", "0x44000000"];

        _radio.SetTransmit(true);
        var keyed = _broadcastLines.Last(l => l.Contains("state=TRANSMITTING"));

        Assert.Contains("amplifier=0x40000000,0x44000000", keyed);
        Assert.DoesNotContain("0x41000000", keyed);
    }

    [Fact]
    public void ADisconnectedAmplifierDropsOutOfTheNextKey()
    {
        var connected = new List<string> { "0x40000000", "0x44000000" };
        _radio.EngagedAmplifierHandles = () => connected;

        _radio.SetTransmit(true);
        _radio.SetTransmit(false);
        connected.Remove("0x44000000");     // that box dropped
        _radio.SetTransmit(true);

        var keyed = _broadcastLines.Last(l => l.Contains("state=TRANSMITTING"));
        Assert.Contains("amplifier=0x40000000", keyed);
        Assert.DoesNotContain("0x44000000", keyed);
    }

    [Fact]
    public void RadioStatusCarriesTheStationIdentity()
    {
        var line = _radio.RadioStatusLine();

        Assert.Contains("nickname=Test_Radio", line);   // spaces become underscores
        Assert.Contains("callsign=AB0R", line);
    }

    [Fact]
    public void SubscribeTimeStatusIsAFullSliceLine()
    {
        var lines = _radio.SliceStatusLines();

        Assert.Contains("mode_list", Assert.Single(lines));
        Assert.Contains("index_letter=A", lines[0]);
    }
}
