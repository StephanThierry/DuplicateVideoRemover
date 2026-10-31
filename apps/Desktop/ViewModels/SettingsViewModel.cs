using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using deepduplicates;

namespace DuplicateVideoRemover.Desktop.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppController app;
    private CancellationTokenSource? debounce;
    private bool loading;

    /// <summary>Set by the view: opens the system folder picker.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    public PathList ContentFolders { get; }
    public PathList ExcludePaths { get; }
    public PathList PriorityFolders { get; }
    public ObservableCollection<MappingItem> Mappings { get; } = new();
    public ObservableCollection<SynonymItem> Synonyms { get; } = new();

    // "Update paths" confirmation for a synonym
    [ObservableProperty] private SynonymItem? confirmSynonym;
    [ObservableProperty] private string confirmTitle = "";
    [ObservableProperty] private string confirmText = "";
    [ObservableProperty] private bool updating;
    public bool ConfirmVisible => ConfirmSynonym != null;
    public ObservableCollection<SwitchItem> Switches { get; } = new();
    public MatchViewModel Match { get; }
    public MatchViewModel SameFolder { get; }
    public EncodeSettingsViewModel Encoding { get; }

    [ObservableProperty] private bool useSameFolder;
    [ObservableProperty] private decimal minLength;
    [ObservableProperty] private decimal minSizeKb;
    [ObservableProperty] private string testPath = "";
    [ObservableProperty] private string testResult = "";
    [ObservableProperty] private bool dirty;
    [ObservableProperty] private string savedMessage = "";

    [ObservableProperty] private string previewDuplicates = "";
    [ObservableProperty] private string previewGroups = "";
    [ObservableProperty] private string previewBroken = "";
    [ObservableProperty] private string previewSize = "";
    [ObservableProperty] private string previewNote = "Working out the result...";

    public string Placeholder => OperatingSystem.IsWindows() ? @"D:\Videos or \\nas\videos" : "/mnt/videos";

    public SettingsViewModel(AppController app)
    {
        this.app = app;
        loading = true;
        var s = app.Settings.Clone();
        ContentFolders = new PathList(s.contentFolders, Edited, () => PickFolder);
        ExcludePaths = new PathList(s.excludePaths, Edited, null);
        PriorityFolders = new PathList(s.priorityFolders, Edited, () => PickFolder);
        foreach (var m in PathMapper.Mappings) Mappings.Add(new MappingItem(m.Windows, m.Linux, Edited));
        foreach (var syn in PathMapper.Synonyms) Synonyms.Add(new SynonymItem(syn.Main, syn.Alias, Edited, AskUpdate));
        foreach (var w in s.switchPriority) Switches.Add(new SwitchItem(w.up, w.down, w.triggerBelowPctSizeDiff, Edited));
        Match = new MatchViewModel(s.matchSettings, Edited);
        UseSameFolder = s.matchSettings_sameFolder != null;
        SameFolder = new MatchViewModel(s.matchSettings_sameFolder ?? s.matchSettings.Clone(), Edited);
        Encoding = new EncodeSettingsViewModel(s.EncodeOrDefault(), Edited);
        MinLength = s.minVideoLength;
        MinSizeKb = s.minVideoSizeKb;
        loading = false;
        SchedulePreview(0);
        app.DataChanged += () => SchedulePreview(0);
    }

    partial void OnUseSameFolderChanged(bool value) => Edited();
    partial void OnMinLengthChanged(decimal value) => Edited();
    partial void OnMinSizeKbChanged(decimal value) => Edited();
    partial void OnTestPathChanged(string value) => UpdateTest();

    private void Edited()
    {
        if (loading) return;
        Dirty = true;
        SavedMessage = "";
        UpdateTest();
        SchedulePreview(400);
    }

    private deepduplicates.Settings Current()
    {
        var s = app.Settings.Clone();
        s.contentFolders = ContentFolders.Values();
        s.excludePaths = ExcludePaths.Values();
        s.priorityFolders = PriorityFolders.Values();
        s.switchPriority = Switches.Where(w => !string.IsNullOrWhiteSpace(w.Up) && !string.IsNullOrWhiteSpace(w.Down))
            .Select(w => new switchPrioritySet { up = w.Up.Trim(), down = w.Down.Trim(), triggerBelowPctSizeDiff = (int)w.Percent }).ToArray();
        s.matchSettings = Match.ToSettings();
        s.matchSettings_sameFolder = UseSameFolder ? SameFolder.ToSettings() : null;
        s.minVideoLength = (int)MinLength;
        s.minVideoSizeKb = (int)MinSizeKb;
        s.encodeSettings = Encoding.ToSettings();
        return s;
    }

    private PathMapping[] CurrentMappings() =>
        Mappings.Select(m => new PathMapping { Windows = m.Windows, Linux = m.Linux }).ToArray();

    private PathSynonym[] CurrentSynonyms() =>
        Synonyms.Select(s => new PathSynonym { Main = s.Main, Alias = s.Alias }).ToArray();

    partial void OnConfirmSynonymChanged(SynonymItem? value) => OnPropertyChanged(nameof(ConfirmVisible));

    private void AskUpdate(SynonymItem item)
    {
        ConfirmSynonym = item;
        ConfirmTitle = $"Update {Fmt.Plural(item.Count, "path", "paths")} in the database?";
        ConfirmText = $"Paths starting with {PathMapper.NormalizeRoot(item.Alias)} are changed to start with {PathMapper.NormalizeRoot(item.Main)}. "
                    + "Videos stored under both names become one. Your settings are saved too, so new scans use the main name. This can't be undone.";
    }

    [RelayCommand] private void CancelUpdate() => ConfirmSynonym = null;

    // Saves the settings (so new scans use the main name) and rewrites the stored paths once
    [RelayCommand]
    private async Task ConfirmUpdate()
    {
        var item = ConfirmSynonym;
        if (item == null) return;
        Updating = true;
        Save();
        var report = await Task.Run(() => app.UpdateSynonymPaths(new PathSynonym { Main = item.Main, Alias = item.Alias }));
        Updating = false;
        ConfirmSynonym = null;
        item.ShowResult(report);
    }

    [RelayCommand] private void AddSynonym() { Synonyms.Add(new SynonymItem("", "", Edited, AskUpdate)); }
    [RelayCommand] private void RemoveSynonym(SynonymItem item) { item.Cancel(); Synonyms.Remove(item); Edited(); }

    private void UpdateTest()
    {
        if (string.IsNullOrWhiteSpace(TestPath)) { TestResult = ""; return; }
        var local = PathMapper.PreviewLocal(TestPath.Trim(), CurrentMappings());
        bool exists = Directory.Exists(local) || File.Exists(local);
        TestResult = $"On this computer: {local} ({(exists ? "found" : "not found")})";
    }

    // Re-runs the comparison on videos already read, a moment after the last change
    private void SchedulePreview(int delayMs)
    {
        debounce?.Cancel();
        debounce = new CancellationTokenSource();
        var token = debounce.Token;
        var snapshot = Current();
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, token);
                var r = app.Preview(snapshot);
                if (token.IsCancellationRequested) return;
                Dispatcher.UIThread.Post(() =>
                {
                    PreviewDuplicates = r.Duplicates.ToString("N0");
                    PreviewGroups = r.Groups.ToString("N0");
                    PreviewBroken = r.Broken.ToString("N0");
                    PreviewSize = Fmt.Size(r.DuplicateBytes);
                    PreviewNote = $"Based on the {r.VideosInScope:N0} videos already read in these folders. New videos count after the next scan.";
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Dispatcher.UIThread.Post(() => PreviewNote = "Could not work out the result: " + e.Message); }
        });
    }

    [RelayCommand]
    private void Save()
    {
        app.SaveSettings(Current(), CurrentMappings(), CurrentSynonyms());
        Dirty = false;
        SavedMessage = "Saved";
    }

    [RelayCommand]
    private async Task SaveAndCompare()
    {
        Save();
        await Task.Run(app.ReanalyzeFromDatabase);
        SavedMessage = "Saved and compared - see Review";
    }

    [RelayCommand] private void AddMapping() { Mappings.Add(new MappingItem("", "", Edited)); }
    [RelayCommand] private void RemoveMapping(MappingItem item) { item.Cancel(); Mappings.Remove(item); Edited(); }
    [RelayCommand] private void AddSwitch() { Switches.Add(new SwitchItem("x265", "x264", 15, Edited)); Edited(); }
    [RelayCommand] private void RemoveSwitch(SwitchItem item) { Switches.Remove(item); Edited(); }
}

