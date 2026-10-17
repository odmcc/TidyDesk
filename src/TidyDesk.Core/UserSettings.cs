using System.Text.Json;

namespace TidyDesk.Core;

/// <summary>The user's choices (settings.json in the data folder).</summary>
public sealed class UserSettings
{
    /// <summary>Folders to organize. null: first launch, the default selection is used.</summary>
    public List<string>? Folders { get; set; }
    /// <summary>Folders the user added with "Add folder" (they stay in the list even when unselected).</summary>
    public List<string> CustomFolders { get; set; } = [];
    /// <summary>Interface language: "tr" (default) or "en".</summary>
    public string Language { get; set; } = "tr";

    public static UserSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path), RuleSet.JsonOptions) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A corrupt settings file falls back to defaults.
        }
        return new UserSettings();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, RuleSet.JsonOptions));
    }
}
