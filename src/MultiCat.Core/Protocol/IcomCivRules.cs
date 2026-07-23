namespace MultiCat.Core.Protocol;

/// <summary>
/// Rules for Icom CI-V. Frame layout: FE FE [to] [from] [cmd] [data...] FD.
/// Every addressed command is answered — with data for reads, or an OK (0xFB) /
/// NG (0xFA) acknowledgement for sets.
/// </summary>
public sealed class IcomCivRules : ICatProtocolRules
{
    private const byte AckOk = 0xFB;
    private const byte AckNg = 0xFA;

    /// <summary>CI-V command numbers that read state without changing it.</summary>
    private static readonly HashSet<byte> ReadCommands = [0x03, 0x04, 0x0F, 0x15, 0x16, 0x19, 0x1A];

    public bool ExpectsResponse(CatFrame command) => command.Length >= 6;

    public bool IsResponseTo(CatFrame response, CatFrame command)
    {
        if (command.Length < 6 || response.Length < 6)
        {
            return false;
        }

        var cmd = command.Data.Span;
        var resp = response.Data.Span;

        // The reply must come from the rig we addressed, back to us.
        if (resp[3] != cmd[2] || resp[2] != cmd[3])
        {
            return false;
        }

        return resp[4] == cmd[4] || resp[4] is AckOk or AckNg;
    }

    public bool IsCacheable(CatFrame command) =>
        command.Length >= 6 && ReadCommands.Contains(command.Data.Span[4]);
}
