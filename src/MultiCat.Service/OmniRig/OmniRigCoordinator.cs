using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace MultiCat.Service.OmniRig;

/// <summary>
/// Bridges MultiCAT radios to the OmniRig COM server: writes the shared config the
/// server reads (which rigctld port each OmniRig "Rig" maps to) and manages the
/// server's machine-wide COM registration. The server itself is a separate process
/// launched on demand by COM; here we only prepare what it needs.
/// </summary>
public sealed class OmniRigCoordinator(ILogger<OmniRigCoordinator> logger)
{
    // Must match OmniRigGuids.OmniRigXClass in the MultiCat.OmniRig project.
    private const string OmniRigClsid = "{0839E8C6-ED30-4950-8087-966F970F0CAE}";

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "MultiCAT", "omnirig.settings.json");

    /// <summary>True when the OmniRig COM server is registered so loggers can see it.</summary>
    public bool IsRegistered =>
        Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Classes\CLSID\{OmniRigClsid}") is not null;

    /// <summary>Points OmniRig Rig <paramref name="rig"/> (1 or 2) at a rigctld endpoint.</summary>
    public void AssignRig(int rig, string host, int port)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var root = File.Exists(ConfigPath)
            ? JsonNode.Parse(File.ReadAllText(ConfigPath))?.AsObject() ?? []
            : [];

        root["Host"] = host;
        root[rig == 2 ? "Rig2Port" : "Rig1Port"] = port;
        File.WriteAllText(ConfigPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        logger.LogInformation("OmniRig Rig {Rig} → {Host}:{Port}", rig, host, port);
    }

    /// <summary>Registers the OmniRig COM server if needed. The server exe self-elevates,
    /// so the user sees one UAC prompt. Returns false (with a reason) if it can't be run.</summary>
    public async Task<(bool Ok, string Message)> EnsureRegisteredAsync(CancellationToken cancellationToken)
    {
        if (IsRegistered)
        {
            return (true, "already registered");
        }

        if (LocateServerExe() is not { } exe)
        {
            return (false, "OmniRig server not found — run \"Register OmniRig.cmd\" from the install folder");
        }

        try
        {
            var process = Process.Start(new ProcessStartInfo(exe, "--register") { UseShellExecute = true });
            if (process is null)
            {
                return (false, "could not launch the OmniRig server");
            }

            await process.WaitForExitAsync(cancellationToken);
            return IsRegistered ? (true, "registered") : (false, "registration was declined");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OmniRig registration failed");
            return (false, $"registration failed: {ex.Message}");
        }
    }

    private static string? LocateServerExe()
    {
        var baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, "..", "MultiCat.OmniRig", "MultiCat.OmniRig.exe"), // release layout
            Path.Combine(baseDir, "MultiCat.OmniRig.exe"),                            // co-located
            Path.Combine(baseDir, "..", "..", "..", "..", "MultiCat.OmniRig", "bin", "Debug", "net10.0-windows", "MultiCat.OmniRig.exe"),
            Path.Combine(baseDir, "..", "..", "..", "..", "MultiCat.OmniRig", "bin", "Release", "net10.0-windows", "MultiCat.OmniRig.exe"),
        ];

        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }
}
