using System.Globalization;

namespace MultiCat.Core.Flex;

/// <summary>
/// One receiver slice. The status format mirrors what a real FLEX-8600 emits: the
/// Genius boxes are strict parsers, so identity, antenna and mode fields are kept
/// even though the DSP detail is trimmed.
/// </summary>
public sealed class FlexSlice
{
    public int Index { get; init; }

    public bool InUse { get; set; } = true;

    public long FrequencyHz { get; set; } = 14_074_000;

    public string Mode { get; set; } = "USB";

    /// <summary>The transmit slice designation — static, not PTT.</summary>
    public bool IsTransmitSlice { get; set; } = true;

    public string TransmitAntenna { get; set; } = "ANT1";

    /// <summary>slice 0 -> A, 1 -> B, ...</summary>
    public char IndexLetter => (char)('A' + Index);

    /// <summary>Frequency in MHz to six places, invariant — a locale that formats
    /// decimals with a comma would produce a line the boxes cannot parse.</summary>
    public static string Megahertz(long hz) =>
        (hz / 1_000_000.0).ToString("F6", CultureInfo.InvariantCulture);

    /// <summary>A real slice references the SmartSDR client that created it; the
    /// boxes only need the reference to be present and consistent.</summary>
    public const string GuiClientHandle = "0x39BEDD22";

    public string StatusLine(string handle = "0") =>
        $"S{handle}|slice {Index} in_use={(InUse ? 1 : 0)} " +
        $"sample_rate=24000 RF_frequency={Megahertz(FrequencyHz)} " +
        $"client_handle={GuiClientHandle} index_letter={IndexLetter} " +
        "rit_on=0 rit_freq=0 xit_on=0 xit_freq=0 " +
        $"rxant={TransmitAntenna} mode={Mode} wide=0 filter_lo=0 filter_hi=2900 step=100 " +
        "step_list=1,10,50,100,500,1000,2000,3000 agc_mode=med agc_threshold=25 " +
        $"agc_off_level=10 pan=0x40000000 txant={TransmitAntenna} loopa=0 loopb=0 " +
        $"qsk=0 dax=0 dax_clients=0 lock=0 tx={(IsTransmitSlice ? 1 : 0)} active=1 " +
        "ant_list=ANT1,ANT2,RX_A,RX_B " +
        "mode_list=LSB,USB,AM,CW,DIGL,DIGU,SAM,FM,NFM,DFM,RTTY " +
        "rfgain=32 tx_ant_list=ANT1,ANT2";
}
