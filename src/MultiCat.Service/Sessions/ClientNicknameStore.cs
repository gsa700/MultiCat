using System.Text.Json;

namespace MultiCat.Service.Sessions;

/// <summary>
/// Resolves a friendly display name for a connected client process. User nicknames
/// (set in the GUI, persisted) win; otherwise a built-in map turns known ham apps'
/// process names into readable labels; otherwise the raw process name is used.
/// </summary>
public sealed class ClientNicknameStore(string filePath)
{
    private static readonly Dictionary<string, string> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["L4ONG"] = "Log4OM",
        ["Log4OM"] = "Log4OM",
        ["wsjtx"] = "WSJT-X",
        ["jtdx"] = "JTDX",
        ["fldigi"] = "fldigi",
        ["flrig"] = "flrig",
        ["N1MMLogger.net"] = "N1MM+",
        ["Lp100aMonitor"] = "LP-100A Monitor",
        ["GridTracker"] = "GridTracker",
        ["dxlog"] = "DXLog",
        ["cwskimmer"] = "CW Skimmer",
        ["JTAlert"] = "JTAlert",
        ["SmartSDR"] = "SmartSDR",
        ["MultiCat.Service"] = "MultiCAT (self)",
    };

    private readonly Lock _lock = new();
    private Dictionary<string, string> _user = Read(filePath);

    public string Resolve(string processName)
    {
        lock (_lock)
        {
            if (_user.TryGetValue(processName, out var nick) && nick.Length > 0)
            {
                return nick;
            }
        }

        return Defaults.GetValueOrDefault(processName, processName);
    }

    public void Set(string processName, string nickname)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(nickname))
            {
                _user.Remove(processName);
            }
            else
            {
                _user[processName] = nickname.Trim();
            }

            try
            {
                File.WriteAllText(filePath, JsonSerializer.Serialize(_user, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception)
            {
            }
        }
    }

    private static Dictionary<string, string> Read(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch (Exception)
        {
            return [];
        }
    }
}
