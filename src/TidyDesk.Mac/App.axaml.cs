using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using TidyDesk.Core;

namespace TidyDesk.Mac;

public partial class App : Application
{
    internal static OrganizerService? Service;
    internal static Func<string, string?> Resolver = KnownFolders.Resolve;
    internal static bool DemoMode;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && Service is not null)
            desktop.MainWindow = new MainWindow(Service, Resolver, DemoMode);
        base.OnFrameworkInitializationCompleted();
    }
}
