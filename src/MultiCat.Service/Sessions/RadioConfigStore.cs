using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MultiCat.Service.Sessions;

/// <summary>
/// Reads and writes the "Radios" array in appsettings.json, leaving every other
/// section (Logging, etc.) untouched. The single source of radio configuration,
/// mutated live by the radio editor.
/// </summary>
public sealed class RadioConfigStore(string filePath)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string FilePath => filePath;

    public List<RadioSessionOptions> Load()
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        var root = JsonNode.Parse(File.ReadAllText(filePath), documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        })?.AsObject();

        if (root?["Radios"] is not JsonArray radios)
        {
            return [];
        }

        return radios.Deserialize<List<RadioSessionOptions>>(SerializerOptions) ?? [];
    }

    public void Save(IEnumerable<RadioSessionOptions> radios)
    {
        var root = File.Exists(filePath)
            ? JsonNode.Parse(File.ReadAllText(filePath))!.AsObject()
            : [];

        root["Radios"] = JsonSerializer.SerializeToNode(radios, SerializerOptions);
        File.WriteAllText(filePath, root.ToJsonString(SerializerOptions));
    }
}
