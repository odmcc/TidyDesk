using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using TidyDesk.Core;

namespace TidyDesk.Mac;

/// <summary>
/// Same flow as the Windows app: pick folders and press the button → "done" screen with undo.
/// Scanning runs in the background whenever the selection changes; the user only sees the counts.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly (string Token, string NameKey, string Glyph)[] StandardFolders =
    [
        ("desktop", "folder.desktop", "🖥"),
        ("documents", "folder.documents", "📄"),
        ("downloads", "folder.downloads", "⬇️"),
    ];

    private readonly OrganizerService _service;
    private readonly Func<string, string?> _resolver;
    private readonly ObservableCollection<object> _tiles = [];
    private readonly UserSettings _settings;
    private readonly bool _demoMode;
    private int _analyzeVersion;
    private bool _busy;
    private string? _lastRunId;
    private List<string> _lastTargets = [];

    // Required by the XAML loader and previewer.
    public MainWindow() : this(App.Service!, App.Resolver, App.DemoMode) { }

    internal MainWindow(OrganizerService service, Func<string, string?> resolver, bool demoMode)
    {
        _service = service;
        _resolver = resolver;
        _settings = service.LoadSettings();
        _demoMode = demoMode;
        InitializeComponent();

        FolderTiles.ItemsSource = _tiles;
        ApplyLanguage();
        LoadFolders();
        UpdateUndoLink();
        Opened += async (_, _) =>
        {
            _ = Reveal(HomeView);
            await AnalyzeAsync();
        };
        // When the window regains focus the counts refresh quietly; the button stays usable meanwhile.
        Activated += async (_, _) => { if (HomeView.IsVisible && !_busy) await AnalyzeAsync(quiet: true); };
    }

    // ================= Folders =================

    private void LoadFolders()
    {
        _tiles.Clear();
        var folders = new List<FolderItem>();
        foreach (var (token, nameKey, glyph) in StandardFolders)
        {
            if (_resolver(token) is { } path)
                folders.Add(new FolderItem(Strings.T(nameKey), path, glyph, isCustom: false, Problem(path), Strings.T("s.removeFolder")));
        }
        foreach (var path in _settings.CustomFolders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!folders.Any(f => SamePath(f.Path, path)))
                folders.Add(new FolderItem(DisplayName(path), path, "📁", isCustom: true, Problem(path), Strings.T("s.removeFolder")));
        }

        // Desktop and Downloads are selected on first launch; after that the user's choice is remembered.
        var selected = _settings.Folders ?? StandardFolders.Where(f => f.Token != "documents").Select(f => _resolver(f.Token)).OfType<string>().ToList();
        foreach (var folder in folders)
        {
            folder.IsChecked = selected.Any(s => SamePath(s, folder.Path));
            _tiles.Add(folder);
        }
        _tiles.Add(new AddFolderTile(Strings.T("s.addFolder"), Strings.T("s.addFolderTip")));
    }

    private IEnumerable<FolderItem> Folders => _tiles.OfType<FolderItem>();

    private async void Folder_Toggled(object? sender, RoutedEventArgs e) => await SelectionChangedAsync();

    private async void AddFolder_Click(object? sender, RoutedEventArgs e)
    {
        var options = new FolderPickerOpenOptions { Title = Strings.T("dialog.pickFolder"), AllowMultiple = false };
        if (_resolver("documents") is { } documents && await StorageProvider.TryGetFolderFromPathAsync(documents) is { } start)
            options.SuggestedStartLocation = start;
        var picked = await StorageProvider.OpenFolderPickerAsync(options);
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path)
            return;
        path = Path.TrimEndingDirectorySeparator(path); // keeps a root like "/" or "E:\" as it is

        if (Problem(path) is { } problem)
        {
            ShowFolderError(Strings.T("error.folderBlocked", problem));
            return;
        }
        if (Folders.FirstOrDefault(f => SamePath(f.Path, path)) is { } existing)
        {
            existing.IsChecked = true;
        }
        else
        {
            _tiles.Insert(_tiles.Count - 1, new FolderItem(DisplayName(path), path, "📁", isCustom: true, problem: null, Strings.T("s.removeFolder")) { IsChecked = true });
            _settings.CustomFolders.Add(path);
        }
        await SelectionChangedAsync();
    }

    private async void RemoveFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: FolderItem folder })
            return;
        _tiles.Remove(folder);
        _settings.CustomFolders.RemoveAll(p => SamePath(p, folder.Path));
        await SelectionChangedAsync();
    }

    private async Task SelectionChangedAsync()
    {
        FolderError.IsVisible = false;
        HomeMessage.IsVisible = false;
        _settings.Folders = SelectedFolders();
        SaveSettings();
        await AnalyzeAsync();
    }

    private void SaveSettings()
    {
        try
        {
            _service.SaveSettings(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _service.Log.Warn($"Couldn't save settings: {ex.Message}");
        }
    }

    private List<string> SelectedFolders() => Folders.Where(f => f.IsChecked).Select(f => f.Path).ToList();

    private OrganizeOptions Options() => new(SelectedFolders(), MinAgeDays: 0, MonthFolders: false, Strings.Language);

    private string? Problem(string path) => _service.Guard.Check(path) is { } problem ? Strings.Core(problem) : null;

    private void ShowFolderError(string text)
    {
        FolderError.Text = text;
        FolderError.IsVisible = true;
    }

    // ================= Language =================

    private async void Language_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string language } || language == Strings.Language)
            return;
        _settings.Language = language;
        _settings.Folders = SelectedFolders();
        SaveSettings();
        ApplyLanguage();
        LoadFolders();
        UpdateUndoLink();
        FolderError.IsVisible = false;
        HomeMessage.IsVisible = false;
        await AnalyzeAsync();
    }

    private void ApplyLanguage()
    {
        Strings.SetLanguage(_settings.Language);
        Title = Strings.T(_demoMode ? "s.demoTitle" : "s.appTitle");
        HomeTitle.Text = Strings.T("s.homeTitle");
        HomeSubtitle.Text = Strings.T("s.homeSubtitle");
        HomeHint.Text = Strings.T("s.homeHint");
        Footer.Text = Strings.T("s.footer");
        OpenText.Text = Strings.T(OperatingSystem.IsMacOS() ? "mac.showInFinder" : "done.open");
        UndoText.Text = "↶  " + Strings.T("done.undo");
        OkText.Text = Strings.T("done.ok");
        foreach (var button in new[] { LangTr, LangEn })
            button.Classes.Set("selected", (string?)button.Tag == Strings.Language);
    }

    // ================= Background scan =================

    /// <param name="quiet">true: don't show the "scanning" state (the button stays usable).</param>
    private async Task AnalyzeAsync(bool quiet = false)
    {
        var version = ++_analyzeVersion;
        var options = Options();
        if (options.Folders.Count == 0)
        {
            foreach (var f in Folders)
                f.Badge = f.IsAllowed ? "" : Strings.T("badge.blocked");
            SetOrganizeButton(Strings.T("btn.pickFolder"), enabled: false);
            return;
        }

        if (!quiet)
            SetOrganizeButton(Strings.T("btn.scanning"), enabled: false);
        OrganizePlan plan;
        try
        {
            plan = await Task.Run(() => _service.Analyze(options));
        }
        catch (Exception ex)
        {
            _service.Log.Error("Scan failed", ex);
            SetOrganizeButton(Strings.T("btn.retry"), enabled: true);
            return;
        }
        if (version != _analyzeVersion)
            return; // The selection changed meanwhile; a newer scan is running.

        foreach (var folder in Folders)
        {
            var count = plan.Moves.Count(m => SamePath(m.SourceFolder, folder.Path));
            folder.Badge = !folder.IsAllowed ? Strings.T("badge.blocked") : !folder.IsChecked ? "" : count == 0 ? Strings.T("badge.tidy") : Strings.T("badge.files", count);
        }
        if (plan.Errors.Count > 0)
        {
            var text = Strings.Core(string.Join("\n", plan.Errors));
            // macOS asks once per folder (Desktop, Documents, Downloads, USB); a "no" shows up as an unreadable folder.
            if (OperatingSystem.IsMacOS() && plan.Errors.Any(e => e.StartsWith("Couldn't read folder:", StringComparison.Ordinal)))
                text += "\n\n" + Strings.T("mac.permission");
            ShowFolderError(text);
        }

        if (plan.Moves.Count == 0)
            SetOrganizeButton(Strings.T("btn.allTidy"), enabled: false);
        else
            SetOrganizeButton(Strings.T("btn.organize", plan.Moves.Count), enabled: true);
    }

    private void SetOrganizeButton(string text, bool enabled)
    {
        OrganizeText.Text = text;
        OrganizeButton.IsEnabled = enabled && !_busy;
    }

    // ================= Organize / undo =================

    private async void Organize_Click(object? sender, RoutedEventArgs e)
    {
        var options = Options();
        if (options.Folders.Count == 0 || _busy)
            return;

        SetBusy(true, Strings.T("btn.organizing"));
        OrganizeResult? result = null;
        try
        {
            // Re-scan right before moving so files that changed in the meantime are taken into account.
            result = await Task.Run(() => _service.Execute(_service.Analyze(options)));
        }
        catch (Exception ex)
        {
            _service.Log.Error("Organize failed", ex);
        }
        SetBusy(false);

        if (result is null)
        {
            ShowHomeMessage(Strings.T("msg.organizeFailed"), success: false);
            await AnalyzeAsync();
            return;
        }
        if (result.Moved.Count == 0)
        {
            ShowHomeMessage(result.Failed.Count > 0 ? Strings.T("msg.filesOpen") : Strings.T("msg.nothingLeft"), success: result.Failed.Count == 0);
            await AnalyzeAsync();
            return;
        }
        ShowDone(result);
    }

    private async void Undo_Click(object? sender, RoutedEventArgs e)
    {
        if (_lastRunId is { } runId)
            await UndoAsync(runId);
    }

    private async void UndoLast_Click(object? sender, RoutedEventArgs e)
    {
        if (_service.Journal.ListRuns().FirstOrDefault() is { } last)
            await UndoAsync(last.RunId);
    }

    /// <summary>No confirmation: undo never deletes anything, it only moves files back.</summary>
    private async Task UndoAsync(string runId)
    {
        if (_busy)
            return;
        SetBusy(true, Strings.T("btn.undoing"));
        UndoResult? undo = null;
        try
        {
            undo = await Task.Run(() => _service.Undo(runId));
        }
        catch (Exception ex)
        {
            _service.Log.Error("Undo failed", ex);
        }
        SetBusy(false);

        ShowHome();
        if (undo is null)
            ShowHomeMessage(Strings.T("msg.undoFailed"), success: false);
        else
            ShowHomeMessage(undo.Failed.Count == 0
                ? Strings.T("msg.undone", undo.Restored)
                : Strings.T("msg.undoneMissing", undo.Restored, undo.Failed.Count),
                success: undo.Failed.Count == 0);
        await AnalyzeAsync();
    }

    private void Back_Click(object? sender, RoutedEventArgs e)
    {
        ShowHome();
        _ = AnalyzeAsync();
    }

    private void OpenTargets_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var folder in _lastTargets.Where(Directory.Exists))
        {
            try
            {
                var start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "explorer.exe") { UseShellExecute = false };
                start.ArgumentList.Add(folder);
                Process.Start(start)?.Dispose();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _service.Log.Warn($"Couldn't open folder {folder}: {ex.Message}");
            }
        }
    }

    private void SetBusy(bool busy, string? text = null)
    {
        _busy = busy;
        BusyBar.Opacity = busy ? 1 : 0;
        UndoButton.IsEnabled = !busy;
        if (busy)
            SetOrganizeButton(text ?? "", enabled: false);
    }

    private void UpdateUndoLink()
    {
        var last = _service.Journal.ListRuns().FirstOrDefault();
        UndoLastLink.IsVisible = last is not null;
        if (last is not null)
            UndoLastText.Text = Strings.T("msg.undoLast", last.Count);
    }

    private void ShowHomeMessage(string text, bool success)
    {
        HomeMessage.Text = text;
        HomeMessage.Foreground = new SolidColorBrush(Color.Parse(success ? "#16A34A" : "#C27C0E"));
        HomeMessage.IsVisible = true;
    }

    // ================= Screens and animation =================

    private void ShowHome()
    {
        UpdateUndoLink();
        LangSwitch.IsVisible = true;
        DoneView.IsVisible = false;
        HomeView.IsVisible = true;
        _ = Reveal(HomeView);
    }

    private void ShowDone(OrganizeResult result)
    {
        _lastRunId = result.RunId;
        _lastTargets = result.Moved.Select(m => m.SourceFolder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var sources = result.Moved.Select(m => FolderName(m.SourceFolder)).Distinct().ToList();
        DoneTitle.Text = Strings.T("done.title", result.Moved.Count);
        DoneSubtitle.Text = Strings.T("done.subtitle", Strings.JoinList(sources));
        DoneChips.ItemsSource = result.Moved.GroupBy(m => m.Category).OrderByDescending(g => g.Count())
            .Select(g => new ChipItem($"{g.Key}  {g.Count()}", CategoryVisuals.Glyph(g.Key), CategoryVisuals.Foreground(g.Key), CategoryVisuals.Background(g.Key)))
            .ToList();
        DoneNote.Text = Strings.T("done.skipped", result.Failed.Count);
        DoneNote.IsVisible = result.Failed.Count > 0;

        LangSwitch.IsVisible = false; // language can't be changed on the result screen
        HomeView.IsVisible = false;
        DoneView.IsVisible = true;
        _ = Reveal(DoneView);
        _ = Pop(CheckGroup);
    }

    /// <summary>The new screen fades in.</summary>
    private static Task Reveal(Visual view) => new Animation
    {
        Duration = TimeSpan.FromMilliseconds(260),
        FillMode = FillMode.Forward,
        Easing = new CubicEaseOut(),
        Children =
        {
            new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0.0) } },
            new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1.0) } },
        },
    }.RunAsync(view);

    /// <summary>The check circle pops in.</summary>
    private static Task Pop(Visual target) => new Animation
    {
        Duration = TimeSpan.FromMilliseconds(420),
        FillMode = FillMode.Forward,
        Easing = new BackEaseOut(),
        Children =
        {
            new KeyFrame { Cue = new Cue(0), Setters = { new Setter(ScaleTransform.ScaleXProperty, 0.0), new Setter(ScaleTransform.ScaleYProperty, 0.0) } },
            new KeyFrame { Cue = new Cue(1), Setters = { new Setter(ScaleTransform.ScaleXProperty, 1.0), new Setter(ScaleTransform.ScaleYProperty, 1.0) } },
        },
    }.RunAsync(target);

    // ================= Helpers =================

    private string FolderName(string path) =>
        Folders.FirstOrDefault(f => SamePath(f.Path, path))?.Name ?? DisplayName(path);

    /// <summary>Folder name; a USB stick on a Mac is /Volumes/NAME, so that is its name too. A Windows drive root shows "USB (E:)".</summary>
    private static string DisplayName(string path)
    {
        if (Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name)
            return name;
        var letter = path.TrimEnd('\\', '/');
        try
        {
            if (new DriveInfo(path).VolumeLabel is { Length: > 0 } label)
                return $"{label} ({letter})";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Drive not ready: the letter alone is enough.
        }
        return letter.Length > 0 ? letter : path;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);
}
