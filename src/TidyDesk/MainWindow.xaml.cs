using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using TidyDesk.Core;
using Microsoft.Win32;

namespace TidyDesk;

/// <summary>
/// Main screens: pick folders and press the button → animated "done" screen. Also a preview of what will move
/// and a history of earlier cleanups, each of which can be undone.
/// Scanning runs in the background whenever the selection changes; the user only sees the counts.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly (string Token, string NameKey, string Glyph)[] StandardFolders =
    [
        ("desktop", "folder.desktop", ""),
        ("documents", "folder.documents", ""),
        ("downloads", "folder.downloads", ""),
    ];

    /// <summary>Animated value that makes the number in the title count up.</summary>
    private static readonly DependencyProperty CountProperty = DependencyProperty.Register(
        nameof(Count), typeof(double), typeof(MainWindow),
        new PropertyMetadata(0.0, (d, e) => ((MainWindow)d).DoneTitle.Text = Strings.T("done.title", (int)Math.Round((double)e.NewValue))));

    private readonly OrganizerService _service;
    private readonly Func<string, string?> _resolver;
    private readonly ObservableCollection<FolderItem> _folders = [];
    private readonly UserSettings _settings;
    private readonly bool _demoMode;
    private int _analyzeVersion;
    private bool _busy;
    private string? _lastRunId;
    private OrganizePlan? _lastPlan;
    private bool _fillingAgeBox;
    private static readonly int[] AgeChoices = [0, 7, 30, 90, 365];
    private List<string> _lastTargets = [];

    private double Count { get => (double)GetValue(CountProperty); set => SetValue(CountProperty, value); }

    /// <summary>Animations are skipped when "Show animations" is turned off in Windows.</summary>
    private static bool Animate => SystemParameters.ClientAreaAnimation;

    internal MainWindow(OrganizerService service, Func<string, string?> resolver, bool demoMode)
    {
        _service = service;
        _resolver = resolver;
        _settings = service.LoadSettings();
        _demoMode = demoMode;
        InitializeComponent();
        ApplyLanguage();

        FolderTiles.ItemsSource = new CompositeCollection { new CollectionContainer { Collection = _folders }, new AddFolderTile() };
        LoadFolders();
        UpdateUndoLink();
        Loaded += async (_, _) =>
        {
            Reveal(HomeView);
            await AnalyzeAsync();
        };
        // When the window regains focus the counts refresh quietly; the button stays usable meanwhile.
        Activated += async (_, _) => { if (HomeView.Visibility == Visibility.Visible && !_busy) await AnalyzeAsync(quiet: true); };
    }

    internal void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    // ================= Folders =================

    private void LoadFolders()
    {
        _folders.Clear();
        foreach (var (token, nameKey, glyph) in StandardFolders)
        {
            if (_resolver(token) is { } path)
                _folders.Add(new FolderItem(Strings.T(nameKey), path, glyph, isCustom: false, Problem(path)));
        }
        foreach (var path in _settings.CustomFolders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_folders.Any(f => SamePath(f.Path, path)))
                _folders.Add(new FolderItem(DisplayName(path), path, "", isCustom: true, Problem(path))
                {
                    IncludeSubfolders = _settings.Subfolders.Any(s => SamePath(s, path)),
                });
        }

        // Desktop and Downloads are selected on first launch; after that the user's choice is remembered.
        var selected = _settings.Folders ?? StandardFolders.Where(f => f.Token != "documents").Select(f => _resolver(f.Token)).OfType<string>().ToList();
        foreach (var folder in _folders)
            folder.IsChecked = selected.Any(s => SamePath(s, folder.Path));
    }

    private async void Folder_Toggled(object sender, RoutedEventArgs e) => await SelectionChangedAsync();

    private async void Subfolders_Toggled(object sender, RoutedEventArgs e) => await SelectionChangedAsync();

    private async void Age_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingAgeBox || AgeBox.SelectedItem is not ComboBoxItem { Tag: int days })
            return;
        _settings.MinAgeDays = days;
        await SelectionChangedAsync();
    }

    private void FillAgeBox()
    {
        _fillingAgeBox = true;
        AgeBox.Items.Clear();
        // A value from a hand-edited settings file that isn't in the list is still shown.
        foreach (var days in AgeChoices.Append(_settings.MinAgeDays).Distinct().Order())
        {
            var text = AgeChoices.Contains(days) ? Strings.T($"age.{days}") : Strings.Language == "en" ? $"Older than {days} days" : $"{days} günden eski";
            var item = new ComboBoxItem { Content = text, Tag = days };
            AgeBox.Items.Add(item);
            if (days == _settings.MinAgeDays)
                AgeBox.SelectedItem = item;
        }
        _fillingAgeBox = false;
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Strings.T("dialog.pickFolder"), InitialDirectory = _resolver("documents") ?? "" };
        if (dialog.ShowDialog(this) != true)
            return;

        var path = dialog.FolderName;
        if (Problem(path) is { } problem)
        {
            FolderError.Text = Strings.T("error.folderBlocked", problem);
            FolderError.Visibility = Visibility.Visible;
            return;
        }
        if (_folders.FirstOrDefault(f => SamePath(f.Path, path)) is { } existing)
        {
            existing.IsChecked = true;
        }
        else
        {
            _folders.Add(new FolderItem(DisplayName(path), path, "", isCustom: true, problem: null) { IsChecked = true });
            _settings.CustomFolders.Add(path);
        }
        await SelectionChangedAsync();
    }

    private async void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FolderItem folder })
            return;
        _folders.Remove(folder);
        _settings.CustomFolders.RemoveAll(p => SamePath(p, folder.Path));
        await SelectionChangedAsync();
    }

    private async Task SelectionChangedAsync()
    {
        FolderError.Visibility = Visibility.Collapsed;
        HomeMessage.Visibility = Visibility.Collapsed;
        SaveChoices();
        await AnalyzeAsync();
    }

    private void SaveChoices()
    {
        _settings.Folders = SelectedFolders();
        _settings.Subfolders = _folders.Where(f => f.IncludeSubfolders).Select(f => f.Path).ToList();
        try
        {
            _service.SaveSettings(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _service.Log.Warn($"Couldn't save settings: {ex.Message}");
        }
    }

    private List<string> SelectedFolders() => _folders.Where(f => f.IsChecked).Select(f => f.Path).ToList();

    private OrganizeOptions Options() => new(SelectedFolders(), _settings.MinAgeDays, MonthFolders: false, Strings.Language,
        _folders.Where(f => f.IsChecked && f.IncludeSubfolders).Select(f => f.Path).ToList());

    private string? Problem(string path) => _service.Guard.Check(path) is { } problem ? Strings.Core(problem) : null;

    // ================= Language =================

    private async void Language_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string language } || language == Strings.Language)
            return;
        _settings.Language = language;
        SaveChoices();
        ApplyLanguage();
        LoadFolders();
        UpdateUndoLink();
        FolderError.Visibility = Visibility.Collapsed;
        HomeMessage.Visibility = Visibility.Collapsed;
        await AnalyzeAsync();
    }

    private void ApplyLanguage()
    {
        Strings.Apply(this, _settings.Language);
        FillAgeBox();
        if (_demoMode)
            Title = Strings.T("s.demoTitle");
        foreach (var button in new[] { LangTr, LangEn })
        {
            var selected = (string)button.Tag == Strings.Language;
            button.Background = selected ? (Brush)FindResource("BrandGradient") : Brushes.Transparent;
            if (selected)
                button.Foreground = Brushes.White;
            else
                button.SetResourceReference(ForegroundProperty, "TextFillColorSecondaryBrush");
        }
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
            foreach (var f in _folders)
                f.Badge = "";
            SetOrganizeButton(Strings.T("btn.pickFolder"), "", enabled: false);
            return;
        }

        if (!quiet)
            SetOrganizeButton(Strings.T("btn.scanning"), "", enabled: false);
        OrganizePlan plan;
        try
        {
            plan = await Task.Run(() => _service.Analyze(options));
        }
        catch (Exception ex)
        {
            _service.Log.Error("Scan failed", ex);
            SetOrganizeButton(Strings.T("btn.retry"), "", enabled: true);
            return;
        }
        if (version != _analyzeVersion)
            return; // The selection changed meanwhile; a newer scan is running.
        _lastPlan = plan;
        PreviewLink.IsEnabled = plan.Moves.Count + plan.Skipped.Count > 0;

        foreach (var folder in _folders)
        {
            var count = plan.Moves.Count(m => SamePath(m.SourceFolder, folder.Path));
            folder.Badge = !folder.IsAllowed ? Strings.T("badge.blocked") : !folder.IsChecked ? "" : count == 0 ? Strings.T("badge.tidy") : Strings.T("badge.files", count);
        }
        if (plan.Errors.Count > 0)
        {
            FolderError.Text = Strings.Core(string.Join("\n", plan.Errors));
            FolderError.Visibility = Visibility.Visible;
        }

        if (plan.Moves.Count == 0)
            SetOrganizeButton(Strings.T("btn.allTidy"), "", enabled: false);
        else
            SetOrganizeButton(Strings.T("btn.organize", plan.Moves.Count), "", enabled: true);
    }

    private void SetOrganizeButton(string text, string glyph, bool enabled)
    {
        OrganizeText.Text = text;
        OrganizeGlyph.Text = glyph;
        OrganizeButton.IsEnabled = enabled && !_busy;
    }

    // ================= Organize / undo =================

    private async void Organize_Click(object sender, RoutedEventArgs e)
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
            ShowHomeMessage(result.Failed.Count > 0
                ? Strings.T("msg.filesOpen")
                : Strings.T("msg.nothingLeft"), success: result.Failed.Count == 0);
            await AnalyzeAsync();
            return;
        }
        ShowDone(result);
    }

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_lastRunId is { } runId)
            await UndoAsync(runId);
    }

    private async void UndoLast_Click(object sender, RoutedEventArgs e)
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

    private async void HistoryUndo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string runId })
            await UndoAsync(runId);
    }

    // ================= Preview, history, rules =================

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (_lastPlan is not { } plan || _busy)
            return;
        var caution = (Brush)FindResource("SystemFillColorCautionBrush");
        var cautionTint = new SolidColorBrush(Color.FromArgb(0x26, 0xC2, 0x7C, 0x0E));
        PreviewList.ItemsSource = plan.Moves
            .OrderBy(m => FolderName(m.SourceFolder)).ThenBy(m => m.Category).ThenBy(m => m.SourcePath, StringComparer.OrdinalIgnoreCase)
            .Select(m => new PreviewItem(System.IO.Path.GetFileName(m.SourcePath), WhereTo(m), CategoryVisuals.Glyph(m.Category),
                CategoryVisuals.Foreground(m.Category), CategoryVisuals.Background(m.Category)))
            .Concat(plan.Skipped.Select(s => new PreviewItem(System.IO.Path.GetFileName(s.Path), Strings.T("preview.skipped", Strings.Core(s.Reason)),
                "\uE7BA", caution, cautionTint)))
            .ToList();
        PreviewTitle.Text = plan.Moves.Count == 0 ? Strings.T("preview.empty") : Strings.T("preview.title", plan.Moves.Count);
        PreviewOrganizeText.Text = Strings.T("btn.organize", plan.Moves.Count);
        PreviewOrganize.IsEnabled = plan.Moves.Count > 0 && !_busy;
        ShowView(PreviewView);
    }

    /// <summary>"Desktop › PDF", or "Documents › 2024 › PDF" for a file in a subfolder.</summary>
    private string WhereTo(PlannedMove move)
    {
        var relative = System.IO.Path.GetRelativePath(move.SourceFolder, System.IO.Path.GetDirectoryName(move.DestinationPath)!);
        var parts = relative.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar).Where(p => p is not ("" or "."));
        return string.Join("  ›  ", parts.Prepend(FolderName(move.SourceFolder)));
    }

    private void History_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;
        var runs = _service.Journal.ListRuns(includeUndone: true).Take(50).ToList();
        HistoryList.ItemsSource = runs
            .Select(r => new HistoryItem(r.RunId, Strings.T("history.item", Strings.Date(r.Time), r.Count), !r.Undone, r.Undone ? Strings.T("history.undone") : ""))
            .ToList();
        HistorySubtitle.Text = runs.Count == 0 ? Strings.T("history.empty") : Strings.T("history.subtitle");
        ShowView(HistoryView);
    }

    private void Rules_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _service.EnsureRulesFile();
            using var _ = Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { path }, UseShellExecute = false });
            ShowHome();
            ShowHomeMessage(Strings.T("rules.opened"), success: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _service.Log.Warn($"Couldn't open the rules file: {ex.Message}");
            ShowHome();
            ShowHomeMessage(Strings.T("rules.openFailed", ex.Message), success: false);
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        ShowHome();
        _ = AnalyzeAsync();
    }

    private void OpenTargets_Click(object sender, RoutedEventArgs e)
    {
        foreach (var folder in _lastTargets.Where(Directory.Exists))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private void SetBusy(bool busy, string? text = null)
    {
        _busy = busy;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
        UndoButton.IsEnabled = !busy;
        PreviewOrganize.IsEnabled = !busy && _lastPlan?.Moves.Count > 0;
        HistoryList.IsEnabled = !busy;
        if (busy)
            SetOrganizeButton(text ?? "", "", enabled: false);
    }

    private void UpdateUndoLink()
    {
        var last = _service.Journal.ListRuns().FirstOrDefault();
        UndoLastLink.Visibility = last is null ? Visibility.Collapsed : Visibility.Visible;
        UndoLastSeparator.Visibility = UndoLastLink.Visibility;
        if (last is not null)
            UndoLastText.Text = Strings.T("msg.undoLast", last.Count);
    }

    private void ShowHomeMessage(string text, bool success)
    {
        HomeMessage.Text = text;
        HomeMessage.SetResourceReference(TextBlock.ForegroundProperty, success ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush");
        HomeMessage.Visibility = Visibility.Visible;
    }

    // ================= Screens and animation =================

    private void ShowHome()
    {
        UpdateUndoLink();
        ShowView(HomeView);
    }

    private void ShowDone(OrganizeResult result)
    {
        _lastRunId = result.RunId;
        _lastTargets = result.Moved
            .Select(m => m.SourceFolder)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sources = result.Moved.Select(m => FolderName(m.SourceFolder)).Distinct().ToList();
        DoneSubtitle.Text = Strings.T("done.subtitle", Strings.JoinList(sources));
        DoneChips.ItemsSource = result.Moved.GroupBy(m => m.Category).OrderByDescending(g => g.Count())
            .Select(g => new ChipItem($"{g.Key}  {g.Count()}", CategoryVisuals.Glyph(g.Key), CategoryVisuals.Foreground(g.Key), CategoryVisuals.Background(g.Key)))
            .ToList();
        DoneNote.Text = Strings.T("done.skipped", result.Failed.Count);
        DoneNote.Visibility = result.Failed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        ShowView(DoneView);
        PlaySuccess(result.Moved.Count);
    }

    /// <summary>Shows one screen and hides the others. The language can be changed only on the main screen.</summary>
    private void ShowView(FrameworkElement show)
    {
        if (show.Visibility == Visibility.Visible)
            return;
        foreach (var view in new FrameworkElement[] { HomeView, DoneView, PreviewView, HistoryView })
            view.Visibility = view == show ? Visibility.Visible : Visibility.Collapsed;
        LangSwitch.Visibility = show == HomeView ? Visibility.Visible : Visibility.Collapsed;
        Reveal(show);
    }

    /// <summary>Smooth transition: the new screen fades in while sliding up slightly.</summary>
    private static void Reveal(FrameworkElement show)
    {
        if (!Animate)
            return;
        Run(show, OpacityProperty, 0, 1, 260);
        if (show is Viewbox { Child: FrameworkElement { RenderTransform: TranslateTransform slide } })
            Run(slide, TranslateTransform.YProperty, 18, 0, 320, ease: new CubicEase { EasingMode = EasingMode.EaseOut });
    }

    /// <summary>The circle pops in, the check is drawn, confetti bursts, the number counts up and categories appear one by one.</summary>
    private void PlaySuccess(int movedCount)
    {
        var scale = (ScaleTransform)CheckCircle.RenderTransform;
        Burst.Children.Clear();
        if (!Animate)
        {
            scale.ScaleX = scale.ScaleY = 1;
            CheckMark.StrokeDashOffset = 0;
            Count = movedCount;
            Dispatcher.BeginInvoke(() => ForEachChip(chip => chip.Opacity = 1), System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }

        var pop = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 };
        Run(scale, ScaleTransform.ScaleXProperty, 0, 1, 420, ease: pop);
        Run(scale, ScaleTransform.ScaleYProperty, 0, 1, 420, ease: pop);
        var ring = (ScaleTransform)PulseRing.RenderTransform;
        var spread = new CubicEase { EasingMode = EasingMode.EaseOut };
        Run(ring, ScaleTransform.ScaleXProperty, 1, 1.8, 900, 250, spread);
        Run(ring, ScaleTransform.ScaleYProperty, 1, 1.8, 900, 250, spread);
        Run(PulseRing, OpacityProperty, 0.8, 0, 900, 250);
        Run(CheckMark, Shape.StrokeDashOffsetProperty, 8, 0, 380, delay: 300, ease: new CubicEase { EasingMode = EasingMode.EaseOut });
        Run(this, CountProperty, 0, movedCount, Math.Clamp(movedCount * 40, 500, 1100), delay: 200, ease: new QuadraticEase { EasingMode = EasingMode.EaseOut });
        PlayBurst();

        // Category chips appear one by one once their templates exist.
        Dispatcher.BeginInvoke(() =>
        {
            var i = 0;
            ForEachChip(chip =>
            {
                var delay = 650 + i++ * 70;
                var slide = new TranslateTransform(0, 10); // objects from templates are frozen; use a new one to animate
                chip.RenderTransform = slide;
                Run(chip, OpacityProperty, 0, 1, 260, delay);
                Run(slide, TranslateTransform.YProperty, 10, 0, 320, delay, new CubicEase { EasingMode = EasingMode.EaseOut });
            });
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void PlayBurst()
    {
        string[] colors = ["#2B7CD3", "#D13438", "#107C41", "#E3742A", "#8764B8", "#C19C00", "#00A39A", "#C239B3"];
        var center = new Point(Burst.Width / 2, Burst.Height / 2);
        var random = new Random();
        const int count = 18;
        for (var i = 0; i < count; i++)
        {
            var size = 6 + random.NextDouble() * 6;
            var dot = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i % colors.Length])),
                Opacity = 0,
            };
            var move = new TranslateTransform();
            dot.RenderTransform = move;
            Canvas.SetLeft(dot, center.X - size / 2);
            Canvas.SetTop(dot, center.Y - size / 2);
            Burst.Children.Add(dot);

            var angle = i * (2 * Math.PI / count) + random.NextDouble() * 0.3;
            var distance = 72 + random.NextDouble() * 30;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Run(move, TranslateTransform.XProperty, 0, Math.Cos(angle) * distance, 700, 260, ease);
            Run(move, TranslateTransform.YProperty, 0, Math.Sin(angle) * distance, 700, 260, ease);
            var fade = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(260) };
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80))));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(800))));
            dot.BeginAnimation(OpacityProperty, fade);
        }
    }

    private void ForEachChip(Action<Border> action)
    {
        foreach (var item in DoneChips.Items)
        {
            if (DoneChips.ItemContainerGenerator.ContainerFromItem(item) is ContentPresenter presenter
                && VisualTreeHelper.GetChildrenCount(presenter) > 0
                && VisualTreeHelper.GetChild(presenter, 0) is Border chip)
                action(chip);
        }
    }

    private static void Run(IAnimatable target, DependencyProperty property, double from, double to, double ms,
        double delay = 0, IEasingFunction? ease = null) =>
        target.BeginAnimation(property, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delay),
            EasingFunction = ease,
        });

    // ================= Helpers =================

    private string FolderName(string path) =>
        _folders.FirstOrDefault(f => SamePath(f.Path, path))?.Name ?? DisplayName(path);

    /// <summary>Folder name, or for a whole drive its label like Explorer shows it: "USB (E:)".</summary>
    private static string DisplayName(string path)
    {
        if (System.IO.Path.GetFileName(path) is { Length: > 0 } name)
            return name;
        var letter = path.TrimEnd('\\');
        try
        {
            if (new DriveInfo(path).VolumeLabel is { Length: > 0 } label)
                return $"{label} ({letter})";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Drive not ready (card reader without a card) or not a local drive: the letter alone is enough.
        }
        return letter;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(System.IO.Path.TrimEndingDirectorySeparator(a), System.IO.Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);
}
