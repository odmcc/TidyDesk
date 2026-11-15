using System.Text.Json;

namespace TidyDesk.Core;

/// <summary>The choices the user made in the app.</summary>
/// <param name="Language">With "en", category folders get English names (Documents, Images...).</param>
/// <param name="Subfolders">Folders (from <paramref name="Folders"/>) whose subfolders are organized too.</param>
public sealed record OrganizeOptions(IReadOnlyList<string> Folders, int MinAgeDays, bool MonthFolders, string Language = "tr",
    IReadOnlyCollection<string>? Subfolders = null);

/// <summary>
/// Entry point used by the app: scans the selected folders, moves the files, and undoes runs.
/// Every folder goes through <see cref="PathGuard"/>; files are moved into category folders
/// (PDF, Documents, Spreadsheets...) inside the same folder.
/// </summary>
public sealed class OrganizerService
{
    private readonly AppPaths _paths;
    private readonly Func<DateTime>? _clock;
    private readonly object _gate = new();
    private RuleSet? _lastRules;

    public OrganizerService(AppPaths paths, ILog log, PathGuard? guard = null, Func<DateTime>? clock = null)
    {
        _paths = paths;
        _clock = clock;
        Log = log;
        Guard = guard ?? new PathGuard();
        Journal = new UndoJournal(paths.JournalDir, log);
    }

    public ILog Log { get; }
    public PathGuard Guard { get; }
    public UndoJournal Journal { get; }

    public UserSettings LoadSettings() => UserSettings.Load(_paths.SettingsFile);

    public void SaveSettings(UserSettings settings) => settings.Save(_paths.SettingsFile);

    /// <summary>Works out what would happen without touching any file.</summary>
    public OrganizePlan Analyze(OrganizeOptions options)
    {
        var rules = LoadRules(out var rulesError);
        if (options.Language == "en")
            rules.UseEnglishNames();
        rules.MinAgeDays = Math.Max(0, options.MinAgeDays);
        rules.DateFolderFormat = options.MonthFolders ? "yyyy-MM" : "";
        rules.Sources = options.Folders
            .Select(f => new SourceFolderRule
            {
                Folder = f,
                Target = f,
                IncludeSubfolders = options.Subfolders?.Contains(f, StringComparer.OrdinalIgnoreCase) == true,
            })
            .ToList();
        _lastRules = rules;

        var plan = CreateOrganizer(rules).CreatePlan();
        if (rulesError is not null)
            plan.Errors.Insert(0, rulesError);
        foreach (var error in plan.Errors)
            Log.Warn(error);
        Log.Info($"Scan: {options.Folders.Count} folders, {plan.Moves.Count} files to organize, {plan.Skipped.Count} skipped.");
        return plan;
    }

    /// <summary>Applies the plan; each file is checked again right before it moves. Returns null if another operation is running.</summary>
    public OrganizeResult? Execute(OrganizePlan plan) =>
        Exclusive(() => CreateOrganizer(_lastRules ?? RuleSet.CreateDefault()).Execute(plan));

    /// <summary>Undoes a run. Returns null if another operation is running.</summary>
    public UndoResult? Undo(string runId) => Exclusive(() => Journal.Undo(runId));

    /// <summary>Path of the user's own rules file (it may not exist yet).</summary>
    public string RulesFile => _paths.RulesFile;

    /// <summary>Creates the user's rules file from the built-in rules if it doesn't exist; returns its path.</summary>
    public string EnsureRulesFile()
    {
        if (!File.Exists(_paths.RulesFile))
        {
            Directory.CreateDirectory(_paths.DataDir);
            File.WriteAllText(_paths.RulesFile,
                "// Your own TidyDesk rules. Edit the categories and exclusions below, save, and switch back to TidyDesk.\n" +
                "// Delete this file to go back to the built-in rules.\n" + RuleSet.DefaultJson());
        }
        return _paths.RulesFile;
    }

    /// <summary>The user's rules file if there is one, otherwise the built-in rules. A broken file falls back to the built-in rules.</summary>
    private RuleSet LoadRules(out string? error)
    {
        error = null;
        if (File.Exists(_paths.RulesFile))
        {
            try
            {
                return RuleSet.Parse(File.ReadAllText(_paths.RulesFile));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                error = $"Your rules file has an error, so the built-in rules are used: {ex.Message}";
                Log.Warn(error);
            }
        }
        return RuleSet.CreateDefault();
    }

    private Organizer CreateOrganizer(RuleSet rules) => new(rules, _ => null, Journal, Log, _clock, Guard);

    private T? Exclusive<T>(Func<T> action) where T : class
    {
        if (!Monitor.TryEnter(_gate))
            return null;
        try
        {
            return action();
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }
}
