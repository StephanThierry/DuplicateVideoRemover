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
        var e = app.Settings.EncodeOrDefault();
        SettingsSummary = $"{EncodeOptions.Codec(e.codec).Label}, {e.resolution}p, quality {e.crf}";
        QualitySummary = $"{EncodeOptions.QualityLabel(e.codec, e.crf ?? 0)}. Speed: {e.preset}, {(e.audio == "copy" ? "original sound" : "AAC sound")}, {e.container.ToUpperInvariant()}";
        Loading = true;
        var picked = all.Where(r => r.IsPicked).Select(r => r.Candidate.Id).ToHashSet();
        var candidates = await Task.Run(app.EncodeCandidates);
        all = candidates.Select(c => new EncodeRow(c, picked.Contains(c.Id), UpdatePicked)).ToList();
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
        PickedSize = Fmt.Size(picked.Sum(r => r.Candidate.SizeBytes)) + " now";
    }

    [RelayCommand] private void SelectTop() { foreach (var r in Visible().Take(20)) r.IsPicked = true; }
    [RelayCommand] private void SelectAllShown() { foreach (var r in Visible()) r.IsPicked = true; }
    [RelayCommand] private void SelectNone() { foreach (var r in all) r.IsPicked = false; }
    [RelayCommand] private void EditSettings() => main.ShowSettings();
    [RelayCommand] private void GoToScan() => main.ShowScan();

    [RelayCommand]
    private void Create()
    {
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
    private Bitmap? shot;
    private bool shotLoaded;

    public EncodeCandidate Candidate { get; }
    public string FileName => Candidate.FileName;
    public string Folder => Candidate.Folder;
    public string Bitrate => $"{Candidate.AverageKbps:N0} kbit/s";
    public string Size => Fmt.Size(Candidate.SizeBytes);
    public string Length => Fmt.Duration(Candidate.DurationSeconds);
    public Bitmap? Shot { get { if (!shotLoaded) { shotLoaded = true; shot = Shots.Get(Candidate.Id, 2); } return shot; } }

    [ObservableProperty] private bool isPicked;

    public EncodeRow(EncodeCandidate candidate, bool picked, Action changed)
    {
        Candidate = candidate;
        isPicked = picked;
        this.changed = changed;
    }

    partial void OnIsPickedChanged(bool value) => changed();
}
