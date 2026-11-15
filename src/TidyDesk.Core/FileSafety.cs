using System.ComponentModel;
using System.Diagnostics;

namespace TidyDesk.Core;

/// <summary>
/// macOS doesn't lock open files, so a file open in Preview or a video player would pass <see cref="FileSafety.IsInUse"/>.
/// There, one "lsof" call lists every file the user's programs have open; it runs at most once per scan or run.
/// Elsewhere this is always empty.
/// </summary>
internal sealed class OpenFiles
{
    private HashSet<string>? _paths;
    private readonly Dictionary<string, string> _realDirs = new(StringComparer.OrdinalIgnoreCase);

    public bool Contains(FileInfo file)
    {
        if (!OperatingSystem.IsMacOS())
            return false;
        _paths ??= FileSafety.ListOpenFiles();
        if (_paths.Count == 0)
            return false;
        var dir = file.DirectoryName!;
        if (!_realDirs.TryGetValue(dir, out var realDir))
        {
            try
            {
                realDir = PathGuard.RealPath(dir); // lsof reports real paths (/private/var, not /var)
            }
            catch (IOException)
            {
                realDir = dir;
            }
            _realDirs[dir] = realDir;
        }
        return _paths.Contains(file.FullName) || _paths.Contains(Path.Combine(realDir, file.Name));
    }
}

internal static class FileSafety
{
    // Opening an online-only OneDrive file triggers a download, so these are not checked for locks.
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    public static bool IsCloudPlaceholder(FileAttributes attrs) =>
        (attrs & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0;

    /// <summary>True if the file is open in another program (Word, Excel, a PDF reader...).</summary>
    public static bool IsInUse(FileInfo file)
    {
        if (IsCloudPlaceholder(file.Attributes))
            return false;
        try
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Full paths of files open in the user's programs (macOS, via lsof). Empty if lsof can't be run.</summary>
    public static HashSet<string> ListOpenFiles()
    {
        try
        {
            var start = new ProcessStartInfo("/usr/sbin/lsof")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in new[] { "-n", "-P", "-w", "-F", "n" })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            if (process is null)
                return [];
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                process.Kill();
                return [];
            }
            return ParseLsof(output.Result);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or AggregateException)
        {
            return [];
        }
    }

    /// <summary>lsof -F n prints one field per line; file names start with "n".</summary>
    internal static HashSet<string> ParseLsof(string output) =>
        output.Split('\n')
            .Where(line => line.Length > 2 && line[0] == 'n' && line[1] == '/')
            .Select(line => line[1..].TrimEnd('\r'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Office leaves an owner file starting with "~$" next to an open document.</summary>
    public static bool HasOfficeOwnerFile(FileInfo file)
    {
        var dir = file.DirectoryName!;
        var name = file.Name;
        if (File.Exists(Path.Combine(dir, "~$" + name)))
            return true;
        return name.Length > 2 && File.Exists(Path.Combine(dir, "~$" + name[2..]));
    }

    /// <summary>If the name is taken, produces "name (2).ext", "name (3).ext"...</summary>
    public static string MakeUnique(string path, ISet<string>? reserved = null)
    {
        bool Taken(string p) => File.Exists(p) || Directory.Exists(p) || (reserved?.Contains(p) ?? false);
        if (!Taken(path))
            return path;

        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!Taken(candidate))
                return candidate;
        }
    }

    public static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrEmpty(clean) ? "Other" : clean;
    }
}
