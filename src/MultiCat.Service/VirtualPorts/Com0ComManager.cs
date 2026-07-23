using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MultiCat.Service.VirtualPorts;

public sealed record PortPair(int Number, string? PortA, string? PortB);

/// <summary>
/// Drives the com0com virtual serial driver so users never touch setupc.exe or see
/// CNCA0/CNCB0 names. Pair creation runs setupc silently (elevated when required);
/// everything else is detection and list parsing.
/// </summary>
public sealed partial class Com0ComManager(IConfiguration configuration, ILogger<Com0ComManager> logger)
{
    private static readonly string[] DefaultSetupcPaths =
    [
        @"C:\Program Files (x86)\com0com\setupc.exe",
        @"C:\Program Files\com0com\setupc.exe",
    ];

    [GeneratedRegex(@"^\s*CNC(?<side>[AB])(?<num>\d+)\s+PortName=(?<name>[^,\s]+)", RegexOptions.Multiline)]
    private static partial Regex PairLine();

    public string? SetupcPath =>
        configuration["Com0Com:SetupcPath"] is { Length: > 0 } configured && File.Exists(configured)
            ? configured
            : DefaultSetupcPaths.FirstOrDefault(File.Exists);

    public bool IsInstalled => SetupcPath is not null;

    /// <summary>Parses `setupc list` output into pairs. Internal for testing.</summary>
    public static List<PortPair> ParseList(string output)
    {
        var sides = new Dictionary<int, (string? A, string? B)>();
        foreach (Match match in PairLine().Matches(output))
        {
            var number = int.Parse(match.Groups["num"].Value);
            var name = match.Groups["name"].Value;
            var entry = sides.TryGetValue(number, out var existing) ? existing : (null, null);
            sides[number] = match.Groups["side"].Value == "A" ? (name, entry.Item2) : (entry.Item1, name);
        }

        return [.. sides.OrderBy(kv => kv.Key).Select(kv => new PortPair(kv.Key, kv.Value.A, kv.Value.B))];
    }

    /// <summary>
    /// Picks the next free COMnn pair, avoiding real ports, existing com0com pairs,
    /// and ports already referenced by configuration. App side counts up from COM11;
    /// mux side is app + 10.
    /// </summary>
    public static (string AppPort, string MuxPort) PickFreePair(IEnumerable<string> inUse)
    {
        var taken = new HashSet<string>(inUse, StringComparer.OrdinalIgnoreCase);
        for (var n = 11; n < 200; n++)
        {
            var app = $"COM{n}";
            var mux = $"COM{n + 10}";
            if (!taken.Contains(app) && !taken.Contains(mux))
            {
                return (app, mux);
            }
        }

        throw new InvalidOperationException("No free COM name pair found");
    }

    public async Task<List<PortPair>> ListPairsAsync(CancellationToken cancellationToken = default)
    {
        var output = await RunSetupcAsync("list", elevated: false, cancellationToken);
        return output is null ? [] : ParseList(output);
    }

    /// <summary>Creates a com0com pair. Requires elevation; in dev this pops one UAC prompt.
    /// Success is verified by the port names appearing in the system port list —
    /// setupc's own list command demands elevation, so it can't be used for checks.</summary>
    public async Task<bool> CreatePairAsync(string appPort, string muxPort, CancellationToken cancellationToken = default)
    {
        var output = await RunSetupcAsync(
            $"--silent install PortName={appPort},EmuBR=yes PortName={muxPort}", elevated: true, cancellationToken);
        if (output is null)
        {
            return false;
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var names = System.IO.Ports.SerialPort.GetPortNames();
            if (names.Contains(appPort, StringComparer.OrdinalIgnoreCase) &&
                names.Contains(muxPort, StringComparer.OrdinalIgnoreCase))
            {
                logger.LogInformation("com0com pair {App} <-> {Mux} created", appPort, muxPort);
                return true;
            }

            await Task.Delay(300, cancellationToken);
        }

        logger.LogWarning("com0com pair {App} <-> {Mux} not visible after install", appPort, muxPort);
        return false;
    }

    private async Task<string?> RunSetupcAsync(string arguments, bool elevated, CancellationToken cancellationToken)
    {
        if (SetupcPath is not { } setupc)
        {
            return null;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = setupc,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(setupc)!,
                CreateNoWindow = true,
            };

            if (elevated)
            {
                // Elevation requires ShellExecute, which cannot redirect output.
                startInfo.UseShellExecute = true;
                startInfo.Verb = "runas";
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            }
            else
            {
                startInfo.UseShellExecute = false;
                startInfo.RedirectStandardOutput = true;
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = elevated ? string.Empty : await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex)
        {
            // Covers UAC decline (Win32Exception 1223) and anything else setupc throws.
            logger.LogWarning(ex, "setupc {Arguments} failed", arguments);
            return null;
        }
    }
}
