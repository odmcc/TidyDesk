using TidyDesk.Core;

namespace TidyDesk.Tests;

public sealed class RulesTests : IDisposable
{
    private readonly TestSandbox _box = new();

    public void Dispose() => _box.Dispose();

    [Fact]
    public void Embedded_category_rules_are_loaded()
    {
        var rules = RuleSet.CreateDefault();

        Assert.Contains(rules.Categories, c => c.Name == "PDF");
        Assert.Contains(".lnk", rules.ExcludeExtensions);
        Assert.Equal(0, rules.MinAgeDays);
    }

    [Fact]
    public void Extensions_are_normalized()
    {
        var rules = RuleSet.Parse("""
            {
              // comments are allowed
              "categories": [ { "name": "CAD", "extensions": ["DWG", ".Dxf"] } ],
              "minAgeDays": -3,
            }
            """);

        Assert.Equal([".dwg", ".dxf"], rules.Categories[0].Extensions);
        Assert.Equal(0, rules.MinAgeDays);
    }

    [Fact]
    public void Path_tokens_and_environment_variables_are_expanded()
    {
        Environment.SetEnvironmentVariable("DD_TEST_SUB", "Sub");

        var path = PathTokens.Expand("{Desktop}\\%DD_TEST_SUB%\\Folder", _box.Resolve);

        Assert.Equal(Path.Combine(_box.Desktop, "Sub", "Folder"), path);
        Assert.Throws<InvalidOperationException>(() => PathTokens.Expand("{Unknown}\\x", _box.Resolve));
        Assert.Throws<InvalidOperationException>(() => PathTokens.Expand("relative\\path", _box.Resolve));
    }

    [Theory]
    [InlineData("Desktop")]
    [InlineData("Downloads")]
    [InlineData("Documents")]
    public void Known_folder_api_resolves_real_folder(string token)
    {
        // Only reads the path; no file in the real folders is touched.
        var path = KnownFolders.Resolve(token);

        Assert.NotNull(path);
        Assert.True(Directory.Exists(path), path);
    }

    [Fact]
    public void Settings_are_saved_and_loaded()
    {
        var file = _box.Paths.SettingsFile;
        new UserSettings { Folders = [_box.Desktop], CustomFolders = [_box.Documents], Language = "en" }.Save(file);

        var loaded = UserSettings.Load(file);

        Assert.Equal([_box.Desktop], loaded.Folders);
        Assert.Equal([_box.Documents], loaded.CustomFolders);
        Assert.Equal("en", loaded.Language);
    }

    [Fact]
    public void Corrupt_settings_file_falls_back_to_defaults()
    {
        Directory.CreateDirectory(_box.Paths.DataDir);
        File.WriteAllText(_box.Paths.SettingsFile, "{ broken");

        var loaded = UserSettings.Load(_box.Paths.SettingsFile);

        Assert.Null(loaded.Folders);
        Assert.Equal("tr", loaded.Language);
    }

    [Fact]
    public void Service_scans_moves_and_undoes()
    {
        var file = _box.CreateFile(_box.Desktop, "end-to-end.pdf");
        var service = new OrganizerService(_box.Paths, NullLog.Instance, _box.Guard, () => TestSandbox.Now);
        var options = new OrganizeOptions([_box.Desktop], MinAgeDays: 0, MonthFolders: false);

        var plan = service.Analyze(options);
        Assert.Equal(Path.Combine(_box.Desktop, "PDF", "end-to-end.pdf"), Assert.Single(plan.Moves).DestinationPath);
        Assert.True(File.Exists(file));

        var result = service.Execute(plan)!;
        Assert.Single(result.Moved);
        Assert.False(File.Exists(file));

        Assert.Equal(1, service.Undo(result.RunId!)!.Restored);
        Assert.True(File.Exists(file));
        Assert.False(Directory.Exists(Path.Combine(_box.Desktop, "PDF"))); // the now-empty category folder is removed
    }

    [Fact]
    public void Undo_never_deletes_the_source_folder_even_if_empty()
    {
        var file = _box.CreateFile(_box.Desktop, "only.pdf");
        var service = new OrganizerService(_box.Paths, NullLog.Instance, _box.Guard, () => TestSandbox.Now);
        var result = service.Execute(service.Analyze(new OrganizeOptions([_box.Desktop], 0, false)))!;
        File.Delete(result.Moved[0].DestinationPath); // the user deleted the moved file: the Desktop ends up empty after undo

        service.Undo(result.RunId!);

        Assert.True(Directory.Exists(_box.Desktop));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void English_language_uses_english_folder_names()
    {
        _box.CreateFile(_box.Desktop, "report.docx");
        var service = new OrganizerService(_box.Paths, NullLog.Instance, _box.Guard, () => TestSandbox.Now);

        var plan = service.Analyze(new OrganizeOptions([_box.Desktop], 0, false, Language: "en"));

        Assert.Contains(plan.Moves, m => m.DestinationPath == Path.Combine(_box.Desktop, "Documents", "report.docx"));
        Assert.All(RuleSet.CreateDefault().Categories, c => Assert.False(string.IsNullOrEmpty(c.NameEn)));
    }

    [Fact]
    public void Category_folders_are_not_scanned_again()
    {
        _box.CreateFile(_box.Desktop, "a.pdf");
        var service = new OrganizerService(_box.Paths, NullLog.Instance, _box.Guard, () => TestSandbox.Now);
        var options = new OrganizeOptions([_box.Desktop], 0, false);
        service.Execute(service.Analyze(options));

        Assert.Empty(service.Analyze(options).Moves);
    }

    [Fact]
    public void Service_does_not_scan_a_folder_rejected_by_the_guard()
    {
        var outside = Path.Combine(Path.GetTempPath(), "TidyDeskTest-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "a.pdf"), "x");
            var service = new OrganizerService(_box.Paths, NullLog.Instance, _box.Guard);

            var plan = service.Analyze(new OrganizeOptions([outside], 0, true));

            Assert.Empty(plan.Moves);
            Assert.Single(plan.Errors);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }
}
