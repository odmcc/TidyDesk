using System.Runtime.InteropServices;

namespace TidyDesk.Core;

/// <summary>
/// Resolves user folders through the Windows Known Folder API, so Desktop/Documents folders
/// redirected to OneDrive (Known Folder Move) or moved by Group Policy are found correctly.
/// </summary>
public static class KnownFolders
{
    private static readonly Guid Desktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    private static readonly Guid Downloads = new("374DE290-123F-4565-9164-39C4925E467B");
    private static readonly Guid Documents = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    public static string? GetPath(Guid folderId)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var hr = SHGetKnownFolderPath(folderId, 0, IntPtr.Zero, out var ptr);
        try
        {
            return hr == 0 ? Marshal.PtrToStringUni(ptr) : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    /// <summary>Resolves the {Desktop}, {Downloads}, {Documents} and {UserProfile} tokens.</summary>
    public static string? Resolve(string token) => OperatingSystem.IsWindows() ? ResolveWindows(token) : ResolveUnix(token);

    // macOS has fixed ~/Desktop, ~/Documents, ~/Downloads (iCloud "Desktop & Documents" keeps these paths).
    private static string? ResolveUnix(string token)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return token.ToLowerInvariant() switch
        {
            "desktop" => Path.Combine(home, "Desktop"),
            "downloads" => Path.Combine(home, "Downloads"),
            "documents" => Path.Combine(home, "Documents"),
            "userprofile" => home,
            _ => null,
        };
    }

    private static string? ResolveWindows(string token) => token.ToLowerInvariant() switch
    {
        "desktop" => GetPath(Desktop) ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "downloads" => GetPath(Downloads) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        "documents" => GetPath(Documents) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "userprofile" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        _ => null,
    };
}
