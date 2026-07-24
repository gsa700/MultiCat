namespace MultiCat.Hamlib;

/// <summary>
/// One rig from the harvested hamlib capability database. Serial speeds are the
/// supported range; SerialConfig is data bits/parity/stop bits ("8N1"); Handshake
/// is hamlib's ctrl value (NONE, XONXOFF, CTS_RTS).
/// </summary>
public sealed record RigModel(
    int Id,
    string Manufacturer,
    string Model,
    string Status,
    string PortType,
    int SerialSpeedMin,
    int SerialSpeedMax,
    string SerialConfig,
    string Handshake)
{
    public string DisplayName => $"{Manufacturer} {Model}";
}
