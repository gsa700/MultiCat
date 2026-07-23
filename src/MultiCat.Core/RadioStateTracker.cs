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

    public long? FrequencyHz { get; private set; }

    public string? Mode { get; private set; }

    public event Action<long>? FrequencyChanged;

    public event Action<string>? ModeChanged;

    public void Observe(CatFrame frame)
    {
        var text = frame.ToAscii();
        if (text.Length < 3 || !text.EndsWith(';'))
        {
            return;
        }

        if ((text.StartsWith("FA") || text.StartsWith("FB")) && text.Length == 14)
        {
            if (long.TryParse(text.AsSpan(2, 11), NumberStyles.None, CultureInfo.InvariantCulture, out var hz)
                && text.StartsWith("FA") && hz != FrequencyHz)
            {
                FrequencyHz = hz;
                FrequencyChanged?.Invoke(hz);
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
