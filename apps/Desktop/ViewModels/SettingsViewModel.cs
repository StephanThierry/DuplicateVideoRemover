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
    public ObservableCollection<SwitchItem> Switches { get; } = new();
    public MatchViewModel Match { get; }
    public MatchViewModel SameFolder { get; }

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
        foreach (var w in s.switchPriority) Switches.Add(new SwitchItem(w.up, w.down, w.triggerBelowPctSizeDiff, Edited));
        Match = new MatchViewModel(s.matchSettings, Edited);
        UseSameFolder = s.matchSettings_sameFolder != null;
        SameFolder = new MatchViewModel(s.matchSettings_sameFolder ?? s.matchSettings.Clone(), Edited);
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
        return s;
    }

    private PathMapping[] CurrentMappings() =>
        Mappings.Select(m => new PathMapping { Windows = m.Windows, Linux = m.Linux }).ToArray();

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
        app.SaveSettings(Current(), CurrentMappings());
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
    [RelayCommand] private void RemoveMapping(MappingItem item) { Mappings.Remove(item); Edited(); }
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
    [ObservableProperty] private string windows;
    [ObservableProperty] private string linux;
    public MappingItem(string windows, string linux, Action changed) { this.windows = windows; this.linux = linux; this.changed = changed; }
    partial void OnWindowsChanged(string value) => changed();
    partial void OnLinuxChanged(string value) => changed();
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
