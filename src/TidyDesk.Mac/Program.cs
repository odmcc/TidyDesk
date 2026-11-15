using Avalonia;
using Avalonia.Threading;
using TidyDesk.Core;

namespace TidyDesk.Mac;

internal static class Program
{
    /// <summary>
    /// No installer step, no network access. Data (settings, undo history, logs) lives in
    /// ~/Library/Application Support/TidyDesk. macOS itself keeps a .app to a single instance.
    /// Command line (for testing and demos only):
    ///   --demo FOLDER   Uses the Desktop, Documents and Downloads subfolders of FOLDER instead of the
    ///                   real user folders; real files are never touched.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        var demoRoot = ArgValue(args, "--demo") is { } d ? Path.GetFullPath(d) : null;
        var paths = demoRoot is null ? AppPaths.Portable() : AppPaths.UnderRoot(Path.Combine(demoRoot, "app"));
        var log = new FileLog(paths.LogDir);
        App.Service = demoRoot is null
            ? new OrganizerService(paths, log)
            : new OrganizerService(paths, log, new PathGuard(demoRoot, [], []));
        App.Resolver = demoRoot is null ? KnownFolders.Resolve : DemoResolver(demoRoot);
        App.DemoMode = demoRoot is not null;
        log.Info($"App started (version {typeof(Program).Assembly.GetName().Version}, data: {paths.DataDir}).");

        var builder = AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
        builder.AfterSetup(_ => Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            log.Error("Unexpected error", e.Exception);
            e.Handled = true;
        });
        var code = builder.StartWithClassicDesktopLifetime(args);
        log.Info("App closed.");
        return code;
    }

    // Lets the Avalonia previewer find the app.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static Func<string, string?> DemoResolver(string root)
    {
        foreach (var name in new[] { "Desktop", "Downloads", "Documents" })
            Directory.CreateDirectory(Path.Combine(root, name));
        return token => token.ToLowerInvariant() switch
        {
            "desktop" => Path.Combine(root, "Desktop"),
            "downloads" => Path.Combine(root, "Downloads"),
            "documents" => Path.Combine(root, "Documents"),
            _ => null,
        };
    }
}