/// <summary>An editable list of paths with an "add" box.</summary>
public partial class PathList : ObservableObject
{
    private readonly Action changed;
    private readonly Func<Func<Task<string?>>?>? picker;

    public ObservableCollection<PathItem> Items { get; } = new();
    [ObservableProperty] private string newValue = "";
    public bool CanBrowse => picker != null;

    public PathList(string[] values, Action changed, Func<Func<Task<string?>>?>? picker)
    {
        this.changed = changed;
        this.picker = picker;
        foreach (var v in values) Items.Add(new PathItem(v, this));
    }

    public string[] Values() => Items.Select(i => i.Value.Trim()).Where(v => v.Length > 0).ToArray();

    internal void Changed() => changed();

    [RelayCommand]
    private void Add()
    {
        if (string.IsNullOrWhiteSpace(NewValue)) return;
        Items.Add(new PathItem(NewValue.Trim(), this));
        NewValue = "";
        changed();
    }

    [RelayCommand]
    private async Task Browse()
    {
        var pick = picker?.Invoke();
        if (pick == null) return;
        var path = await pick();
        if (string.IsNullOrEmpty(path)) return;
        NewValue = path;
        Add();
    }

    internal void Remove(PathItem item) { Items.Remove(item); changed(); }
}

public partial class PathItem : ObservableObject
{
    private readonly PathList owner;
    [ObservableProperty] private string value;
    public PathItem(string value, PathList owner) { this.value = value; this.owner = owner; }
    partial void OnValueChanged(string value) => owner.Changed();
    [RelayCommand] private void Remove() => owner.Remove(this);
}

