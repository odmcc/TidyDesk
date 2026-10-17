namespace TidyDesk.Core;

/// <summary>
/// The app's own files (settings, undo history, logs). The app is portable:
/// data is kept in a "TidyDesk-Data" folder next to the exe. If that location isn't writable
/// (e.g. a read-only share), %LOCALAPPDATA%\TidyDesk is used. Nothing is written to the registry.
/// </summary>
public sealed class AppPaths
{
    public const string AppName = "TidyDesk";

    public required string DataDir { get; init; }

    public string LogDir => Path.Combine(DataDir, "logs");
    public string JournalDir => Path.Combine(DataDir, "history");
    public string SettingsFile => Path.Combine(DataDir, "settings.json");

    public static AppPaths Portable()
    {
        var exeDir = AppContext.BaseDirectory;
        var beside = Path.Combine(exeDir, AppName + "-Data");
        if (CanWrite(beside))
            return new AppPaths { DataDir = beside };
        return new AppPaths { DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName) };
    }

    public static AppPaths UnderRoot(string root) => new() { DataDir = Path.Combine(root, "data") };

    private static bool CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe); // only the empty probe file it just created
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
