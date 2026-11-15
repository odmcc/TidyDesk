using System.Globalization;
using System.Text.Json;

namespace TidyDesk.Core;

public sealed record JournalEntry(string RunId, DateTime Time, string From, string To);

public sealed record RunInfo(string RunId, DateTime Time, int Count, bool Undone = false);

public sealed class UndoResult
{
    public int Restored { get; set; }
    public List<SkippedFile> Failed { get; } = [];
}

/// <summary>
/// For every run, appends one line to history\{runId}.jsonl at the moment each file is moved,
/// so moves can be undone even if the app closes halfway.
/// After an undo the run's file is renamed to {runId}.undone.jsonl and kept as an audit record.
/// </summary>
/// <param name="volumeRoots">For tests; defaults to the mounted disks and USB sticks.</param>
public sealed class UndoJournal(string dir, ILog log, Func<IEnumerable<string>>? volumeRoots = null)
{
    private const string ActiveSuffix = ".jsonl";
    private const string UndoneSuffix = ".undone.jsonl";
    private readonly object _gate = new();

    public string NewRunId() => DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);

    public void Record(string runId, string from, string to)
    {
        var line = JsonSerializer.Serialize(new JournalEntry(runId, DateTime.Now, from, to)) + Environment.NewLine;
        lock (_gate)
        {
            Directory.CreateDirectory(dir);
            File.AppendAllText(RunFile(runId), line);
        }
    }

    /// <summary>Runs, newest first. By default only runs that can still be undone.</summary>
    public List<RunInfo> ListRuns(bool includeUndone = false)
    {
        if (!Directory.Exists(dir))
            return [];
        return Directory.EnumerateFiles(dir, "*" + ActiveSuffix)
            .Select(f => (File: f, Undone: f.EndsWith(UndoneSuffix, StringComparison.OrdinalIgnoreCase)))
            .Where(f => includeUndone || !f.Undone)
            .Select(f => (Entries: ReadEntries(f.File), f.Undone))
            .Where(r => r.Entries.Count > 0)
            .Select(r => new RunInfo(r.Entries[0].RunId, r.Entries[0].Time, r.Entries.Count, r.Undone))
            .OrderByDescending(r => r.RunId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Files moved in a run (even if the run was undone).</summary>
    public List<JournalEntry> ReadRun(string runId)
    {
        var active = RunFile(runId);
        return File.Exists(active) ? ReadEntries(active) : ReadEntries(Path.Combine(dir, runId + UndoneSuffix));
    }

    public UndoResult? UndoLast() => ListRuns().FirstOrDefault() is { } last ? Undo(last.RunId) : null;

    public UndoResult Undo(string runId)
    {
        var result = new UndoResult();
        var file = RunFile(runId);
        var entries = ReadEntries(file);
        var touchedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var sourceDirs = entries.Select(e => Path.GetDirectoryName(e.From)!).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in Enumerable.Reverse(entries))
        {
            var (from, to) = (entry.From, entry.To);
            try
            {
                if (!File.Exists(to) && FindOnRenamedVolume(from, to) is { } found)
                {
                    (from, to) = found;
                    sourceDirs.Add(Path.GetDirectoryName(from)!);
                    log.Info($"Found on a renamed disk: {entry.To} -> {to}");
                }
                if (!File.Exists(to))
                {
                    result.Failed.Add(new SkippedFile(to, "The moved file is no longer there (it may have been moved or deleted)."));
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(from)!);
                var restoreTo = FileSafety.MakeUnique(from);
                File.Move(to, restoreTo);
                touchedDirs.Add(Path.GetDirectoryName(to)!);
                result.Restored++;
                log.Info($"Restored: {to} -> {restoreTo}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Failed.Add(new SkippedFile(to, ex.Message));
                log.Error($"Couldn't restore: {to}", ex);
            }
        }

        RemoveEmptyCreatedDirs(touchedDirs, sourceDirs);

        if (File.Exists(file))
            File.Move(file, Path.Combine(dir, runId + UndoneSuffix), overwrite: true);
        log.Info($"Run {runId} undone: {result.Restored} files, {result.Failed.Count} errors.");
        return result;
    }

    /// <summary>
    /// Removes date and category folders created during the run if they are now empty (empty folders only).
    /// Never climbs to the folder the files came from (e.g. the Desktop) or above it.
    /// </summary>
    private static void RemoveEmptyCreatedDirs(IEnumerable<string> dirs, HashSet<string> sourceDirs)
    {
        foreach (var start in dirs)
        {
            var current = start;
            for (var level = 0; level < 3 && current is not null && !sourceDirs.Contains(current); level++)
            {
                try
                {
                    if (!Directory.Exists(current) || Directory.EnumerateFileSystemEntries(current).Any())
                        break;
                    Directory.Delete(current, recursive: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    break;
                }
                current = Path.GetDirectoryName(current);
            }
        }
    }

    /// <summary>
    /// A USB stick or second disk can come back under another letter (E: to F:) or name (/Volumes/USB to /Volumes/USB 1).
    /// Looks for the file at the same place on the other mounted disks; used only when exactly one disk has it.
    /// </summary>
    private (string From, string To)? FindOnRenamedVolume(string from, string to)
    {
        if (VolumeRoot(to) is not { } oldRoot || !string.Equals(VolumeRoot(from), oldRoot, StringComparison.OrdinalIgnoreCase))
            return null;
        var relativeFrom = Path.GetRelativePath(oldRoot, from);
        var relativeTo = Path.GetRelativePath(oldRoot, to);
        var matches = (volumeRoots ?? CurrentVolumeRoots)()
            .Where(root => !string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(oldRoot), StringComparison.OrdinalIgnoreCase))
            .Select(root => (From: Path.Combine(root, relativeFrom), To: Path.Combine(root, relativeTo)))
            .Where(c => File.Exists(c.To))
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>"E:\" for a file on a non-system Windows drive, "/Volumes/USB" for an external disk on a Mac; otherwise null.</summary>
    internal static string? VolumeRoot(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var root = Path.GetPathRoot(path);
            var system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            return root is { Length: 3 } && root[1] == ':' && !string.Equals(root, system, StringComparison.OrdinalIgnoreCase) ? root : null;
        }
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0] == "Volumes" ? "/Volumes/" + parts[1] : null;
    }

    private static IEnumerable<string> CurrentVolumeRoots()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
                .Select(d => d.RootDirectory.FullName)
                .Where(root => VolumeRoot(Path.Combine(root, "x")) is not null)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private string RunFile(string runId) => Path.Combine(dir, runId + ActiveSuffix);

    private List<JournalEntry> ReadEntries(string file)
    {
        if (!File.Exists(file))
            return [];
        var list = new List<JournalEntry>();
        foreach (var line in File.ReadLines(file))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                if (JsonSerializer.Deserialize<JournalEntry>(line) is { } entry)
                    list.Add(entry);
            }
            catch (JsonException)
            {
                log.Warn($"Skipped an unreadable line in history file: {file}");
            }
        }
        return list;
    }
}
