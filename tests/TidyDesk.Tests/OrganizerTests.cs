using TidyDesk.Core;

namespace TidyDesk.Tests;

public sealed class OrganizerTests : IDisposable
{
    private readonly TestSandbox _box = new();

    public void Dispose() => _box.Dispose();

    private string Month(int ageDays = 30) => TestSandbox.Now.AddDays(-ageDays).ToString("yyyy-MM");

    [Fact]
    public void Plan_does_not_touch_any_file()
    {
        var file = _box.CreateFile(_box.Desktop, "report.docx");

        var plan = _box.CreateOrganizer().CreatePlan();

        Assert.Single(plan.Moves);
        Assert.True(File.Exists(file));
        Assert.False(Directory.Exists(_box.Organized(_box.Desktop)));
    }

    // Default (Turkish) folder names; English names are covered in RulesTests.
    [Theory]
    [InlineData("report.docx", "Belgeler")]
    [InlineData("INVOICE.PDF", "PDF")]
    [InlineData("budget.xlsx", "Tablolar")]
    [InlineData("slides.pptx", "Sunumlar")]
    [InlineData("photo.JPG", "Görseller")]
    [InlineData("backup.zip", "Arşivler")]
    [InlineData("setup.msi", "Kurulum Dosyaları")]
    [InlineData("clip.webm", "Video ve Ses")]
    public void Moves_by_extension_into_category_and_month_folder(string name, string category)
    {
        var file = _box.CreateFile(_box.Downloads, name);

        var result = _box.CreateOrganizer().Run();

        var expected = Path.Combine(_box.Organized(_box.Downloads), category, Month(), name);
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(expected), $"Expected: {expected}");
        Assert.Equal(expected, Assert.Single(result.Moved).DestinationPath);
    }

    [Fact]
    public void Files_newer_than_threshold_are_not_moved()
    {
        var fresh = _box.CreateFile(_box.Desktop, "new.pdf", ageDays: 2);
        var old = _box.CreateFile(_box.Desktop, "old.pdf", ageDays: 8);

        var plan = _box.CreateOrganizer().CreatePlan();

        Assert.Equal(old, Assert.Single(plan.Moves).SourcePath);
        var skipped = Assert.Single(plan.Skipped);
        Assert.Equal(fresh, skipped.Path);
        Assert.Contains("Newer than", skipped.Reason);
    }

    [Fact]
    public void Recently_copied_old_file_counts_as_new()
    {
        // An old file copied to the desktop today: old modified date, new creation date.
        var path = _box.CreateFile(_box.Desktop, "copy.docx", ageDays: 400);
        File.SetCreationTime(path, TestSandbox.Now.AddHours(-1));

        var plan = _box.CreateOrganizer().CreatePlan();

        Assert.Empty(plan.Moves);
    }

    [Fact]
    public void Shortcuts_folders_hidden_and_temp_files_are_excluded()
    {
        _box.CreateFile(_box.Desktop, "App.lnk");
        _box.CreateFile(_box.Desktop, "Site.url");
        _box.CreateFile(_box.Desktop, "desktop.ini");
        _box.CreateFile(_box.Desktop, "~$report.docx");
        _box.CreateFile(_box.Downloads, "downloading.zip.crdownload");
        var hidden = _box.CreateFile(_box.Desktop, "hidden.txt");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        var subfolder = Path.Combine(_box.Desktop, "Project Folder");
        Directory.CreateDirectory(subfolder);
        _box.CreateFile(subfolder, "inside.docx");

        var plan = _box.CreateOrganizer().CreatePlan();

        Assert.Empty(plan.Moves);
        Assert.True(Directory.Exists(subfolder));
    }

    [Fact]
    public void Document_with_office_owner_file_is_skipped()
    {
        _box.CreateFile(_box.Desktop, "meeting-notes.docx");
        // Word creates an owner file named "~$" + the name without its first two characters.
        _box.CreateFile(_box.Desktop, "~$eting-notes.docx");

        var plan = _box.CreateOrganizer().CreatePlan();

        Assert.Empty(plan.Moves);
        Assert.Equal("Open in Office", Assert.Single(plan.Skipped).Reason);
    }

    [Fact]
    public void Locked_file_is_skipped_and_stays_in_place()
    {
        var locked = _box.CreateFile(_box.Desktop, "locked.xlsx");
        var free = _box.CreateFile(_box.Desktop, "free.xlsx");

        OrganizeResult result;
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            result = _box.CreateOrganizer().Run();
        }

        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(free));
        Assert.Equal(free, Assert.Single(result.Moved).SourcePath);
    }

    [Fact]
    public void File_locked_after_planning_is_not_moved()
    {
        var path = _box.CreateFile(_box.Desktop, "opened-later.pptx");
        var organizer = _box.CreateOrganizer();
        var plan = organizer.CreatePlan();

        OrganizeResult result;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = organizer.Execute(plan);
        }

        Assert.True(File.Exists(path));
        Assert.Empty(result.Moved);
        Assert.Single(result.Failed);
    }

    [Fact]
    public void Name_conflict_renames_instead_of_overwriting()
    {
        var targetDir = Path.Combine(_box.Organized(_box.Desktop), "PDF", Month());
        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, "invoice.pdf"), "OLD");
        File.WriteAllText(Path.Combine(targetDir, "invoice (2).pdf"), "OLD-2");
        _box.CreateFile(_box.Desktop, "invoice.pdf", content: "NEW");

        _box.CreateOrganizer().Run();

        Assert.Equal("OLD", File.ReadAllText(Path.Combine(targetDir, "invoice.pdf")));
        Assert.Equal("OLD-2", File.ReadAllText(Path.Combine(targetDir, "invoice (2).pdf")));
        Assert.Equal("NEW", File.ReadAllText(Path.Combine(targetDir, "invoice (3).pdf")));
    }

    [Fact]
    public void Two_sources_with_same_file_name_do_not_collide()
    {
        _box.CreateFile(_box.Desktop, "contract.pdf", content: "DESKTOP");
        _box.CreateFile(_box.Downloads, "contract.pdf", content: "DOWNLOADS");
        var rules = _box.DefaultRules();
        foreach (var s in rules.Sources)
            s.Target = "{Documents}\\Archive";

        var result = _box.CreateOrganizer(rules).Run();

        Assert.Equal(2, result.Moved.Count);
        Assert.Equal(2, result.Moved.Select(m => m.DestinationPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Month_folders_can_be_off_and_uncategorized_files_left_alone()
    {
        var rules = _box.DefaultRules();
        rules.DateFolderFormat = "";
        rules.MoveUncategorized = false;
        _box.CreateFile(_box.Desktop, "note.txt");
        var unknown = _box.CreateFile(_box.Desktop, "special.abc");

        _box.CreateOrganizer(rules).Run();

        Assert.True(File.Exists(Path.Combine(_box.Organized(_box.Desktop), "Belgeler", "note.txt")));
        Assert.True(File.Exists(unknown));
    }

    [Fact]
    public void Unknown_files_stay_put_by_default_and_go_to_other_when_enabled()
    {
        var unknown = _box.CreateFile(_box.Desktop, "special.xyz");
        Assert.Empty(_box.CreateOrganizer().CreatePlan().Moves);

        var rules = _box.DefaultRules();
        rules.MoveUncategorized = true;
        var plan = _box.CreateOrganizer(rules).CreatePlan();

        Assert.Equal(Path.Combine(_box.Organized(_box.Desktop), "Diğer", Month(), "special.xyz"), Assert.Single(plan.Moves).DestinationPath);
        Assert.True(File.Exists(unknown));
    }

    [Fact]
    public void Dll_with_exe_signature_is_not_taken_for_an_installer()
    {
        var path = Path.Combine(_box.Desktop, "helper.dll");
        File.WriteAllBytes(path, [0x4D, 0x5A, 0x90, 0x00, .. new byte[32]]);
        File.SetCreationTime(path, TestSandbox.Now.AddDays(-30));
        File.SetLastWriteTime(path, TestSandbox.Now.AddDays(-30));
        var rules = _box.DefaultRules();
        rules.MoveUncategorized = true;

        var move = Assert.Single(_box.CreateOrganizer(rules).CreatePlan().Moves);

        Assert.Equal("Diğer", move.Category);
    }

    [Fact]
    public void Execute_rechecks_the_folder_and_moves_nothing_when_it_is_no_longer_allowed()
    {
        var file = _box.CreateFile(_box.Desktop, "report.docx");
        var plan = _box.CreateOrganizer().CreatePlan();
        var strict = new PathGuard(Path.Combine(_box.Root, "other-profile"), []); // the sandbox is outside this "profile"
        var organizer = new Organizer(_box.DefaultRules(), _box.Resolve, _box.CreateJournal(), NullLog.Instance, () => TestSandbox.Now, strict);

        var result = organizer.Execute(plan);

        Assert.Empty(result.Moved);
        Assert.Single(result.Failed);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Junction_to_a_forbidden_folder_is_rejected()
    {
        var target = Path.Combine(Path.GetTempPath(), "TidyDeskLinkTarget", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        var link = Path.Combine(_box.Root, "Linked");
        try
        {
            using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true })!)
                p.WaitForExit();
            Assert.True(Directory.Exists(link), "mklink failed");

            // The link sits inside the sandbox "profile", but its target does not.
            Assert.NotNull(_box.Guard.Check(link));
            Assert.Null(_box.Guard.Check(_box.Desktop));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link); // removes the link only, not the target
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public void Same_folder_listed_twice_is_scanned_once()
    {
        var rules = _box.DefaultRules();
        rules.Sources.Add(new SourceFolderRule { Folder = "{Desktop}", Target = "{Documents}\\Other" });
        _box.CreateFile(_box.Desktop, "single.pdf");

        var plan = _box.CreateOrganizer(rules).CreatePlan();

        Assert.Single(plan.Moves);
    }

    [Fact]
    public void Missing_folder_and_unknown_token_are_reported_as_errors()
    {
        var rules = _box.DefaultRules();
        rules.Sources =
        [
            new SourceFolderRule { Folder = Path.Combine(_box.Root, "missing"), Target = "{Documents}\\X" },
            new SourceFolderRule { Folder = "{Desk}", Target = "{Documents}\\X" },
        ];

        var plan = _box.CreateOrganizer(rules).CreatePlan();

        Assert.Equal(2, plan.Errors.Count);
        Assert.Empty(plan.Moves);
    }

    [Fact]
    public void No_file_is_ever_deleted_count_and_content_are_preserved()
    {
        var contents = new Dictionary<string, string>();
        foreach (var name in new[] { "a.docx", "b.pdf", "c.xlsx", "d.png", "e.zip", "f.unknown" })
        {
            _box.CreateFile(_box.Desktop, name, content: "content-" + name);
            contents[name] = "content-" + name;
        }

        _box.CreateOrganizer().Run();

        var all = Directory.GetFiles(_box.Desktop, "*", SearchOption.AllDirectories);
        Assert.Equal(contents.Count, all.Length);
        foreach (var file in all)
            Assert.Equal(contents[Path.GetFileName(file)], File.ReadAllText(file));
    }
}
