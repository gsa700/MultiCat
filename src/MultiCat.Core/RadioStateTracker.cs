using System.Globalization;

namespace MultiCat.Core;

/// <summary>
/// Watches Kenwood/Elecraft frames flowing through the arbiter and raises typed
/// events when the radio's state changes. This is the feed for the event-stream
/// endpoint (frequency tracking, LP-100A channel attribution).
/// </summary>
public sealed class RadioStateTracker
{
    private static readonly Dictionary<char, string> Modes = new()
    {
        ['1'] = "LSB", ['2'] = "USB", ['3'] = "CW", ['4'] = "FM",
        ['5'] = "AM", ['6'] = "DATA", ['7'] = "CW-R", ['9'] = "DATA-R",
    };

    /// <summary>VFO A — what the operator is listening on.</summary>
    public long? FrequencyHz { get; private set; }

    /// <summary>VFO B, which becomes the transmit frequency while split is on.</summary>
    public long? VfoBHz { get; private set; }

    /// <summary>True when the radio transmits on VFO B rather than VFO A.</summary>
    public bool Split { get; private set; }

    /// <summary>
    /// The frequency about to be transmitted on. Anything that follows the radio to
    /// select a band — an amplifier, tuner or antenna switch — must use this rather
    /// than <see cref="FrequencyHz"/>: during a split QSO those differ, and choosing
    /// the receive VFO would band-select for the wrong one.
    /// </summary>
    public long? TransmitFrequencyHz => Split ? VfoBHz ?? FrequencyHz : FrequencyHz;

    /// <summary>VFO A's mode.</summary>
    public string? Mode { get; private set; }

    /// <summary>
    /// VFO B's mode. Elecraft reports the sub/VFO-B value under the "$" suffix
    /// ("MD$"), and it can differ from VFO A during a cross-mode split.
    /// </summary>
    public string? ModeB { get; private set; }

    /// <summary>The mode that will be transmitted in — VFO B's while split is on.</summary>
    public string? TransmitMode => Split ? ModeB ?? Mode : Mode;

    /// <summary>True while transmitting, false while receiving, null until first known.</summary>
    public bool? Transmitting { get; private set; }

    public event Action<long>? FrequencyChanged;

    /// <summary>Raised when the transmit frequency changes — whether because the dial
    /// moved, VFO B moved, or split was switched.</summary>
    public event Action<long>? TransmitFrequencyChanged;

    public event Action<string>? ModeChanged;

    public event Action<string>? ModeBChanged;

    public event Action<bool>? TransmitChanged;

    public event Action<bool>? SplitChanged;

    private long? _lastTransmitFrequency;

    /// <summary>Raises the transmit-frequency event if the effective value moved.</summary>
    private void NotifyTransmitFrequency()
    {
        if (TransmitFrequencyHz is { } tx && tx != _lastTransmitFrequency)
        {
            _lastTransmitFrequency = tx;
            TransmitFrequencyChanged?.Invoke(tx);
        }
    }

    public void Observe(CatFrame frame)
    {
        var text = frame.ToAscii();
        if (text.Length < 3 || !text.EndsWith(';'))
        {
            return;
        }

        // Elecraft transmit query: TQ0; = receive, TQ1; = transmit.
        if (text.StartsWith("TQ") && text.Length == 4 && text[2] is '0' or '1')
        {
            var tx = text[2] == '1';
            if (tx != Transmitting)
            {
                Transmitting = tx;
                TransmitChanged?.Invoke(tx);
            }

            return;
        }

        // Split / transmit-VFO select: FT0; transmits on VFO A, FT1; on VFO B.
        if (text.StartsWith("FT") && text.Length == 4 && text[2] is '0' or '1')
        {
            var split = text[2] == '1';
            if (split != Split)
            {
                Split = split;
                SplitChanged?.Invoke(split);
                NotifyTransmitFrequency();
            }

            return;
        }

        if ((text.StartsWith("FA") || text.StartsWith("FB")) && text.Length == 14)
        {
            if (long.TryParse(text.AsSpan(2, 11), NumberStyles.None, CultureInfo.InvariantCulture, out var hz))
            {
                if (text.StartsWith("FA") && hz != FrequencyHz)
                {
                    FrequencyHz = hz;
                    FrequencyChanged?.Invoke(hz);
                    NotifyTransmitFrequency();
                }
                else if (text.StartsWith("FB") && hz != VfoBHz)
                {
                    VfoBHz = hz;
                    NotifyTransmitFrequency();
                }
            }
        }
        // VFO B's mode carries the "$" suffix, and must be matched before plain MD
        // because it shares the prefix.
        else if (text.StartsWith("MD$") && text.Length == 5 && Modes.TryGetValue(text[3], out var modeB))
        {
            if (modeB != ModeB)
            {
                ModeB = modeB;
                ModeBChanged?.Invoke(modeB);
            }
        }
        else if (text.StartsWith("MD") && text.Length == 4 && Modes.TryGetValue(text[2], out var mode))
        {
            if (mode != Mode)
            {
                Mode = mode;
                ModeChanged?.Invoke(mode);
            }
        }
        else if (text.StartsWith("IF") && text.Length >= 31)
        {
            if (long.TryParse(text.AsSpan(2, 11), NumberStyles.None, CultureInfo.InvariantCulture, out var hz)
                && hz != FrequencyHz)
            {
                FrequencyHz = hz;
                FrequencyChanged?.Invoke(hz);
            }

            if (Modes.TryGetValue(text[29], out var ifMode) && ifMode != Mode)
            {
                Mode = ifMode;
                ModeChanged?.Invoke(ifMode);
            }
        }
    }
}
