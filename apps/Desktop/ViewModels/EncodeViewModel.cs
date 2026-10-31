using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using deepduplicates;

namespace DuplicateVideoRemover.Desktop.ViewModels;

/// <summary>The Re-encode screen: videos by bitrate, pick some, create the encoding script.</summary>
public partial class EncodeViewModel : ObservableObject
{
    private readonly AppController app;
    private readonly MainViewModel main;
    private List<EncodeRow> all = new();

    public ObservableCollection<EncodeRow> Rows { get; } = new();

    /// <summary>Set by the view: opens a folder in the file manager.</summary>
    public Func<string, Task>? OpenFolder { get; set; }

    [ObservableProperty] private string settingsSummary = "";
    [ObservableProperty] private string qualitySummary = "";
    [ObservableProperty] private string search = "";
    [ObservableProperty] private decimal minKbps;
    [ObservableProperty] private bool loading;
    [ObservableProperty] private bool hasVideos;
    [ObservableProperty] private string pickedText = "";
    [ObservableProperty] private string pickedSize = "";
    [ObservableProperty] private bool confirmVisible;
    [ObservableProperty] private string confirmTitle = "";
    [ObservableProperty] private string confirmText = "";
    private EncodeSettings settings = EncodeSettings.CreateDefault();
    private EncodeRow? anchor;          // last clicked row, for Shift-click ranges
    private bool anchorState = true;
    [ObservableProperty] private bool hasPicked;
    [ObservableProperty] private string? scriptPath;
    [ObservableProperty] private string resultText = "";
    [ObservableProperty] private string runText = "";

    public bool HasResult => ScriptPath != null;

    public EncodeViewModel(AppController app, MainViewModel main)
    {
        this.app = app;
        this.main = main;
        app.DataChanged += () => Dispatcher.UIThread.Post(() => _ = Reload());
    }

    partial void OnScriptPathChanged(string? value) => OnPropertyChanged(nameof(HasResult));
    partial void OnSearchChanged(string value) => Filter();
    partial void OnMinKbpsChanged(decimal value) => Filter();

    /// <summary>Loads the list again (when the screen is opened, after a scan or new settings).</summary>
    public async Task Reload()
    {
        var e = settings = app.Settings.EncodeOrDefault();
        double target = EncodeEstimator.TargetKbps(e);
        SettingsSummary = $"{EncodeOptions.Codec(e.codec).Label}, {e.resolution}p, quality {e.crf}";
        QualitySummary = $"{EncodeOptions.QualityLabel(e.codec, e.crf ?? 0)}. Speed: {e.preset}, {(e.audio == "copy" ? "original sound" : "AAC sound")}, {e.container.ToUpperInvariant()}";
        Loading = true;
        var picked = all.Where(r => r.IsPicked).Select(r => r.Candidate.Id).ToHashSet();
        var candidates = await Task.Run(app.EncodeCandidates);
        all = candidates.Select(c => new EncodeRow(c, picked.Contains(c.Id), EncodeEstimator.EstimatedBytes(c, e, target), UpdatePicked)).ToList();
        anchor = null;
        HasVideos = all.Count > 0;
        Loading = false;
        Filter();
    }

