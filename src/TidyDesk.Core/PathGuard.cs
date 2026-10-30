namespace TidyDesk.Core;

/// <summary>
/// Checks whether a selected folder is safe to organize. Windows, Program Files, ProgramData, AppData,
/// drive roots, network paths, the Recycle Bin and other users' profiles are never touched.
/// On the system drive (C:) only folders inside the signed-in user's profile are allowed.
/// </summary>
public sealed class PathGuard
{
    private readonly string _userProfile;
    private readonly string _systemDrive;
    private readonly List<string> _blocked;
    private readonly List<string> _networkRoots;

    /// <param name="userProfile">For tests; defaults to the signed-in user's profile.</param>
    /// <param name="blockedRoots">For tests; defaults to the Windows, Program Files, ProgramData and AppData folders.</param>
    /// <param name="networkRoots">
    /// Allowed network folders. Default: only the user's own Desktop/Documents/Downloads when an organization
    /// has redirected them to a network share (Folder Redirection).
    /// </param>
    public PathGuard(string? userProfile = null, IEnumerable<string>? blockedRoots = null, IEnumerable<string>? networkRoots = null)
    {
        _userProfile = Normalize(userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _systemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) is { Length: > 0 } d ? d : @"C:\";
        _blocked = (blockedRoots ?? DefaultBlockedRoots(_userProfile))
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(Normalize)
            .ToList();
        _networkRoots = (networkRoots ?? new[] { "desktop", "documents", "downloads" }.Select(KnownFolders.Resolve).OfType<string>())
            .Where(p => p.StartsWith(@"\\", StringComparison.Ordinal))
            .Select(Normalize)
            .ToList();
    }

    private static IEnumerable<string> DefaultBlockedRoots(string profile) =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Path.Combine(profile, "AppData"),
    ];

    /// <summary>Returns null if the folder may be organized; otherwise a reason to show the user.</summary>
    public string? Check(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
            return "Choose a valid folder.";

        string full;
        try
        {
            full = Normalize(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "Choose a valid folder.";
        }

        if (full.StartsWith(@"\\", StringComparison.Ordinal) && !_networkRoots.Any(r => IsSameOrInside(full, r)))
            return "Network folders can't be used; choose a folder on this PC.";
        if (string.Equals(Path.GetPathRoot(full), full, StringComparison.OrdinalIgnoreCase))
            return "A whole drive can't be used; choose a folder inside it.";
        if (full.Split(Path.DirectorySeparatorChar).Any(n =>
                n.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)))
            return "System folders can't be used.";
        if (_blocked.Any(b => IsSameOrInside(full, b)))
            return "System and program folders can't be used.";

        if (string.Equals(Path.GetPathRoot(full), _systemDrive, StringComparison.OrdinalIgnoreCase))
        {
            if (full.Equals(_userProfile, StringComparison.OrdinalIgnoreCase))
                return "Your whole user folder can't be used; choose a folder inside it, like Desktop or Documents.";
            if (!IsSameOrInside(full, _userProfile))
                return "On the C: drive, only folders inside your own user folder can be used.";
        }

        if (!Directory.Exists(full))
            return "Folder not found.";

        // A junction or symlink inside the profile can point at a system folder; check where it really leads.
        // Other reparse points (OneDrive) return null here and are left alone.
        if (Directory.ResolveLinkTarget(full, returnFinalTarget: true) is { } target)
            return Check(target.FullName) is null ? null : "This folder is a link to a folder that can't be used.";
        return null;
    }

    private static bool IsSameOrInside(string path, string parent) =>
        path.Equals(parent, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Full path without a trailing separator (a drive root stays "C:\").</summary>
    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        return full.Length > (Path.GetPathRoot(full)?.Length ?? 0) ? Path.TrimEndingDirectorySeparator(full) : full;
    }
}
