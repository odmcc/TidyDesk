using TidyDesk.Core;

namespace TidyDesk.Tests;

/// <summary>
/// Creates a temporary "Desktop", "Downloads" and "Documents" under %TEMP% for each test.
/// Tests never touch the user's real folders: {Desktop}/{Downloads} resolve to these temporary folders.
/// </summary>
public sealed class TestSandbox : IDisposable
{
    public static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0);

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "TidyDeskTest", Guid.NewGuid().ToString("N"));
    public string Desktop => Path.Combine(Root, "Desktop");
    public string Downloads => Path.Combine(Root, "Downloads");
    public string Documents => Path.Combine(Root, "Documents");
    public AppPaths Paths => AppPaths.UnderRoot(Path.Combine(Root, "app"));

    public TestSandbox()
    {
        Directory.CreateDirectory(Desktop);
        Directory.CreateDirectory(Downloads);
        Directory.CreateDirectory(Documents);
    }

    public string? Resolve(string token) => token.ToLowerInvariant() switch
    {
        "desktop" => Desktop,
        "downloads" => Downloads,
        "documents" => Documents,
        "userprofile" => Root,
        _ => null,
    };

    /// <summary>Creates a file with the given age.</summary>
    public string CreateFile(string folder, string name, int ageDays = 30, string content = "test")
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, content);
        var time = Now.AddDays(-ageDays);
        File.SetCreationTime(path, time);
        File.SetLastWriteTime(path, time);
        return path;
    }

    /// <summary>Embedded categories + temporary Desktop/Downloads, 7-day age threshold.</summary>
    public RuleSet DefaultRules()
    {
        var rules = RuleSet.CreateDefault();
        rules.MinAgeDays = 7;
        rules.Sources =
        [
            new SourceFolderRule { Folder = "{Desktop}", Target = @"{Desktop}\Sorted" },
            new SourceFolderRule { Folder = "{Downloads}", Target = @"{Downloads}\Sorted" },
        ];
        return rules;
    }

    /// <summary>A guard that treats the sandbox root as the "user profile" and blocks nothing else.</summary>
    public PathGuard Guard => new(Root, []);

    public Organizer CreateOrganizer(RuleSet? rules = null) =>
        new(rules ?? DefaultRules(), Resolve, CreateJournal(), NullLog.Instance, () => Now);

    public UndoJournal CreateJournal() => new(Paths.JournalDir, NullLog.Instance);

    public string Organized(string folder) => Path.Combine(folder, "Sorted");

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
