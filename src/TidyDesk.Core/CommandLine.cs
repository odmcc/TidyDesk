namespace TidyDesk.Core;

/// <summary>
/// Command-line mode, for scripts and scheduled tasks. Same rules and the same undo history as the app.
///   TidyDesk --run                         tidy the folders selected in the app
///   TidyDesk --run --folder PATH ...       tidy these folders instead (repeatable)
///   TidyDesk --run --subfolders            with --folder: also organize their subfolders, each in place
///   TidyDesk --run --older-than DAYS       only move files older than DAYS
///   TidyDesk --run --dry-run               only show what would move
///   TidyDesk --undo                        undo the last run
/// Without --folder, --subfolders and --older-than, the choices saved in the app are used.
/// </summary>
public static class CommandLine
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int UsageError = 2;

    public static bool IsCommand(string[] args) =>
        args.Any(a => a.Equals("--run", StringComparison.OrdinalIgnoreCase)
                   || a.Equals("--undo", StringComparison.OrdinalIgnoreCase)
                   || a.Equals("--help", StringComparison.OrdinalIgnoreCase));

    public static string Usage =>
        """
        TidyDesk command line
          TidyDesk --run                     tidy the folders selected in the app
          TidyDesk --run --folder PATH       tidy PATH instead (repeat for more folders)
          TidyDesk --run --subfolders        with --folder: also organize subfolders, each in its own place
          TidyDesk --run --older-than DAYS   only move files older than DAYS days
          TidyDesk --run --dry-run           show what would move, move nothing
          TidyDesk --undo                    undo the last run
        Nothing is ever deleted; every run can be undone.
        """;

    public static int Run(string[] args, OrganizerService service, Func<string, string?> resolver, TextWriter output)
    {
        bool Has(string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (Has("--help"))
        {
            output.WriteLine(Usage);
            return Ok;
        }
        if (Has("--undo"))
        {
            var undo = service.Journal.ListRuns().FirstOrDefault() is { } last ? service.Undo(last.RunId) : null;
            if (undo is null)
            {
                output.WriteLine("Nothing to undo.");
                return Ok;
            }
            output.WriteLine($"{undo.Restored} files moved back.");
            foreach (var failed in undo.Failed)
                output.WriteLine($"  not found: {failed.Path}");
            return undo.Failed.Count == 0 ? Ok : Failed;
        }

        var settings = service.LoadSettings();
        var folders = new List<string>();
        int? olderThan = null;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals("--demo", StringComparison.OrdinalIgnoreCase))
            {
                i++; // handled by the app (test folders instead of the real ones)
                continue;
            }
            if (arg.Equals("--folder", StringComparison.OrdinalIgnoreCase) || arg.Equals("--older-than", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                    return Fail($"{arg} needs a value.", output);
                var value = args[++i];
                if (arg.Equals("--folder", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Path.IsPathFullyQualified(value) && !Directory.Exists(value))
                        return Fail($"Folder not found: {value}", output);
                    folders.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)));
                }
                else if (int.TryParse(value, out var days) && days >= 0)
                {
                    olderThan = days;
                }
                else
                {
                    return Fail($"--older-than needs a number of days, not \"{value}\".", output);
                }
            }
            else if (!new[] { "--run", "--dry-run", "--subfolders" }.Contains(arg, StringComparer.OrdinalIgnoreCase))
            {
                return Fail($"Unknown option: {arg}", output);
            }
        }

        IReadOnlyCollection<string> subfolders;
        if (folders.Count == 0)
        {
            folders = settings.Folders ?? new[] { "desktop", "downloads" }.Select(resolver).OfType<string>().ToList();
            subfolders = settings.Subfolders;
        }
        else
        {
            subfolders = Has("--subfolders") ? folders : [];
        }
        if (folders.Count == 0)
            return Fail("No folders are selected. Pick some in the app or use --folder.", output);

        var options = new OrganizeOptions(folders, olderThan ?? settings.MinAgeDays, MonthFolders: false, settings.Language, subfolders);
        var plan = service.Analyze(options);
        foreach (var error in plan.Errors)
            output.WriteLine($"! {error}");

        if (Has("--dry-run"))
        {
            foreach (var move in plan.Moves)
                output.WriteLine($"{move.SourcePath} -> {move.DestinationPath}");
            foreach (var skipped in plan.Skipped)
                output.WriteLine($"skipped: {skipped.Path} ({skipped.Reason})");
            output.WriteLine($"{plan.Moves.Count} files would be moved. Nothing was moved (--dry-run).");
            return plan.Errors.Count == 0 ? Ok : Failed;
        }

        var result = service.Execute(plan);
        if (result is null)
        {
            output.WriteLine("Another operation is running; try again.");
            return Failed;
        }
        output.WriteLine($"{result.Moved.Count} files moved.");
        foreach (var failed in result.Failed)
            output.WriteLine($"  left in place: {failed.Path} ({failed.Reason})");
        if (result.Moved.Count > 0)
            output.WriteLine("Undo with: TidyDesk --undo");
        return plan.Errors.Count == 0 && result.Failed.Count == 0 ? Ok : Failed;
    }

    private static int Fail(string message, TextWriter output)
    {
        output.WriteLine(message);
        output.WriteLine();
        output.WriteLine(Usage);
        return UsageError;
    }
}
