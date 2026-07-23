namespace MultiCat.Core.Configuration;

public enum CatProtocolFamily
{
    Kenwood,
    IcomCiv,
}

public enum RadioConnectionKind
{
    Serial,
    Tcp,
}

public enum PttMode
{
    None,
    CatOnly,
    CatAndRts,
}

public sealed record RadioConfig
{
    public required string Name { get; init; }

    public required CatProtocolFamily Protocol { get; init; }

    public RadioConnectionKind Connection { get; init; } = RadioConnectionKind.Serial;

    public string? ComPort { get; init; }

    public int BaudRate { get; init; } = 38400;

    public string? Host { get; init; }

    public int? TcpPort { get; init; }

    /// <summary>Hamlib rig model id, used to prefill defaults from the rig database.</summary>
    public int? HamlibModelId { get; init; }

    public List<ClientPortConfig> ClientPorts { get; init; } = [];
}

public sealed record ClientPortConfig
{
    public required string Label { get; init; }

    /// <summary>Virtual COM port name (e.g. "COM11"), or null for the rigctld TCP listener.</summary>
    public string? ComPort { get; init; }

    public int? RigctldPort { get; init; }

    public PttMode Ptt { get; init; } = PttMode.CatOnly;
}