public partial class MappingItem : ObservableObject
{
    private readonly Action changed;
    private CancellationTokenSource? checkRun;
    [ObservableProperty] private string windows;
    [ObservableProperty] private string linux;

    // Result of the background check against the database, shown under the row
    [ObservableProperty] private string checkText = "";
    [ObservableProperty] private string checkLevel = "";
    public bool HasCheck => CheckText.Length > 0;
    public bool IsOk => CheckLevel == "ok";
    public bool IsWarning => CheckLevel == "warning";
    public bool IsError => CheckLevel == "error";

    public MappingItem(string windows, string linux, Action changed)
    {
        this.windows = windows;
        this.linux = linux;
        this.changed = changed;
        StartCheck(0);
    }

    partial void OnWindowsChanged(string value) { changed(); StartCheck(500); }
    partial void OnLinuxChanged(string value) { changed(); StartCheck(500); }
    partial void OnCheckTextChanged(string value) => OnPropertyChanged(nameof(HasCheck));
    partial void OnCheckLevelChanged(string value)
    {
        OnPropertyChanged(nameof(IsOk));
        OnPropertyChanged(nameof(IsWarning));
        OnPropertyChanged(nameof(IsError));
    }

    public void Cancel() => checkRun?.Cancel();

    // Checks a sample of the videos in the database against the pair, a moment after the last change
    private void StartCheck(int delayMs)
    {
        checkRun?.Cancel();
        if (string.IsNullOrWhiteSpace(Windows) || string.IsNullOrWhiteSpace(Linux)) { CheckText = ""; CheckLevel = ""; return; }
        var cts = checkRun = new CancellationTokenSource();
        var token = cts.Token;
        var pair = new PathMapping { Windows = Windows, Linux = Linux };
        void Show(MappingCheck c) => Dispatcher.UIThread.Post(() =>
        {
            if (token.IsCancellationRequested) return;
            CheckText = c.Message;
            CheckLevel = c.Level.ToString().ToLowerInvariant();
        });
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, token);
                var result = await MappingChecker.CheckAsync(pair, new SyncProgress<MappingCheck>(Show), token);
                Show(result);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Show(new MappingCheck(MappingCheckLevel.Warning, "Could not check this path: " + e.Message)); }
        });
    }
}

/// <summary>Another name for the same place (e.g. "L:\\" for "\\\\nas\\video\\"), with its check against the database.</summary>
public partial class SynonymItem : ObservableObject
{
    private readonly Action changed;
    private readonly Action<SynonymItem> askUpdate;
    private CancellationTokenSource? checkRun;
    [ObservableProperty] private string main;
    [ObservableProperty] private string alias;
    [ObservableProperty] private string checkText = "";
    [ObservableProperty] private string checkLevel = "";
    [ObservableProperty] private int count;
    public bool HasCheck => CheckText.Length > 0;
    public bool IsOk => CheckLevel == "ok";
    public bool IsWarning => CheckLevel == "warning";
    public bool IsError => CheckLevel == "error";
    /// <summary>Videos are stored under the alias and the check found them under the main name.</summary>
    public bool CanUpdate => Count > 0 && (IsOk || IsWarning);

