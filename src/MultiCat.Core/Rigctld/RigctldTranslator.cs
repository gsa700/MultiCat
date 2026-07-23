using System.Globalization;

namespace MultiCat.Core.Rigctld;

/// <summary>
/// Speaks the hamlib rigctld network protocol on behalf of one connected client,
/// translating each request into Kenwood-family CAT through the arbiter. This is
/// what lets WSJT-X, fldigi, and friends connect to MultiCAT natively.
/// </summary>
public sealed class RigctldTranslator(TransactionArbiter arbiter, string clientId)
{
    private static readonly Dictionary<char, string> KenwoodToHamlib = new()
    {
        ['1'] = "LSB", ['2'] = "USB", ['3'] = "CW", ['4'] = "FM",
        ['5'] = "AM", ['6'] = "PKTUSB", ['7'] = "CWR", ['9'] = "PKTLSB",
    };

    private static readonly Dictionary<string, char> HamlibToKenwood = new()
    {
        ["LSB"] = '1', ["USB"] = '2', ["CW"] = '3', ["FM"] = '4',
        ["AM"] = '5', ["PKTUSB"] = '6', ["DATA"] = '6', ["RTTY"] = '6',
        ["CWR"] = '7', ["PKTLSB"] = '9',
    };

    // Canned dump_state modeled on hamlib's dummy rig: wide RX range, all modes.
    // Clients only need plausible capabilities; the radio's real limits still apply.
    private const string DumpState =
        "0\n2\n2\n" +
        "150000.000000 1500000000.000000 0x1ff -1 -1 0x10000003 0x3\n" +
        "0 0 0 0 0 0 0\n" +
        "150000.000000 1500000000.000000 0x1ff -1 -1 0x10000003 0x3\n" +
        "0 0 0 0 0 0 0\n" +
        "0x1ff 1\n0x1ff 0\n0 0\n" +
        "0x1e 2400\n0x2 500\n0x1 8000\n0x1 2400\n0x20 15000\n0x20 8000\n0x40 230000\n0 0\n" +
        "9990\n9990\n10000\n0\n" +
        "10 \n10 20 30 \n" +
        "0x3effffff\n0x3effffff\n0x7fffffff\n0x7fffffff\n0x7fffffff\n0x7fffffff\n";

    private bool _pttOn;

    /// <summary>Handles one request line; returns the reply text, or null when the
    /// client asked to close the connection (q).</summary>
    public async Task<string?> HandleLineAsync(string line, CancellationToken cancellationToken = default)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0];

        switch (command)
        {
            case "q" or "Q" or "\\quit":
                return null;

            case "\\chk_vfo":
                return "CHKVFO 0\n";

            case "\\dump_state":
                return DumpState;

            case "v" or "\\get_vfo":
                return "VFOA\n";

            case "V" or "\\set_vfo":
                return Report(0);

            case "f" or "\\get_freq":
            {
                var response = await arbiter.ExecuteAsync(clientId, CatFrame.FromAscii("FA;"), cancellationToken);
                return response is { } frame && frame.Length == 14 && long.TryParse(frame.ToAscii().AsSpan(2, 11), out var hz)
                    ? $"{hz}\n"
                    : Report(-5);
            }

            case "F" or "\\set_freq" when parts.Length >= 2 &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var setHz):
            {
                await arbiter.ExecuteAsync(clientId, CatFrame.FromAscii($"FA{(long)setHz:00000000000};"), cancellationToken);
                return Report(0);
            }

            case "m" or "\\get_mode":
            {
                var response = await arbiter.ExecuteAsync(clientId, CatFrame.FromAscii("MD;"), cancellationToken);
                return response is { } frame && frame.Length == 4 && KenwoodToHamlib.TryGetValue(frame.ToAscii()[2], out var mode)
                    ? $"{mode}\n2700\n"
                    : Report(-5);
            }

            case "M" or "\\set_mode" when parts.Length >= 2:
            {
                if (!HamlibToKenwood.TryGetValue(parts[1].ToUpperInvariant(), out var digit))
                {
                    return Report(-11);
                }

                await arbiter.ExecuteAsync(clientId, CatFrame.FromAscii($"MD{digit};"), cancellationToken);
                return Report(0);
            }

            case "t" or "\\get_ptt":
                return _pttOn ? "1\n" : "0\n";

            case "T" or "\\set_ptt" when parts.Length >= 2:
            {
                _pttOn = parts[1] != "0";
                await arbiter.ExecuteAsync(clientId, CatFrame.FromAscii(_pttOn ? "TX;" : "RX;"), cancellationToken);
                return Report(0);
            }

            case "s" or "\\get_split_vfo":
                return "0\nVFOA\n";

            case "S" or "\\set_split_vfo":
                return Report(0);

            default:
                return Report(-11);
        }
    }

    private static string Report(int code) => $"RPRT {code}\n";
}
