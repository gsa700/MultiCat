namespace MultiCat.Hamlib;

public static partial class RigDatabase
{
    /// <summary>
    /// Best-effort resolution of a hamlib model from a free-text radio name
    /// (e.g. "Elecraft K4D" → K4, id 2047). Prefers the longest model-name match
    /// so "K4" wins over a shorter incidental substring. Returns null if nothing fits.
    /// </summary>
    public static RigModel? FindByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return All
            .Where(r => name.Contains(r.DisplayName, StringComparison.OrdinalIgnoreCase)
                        || name.Contains(r.Model, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => name.Contains(r.DisplayName, StringComparison.OrdinalIgnoreCase) ? r.DisplayName.Length : r.Model.Length)
            .FirstOrDefault();
    }
}