    public SynonymItem(string main, string alias, Action changed, Action<SynonymItem> askUpdate)
    {
        this.main = main;
        this.alias = alias;
        this.changed = changed;
        this.askUpdate = askUpdate;
        StartCheck(0);
    }

    partial void OnMainChanged(string value) { changed(); StartCheck(500); }
    partial void OnAliasChanged(string value) { changed(); StartCheck(500); }
    partial void OnCheckTextChanged(string value) => OnPropertyChanged(nameof(HasCheck));
    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(CanUpdate));
    partial void OnCheckLevelChanged(string value)
    {
        OnPropertyChanged(nameof(IsOk));
        OnPropertyChanged(nameof(IsWarning));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(CanUpdate));
    }

    public void Cancel() => checkRun?.Cancel();

    [RelayCommand] private void Update() => askUpdate(this);

    public void ShowResult(SynonymUpdateReport report)
    {
        checkRun?.Cancel();
        Count = 0;
        CheckText = report.Message;
        CheckLevel = report.Updated + report.Merged > 0 ? "ok" : "warning";
    }

    // Counts the videos stored under the alias and checks a sample of them under the main name
    private void StartCheck(int delayMs)
    {
        checkRun?.Cancel();
        if (string.IsNullOrWhiteSpace(Main) || string.IsNullOrWhiteSpace(Alias)) { CheckText = ""; CheckLevel = ""; Count = 0; return; }
        var cts = checkRun = new CancellationTokenSource();
        var token = cts.Token;
        var synonym = new PathSynonym { Main = Main, Alias = Alias };
        void Show(MappingCheck c) => Dispatcher.UIThread.Post(() =>
        {
            if (token.IsCancellationRequested) return;
            CheckText = c.Message;
            CheckLevel = c.Level.ToString().ToLowerInvariant();
            Count = c.Level == MappingCheckLevel.Checking ? 0 : c.InDatabase;
        });
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, token);
                var result = await SynonymTools.CheckAsync(synonym, new SyncProgress<MappingCheck>(Show), token);
                Show(result);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Show(new MappingCheck(MappingCheckLevel.Warning, "Could not check this synonym: " + e.Message)); }
        });
    }
}

public partial class SwitchItem : ObservableObject
{
    private readonly Action changed;
    [ObservableProperty] private string up;
    [ObservableProperty] private string down;
    [ObservableProperty] private decimal percent;
    public SwitchItem(string up, string down, int percent, Action changed) { this.up = up; this.down = down; this.percent = percent; this.changed = changed; }
    partial void OnUpChanged(string value) => changed();
    partial void OnDownChanged(string value) => changed();
    partial void OnPercentChanged(decimal value) => changed();
}

/// <summary>The three match rules of one set of match settings.</summary>
public partial class MatchViewModel : ObservableObject
{
    private readonly Action changed;
    [ObservableProperty] private double colorTolerance;
    [ObservableProperty] private double shapeMatch;
    [ObservableProperty] private double faultTolerance;
    [ObservableProperty] private bool nameMatch;

    public string ColorText => ((int)ColorTolerance).ToString();
    public string ShapeText => $"{(int)ShapeMatch / 4}%";
    public string FaultText => $"{(int)FaultTolerance} of 6";

    public MatchViewModel(matchSettingsSet s, Action changed)
    {
        colorTolerance = s.colorTolerance;
        shapeMatch = s.shapeMatch;
        faultTolerance = s.faultTolerance;
        nameMatch = s.triggerOnNameMatch;
        this.changed = changed;
    }

    public matchSettingsSet ToSettings() => new()
    {
        colorTolerance = (int)Math.Round(ColorTolerance),
        shapeMatch = (int)Math.Round(ShapeMatch),
        faultTolerance = (int)Math.Round(FaultTolerance),
        triggerOnNameMatch = NameMatch
    };

    partial void OnColorToleranceChanged(double value) { OnPropertyChanged(nameof(ColorText)); changed(); }
    partial void OnShapeMatchChanged(double value) { OnPropertyChanged(nameof(ShapeText)); changed(); }
    partial void OnFaultToleranceChanged(double value) { OnPropertyChanged(nameof(FaultText)); changed(); }
    partial void OnNameMatchChanged(bool value) => changed();
}
