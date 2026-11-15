using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace TidyDesk.Mac;

/// <summary>"About TidyDesk" from the app menu: version, what it does, license.</summary>
internal sealed class AboutWindow : Window
{
    public AboutWindow()
    {
        Title = Strings.T("mac.about");
        Width = 360;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var version = typeof(AboutWindow).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";
        var close = new Button { Content = Strings.T("done.ok"), HorizontalAlignment = HorizontalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, MinWidth = 120, Margin = new Thickness(0, 18, 0, 0), IsDefault = true, IsCancel = true };
        close.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(28, 28, 28, 24),
            Spacing = 6,
            Children =
            {
                new Image { Source = new Bitmap(AssetLoader.Open(new Uri("avares://TidyDesk/Assets/logo.png"))), Width = 64, Height = 64, Margin = new Thickness(0, 0, 0, 8) },
                new TextBlock { Text = "TidyDesk", FontSize = 20, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = Strings.T("about.version", version), Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = Strings.T("about.text"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 0) },
                new TextBlock { Text = Strings.T("about.license"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Opacity = 0.7, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) },
                close,
            },
        };
    }
}
