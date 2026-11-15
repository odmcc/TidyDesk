using TidyDesk.Core;

namespace TidyDesk.Tests;

public sealed class SubfolderTests : IDisposable
{
    private readonly TestSandbox _box = new();

    public void Dispose() => _box.Dispose();

    private RuleSet Rules(bool includeSubfolders)
    {
        var rules = RuleSet.CreateDefault();
        rules.DateFolderFormat = "";
        rules.Sources = [new SourceFolderRule { Folder = _box.Documents, Target = _box.Documents, IncludeSubfolders = includeSubfolders }];
        return rules;
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine([_box.Documents, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Subfolders_are_left_alone_by_default()
    {
        _box.CreateFile(Dir("2024"), "report.pdf");

        Assert.Empty(_box.CreateOrganizer(Rules(includeSubfolders: false)).CreatePlan().Moves);
    }

    [Fact]
    public void Each_subfolder_is_organized_in_its_own_place()
    {
        _box.CreateFile(_box.Documents, "offer.pdf");
        _box.CreateFile(Dir("2024"), "report.pdf");
        _box.CreateFile(Dir("2024", "Trip"), "photo.jpg");

        var result = _box.CreateOrganizer(Rules(includeSubfolders: true)).Run();

        Assert.Equal(3, result.Moved.Count);
        Assert.True(File.Exists(Path.Combine(_box.Documents, "PDF", "offer.pdf")));
        Assert.True(File.Exists(Path.Combine(_box.Documents, "2024", "PDF", "report.pdf")));
        Assert.True(File.Exists(Path.Combine(_box.Documents, "2024", "Trip", "Görseller", "photo.jpg")));
        // Every move counts towards the folder the user picked.
        Assert.All(result.Moved, m => Assert.Equal(_box.Documents, m.SourceFolder));
    }

    [Fact]
    public void Running_twice_does_not_move_organized_files_again()
    {
        _box.CreateFile(Dir("2024"), "report.pdf");
        _box.CreateOrganizer(Rules(includeSubfolders: true)).Run();

        Assert.Empty(_box.CreateOrganizer(Rules(includeSubfolders: true)).CreatePlan().Moves);
        // English folder names are recognized as organized too.
        _box.CreateFile(Dir("Images"), "old.jpg");
        Assert.Empty(_box.CreateOrganizer(Rules(includeSubfolders: true)).CreatePlan().Moves);
    }

    [Fact]
    public void Repositories_app_bundles_hidden_and_tool_folders_are_skipped()
    {
        Dir("code", ".git");
        _box.CreateFile(Dir("code"), "logo.png");
        _box.CreateFile(Dir("Tool.app", "Contents"), "icon.png");
        _box.CreateFile(Dir(".cache"), "a.pdf");
        _box.CreateFile(Dir("web", "node_modules", "pkg"), "readme.md");
        _box.CreateFile(Dir("TidyDesk-Data"), "notes.txt");

        Assert.Empty(_box.CreateOrganizer(Rules(includeSubfolders: true)).CreatePlan().Moves);
    }

    [Fact]
    public void Links_inside_the_folder_are_not_followed()
    {
        var outside = Directory.CreateTempSubdirectory("TidyDeskOutside").FullName;
        var link = Path.Combine(_box.Documents, "linked");
        try
        {
            _box.CreateFile(outside, "elsewhere.pdf");
            if (OperatingSystem.IsWindows())
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd", $"/c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true })!;
                p.WaitForExit();
            }
            else
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            Assert.True(Directory.Exists(link));

            Assert.Empty(_box.CreateOrganizer(Rules(includeSubfolders: true)).CreatePlan().Moves);
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Undo_puts_subfolder_files_back()
    {
        var file = _box.CreateFile(Dir("2024"), "report.pdf", content: "R");
        _box.CreateOrganizer(Rules(includeSubfolders: true)).Run();
        Assert.False(File.Exists(file));

        var undo = _box.CreateJournal().UndoLast();

        Assert.Equal(1, undo!.Restored);
        Assert.Equal("R", File.ReadAllText(file));
        Assert.False(Directory.Exists(Path.Combine(_box.Documents, "2024", "PDF")));
        Assert.True(Directory.Exists(Path.Combine(_box.Documents, "2024")));
    }
}

public sealed class UserRulesTests : IDisposable
{
    private readonly TestSandbox _box = new();

    public void Dispose() => _box.Dispose();

    private OrganizerService Service() => new(_box.Paths, NullLog.Instance, new PathGuard(_box.Root, [], []), () => TestSandbox.Now);

    private OrganizeOptions Options => new([_box.Desktop], MinAgeDays: 0, MonthFolders: false);

    [Fact]
    public void Rules_file_starts_as_a_copy_of_the_built_in_rules()
    {
        var service = Service();
        var path = service.EnsureRulesFile();

        Assert.Equal(_box.Paths.RulesFile, path);
        Assert.Equal(RuleSet.CreateDefault().Categories.Count, RuleSet.Parse(File.ReadAllText(path)).Categories.Count);
        File.WriteAllText(path, "{}");
        service.EnsureRulesFile();
        Assert.Equal("{}", File.ReadAllText(path)); // an existing file is never overwritten
    }

    [Fact]
    public void Users_own_categories_are_used()
    {
        Directory.CreateDirectory(_box.Paths.DataDir);
        File.WriteAllText(_box.Paths.RulesFile, """{ "categories": [ { "name": "Çizimler", "extensions": [".dwg"] } ] }""");
        _box.CreateFile(_box.Desktop, "plan.dwg");
        _box.CreateFile(_box.Desktop, "report.pdf");

        var plan = Service().Analyze(Options);

        Assert.Equal("Çizimler", Assert.Single(plan.Moves).Category);
        Assert.Empty(plan.Errors);
    }

    [Fact]
    public void A_broken_rules_file_falls_back_to_the_built_in_rules_and_says_so()
    {
        Directory.CreateDirectory(_box.Paths.DataDir);
        File.WriteAllText(_box.Paths.RulesFile, "{ \"categories\": [ ");
        _box.CreateFile(_box.Desktop, "report.pdf");

        var plan = Service().Analyze(Options);

        Assert.Equal("PDF", Assert.Single(plan.Moves).Category);
        Assert.StartsWith("Your rules file has an error", Assert.Single(plan.Errors));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData("a/b")]
    public void Category_names_cannot_leave_the_folder(string name)
    {
        Directory.CreateDirectory(_box.Paths.DataDir);
        File.WriteAllText(_box.Paths.RulesFile, $$"""{ "categories": [ { "name": "{{name}}", "extensions": [".pdf"] } ] }""");
        _box.CreateFile(_box.Desktop, "report.pdf");

        var move = Assert.Single(Service().Analyze(Options).Moves);

        Assert.Equal(_box.Desktop, Path.GetDirectoryName(Path.GetDirectoryName(move.DestinationPath)));
    }
}

public sealed class RenamedVolumeTests : IDisposable
{
    private readonly TestSandbox _box = new();

    public void Dispose() => _box.Dispose();

    // A drive letter (Windows) or volume name (Mac) that no longer exists.
    private static string OldRoot => OperatingSystem.IsWindows() ? @"Q:\" : "/Volumes/OLDUSB";

    [Fact]
    public void Undo_finds_files_when_the_usb_stick_comes_back_under_another_name()
    {
        // The stick was E: (or /Volumes/OLDUSB) during the run and is now mounted at the sandbox root.
        var moved = _box.CreateFile(Directory.CreateDirectory(Path.Combine(_box.Root, "Photos", "Görseller")).FullName, "a.jpg", content: "A");
        var journal = new UndoJournal(_box.Paths.JournalDir, NullLog.Instance, () => [_box.Root]);
        journal.Record("run1", Path.Combine(OldRoot, "Photos", "a.jpg"), Path.Combine(OldRoot, "Photos", "Görseller", "a.jpg"));

        var undo = journal.Undo("run1");

        Assert.Equal(1, undo.Restored);
        Assert.False(File.Exists(moved));
        Assert.Equal("A", File.ReadAllText(Path.Combine(_box.Root, "Photos", "a.jpg")));
    }

    [Fact]
    public void Undo_does_not_guess_when_two_disks_have_the_file()
    {
        var other = Directory.CreateTempSubdirectory("TidyDeskOtherDisk").FullName;
        try
        {
            foreach (var root in new[] { _box.Root, other })
                _box.CreateFile(Directory.CreateDirectory(Path.Combine(root, "Photos", "Görseller")).FullName, "a.jpg");
            var journal = new UndoJournal(_box.Paths.JournalDir, NullLog.Instance, () => [_box.Root, other]);
            journal.Record("run1", Path.Combine(OldRoot, "Photos", "a.jpg"), Path.Combine(OldRoot, "Photos", "Görseller", "a.jpg"));

            var undo = journal.Undo("run1");

            Assert.Equal(0, undo.Restored);
            Assert.Single(undo.Failed);
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    [WindowsFact]
    public void System_drive_files_are_never_looked_for_elsewhere()
    {
        Assert.Null(UndoJournal.VolumeRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop", "a.pdf")));
        Assert.Equal(@"Q:\", UndoJournal.VolumeRoot(@"Q:\Photos\a.jpg"));
    }
}

public sealed class OpenFilesTests : IDisposable
{
    private readonly TestSandbox _box = new();

    public void Dispose() => _box.Dispose();

    [Fact]
    public void Lsof_output_is_parsed_into_paths()
    {
        var output = "p123\nfcwd\nn/Users/alex\nf12\nn/Users/alex/Desktop/movie.mp4\nf13\nnlocalhost:8080\n";

        var paths = FileSafety.ParseLsof(output);

        Assert.Contains("/Users/alex/Desktop/movie.mp4", paths);
        Assert.DoesNotContain("localhost:8080", paths);
    }

    [MacFact]
    public void File_open_in_another_program_is_skipped_on_mac()
    {
        var file = _box.CreateFile(_box.Desktop, "movie.mp4");
        _box.CreateFile(_box.Desktop, "free.pdf");
        // "tail -f" keeps the file open without locking it, like a video player or Preview.
        using var holder = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("tail", ["-f", file]) { RedirectStandardOutput = true })!;
        try
        {
            Thread.Sleep(500);
            var plan = _box.CreateOrganizer().CreatePlan();

            Assert.Equal("free.pdf", Path.GetFileName(Assert.Single(plan.Moves).SourcePath));
            Assert.Equal("Open in another program", Assert.Single(plan.Skipped).Reason);
        }
        finally
        {
            holder.Kill();
        }
    }
}

public sealed class CommandLineTests : IDisposable
{
    private readonly TestSandbox _box = new();

    public void Dispose() => _box.Dispose();

    private OrganizerService Service() => new(_box.Paths, NullLog.Instance, new PathGuard(_box.Root, [], []), () => TestSandbox.Now);

    private (int Code, string Output) Run(params string[] args)
    {
        var output = new StringWriter();
        var code = CommandLine.Run(args, Service(), _box.Resolve, output);
        return (code, output.ToString());
    }

    [Fact]
    public void Dry_run_moves_nothing()
    {
        var file = _box.CreateFile(_box.Desktop, "report.pdf");

        var (code, output) = Run("--run", "--dry-run", "--folder", _box.Desktop);

        Assert.Equal(CommandLine.Ok, code);
        Assert.Contains("1 files would be moved", output);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Run_moves_and_undo_puts_back()
    {
        var file = _box.CreateFile(_box.Desktop, "report.pdf");

        Assert.Equal(CommandLine.Ok, Run("--run", "--folder", _box.Desktop).Code);
        Assert.False(File.Exists(file));
        Assert.Equal(CommandLine.Ok, Run("--undo").Code);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Saved_app_choices_are_used_without_folder()
    {
        _box.CreateFile(Directory.CreateDirectory(Path.Combine(_box.Documents, "2024")).FullName, "report.pdf");
        Service().SaveSettings(new UserSettings { Folders = [_box.Documents], Subfolders = [_box.Documents] });

        var (code, output) = Run("--run");

        Assert.Equal(CommandLine.Ok, code);
        Assert.Contains("1 files moved", output);
        Assert.True(File.Exists(Path.Combine(_box.Documents, "2024", "PDF", "report.pdf")));
    }

    [Fact]
    public void Older_than_skips_new_files()
    {
        _box.CreateFile(_box.Desktop, "new.pdf", ageDays: 2);
        _box.CreateFile(_box.Desktop, "old.pdf", ageDays: 60);

        var (_, output) = Run("--run", "--dry-run", "--older-than", "30", "--folder", _box.Desktop);

        Assert.Contains("old.pdf ->", output);
        Assert.DoesNotContain("new.pdf ->", output);
    }

    [Theory]
    [InlineData("--run", "--bogus")]
    [InlineData("--run", "--older-than", "abc")]
    [InlineData("--run", "--folder")]
    public void Bad_options_are_usage_errors(params string[] args)
    {
        Assert.Equal(CommandLine.UsageError, Run(args).Code);
    }

    [Fact]
    public void Disallowed_folder_is_refused()
    {
        var (code, output) = Run("--run", "--folder", Path.GetTempPath()); // outside the sandbox "profile"

        Assert.Equal(CommandLine.Failed, code);
        Assert.Contains("!", output);
    }
}
