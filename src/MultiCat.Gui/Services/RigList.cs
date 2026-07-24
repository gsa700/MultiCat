using MultiCat.Hamlib;

namespace MultiCat.Gui.Services;

/// <summary>Rig-picker choices from the harvested hamlib database.</summary>
public static class RigList
{
    public static string[] DisplayNames { get; } =
        [.. RigDatabase.All.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase).Select(r => r.DisplayName)];

    /// <summary>Best-effort match of a configured radio name to a database entry.</summary>
    public static int IndexOf(string radioName)
    {
        var clean = radioName.Replace(" (simulated)", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        var index = Array.FindIndex(DisplayNames, n => string.Equals(n, clean, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            index = Array.FindIndex(DisplayNames, n =>
                clean.Contains(n, StringComparison.OrdinalIgnoreCase) || n.Contains(clean, StringComparison.OrdinalIgnoreCase));
        }

        return Math.Max(index, 0);
    }
}