    private IEnumerable<EncodeRow> Visible() => all.Where(r =>
        r.Candidate.AverageKbps >= (double)MinKbps &&
        (string.IsNullOrWhiteSpace(Search) || r.Candidate.Path.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase)));

    private void Filter()
    {
        Rows.Clear();
        foreach (var r in Visible()) Rows.Add(r);
        UpdatePicked();
    }

    private void UpdatePicked()
    {
        var picked = all.Where(r => r.IsPicked).ToList();
        HasPicked = picked.Count > 0;
        PickedText = Fmt.Plural(picked.Count, "video", "videos") + " picked";
        long now = picked.Sum(r => r.Candidate.SizeBytes), after = picked.Sum(r => r.EstimatedBytes);
        PickedSize = $"{Fmt.Size(now)} now, about {Fmt.Size(after)} after. Saves about {Fmt.Size(now - after)}{EncodeRow.Percent(now, after)}";
    }

    /// <summary>
    /// Click: pick or unpick one video. Shift-click: give every video between the last click and this one the
    /// same state as that last click.
    /// </summary>
    public void RowClicked(EncodeRow row, bool shift)
    {
        if (shift && anchor != null)
        {
            int a = Rows.IndexOf(anchor), b = Rows.IndexOf(row);
            if (a >= 0 && b >= 0)
            {
                for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) Rows[i].IsPicked = anchorState;
                return;
            }
        }
        row.IsPicked = !row.IsPicked;
        anchorState = row.IsPicked;
        anchor = row;
    }

    [RelayCommand] private void SelectAll() { foreach (var r in Visible()) r.IsPicked = true; }
    [RelayCommand] private void SelectTop() { foreach (var r in Visible().Take(20)) r.IsPicked = true; }
    [RelayCommand] private void SelectAllShown() { foreach (var r in Visible()) r.IsPicked = true; }
    [RelayCommand] private void SelectNone() { foreach (var r in all) r.IsPicked = false; }
    [RelayCommand] private void EditSettings() => main.ShowSettings();
    [RelayCommand] private void GoToScan() => main.ShowScan();

    [RelayCommand]
    private void AskCreate()
    {
        var picked = all.Where(r => r.IsPicked).ToList();
        if (picked.Count == 0) return;
        long now = picked.Sum(r => r.Candidate.SizeBytes), after = picked.Sum(r => r.EstimatedBytes);
        ConfirmTitle = $"Create an encoding script for {Fmt.Plural(picked.Count, "video", "videos")}?";
        ConfirmText = $"{EncodeOptions.Codec(settings.codec).Label}, {settings.resolution}p, quality {settings.crf}. Now {Fmt.Size(now)}, about {Fmt.Size(after)} after, saving about {Fmt.Size(now - after)}. "
                    + "The script makes new files next to the originals; nothing changes until you run it.";
        ConfirmVisible = true;
    }

    [RelayCommand] private void CancelCreate() => ConfirmVisible = false;

    [RelayCommand]
    private void Create()
    {
        ConfirmVisible = false;
        // In list order: highest bitrate first
        var videos = all.Where(r => r.IsPicked).Select(r => r.Candidate).ToList();
        if (videos.Count == 0) return;
        ScriptPath = app.WriteEncodeScript(videos);
        ResultText = $"Created a script for {Fmt.Plural(videos.Count, "video", "videos")}: {ScriptPath}";
        RunText = (ScriptFile.IsWindows ? $"Run it with: powershell -ExecutionPolicy Bypass -File \"{ScriptPath}\"" : $"Run it with: sh \"{ScriptPath}\"")
                  + ". The new files are saved next to the originals, which are not changed.";
    }

    [RelayCommand]
    private async Task ShowScriptFolder()
    {
        if (ScriptPath != null && OpenFolder != null) await OpenFolder(System.IO.Path.GetDirectoryName(ScriptPath)!);
    }
}

public partial class EncodeRow : ObservableObject
{
    private readonly Action changed;
    private bool shotLoaded;

    public EncodeCandidate Candidate { get; }
    public string FileName => Candidate.FileName;
    public string Folder => Candidate.Folder;
    public string Bitrate => $"{Candidate.AverageKbps:N0} kbit/s";
    public string Size => Fmt.Size(Candidate.SizeBytes);
    public string Length => Fmt.Duration(Candidate.DurationSeconds);
    public long EstimatedBytes { get; }
    public string Estimate => Fmt.Size(EstimatedBytes);
    public string Saving => Candidate.SizeBytes - EstimatedBytes < Candidate.SizeBytes / 20
        ? "little or no saving"
        : $"saves {Fmt.Size(Candidate.SizeBytes - EstimatedBytes)}{Percent(Candidate.SizeBytes, EstimatedBytes)}";
    public bool GoodSaving => Candidate.SizeBytes - EstimatedBytes > Candidate.SizeBytes / 10;

    // Screenshots load when the row is first shown (the list is virtualized)
    public Bitmap? Shot1 { get { LoadShots(); return shots[0]; } }
    public Bitmap? Shot2 { get { LoadShots(); return shots[1]; } }
    public Bitmap? Shot3 { get { LoadShots(); return shots[2]; } }
    private readonly Bitmap?[] shots = new Bitmap?[3];
    private void LoadShots()
    {
        if (shotLoaded) return;
        shotLoaded = true;
        for (int i = 0; i < 3; i++) shots[i] = Shots.Get(Candidate.Id, i + 1);
    }

    public static string Percent(long now, long after) => now > 0 ? $" ({(now - after) * 100 / now}%)" : "";

    [ObservableProperty] private bool isPicked;

    public EncodeRow(EncodeCandidate candidate, bool picked, long estimatedBytes, Action changed)
    {
        Candidate = candidate;
        isPicked = picked;
        EstimatedBytes = estimatedBytes;
        this.changed = changed;
    }

    partial void OnIsPickedChanged(bool value) => changed();
}
