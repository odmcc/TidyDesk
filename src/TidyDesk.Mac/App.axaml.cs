using Avalonia;
using Avalonia.Controls;
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
        {
            desktop.MainWindow = new MainWindow(Service, Resolver, DemoMode);
            RefreshMenu();
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The macOS app menu: "About TidyDesk" in the current language (macOS adds Hide and Quit itself).</summary>
    internal static void RefreshMenu()
    {
        if (Current is not { ApplicationLifetime: IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner } } app)
            return;
        var about = new NativeMenuItem(Strings.T("mac.about"));
        about.Click += (_, _) => new AboutWindow().ShowDialog(owner);
        NativeMenu.SetMenu(app, new NativeMenu { about });
    }
}
