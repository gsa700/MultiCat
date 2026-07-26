namespace MultiCat.Core;

/// <summary>
/// One display vocabulary for operating modes. Hamlib reports internal names like
/// PKTUSB/CWR/RTTYR; the Kenwood tracker derives DATA/CW-R directly. Everything the
/// user sees goes through here so the same mode never shows two different ways.
/// Protocol traffic is never normalized — clients get real hamlib names.
/// </summary>
public static class ModeNames
{
    private static readonly Dictionary<string, string> Display = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USB"] = "USB",
        ["LSB"] = "LSB",
        ["CW"] = "CW",
        ["CWR"] = "CW-R",
        ["CW-R"] = "CW-R",
        ["PKTUSB"] = "DATA",
        ["DATA"] = "DATA",
        ["PKTLSB"] = "DATA-R",
        ["DATA-R"] = "DATA-R",
        ["RTTY"] = "RTTY",
        ["RTTYR"] = "RTTY-R",
        ["RTTY-R"] = "RTTY-R",
        ["AM"] = "AM",
        ["AMS"] = "AM-S",
        ["FM"] = "FM",
        ["FMN"] = "FM-N",
        ["PKTFM"] = "DATA-FM",
        ["WFM"] = "WFM",
    };

    /// <summary>Ham-natural display name for a mode; unknown names pass through.</summary>
    public static string ToDisplay(string mode) =>
        Display.GetValueOrDefault(mode.Trim(), mode.Trim());
}
