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
/// Only files directly inside a source folder are considered, unless the source includes subfolders: then every
/// subfolder is organized in its own place. Folders themselves are never moved.
/// When a <paramref name="guard"/> is given, system folders and disallowed paths are rejected.
/// </summary>
public sealed class Organizer(RuleSet rules, Func<string, string?> folderResolver, UndoJournal journal, ILog log,
    Func<DateTime>? clock = null, PathGuard? guard = null, string? appExe = null)
{
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.Now);
    // A running exe isn't locked against moving, and the app may run from the folder it tidies (a USB stick).
    private readonly string? _appExe = appExe ?? Environment.ProcessPath;
    private OpenFiles _openFiles = new();

    internal const int MaxSubfolderDepth = 8;
    internal const int MaxSubfolders = 5000;

    // Folders that belong to an app or a tool: sorting inside them would break it.
    private static readonly HashSet<string> PackageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".app", ".bundle", ".framework", ".plugin", ".kext", ".photoslibrary", ".photolibrary", ".musiclibrary", ".tvlibrary",
        ".imovielibrary", ".fcpbundle", ".logicx", ".band", ".pages", ".numbers", ".key", ".rtfd", ".xcodeproj", ".xcworkspace",
        ".playground", ".lproj", ".sparsebundle",
    };
    private static readonly HashSet<string> SkippedFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "$Recycle.Bin", "System Volume Information", AppPaths.AppName + "-Data",
    };

    /// <summary>Dry run: works out what would happen without touching any file.</summary>
    public OrganizePlan CreatePlan()
    {
        var plan = new OrganizePlan();
        _openFiles = new OpenFiles();
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

            foreach (var dir in FoldersToScan(folder, source.IncludeSubfolders, plan))
            {
                var dirTarget = dir == folder ? target : Path.Combine(target, Path.GetRelativePath(folder, dir));
                PlanFolder(dir, dirTarget, folder, plan, reserved);
            }
        }
        return plan;
    }

    /// <summary>The folder itself and, when asked, its subfolders (breadth first, without following links).</summary>
    private IEnumerable<string> FoldersToScan(string root, bool includeSubfolders, OrganizePlan plan)
    {
        yield return root;
        if (!includeSubfolders)
            yield break;

        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        var count = 0;
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            if (depth >= MaxSubfolderDepth)
                continue;
            List<DirectoryInfo> subfolders;
            try
            {
                subfolders = new DirectoryInfo(dir).EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // reported when the folder's own files are read
            }
            foreach (var sub in subfolders)
            {
                if (SkipSubfolder(sub))
                    continue;
                if (++count > MaxSubfolders)
                {
                    plan.Errors.Add($"{root}: only the first {MaxSubfolders} subfolders are organized.");
                    yield break;
                }
                yield return sub.FullName;
                queue.Enqueue((sub.FullName, depth + 1));
            }
        }
    }

    private bool SkipSubfolder(DirectoryInfo dir)
    {
        try
        {
            return rules.ReservedFolderNames.Contains(dir.Name)            // already organized: PDF, Belgeler, Images...
                || dir.Name.StartsWith('.')
                || (dir.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0
                || dir.LinkTarget is not null                              // never follow a link or junction out of the folder
                || PackageExtensions.Contains(dir.Extension)
                || SkippedFolderNames.Contains(dir.Name)
                || Directory.Exists(Path.Combine(dir.FullName, ".git"));   // a code repository
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private void PlanFolder(string folder, string targetRoot, string sourceRoot, OrganizePlan plan, HashSet<string> reserved)
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
            plan.Moves.Add(new PlannedMove(file.FullName, destination, category, sourceRoot, file.Length, byContent));
        }
    }

    /// <summary>Applies the plan. Every file is checked again right before it is moved.</summary>
    public OrganizeResult Execute(OrganizePlan plan)
    {
        var result = new OrganizeResult();
        if (plan.Moves.Count == 0)
            return result;

        var runId = journal.NewRunId();
        _openFiles = new OpenFiles();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folderDenied = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var move in plan.Moves)
        {
            var file = new FileInfo(move.SourcePath);
            try
            {
                // The folder may have changed since planning (e.g. replaced by a link); check it again.
                string? denied = null;
                if (guard is not null && !folderDenied.TryGetValue(move.SourceFolder, out denied))
                    folderDenied[move.SourceFolder] = denied = guard.Check(move.SourceFolder);
                if (denied is not null)
                {
                    result.Failed.Add(new SkippedFile(move.SourcePath, denied));
                    continue;
                }
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

    /// <summary>Looks at the extension first; if there is none, tries to detect the type from the content.</summary>
    private (string? Category, bool ByContent) Categorize(FileInfo file)
    {
        if (CategoryOf(file.Extension) is { } byExtension)
            return (byExtension, false);
        // Only extensionless files are sniffed: a known-unknown extension (.dll, .sys) must not be mistaken for an installer.
        if (file.Extension.Length == 0
            && !FileSafety.IsCloudPlaceholder(file.Attributes) // don't read online-only OneDrive files: it would download them
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
        if (string.Equals(file.FullName, _appExe, StringComparison.OrdinalIgnoreCase))
            return true;
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
        if (FileSafety.IsInUse(file) || _openFiles.Contains(file))
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
