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

// Environment.ProcessPath is net6+; on net48 take the path from the assembly.
var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;

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
//
// Threading follows the reference implementation: real OmniRig is a Delphi VCL
// application, i.e. a single-threaded apartment with a message pump, and it fires
// events from that same thread ("FEvents.ParamsChange(...)" on the main thread).
// That matters — a client's sink pointer is only usable in the apartment that
// received it, so firing from a pool thread makes every QueryInterface fail and
// no event is ever delivered. So: register the class object on an STA thread,
// drive polling from a thread timer on that same thread, and pump messages.
var settings = ServerSettings.Load(exePath);
var ready = new ManualResetEventSlim(false);
Exception? startupError = null;

var sta = new Thread(() =>
{
    try
    {
        var instance = new OmniRigXImpl(settings.Host, settings.Rig1Port, settings.Rig2Port);
        var clsid = new Guid(OmniRigGuids.OmniRigXClass);
        var hr = CoRegisterClassObject(
            in clsid, new ClassFactory(() => instance), ClsctxLocalServer, RegclsMultipleUse, out _);
        Marshal.ThrowExceptionForHR(hr);

        // WM_TIMER with a null window posts to this thread's queue, so the callback
        // runs on the STA thread — the apartment that owns the sinks.
        // 'tick' stays rooted by this closure for the life of the thread.
        TimerProc tick = (_, _, _, _) => instance.PollAll();
        if (SetTimer(IntPtr.Zero, UIntPtr.Zero, 500, tick) == UIntPtr.Zero)
        {
            throw new InvalidOperationException("SetTimer failed");
        }

        ready.Set();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }
    catch (Exception ex)
    {
        startupError = ex;
        ready.Set();
    }
})
{
    IsBackground = false,
    Name = "OmniRig STA",
};

sta.SetApartmentState(ApartmentState.STA);
sta.Start();
ready.Wait();

if (startupError is not null)
{
    Console.Error.WriteLine($"OmniRig server failed to start: {startupError.Message}");
    return 1;
}

// Note the wording: we are a CLIENT of rigctld on that port, not a listener there.
Console.WriteLine(
    $"MultiCAT OmniRig server running. Rig 1 reads from rigctld at {settings.Host}:{settings.Rig1Port}. Ctrl+C to stop.");
sta.Join();
return 0;

[DllImport("ole32.dll")]
static extern int CoRegisterClassObject(in Guid rclsid, [MarshalAs(UnmanagedType.IUnknown)] object pUnk, uint dwClsContext, uint flags, out uint lpdwRegister);

[DllImport("user32.dll")]
static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr nIDEvent, uint uElapse, TimerProc lpTimerFunc);

[DllImport("user32.dll")]
static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

[DllImport("user32.dll")]
static extern bool TranslateMessage(in MSG lpMsg);

[DllImport("user32.dll")]
static extern IntPtr DispatchMessage(in MSG lpMsg);

[DllImport("oleaut32.dll", CharSet = CharSet.Unicode)]
static extern int LoadTypeLibEx(string szFile, uint regkind, out IntPtr pptlib);

[DllImport("oleaut32.dll")]
static extern int UnRegisterTypeLib(in Guid libID, ushort wVerMajor, ushort wVerMinor, int lcid, int syskind);

static RegistryKey ClassesKey(string path) =>
    Registry.LocalMachine.CreateSubKey($@"Software\Classes\{path}");

static void Register(string exePath)
{
    var tlbPath = Path.Combine(Path.GetDirectoryName(exePath)!, "OmniRig.tlb");

    // Properly register the type library (REGKIND_REGISTER = 0) so the oleaut32
    // universal marshaller can build an IDispatch proxy for the dual interfaces —
    // hand-written registry keys aren't enough for out-of-process IDispatch.
    if (LoadTypeLibEx(tlbPath, 0, out var tlib) == 0 && tlib != IntPtr.Zero)
    {
        Marshal.Release(tlib);
    }

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
    // SYS_WIN32 = 1, SYS_WIN64 = 3; try both so it clears whichever was registered.
    foreach (var syskind in new[] { 1, 3 })
    {
        try { UnRegisterTypeLib(new Guid(OmniRigGuids.TypeLib), 1, 0, 0, syskind); }
        catch (Exception) { }
    }

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

internal delegate void TimerProc(IntPtr hWnd, uint uMsg, UIntPtr nIDEvent, uint dwTime);

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public IntPtr Hwnd;
    public uint Message;
    public IntPtr WParam;
    public IntPtr LParam;
    public uint Time;
    public int PointX;
    public int PointY;
}

// A plain class, not a record: positional records need init-only setters, which
// net48 lacks without an IsExternalInit shim. Nothing here needs record semantics.
internal sealed class ServerSettings
{
    public ServerSettings(string host, int rig1Port, int? rig2Port)
    {
        Host = host;
        Rig1Port = rig1Port;
        Rig2Port = rig2Port;
    }

    public string Host { get; }

    public int Rig1Port { get; }

    public int? Rig2Port { get; }

    /// <summary>Shared config the MultiCAT service writes when a radio is assigned to
    /// OmniRig. In ProgramData so both processes find it without knowing each other's
    /// install path. Falls back to a file next to the exe, then to the default.</summary>
    public static string SharedConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "MultiCAT", "omnirig.settings.json");

    public static ServerSettings Load(string exePath)
    {
        var candidates = new[]
        {
            SharedConfigPath,
            Path.Combine(Path.GetDirectoryName(exePath)!, "omnirig.settings.json"),
        };

        foreach (var path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

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
