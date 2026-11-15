using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;

namespace TidyDesk.Mac;

/// <summary>Icon and color for each category name (same colors and icon family as the Windows app).</summary>
internal static class CategoryVisuals
{
    private static readonly Dictionary<string, (string Icon, string Color)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Belgeler"] = (Icons.DocumentText, "#2B7CD3"),
        ["PDF"] = (Icons.DocumentPdf, "#D13438"),
        ["Tablolar"] = (Icons.Table, "#107C41"),
        ["Sunumlar"] = (Icons.SlideText, "#E3742A"),
        ["Görseller"] = (Icons.Image, "#8764B8"),
        ["Video ve Ses"] = (Icons.Video, "#C239B3"),
        ["E-postalar"] = (Icons.Mail, "#0099BC"),
        ["Arşivler"] = (Icons.FolderZip, "#C19C00"),
        ["Kurulum Dosyaları"] = (Icons.Box, "#00A39A"),
        ["Documents"] = (Icons.DocumentText, "#2B7CD3"),
        ["Spreadsheets"] = (Icons.Table, "#107C41"),
        ["Presentations"] = (Icons.SlideText, "#E3742A"),
        ["Images"] = (Icons.Image, "#8764B8"),
        ["Video & Audio"] = (Icons.Video, "#C239B3"),
        ["Emails"] = (Icons.Mail, "#0099BC"),
        ["Archives"] = (Icons.FolderZip, "#C19C00"),
        ["Installers"] = (Icons.Box, "#00A39A"),
    };

    public static Geometry Icon(string category) => Icons.Get(Known.TryGetValue(category, out var v) ? v.Icon : Icons.Document);

    public static IBrush Foreground(string category) => Brush(category, 0xFF);

    /// <summary>Semi-transparent background that stays readable in light and dark mode.</summary>
    public static IBrush Background(string category) => Brush(category, 0x26);

    private static IBrush Brush(string category, byte alpha)
    {
        var c = Color.Parse(Known.TryGetValue(category, out var v) ? v.Color : "#8A8886");
        return new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
    }
}

internal abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

/// <summary>A selectable folder card.</summary>
internal sealed class FolderItem(string name, string path, Geometry icon, bool isCustom, string? problem) : Observable
{
    private bool _isChecked;
    private bool _includeSubfolders;
    private string _badge = "";

    public string Name { get; } = name;
    public string Path { get; } = path;
    public Geometry Icon { get; } = icon;
    public bool IsCustom { get; } = isCustom;
    public string RemoveTip { get; init; } = "";
    public string SubfoldersLabel { get; init; } = "";
    public string SubfoldersTip { get; init; } = "";
    /// <summary>Reason when the guard rejects this folder; the card is shown disabled.</summary>
    public string? Problem { get; } = problem;
    public bool IsAllowed => Problem is null;
    public string Tooltip => Problem ?? Path;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value && IsAllowed); }
    /// <summary>Organize subfolders too, each in its own place (only for folders added by hand).</summary>
    public bool IncludeSubfolders { get => _includeSubfolders; set => Set(ref _includeSubfolders, value && IsCustom); }
    /// <summary>Such as "12 files" or "Tidy".</summary>
    public string Badge { get => _badge; set => Set(ref _badge, value); }
}

internal sealed record ChipItem(string Text, Geometry Icon, IBrush Accent, IBrush Tint);

/// <summary>One line of the preview: a file and where it goes (or why it stays).</summary>
internal sealed record PreviewItem(string Title, string Detail, Geometry Icon, IBrush Accent, IBrush Tint);

/// <summary>One earlier cleanup on the history screen.</summary>
internal sealed record HistoryItem(string RunId, string Text, bool CanUndo, string Status, string UndoLabel);

/// <summary>The "Add folder" card after the folder cards.</summary>
internal sealed record AddFolderTile(string Label, string Tip)
{
    public Geometry Icon => Icons.Get(Icons.Add);
}
