using System.Text.Json;
using System.Text.Json.Serialization;

namespace TidyDesk.Core;

public sealed class SourceFolderRule
{
    public string Folder { get; set; } = "";
    public string Target { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Also organize every subfolder, each in its own place (Project\2024\PDF\...).</summary>
    public bool IncludeSubfolders { get; set; }
}

public sealed class CategoryRule
{
    /// <summary>Folder name used with the Turkish interface (default).</summary>
    public string Name { get; set; } = "";
    /// <summary>Folder name used with the English interface.</summary>
    public string? NameEn { get; set; }
    public List<string> Extensions { get; set; } = [];
}

/// <summary>
/// Organizing rules. Categories and exclusions come from the embedded config/rules.default.json;
/// folders, age threshold and date folders are filled in from the user's choices in the app.
/// </summary>
public sealed class RuleSet
{
    public int Version { get; set; } = 2;
    public List<SourceFolderRule> Sources { get; set; } = [];
    public int MinAgeDays { get; set; }
    public string DateFolderFormat { get; set; } = "yyyy-MM";
    public List<CategoryRule> Categories { get; set; } = [];
    public string OtherCategoryName { get; set; } = "Diğer";
    public string? OtherCategoryNameEn { get; set; }
    // Off unless a rules file turns it on: a short user rules file must not scatter unknown files.
    public bool MoveUncategorized { get; set; }
    public List<string> ExcludeExtensions { get; set; } = [];
    public List<string> ExcludeNamePatterns { get; set; } = [];
    public bool SkipHiddenAndSystem { get; set; } = true;

    /// <summary>Category and "other" folder names in both languages; subfolders with these names hold organized files and are not scanned.</summary>
    [JsonIgnore]
    public HashSet<string> ReservedFolderNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static RuleSet Parse(string json)
    {
        var rules = JsonSerializer.Deserialize<RuleSet>(json, JsonOptions) ?? throw new InvalidDataException("The rules file is empty.");
        rules.Normalize();
        return rules;
    }

    /// <summary>Replaces category and "other" folder names with their English versions.</summary>
    public void UseEnglishNames()
    {
        foreach (var c in Categories)
            c.Name = c.NameEn ?? c.Name;
        OtherCategoryName = OtherCategoryNameEn ?? OtherCategoryName;
    }

    /// <summary>The default rules embedded in the app (config/rules.default.json).</summary>
    public static RuleSet CreateDefault() => Parse(DefaultJson());

    /// <summary>The text of the embedded config/rules.default.json, comments included.</summary>
    public static string DefaultJson()
    {
        using var stream = typeof(RuleSet).Assembly.GetManifestResourceStream("rules.default.json")
            ?? throw new InvalidOperationException("The embedded default rules file was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void Normalize()
    {
        MinAgeDays = Math.Max(0, MinAgeDays);
        if (string.IsNullOrWhiteSpace(OtherCategoryName))
            OtherCategoryName = "Diğer";
        foreach (var c in Categories)
            c.Extensions = c.Extensions.Select(NormalizeExtension).ToList();
        ExcludeExtensions = ExcludeExtensions.Select(NormalizeExtension).ToList();
        foreach (var name in Categories.SelectMany(c => new[] { c.Name, c.NameEn }).Append(OtherCategoryName).Append(OtherCategoryNameEn))
        {
            if (!string.IsNullOrWhiteSpace(name))
                ReservedFolderNames.Add(FileSafety.SanitizeFolderName(name));
        }
    }

    private static string NormalizeExtension(string ext)
    {
        ext = ext.Trim().ToLowerInvariant();
        return ext.StartsWith('.') ? ext : "." + ext;
    }
}
