namespace TidyDesk.Core;

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
