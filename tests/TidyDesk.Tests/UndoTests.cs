using TidyDesk.Core;

namespace TidyDesk.Tests;

public sealed class UndoTests : IDisposable
{
    private readonly TestSandbox _box = new();

    public void Dispose() => _box.Dispose();

    [Fact]
    public void Undo_moves_files_back_and_removes_empty_folders()
    {
        var a = _box.CreateFile(_box.Desktop, "a.docx", content: "A");
        var b = _box.CreateFile(_box.Downloads, "b.pdf", content: "B");
        _box.CreateOrganizer().Run();
        Assert.False(File.Exists(a));

        var undo = _box.CreateJournal().UndoLast();

        Assert.NotNull(undo);
        Assert.Equal(2, undo.Restored);
        Assert.Equal("A", File.ReadAllText(a));
        Assert.Equal("B", File.ReadAllText(b));
        Assert.False(Directory.Exists(_box.Organized(_box.Desktop)));
        Assert.False(Directory.Exists(_box.Organized(_box.Downloads)));
    }

    [Fact]
    public void Undone_run_cannot_be_undone_again()
    {
        _box.CreateFile(_box.Desktop, "a.docx");
        _box.CreateOrganizer().Run();
        var journal = _box.CreateJournal();

        Assert.Single(journal.ListRuns());
        journal.UndoLast();

        Assert.Empty(journal.ListRuns());
        Assert.Null(journal.UndoLast());
    }

    [Fact]
    public void Undo_last_only_affects_the_latest_run()
    {
        var first = _box.CreateFile(_box.Desktop, "first.docx");
        _box.CreateOrganizer().Run();
        Thread.Sleep(5); // distinct run id
        var second = _box.CreateFile(_box.Desktop, "second.docx");
        _box.CreateOrganizer().Run();

        _box.CreateJournal().UndoLast();

        Assert.True(File.Exists(second));
        Assert.False(File.Exists(first));
        Assert.Single(_box.CreateJournal().ListRuns());
    }

    [Fact]
    public void Undo_does_not_overwrite_a_new_file_at_the_original_path()
    {
        var original = _box.CreateFile(_box.Desktop, "report.docx", content: "OLD");
        _box.CreateOrganizer().Run();
        File.WriteAllText(original, "USER'S NEW FILE");

        _box.CreateJournal().UndoLast();

        Assert.Equal("USER'S NEW FILE", File.ReadAllText(original));
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(_box.Desktop, "report (2).docx")));
    }

    [Fact]
    public void Undo_reports_missing_files_and_restores_the_rest()
    {
        _box.CreateFile(_box.Desktop, "kept.docx");
        _box.CreateFile(_box.Desktop, "moved-away.pdf");
        var result = _box.CreateOrganizer().Run();
        var moved = result.Moved.Single(m => m.SourcePath.EndsWith("moved-away.pdf")).DestinationPath;
        File.Move(moved, Path.Combine(_box.Documents, "moved-away.pdf"));

        var undo = _box.CreateJournal().UndoLast()!;

        Assert.Equal(1, undo.Restored);
        Assert.Single(undo.Failed);
        Assert.True(File.Exists(Path.Combine(_box.Desktop, "kept.docx")));
    }

    [Fact]
    public void An_older_run_can_be_undone_on_its_own()
    {
        var first = _box.CreateFile(_box.Desktop, "first.docx");
        _box.CreateOrganizer().Run();
        Thread.Sleep(5);
        var second = _box.CreateFile(_box.Desktop, "second.docx");
        _box.CreateOrganizer().Run();
        var journal = _box.CreateJournal();
        var older = journal.ListRuns()[1];

        journal.Undo(older.RunId);

        Assert.True(File.Exists(first));
        Assert.False(File.Exists(second));
        var all = journal.ListRuns(includeUndone: true);
        Assert.Equal(2, all.Count);
        Assert.True(all.Single(r => r.RunId == older.RunId).Undone);
        Assert.Single(journal.ReadRun(older.RunId));
    }
}
