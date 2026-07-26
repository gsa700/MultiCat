namespace MultiCat.Core.Flex;

/// <summary>
/// How one MultiCAT radio presents itself as a FlexRadio. Each radio maps to one
/// virtual radio and therefore one Genius channel — split lives inside a channel,
/// so a two-VFO radio is still a single identity. Two independent radios (SO2R)
/// mean two identities with distinct serials, matching channels A and B on the
/// PGXL/TGXL/AGXL.
/// </summary>
public sealed record FlexIdentity
{
    public string Model { get; init; } = "FLEX-8600";

    /// <summary>Must be unique per radio: the Genius boxes pair on this.</summary>
    public required string Serial { get; init; }

    /// <summary>SmartSDR version string the stack sees.</summary>
    public string Version { get; init; } = "3.6.19.35";

    public string Name { get; init; } = "MultiCAT";

    /// <summary>Shown in the Genius apps.</summary>
    public string Nickname { get; init; } = "MultiCAT";

    public string Callsign { get; init; } = string.Empty;

    /// <summary>This host's address as advertised to the stack.</summary>
    public required string AdvertiseIp { get; init; }

    /// <summary>TCP command port the stack connects back to.</summary>
    public int CommandPort { get; init; } = 4992;

    /// <summary>
    /// Derives a Flex-style serial from a radio name, for radios that carry no
    /// serial of their own. Stable for a given name so a restart keeps the pairing.
    /// </summary>
    public static string DeriveSerial(string radioName)
    {
        var hash = 0;
        foreach (var c in radioName)
        {
            hash = ((hash * 31) + c) & 0x7FFFFFFF;
        }

        return $"8600-0000-0000-{hash % 10000:D4}";
    }
}
