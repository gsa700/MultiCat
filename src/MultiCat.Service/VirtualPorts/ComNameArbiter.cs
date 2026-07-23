using Microsoft.Win32;

namespace MultiCat.Service.VirtualPorts;

/// <summary>
/// Reads the Windows COM Name Arbiter reservation bitmap. Names reserved here are
/// refused by com0com ("already logged as in use") even when no device is present,
/// so pair picking must avoid them. Plain registry read — no elevation needed.
/// </summary>
public static class ComNameArbiter
{
    private const string KeyPath = @"SYSTEM\CurrentControlSet\Control\COM Name Arbiter";

    public static IReadOnlyList<string> ReadReservedNames()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
        return key?.GetValue("ComDB") is byte[] comDb ? ParseComDb(comDb) : [];
    }

    /// <summary>One bit per port number, LSB first: bit N set means COM(N+1) is reserved.</summary>
    public static IReadOnlyList<string> ParseComDb(byte[] comDb)
    {
        var names = new List<string>();
        for (var bit = 0; bit < comDb.Length * 8; bit++)
        {
            if ((comDb[bit / 8] & (1 << (bit % 8))) != 0)
            {
                names.Add($"COM{bit + 1}");
            }
        }

        return names;
    }
}
