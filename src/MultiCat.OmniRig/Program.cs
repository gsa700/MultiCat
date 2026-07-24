// MultiCAT's OmniRig-compatible COM server: apps that speak OmniRig (N1MM+,
// Log4OM, CW Skimmer, …) bind to OmniRig.OmniRigX and reach MultiCAT's arbiter
// through the rigctld endpoint. Registration is machine-wide (one UAC prompt) —
// current Windows 11 builds no longer honor per-user LocalServer32 activation:
//
//   MultiCat.OmniRig --register      MultiCat.OmniRig --unregister
//
// COM activation then launches this exe on demand with -Embedding.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using MultiCat.OmniRig;

const uint ClsctxLocalServer = 4;
const uint RegclsMultipleUse = 1;

var exePath = Environment.ProcessPath!;

switch (args.FirstOrDefault())
{
    case "--register" or "--unregister" when !IsElevated():
        return Elevate(exePath, args[0]);

    case "--register":
        Register(exePath);
        Console.WriteLine("Registered OmniRig.OmniRigX (machine-wide) -> " + exePath);
        return 0;

    case "--unregister":
        Unregister();
        Console.WriteLine("Unregistered OmniRig.OmniRigX (machine-wide)");
        return 0;
}

static bool IsElevated() =>
    new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

static int Elevate(string exePath, string verb)
{
    Console.WriteLine($"{verb} requires administrator rights; requesting elevation…");
    try
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = verb,
            UseShellExecute = true,
            Verb = "runas",
        });
        process!.WaitForExit();
        Console.WriteLine(process.ExitCode == 0 ? $"{verb} completed." : $"{verb} failed (exit {process.ExitCode}).");
        return process.ExitCode;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Elevation declined or failed: {ex.Message}");
        return 1;
    }
}

// Server mode (launched by COM with -Embedding, or manually).
var settings = ServerSettings.Load(exePath);
var instance = new OmniRigXImpl(settings.Host, settings.Rig1Port, settings.Rig2Port);
var clsid = new Guid(OmniRigGuids.OmniRigXClass);
var hr = CoRegisterClassObject(in clsid, new ClassFactory(() => instance), ClsctxLocalServer, RegclsMultipleUse, out _);
Marshal.ThrowExceptionForHR(hr);

Console.WriteLine($"MultiCAT OmniRig server running (rig1: {settings.Host}:{settings.Rig1Port}). Ctrl+C to stop.");
await Task.Delay(Timeout.Infinite);
return 0;

[DllImport("ole32.dll")]
static extern int CoRegisterClassObject(in Guid rclsid, [MarshalAs(UnmanagedType.IUnknown)] object pUnk, uint dwClsContext, uint flags, out uint lpdwRegister);

static RegistryKey ClassesKey(string path) =>
    Registry.LocalMachine.CreateSubKey($@"Software\Classes\{path}");

static void Register(string exePath)
{
    var tlbPath = Path.Combine(Path.GetDirectoryName(exePath)!, "OmniRig.tlb");

    using (var progId = ClassesKey(OmniRigGuids.ProgId))
    {
        progId.SetValue(null, "MultiCAT OmniRig-compatible server");
    }

    using (var progIdClsid = ClassesKey($@"{OmniRigGuids.ProgId}\CLSID"))
    {
        progIdClsid.SetValue(null, $"{{{OmniRigGuids.OmniRigXClass}}}");
    }

    foreach (var view in new[] { "CLSID", @"WOW6432Node\CLSID" })
    {
        var baseKey = $@"{view}\{{{OmniRigGuids.OmniRigXClass}}}";
        using (var clsidKey = ClassesKey(baseKey))
        {
            clsidKey.SetValue(null, "MultiCAT OmniRig-compatible server");
        }

        ClassesKey($@"{baseKey}\ProgID").SetValue(null, OmniRigGuids.ProgId);
        ClassesKey($@"{baseKey}\TypeLib").SetValue(null, $"{{{OmniRigGuids.TypeLib}}}");
        ClassesKey($@"{baseKey}\LocalServer32").SetValue(null, $"\"{exePath}\" -Embedding");
    }

    ClassesKey($@"TypeLib\{{{OmniRigGuids.TypeLib}}}\1.0").SetValue(null, "OmniRig Library (MultiCAT)");
    foreach (var arch in new[] { "win32", "win64" })
    {
        ClassesKey($@"TypeLib\{{{OmniRigGuids.TypeLib}}}\1.0\0\{arch}").SetValue(null, tlbPath);
    }

    // Dual interfaces marshal via the universal oleaut32 proxy, which needs the
    // IID -> typelib mapping in both registry views.
    foreach (var view in new[] { "Interface", @"WOW6432Node\Interface" })
    {
        foreach (var iid in new[] { OmniRigGuids.IOmniRigX, OmniRigGuids.IRigX, OmniRigGuids.IPortBits })
        {
            ClassesKey($@"{view}\{{{iid}}}\ProxyStubClsid32").SetValue(null, "{00020424-0000-0000-C000-000000000046}");
            using var tlbKey = ClassesKey($@"{view}\{{{iid}}}\TypeLib");
            tlbKey.SetValue(null, $"{{{OmniRigGuids.TypeLib}}}");
            tlbKey.SetValue("Version", "1.0");
        }
    }
}

static void Unregister()
{
    var paths = new List<string>
    {
        OmniRigGuids.ProgId,
        $@"CLSID\{{{OmniRigGuids.OmniRigXClass}}}",
        $@"WOW6432Node\CLSID\{{{OmniRigGuids.OmniRigXClass}}}",
        $@"TypeLib\{{{OmniRigGuids.TypeLib}}}",
    };
    foreach (var view in new[] { "Interface", @"WOW6432Node\Interface" })
    {
        foreach (var iid in new[] { OmniRigGuids.IOmniRigX, OmniRigGuids.IRigX, OmniRigGuids.IPortBits })
        {
            paths.Add($@"{view}\{{{iid}}}");
        }
    }

    foreach (var path in paths)
    {
        try
        {
            Registry.LocalMachine.DeleteSubKeyTree($@"Software\Classes\{path}", false);
        }
        catch (Exception)
        {
        }
    }
}

internal sealed record ServerSettings(string Host, int Rig1Port, int? Rig2Port)
{
    public static ServerSettings Load(string exePath)
    {
        var path = Path.Combine(Path.GetDirectoryName(exePath)!, "omnirig.settings.json");
        if (File.Exists(path))
        {
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                return new ServerSettings(
                    root.TryGetProperty("Host", out var h) ? h.GetString() ?? "127.0.0.1" : "127.0.0.1",
                    root.TryGetProperty("Rig1Port", out var p1) ? p1.GetInt32() : 4532,
                    root.TryGetProperty("Rig2Port", out var p2) ? (int?)p2.GetInt32() : null);
            }
            catch (Exception)
            {
            }
        }

        return new ServerSettings("127.0.0.1", 4532, null);
    }
}
