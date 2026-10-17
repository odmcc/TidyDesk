using System.Windows;
using TidyDesk.Core;

namespace TidyDesk;

internal static class Program
{
    /// <summary>
    /// Portable app: no installer, no registry writes, no network access.
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
        var service = demoRoot is null
            ? new OrganizerService(paths, log)
            : new OrganizerService(paths, log, new PathGuard(demoRoot, [], []));
        Func<string, string?> resolver = demoRoot is null ? KnownFolders.Resolve : DemoResolver(demoRoot);

        // Single instance: if the app is already running, its window is brought to the front.
        var suffix = demoRoot is null ? "" : ".Demo";
        using var instance = new Mutex(true, @"Local\" + AppPaths.AppName + suffix, out var isFirst);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + AppPaths.AppName + ".Show" + suffix);
        if (!isFirst)
        {
            showSignal.Set();
            return 0;
        }

        var app = new Application { ThemeMode = ThemeMode.System };
        app.DispatcherUnhandledException += (_, e) =>
        {
            log.Error("Unexpected error", e.Exception);
            e.Handled = true;
        };
        log.Info($"App started (version {typeof(Program).Assembly.GetName().Version}, data: {paths.DataDir}).");

        var window = new MainWindow(service, resolver, demoRoot is not null);
        new Thread(() =>
        {
            while (showSignal.WaitOne())
                app.Dispatcher.BeginInvoke(window.BringToFront);
        }) { IsBackground = true }.Start();

        app.Run(window);
        log.Info("App closed.");
        return 0;
    }

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
