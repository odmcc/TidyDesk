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
/// Two screens: pick folders and press the button → animated "done" screen.
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
                _folders.Add(new FolderItem(System.IO.Path.GetFileName(path), path, "", isCustom: true, Problem(path)));
        }

        // Desktop and Downloads are selected on first launch; after that the user's choice is remembered.
        var selected = _settings.Folders ?? StandardFolders.Where(f => f.Token != "documents").Select(f => _resolver(f.Token)).OfType<string>().ToList();
        foreach (var folder in _folders)
            folder.IsChecked = selected.Any(s => SamePath(s, folder.Path));
    }

    private async void Folder_Toggled(object sender, RoutedEventArgs e) => await SelectionChangedAsync();

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
            _folders.Add(new FolderItem(System.IO.Path.GetFileName(path), path, "", isCustom: true, problem: null) { IsChecked = true });
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
        _settings.Folders = SelectedFolders();
        try
        {
            _service.SaveSettings(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _service.Log.Warn($"Couldn't save settings: {ex.Message}");
        }
        await AnalyzeAsync();
    }

    private List<string> SelectedFolders() => _folders.Where(f => f.IsChecked).Select(f => f.Path).ToList();

    private OrganizeOptions Options() => new(SelectedFolders(), MinAgeDays: 0, MonthFolders: false, Strings.Language);

    private string? Problem(string path) => _service.Guard.Check(path) is { } problem ? Strings.Core(problem) : null;

    // ================= Language =================

    private async void Language_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string language } || language == Strings.Language)
            return;
        _settings.Language = language;
        _settings.Folders = SelectedFolders();
        try
        {
            _service.SaveSettings(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _service.Log.Warn($"Couldn't save settings: {ex.Message}");
        }
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
            ShowHomeMessage(Strings.T("msg.organizeFailed"), success: false);
            await AnalyzeAsync();
            return;
        }
        if (result.Moved.Count == 0)
        {
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
        if (busy)
            SetOrganizeButton(text ?? "", "", enabled: false);
    }

    private void UpdateUndoLink()
    {
        var last = _service.Journal.ListRuns().FirstOrDefault();
        UndoLastLink.Visibility = last is null ? Visibility.Collapsed : Visibility.Visible;
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
        LangSwitch.Visibility = Visibility.Visible;
        SwitchTo(HomeView, DoneView);
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

        LangSwitch.Visibility = Visibility.Collapsed; // language can't be changed on the result screen
        SwitchTo(DoneView, HomeView);
        PlaySuccess(result.Moved.Count);
    }

    /// <summary>Smooth transition: the new screen fades in while sliding up slightly.</summary>
    private static void SwitchTo(FrameworkElement show, FrameworkElement hide)
    {
        hide.Visibility = Visibility.Collapsed;
        show.Visibility = Visibility.Visible;
        Reveal(show);
    }

    private static void Reveal(FrameworkElement show)
    {
        if (!Animate)
            return;
        var content = (FrameworkElement)((Viewbox)show).Child;
        Run(show, OpacityProperty, 0, 1, 260);
        Run((TranslateTransform)content.RenderTransform, TranslateTransform.YProperty, 18, 0, 320, ease: new CubicEase { EasingMode = EasingMode.EaseOut });
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
        _folders.FirstOrDefault(f => SamePath(f.Path, path))?.Name ?? System.IO.Path.GetFileName(path);

    private static bool SamePath(string a, string b) =>
        string.Equals(System.IO.Path.TrimEndingDirectorySeparator(a), System.IO.Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);
}
