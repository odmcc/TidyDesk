using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace TidyDesk;

/// <summary>Icon (Segoe Fluent Icons) and color for each category name.</summary>
internal static class CategoryVisuals
{
    private static readonly Dictionary<string, (string Glyph, string Color)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Belgeler"] = ("", "#2B7CD3"),
        ["PDF"] = ("", "#D13438"),
        ["Tablolar"] = ("", "#107C41"),
        ["Sunumlar"] = ("", "#E3742A"),
        ["Görseller"] = ("", "#8764B8"),
        ["Video ve Ses"] = ("", "#C239B3"),
        ["E-postalar"] = ("", "#0099BC"),
        ["Arşivler"] = ("", "#C19C00"),
        ["Kurulum Dosyaları"] = ("", "#00A39A"),
        // English folder names share the same look
        ["Documents"] = ("", "#2B7CD3"),
        ["Spreadsheets"] = ("", "#107C41"),
        ["Presentations"] = ("", "#E3742A"),
        ["Images"] = ("", "#8764B8"),
        ["Video & Audio"] = ("", "#C239B3"),
        ["Emails"] = ("", "#0099BC"),
        ["Archives"] = ("", "#C19C00"),
        ["Installers"] = ("", "#00A39A"),
    };

    public static string Glyph(string category) => Known.TryGetValue(category, out var v) ? v.Glyph : "";

    public static SolidColorBrush Foreground(string category) =>
        Brush(Known.TryGetValue(category, out var v) ? v.Color : "#8A8886", 0xFF);

    /// <summary>Semi-transparent background that stays readable in light and dark themes.</summary>
    public static SolidColorBrush Background(string category) =>
        Brush(Known.TryGetValue(category, out var v) ? v.Color : "#8A8886", 0x26);

    private static SolidColorBrush Brush(string hex, byte alpha)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        brush.Freeze();
        return brush;
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
internal sealed class FolderItem(string name, string path, string glyph, bool isCustom, string? problem) : Observable
{
    private bool _isChecked;
    private string _badge = "";

    public string Name { get; } = name;
    public string Path { get; } = path;
    public string Glyph { get; } = glyph;
    public bool IsCustom { get; } = isCustom;
    /// <summary>Reason when the guard rejects this folder; the card is shown disabled.</summary>
    public string? Problem { get; } = problem;
    public bool IsAllowed => Problem is null;
    public string Tooltip => Problem ?? Path;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value && IsAllowed); }
    /// <summary>Such as "12 files" or "Tidy".</summary>
    public string Badge { get => _badge; set => Set(ref _badge, value); }
}

internal sealed record ChipItem(string Text, string Glyph, Brush Accent, Brush Tint);

/// <summary>The "Add folder" card after the folder cards.</summary>
internal sealed record AddFolderTile;
