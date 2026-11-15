using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using TidyDesk.Core;
using Path = System.IO.Path;

namespace TidyDesk.Mac;

/// <summary>
/// Same flow as the Windows app: pick folders and press the button → "done" screen with undo.
/// Also a preview of what will move, a history of earlier cleanups (each can be undone) and the user's own rules.
/// Scanning runs in the background whenever the selection changes; the user only sees the counts.
/// </summary>
public partial class MainWindow : Window
{
    public static readonly Geometry CheckIcon = Icons.Get(Icons.Checkmark);
    public static readonly Geometry DismissIcon = Icons.Get(Icons.Dismiss);
    public static readonly Geometry UndoIcon = Icons.Get(Icons.ArrowUndo);

    private static readonly (string Token, string NameKey, string Icon)[] StandardFolders =
    [
        ("desktop", "folder.desktop", Icons.Desktop),
        ("documents", "folder.documents", Icons.Document),
        ("downloads", "folder.downloads", Icons.ArrowDownload),
    ];
    private static readonly int[] AgeChoices = [0, 7, 30, 90, 365];

    private readonly OrganizerService _service;
    private readonly Func<string, string?> _resolver;
    private readonly ObservableCollection<object> _tiles = [];
    private readonly UserSettings _settings;
    private readonly bool _demoMode;
    private int _analyzeVersion;
    private bool _busy;
    private bool _fillingAgeBox;
    private string? _lastRunId;
    private OrganizePlan? _lastPlan;
    private List<string> _lastTargets = [];
    private DispatcherTimer? _countUp;

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
        // When the window regains focus (e.g. after editing the rules) the counts refresh quietly.
        Activated += async (_, _) => { if (HomeView.IsVisible && !_busy) await AnalyzeAsync(quiet: true); };
    }

    // ================= Folders =================

    private void LoadFolders()
    {
        _tiles.Clear();
        var folders = new List<FolderItem>();
        foreach (var (token, nameKey, icon) in StandardFolders)
        {
            if (_resolver(token) is { } path)
                folders.Add(NewFolder(Strings.T(nameKey), path, icon, isCustom: false, Problem(path)));
        }
        foreach (var path in _settings.CustomFolders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!folders.Any(f => SamePath(f.Path, path)))
            {
                var folder = NewFolder(DisplayName(path), path, Icons.Folder, isCustom: true, Problem(path));
                folder.IncludeSubfolders = _settings.Subfolders.Any(s => SamePath(s, path));
                folders.Add(folder);
            }
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

    private static FolderItem NewFolder(string name, string path, string icon, bool isCustom, string? problem) =>
        new(name, path, Icons.Get(icon), isCustom, problem)
        {
            RemoveTip = Strings.T("s.removeFolder"),
            SubfoldersLabel = Strings.T("card.subfolders"),
            SubfoldersTip = Strings.T("card.subfoldersTip"),
        };

    private IEnumerable<FolderItem> Folders => _tiles.OfType<FolderItem>();

    private async void Folder_Toggled(object? sender, RoutedEventArgs e) => await SelectionChangedAsync();

    private async void Subfolders_Toggled(object? sender, RoutedEventArgs e) => await SelectionChangedAsync();

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
            _tiles.Insert(_tiles.Count - 1, NewFolder(DisplayName(path), path, Icons.Folder, isCustom: true, problem: null));
            ((FolderItem)_tiles[^2]).IsChecked = true;
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
        SaveChoices();
        await AnalyzeAsync();
    }

    private void SaveChoices()
    {
        _settings.Folders = SelectedFolders();
        _settings.Subfolders = Folders.Where(f => f.IncludeSubfolders).Select(f => f.Path).ToList();
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

    private OrganizeOptions Options() => new(SelectedFolders(), _settings.MinAgeDays, MonthFolders: false, Strings.Language,
        Folders.Where(f => f.IsChecked && f.IncludeSubfolders).Select(f => f.Path).ToList());

    private string? Problem(string path) => _service.Guard.Check(path) is { } problem ? Strings.Core(problem) : null;

    private void ShowFolderError(string text)
    {
        FolderError.Text = text;
        FolderError.IsVisible = true;
    }

    // ================= Age option =================

    private async void Age_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_fillingAgeBox || AgeBox.SelectedItem is not ComboBoxItem { Tag: int days })
            return;
        _settings.MinAgeDays = days;
        await SelectionChangedAsync();
    }

    private void FillAgeBox()
    {
        _fillingAgeBox = true;
        var items = new List<ComboBoxItem>();
        // A value from a hand-edited settings file that isn't in the list is still shown.
        foreach (var days in AgeChoices.Append(_settings.MinAgeDays).Distinct().Order())
        {
            var text = AgeChoices.Contains(days) ? Strings.T($"age.{days}") : Strings.Language == "en" ? $"Older than {days} days" : $"{days} günden eski";
            items.Add(new ComboBoxItem { Content = text, Tag = days });
        }
        AgeBox.ItemsSource = items;
        AgeBox.SelectedItem = items.First(i => (int)i.Tag! == _settings.MinAgeDays);
        _fillingAgeBox = false;
    }

    // ================= Language =================

    private async void Language_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string language } || language == Strings.Language)
            return;
        _settings.Language = language;
        SaveChoices();
        ApplyLanguage();
        LoadFolders();
        UpdateUndoLink();
        App.RefreshMenu();
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
        AgeLabel.Text = Strings.T("age.label");
        AutomationProperties.SetName(AgeBox, Strings.T("age.label"));
        PreviewLinkText.Text = Strings.T("link.preview");
        HistoryLinkText.Text = Strings.T("link.history");
        RulesLinkText.Text = Strings.T("link.rules");
        Footer.Text = Strings.T("s.footer");
        OpenText.Text = Strings.T(OperatingSystem.IsMacOS() ? "mac.showInFinder" : "done.open");
        UndoText.Text = Strings.T("done.undo");
        OkText.Text = Strings.T("done.ok");
        PreviewSubtitle.Text = Strings.T("preview.subtitle");
        PreviewBackText.Text = HistoryBackText.Text = Strings.T("preview.back");
        HistoryTitle.Text = Strings.T("history.title");
        FillAgeBox();
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
            _lastPlan = null;
            PreviewLink.IsEnabled = false;
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
        _lastPlan = plan;
        PreviewLink.IsEnabled = plan.Moves.Count + plan.Skipped.Count > 0;

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
        else
        {
            FolderError.IsVisible = false;
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
            ShowHome();
            ShowHomeMessage(Strings.T("msg.organizeFailed"), success: false);
            await AnalyzeAsync();
            return;
        }
        if (result.Moved.Count == 0)
        {
            ShowHome();
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

    private async void HistoryUndo_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string runId })
            await UndoAsync(runId);
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
            Open(folder);
    }

    /// <summary>Opens a folder in Finder (Explorer when the app runs on Windows), or a text file with "textEdit".</summary>
    private bool Open(string path, bool textEdit = false)
    {
        try
        {
            var start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : textEdit ? "notepad.exe" : "explorer.exe") { UseShellExecute = false };
            if (OperatingSystem.IsMacOS() && textEdit)
                start.ArgumentList.Add("-t");
            start.ArgumentList.Add(path);
            Process.Start(start)?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _service.Log.Warn($"Couldn't open {path}: {ex.Message}");
            return false;
        }
    }

    private void SetBusy(bool busy, string? text = null)
    {
        _busy = busy;
        BusyBar.Opacity = busy ? 1 : 0;
        UndoButton.IsEnabled = !busy;
        PreviewOrganize.IsEnabled = !busy && _lastPlan?.Moves.Count > 0;
        HistoryList.IsEnabled = !busy;
        if (busy)
            SetOrganizeButton(text ?? "", enabled: false);
    }

    private void UpdateUndoLink()
    {
        var last = _service.Journal.ListRuns().FirstOrDefault();
        UndoLastLink.IsVisible = UndoLastSeparator.IsVisible = last is not null;
        if (last is not null)
            UndoLastText.Text = Strings.T("msg.undoLast", last.Count);
    }

    private void ShowHomeMessage(string text, bool success)
    {
        HomeMessage.Text = text;
        HomeMessage.Foreground = new SolidColorBrush(Color.Parse(success ? "#16A34A" : "#C27C0E"));
        HomeMessage.IsVisible = true;
    }

    // ================= Preview, history, rules =================

    private void Preview_Click(object? sender, RoutedEventArgs e)
    {
        if (_lastPlan is not { } plan || _busy)
            return;
        var caution = new SolidColorBrush(Color.Parse("#C27C0E"));
        var cautionTint = new SolidColorBrush(Color.Parse("#26C27C0E"));
        var warning = Icons.Get(Icons.Warning);
        PreviewList.ItemsSource = plan.Moves
            .OrderBy(m => FolderName(m.SourceFolder)).ThenBy(m => m.Category).ThenBy(m => m.SourcePath, StringComparer.OrdinalIgnoreCase)
            .Select(m => new PreviewItem(Path.GetFileName(m.SourcePath), WhereTo(m), CategoryVisuals.Icon(m.Category),
                CategoryVisuals.Foreground(m.Category), CategoryVisuals.Background(m.Category)))
            .Concat(plan.Skipped.Select(s => new PreviewItem(Path.GetFileName(s.Path), Strings.T("preview.skipped", Strings.Core(s.Reason)), warning, caution, cautionTint)))
            .ToList();
        PreviewTitle.Text = plan.Moves.Count == 0 ? Strings.T("preview.empty") : Strings.T("preview.title", plan.Moves.Count);
        PreviewOrganizeText.Text = Strings.T("btn.organize", plan.Moves.Count);
        PreviewOrganize.IsEnabled = plan.Moves.Count > 0 && !_busy;
        ShowView(PreviewView);
    }

    /// <summary>"Desktop › PDF", or "Documents › 2024 › PDF" for a file in a subfolder.</summary>
    private string WhereTo(PlannedMove move)
    {
        var relative = Path.GetRelativePath(move.SourceFolder, Path.GetDirectoryName(move.DestinationPath)!);
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Where(p => p is not ("" or "."));
        return string.Join("  ›  ", parts.Prepend(FolderName(move.SourceFolder)));
    }

    private void History_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy)
            return;
        var runs = _service.Journal.ListRuns(includeUndone: true).Take(50).ToList();
        HistoryList.ItemsSource = runs
            .Select(r => new HistoryItem(r.RunId, Strings.T("history.item", Strings.Date(r.Time), r.Count), !r.Undone,
                r.Undone ? Strings.T("history.undone") : "", Strings.T("history.undo")))
            .ToList();
        HistorySubtitle.Text = runs.Count == 0 ? Strings.T("history.empty") : Strings.T("history.subtitle");
        ShowView(HistoryView);
    }

    private void Rules_Click(object? sender, RoutedEventArgs e)
    {
        ShowHome();
        try
        {
            var path = _service.EnsureRulesFile();
            if (Open(path, textEdit: true))
            {
                ShowHomeMessage(Strings.T("rules.opened"), success: true);
                return;
            }
            ShowHomeMessage(Strings.T("rules.openFailed", path), success: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _service.Log.Warn($"Couldn't create the rules file: {ex.Message}");
            ShowHomeMessage(Strings.T("rules.openFailed", ex.Message), success: false);
        }
    }

    // ================= Screens and animation =================

    private void ShowHome()
    {
        UpdateUndoLink();
        ShowView(HomeView);
    }

    /// <summary>Shows one screen and hides the others. The language can be changed only on the main screen.</summary>
    private void ShowView(Control show)
    {
        if (show.IsVisible)
            return;
        foreach (var view in new Control[] { HomeView, DoneView, PreviewView, HistoryView })
            view.IsVisible = view == show;
        LangSwitch.IsVisible = show == HomeView;
        _ = Reveal(show);
    }

    private void ShowDone(OrganizeResult result)
    {
        _lastRunId = result.RunId;
        _lastTargets = result.Moved.Select(m => m.SourceFolder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var sources = result.Moved.Select(m => FolderName(m.SourceFolder)).Distinct().ToList();
        DoneSubtitle.Text = Strings.T("done.subtitle", Strings.JoinList(sources));
        DoneChips.ItemsSource = result.Moved.GroupBy(m => m.Category).OrderByDescending(g => g.Count())
            .Select(g => new ChipItem($"{g.Key}  {g.Count()}", CategoryVisuals.Icon(g.Key), CategoryVisuals.Foreground(g.Key), CategoryVisuals.Background(g.Key)))
            .ToList();
        DoneNote.Text = Strings.T("done.skipped", result.Failed.Count);
        DoneNote.IsVisible = result.Failed.Count > 0;

        ShowView(DoneView);
        PlaySuccess(result.Moved.Count);
    }

    /// <summary>The circle pops in, confetti bursts and the number counts up (all skipped with "Reduce motion").</summary>
    private void PlaySuccess(int movedCount)
    {
        _countUp?.Stop();
        Burst.Children.Clear();
        if (!Motion.Animate)
        {
            DoneTitle.Text = Strings.T("done.title", movedCount);
            return;
        }

        _ = Pop(CheckGroup);
        PlayBurst();
        var started = DateTime.UtcNow;
        var duration = TimeSpan.FromMilliseconds(Math.Clamp(movedCount * 40, 500, 1100));
        DoneTitle.Text = Strings.T("done.title", 0);
        _countUp = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            var t = Math.Min(1, (DateTime.UtcNow - started) / duration);
            DoneTitle.Text = Strings.T("done.title", (int)Math.Round(movedCount * (1 - (1 - t) * (1 - t))));
            if (t >= 1)
                _countUp?.Stop();
        });
        _countUp.Start();
    }

    private void PlayBurst()
    {
        string[] colors = ["#2B7CD3", "#D13438", "#107C41", "#E3742A", "#8764B8", "#C19C00", "#00A39A", "#C239B3"];
        var random = new Random();
        const int count = 18;
        for (var i = 0; i < count; i++)
        {
            var size = 6 + random.NextDouble() * 6;
            var dot = new Ellipse { Width = size, Height = size, Fill = new SolidColorBrush(Color.Parse(colors[i % colors.Length])), Opacity = 0 };
            dot.RenderTransform = new TranslateTransform();
            Canvas.SetLeft(dot, Burst.Width / 2 - size / 2);
            Canvas.SetTop(dot, Burst.Height / 2 - size / 2);
            Burst.Children.Add(dot);

            var angle = i * (2 * Math.PI / count) + random.NextDouble() * 0.3;
            var distance = 72 + random.NextDouble() * 30;
            _ = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(800),
                Delay = TimeSpan.FromMilliseconds(260),
                FillMode = FillMode.Forward,
                Easing = new CubicEaseOut(),
                Children =
                {
                    new KeyFrame { Cue = new Cue(0), Setters = { new Setter(TranslateTransform.XProperty, 0.0), new Setter(TranslateTransform.YProperty, 0.0), new Setter(OpacityProperty, 1.0) } },
                    new KeyFrame { Cue = new Cue(0.7), Setters = { new Setter(OpacityProperty, 0.9) } },
                    new KeyFrame { Cue = new Cue(1), Setters = { new Setter(TranslateTransform.XProperty, Math.Cos(angle) * distance), new Setter(TranslateTransform.YProperty, Math.Sin(angle) * distance), new Setter(OpacityProperty, 0.0) } },
                },
            }.RunAsync(dot);
        }
    }

    /// <summary>The new screen fades in.</summary>
    private static Task Reveal(Visual view) => !Motion.Animate ? Task.CompletedTask : new Animation
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
