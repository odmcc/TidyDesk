using System.IO.Enumeration;

namespace TidyDesk.Core;

/// <param name="ByContent">The type was detected from the file's content (signature), not its extension.</param>
public sealed record PlannedMove(string SourcePath, string DestinationPath, string Category, string SourceFolder, long Size, bool ByContent = false);

public sealed record SkippedFile(string Path, string Reason);

public sealed class OrganizePlan
{
    public List<PlannedMove> Moves { get; } = [];
    /// <summary>Files that match the rules but are not moved right now (too new, open, locked...).</summary>
    public List<SkippedFile> Skipped { get; } = [];
    /// <summary>General problems such as a missing folder or an invalid path.</summary>
    public List<string> Errors { get; } = [];
}

public sealed class OrganizeResult
{
    public string? RunId { get; set; }
    public List<PlannedMove> Moved { get; } = [];
    public List<SkippedFile> Failed { get; } = [];
}

/// <summary>
/// The organizing engine. It NEVER deletes files; it only moves them and writes every move to the undo journal.
/// Only files directly inside a source folder are considered; subfolders and folders themselves are left alone.
/// When a <paramref name="guard"/> is given, system folders and disallowed paths are rejected.
/// </summary>
public sealed class Organizer(RuleSet rules, Func<string, string?> folderResolver, UndoJournal journal, ILog log,
    Func<DateTime>? clock = null, PathGuard? guard = null)
{
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.Now);

    /// <summary>Dry run: works out what would happen without touching any file.</summary>
    public OrganizePlan CreatePlan()
    {
        var plan = new OrganizePlan();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in rules.Sources.Where(s => s.Enabled))
        {
            string folder, target;
            try
            {
                folder = PathTokens.Expand(source.Folder, folderResolver);
                target = PathTokens.Expand(source.Target, folderResolver);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
            {
                plan.Errors.Add($"Invalid folder rule ({source.Folder} -> {source.Target}): {ex.Message}");
                continue;
            }

            if (!seenSources.Add(folder))
                continue; // Same folder listed twice (e.g. Downloads redirected to the Desktop)
            if (guard?.Check(folder) is { } denied)
            {
                plan.Errors.Add($"{folder}: {denied}");
                continue;
            }
            if (!Directory.Exists(folder))
            {
                plan.Errors.Add($"Folder not found: {folder}");
                continue;
            }

            PlanFolder(folder, target, plan, reserved);
        }
        return plan;
    }

    private void PlanFolder(string folder, string targetRoot, OrganizePlan plan, HashSet<string> reserved)
    {
        IEnumerable<FileInfo> files;
        try
        {
            files = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            plan.Errors.Add($"Couldn't read folder: {folder} ({ex.Message})");
            return;
        }

        foreach (var file in files)
        {
            if (IsExcluded(file))
                continue;
            var (category, byContent) = Categorize(file);
            if (category is null)
                continue;

            if (CheckBlocked(file) is { } reason)
            {
                plan.Skipped.Add(new SkippedFile(file.FullName, reason));
                continue;
            }

            var destination = BuildDestination(targetRoot, category, file, reserved);
            reserved.Add(destination);
            plan.Moves.Add(new PlannedMove(file.FullName, destination, category, folder, file.Length, byContent));
        }
    }

    /// <summary>Applies the plan. Every file is checked again right before it is moved.</summary>
    public OrganizeResult Execute(OrganizePlan plan)
    {
        var result = new OrganizeResult();
        if (plan.Moves.Count == 0)
            return result;

        var runId = journal.NewRunId();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var move in plan.Moves)
        {
            var file = new FileInfo(move.SourcePath);
            try
            {
                if (!file.Exists)
                {
                    result.Failed.Add(new SkippedFile(move.SourcePath, "The file is no longer there."));
                    continue;
                }
                if (CheckBlocked(file) is { } reason)
                {
                    result.Failed.Add(new SkippedFile(move.SourcePath, reason));
                    continue;
                }

                // A file with the same name may have appeared at the destination since planning; check the name again.
                var destination = FileSafety.MakeUnique(move.DestinationPath, reserved);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(file.FullName, destination, overwrite: false);
                reserved.Add(destination);
                journal.Record(runId, file.FullName, destination);
                result.Moved.Add(move with { DestinationPath = destination });
                log.Info($"Moved: {file.FullName} -> {destination}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Failed.Add(new SkippedFile(move.SourcePath, ex.Message));
                log.Error($"Couldn't move: {move.SourcePath}", ex);
            }
        }

        if (result.Moved.Count > 0)
            result.RunId = runId;
        log.Info($"Run finished ({runId}): {result.Moved.Count} moved, {result.Failed.Count} skipped.");
        return result;
    }

    public OrganizeResult Run() => Execute(CreatePlan());

    /// <summary>Looks at the extension first; if there is none or it's unknown, tries to detect the type from the content.</summary>
    private (string? Category, bool ByContent) Categorize(FileInfo file)
    {
        if (CategoryOf(file.Extension) is { } byExtension)
            return (byExtension, false);
        if (!FileSafety.IsCloudPlaceholder(file.Attributes) // don't read online-only OneDrive files: it would download them
            && ContentSniffer.DetectExtension(file.FullName) is { } detected
            && CategoryOf(detected) is { } byContent)
            return (byContent, true);
        return (rules.MoveUncategorized ? FileSafety.SanitizeFolderName(rules.OtherCategoryName) : null, false);
    }

    private string? CategoryOf(string extension)
    {
        var ext = extension.ToLowerInvariant();
        if (ext.Length == 0)
            return null;
        foreach (var category in rules.Categories)
        {
            if (category.Extensions.Contains(ext))
                return FileSafety.SanitizeFolderName(category.Name);
        }
        return null;
    }

    private bool IsExcluded(FileInfo file)
    {
        if (rules.SkipHiddenAndSystem && (file.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
            return true;
        if (rules.ExcludeExtensions.Contains(file.Extension.ToLowerInvariant()))
            return true;
        if (file.Extension.Length == 0 && file.Name.StartsWith('.'))
            return true;
        return rules.ExcludeNamePatterns.Any(p => FileSystemName.MatchesSimpleExpression(p, file.Name, ignoreCase: true));
    }

    /// <summary>Returns the reason when a file matches the rules but shouldn't be moved right now.</summary>
    private string? CheckBlocked(FileInfo file)
    {
        var lastTouched = file.CreationTime > file.LastWriteTime ? file.CreationTime : file.LastWriteTime;
        if (rules.MinAgeDays > 0 && _now() - lastTouched < TimeSpan.FromDays(rules.MinAgeDays))
            return $"Newer than {rules.MinAgeDays} days";
        if (FileSafety.HasOfficeOwnerFile(file))
            return "Open in Office";
        if (FileSafety.IsInUse(file))
            return "Open in another program";
        return null;
    }

    private string BuildDestination(string targetRoot, string category, FileInfo file, HashSet<string> reserved)
    {
        var dir = Path.Combine(targetRoot, category);
        if (!string.IsNullOrEmpty(rules.DateFolderFormat))
        {
            var lastTouched = file.CreationTime > file.LastWriteTime ? file.CreationTime : file.LastWriteTime;
            dir = Path.Combine(dir, FileSafety.SanitizeFolderName(lastTouched.ToString(rules.DateFolderFormat)));
        }
        return FileSafety.MakeUnique(Path.Combine(dir, file.Name), reserved);
    }
}
