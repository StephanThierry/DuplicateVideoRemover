using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using deepduplicates;

namespace DuplicateVideoRemover.Desktop.ViewModels;

/// <summary>A picture of a video read during the scan; clicking it plays the video.</summary>
public sealed record ScanThumb(Bitmap Image, string VideoPath, IRelayCommand Play, bool IsNew = false)
{
    /// <summary>The file name, shown when the pointer rests on the picture.</summary>
    public string Name => System.IO.Path.GetFileName(VideoPath);

    /// <summary>A thick white border marks a video read for the first time in this scan.</summary>
    public Avalonia.Media.IBrush Frame => IsNew ? Avalonia.Media.Brushes.White : Avalonia.Media.Brushes.Transparent;
}

public partial class ScanViewModel : ObservableObject
{
    private readonly AppController app;
    private readonly MainViewModel main;
    private int? lastShot;

    public ObservableCollection<ScanThumb> Recent { get; } = new();
    public bool HasRecent => Recent.Count > 0;
    [ObservableProperty] private string playError = "";
    public bool HasPlayError => PlayError.Length > 0;
    partial void OnPlayErrorChanged(string value) => OnPropertyChanged(nameof(HasPlayError));

    // Opens a video in the program the system uses for it
    private void Play(string path)
    {
        try
        {
            PlayError = "";
            if (!System.IO.File.Exists(path)) { PlayError = "The file is not there any more: " + path; return; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e) { PlayError = "Could not open the video: " + e.Message; }
    }

    // The two phases of a scan, each with its own bar: A) finding the files, B) processing them
    public PhaseItem FindPhase { get; } = new("A", "Finding files");
    public PhaseItem ProcessPhase { get; } = new("B", "Processing");

    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private bool stopping;
    [ObservableProperty] private bool isIdle = true;
    [ObservableProperty] private string message = "";
    [ObservableProperty] private string found = "0";
    [ObservableProperty] private string alreadyRead = "-";
    [ObservableProperty] private string newRead = "-";
    [ObservableProperty] private string failed = "-";
    // The files that could not be read in this scan (the "i" next to the number)
    public System.Collections.ObjectModel.ObservableCollection<FailedRead> FailedFiles { get; } = new();
    [ObservableProperty] private bool hasFailedFiles;
    [ObservableProperty] private bool failedVisible;
    [ObservableProperty] private string failedTitle = "";
    [RelayCommand] private void ShowFailed() => FailedVisible = true;
    [RelayCommand] private void CloseFailed() => FailedVisible = false;
    [ObservableProperty] private string elapsed = "0:00";
    [ObservableProperty] private string perVideo = "-";
    [ObservableProperty] private string currentFile = "";
    [ObservableProperty] private bool isDone;
    [ObservableProperty] private bool isProblem;
    [ObservableProperty] private string scopeText = "";
    [ObservableProperty] private string filterRulesText = "";
    // The saved file list of the last scan: All files opens it without scanning
    [ObservableProperty] private bool hasSavedList;
    [ObservableProperty] private string savedListText = "";
    /// <summary>The folders to scan, shown as tags.</summary>
    public ObservableCollection<string> ScopeFolders { get; } = new();
    [ObservableProperty] private string lastRunText = "";
    [ObservableProperty] private string lastRunDetail = "";

    public ScanViewModel(AppController app, MainViewModel main)
    {
        this.app = app;
        this.main = main;
        Recent.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecent));
        app.Scanner.ProgressChanged += p => Dispatcher.UIThread.Post(() => Apply(p));
        app.ExcludeAdded += _ => Dispatcher.UIThread.Post(RefreshInfo);
        RefreshInfo();
        Apply(app.Scanner.Current);
    }

    /// <summary>Scope and last run, shown beside the progress.</summary>
    public void RefreshInfo()
    {
        var s = app.Settings;
        ScopeFolders.Clear();
        foreach (var f in s.contentFolders) ScopeFolders.Add(f);
        ScopeText = s.contentFolders.Length == 0 ? "No folders yet. Add the folders with your videos in Settings." : "";
        int rules = s.excludePaths.Length;
        FilterRulesText = rules == 0 ? "" : $"There {(rules == 1 ? "is" : "are")} {Fmt.Plural(rules, "active path filtering rule", "active path filtering rules")}.";
        HasSavedList = LastScan.FoundUtc != null;
        SavedListText = LastScan.FoundUtc is DateTime listed ? "Show all files from " + Fmt.When(listed) : "";
        var last = app.LastRun();
        if (last == null)
        {
            LastRunText = "These folders have not been scanned yet.";
            LastRunDetail = "The first scan reads every video and takes the longest.";
        }
        else
        {
            LastRunText = $"{Fmt.Ago(last.StartedUtc)}, {(last.Completed ? "finished" : "stopped")} after {Fmt.Approx(TimeSpan.FromSeconds(last.TotalSeconds))}.";
            LastRunDetail = $"{last.FilesInScope:N0} videos, {last.NewFiles:N0} of them read for the first time.";
        }
    }

    private void Apply(ScanProgress p)
    {
        // The final report arrives just before the engine clears IsRunning: a finished scan is not running
        IsRunning = app.Scanner.IsRunning && p.Phase is not (ScanPhase.Done or ScanPhase.Cancelled or ScanPhase.Failed);
        IsIdle = p.Phase == ScanPhase.Idle;
        FindPhase.Update(ScanText.FindState(p), p.FindFraction, ScanText.FindUnknownTotal(p), ScanText.FindDetail(p), ScanText.FindLeft(p));
        ProcessPhase.Update(ScanText.ProcessState(p), p.ProcessFraction, false, ScanText.ProcessDetail(p), ScanText.ProcessLeft(p));
        Message = p.Message;
        Found = p.FilesFound.ToString("N0");
        if (p.Phase >= ScanPhase.ReadingVideos)
        {
            AlreadyRead = Math.Max(p.FilesFound - p.NewTotal, 0).ToString("N0");
            NewRead = $"{p.NewDone:N0} of {p.NewTotal:N0}";
            Failed = p.Failed.ToString("N0");
            if (p.FailedFiles.Count != FailedFiles.Count)
            {
                // Only ever grows during a scan: add what is new
                if (p.FailedFiles.Count < FailedFiles.Count) FailedFiles.Clear();
                for (int i = FailedFiles.Count; i < p.FailedFiles.Count; i++) FailedFiles.Add(p.FailedFiles[i]);
            }
            HasFailedFiles = FailedFiles.Count > 0;
            FailedTitle = Fmt.Plural(FailedFiles.Count, "video could not be read", "videos could not be read");
        }
        Elapsed = Fmt.Time(p.Elapsed);
        PerVideo = p.SecondsPerNewFile is double s ? $"{s:0.0} s" : "-";
        CurrentFile = p.Phase == ScanPhase.ReadingVideos && p.CurrentFile.Length > 0 ? "Reading " + p.CurrentFile : "";
        IsDone = p.Phase == ScanPhase.Done;
        IsProblem = p.Phase is ScanPhase.Cancelled or ScanPhase.Failed;
        if (p.Phase == ScanPhase.Failed) Message = "The scan stopped with an error: " + p.Message;


        if (p.LastScreenshotId is int same && same == lastShot && p.LastVideo is { IsNew: true } && Recent.Count > 0 && !Recent[0].IsNew && Recent[0].VideoPath == p.LastVideo.Path)
            Recent[0] = Recent[0] with { IsNew = true };   // shown as a known file, and turned out to be a new one
        if (p.LastScreenshotId is int id && id != lastShot)
        {
            lastShot = id;
            var shot = Shots.Get(id, 2);
            string? video = p.LastVideo?.Path;
            if (shot != null && !string.IsNullOrEmpty(video))
            {
                Recent.Insert(0, new ScanThumb(shot, video, new RelayCommand(() => Play(video)), p.LastVideo!.IsNew));
                while (Recent.Count > 8) Recent.RemoveAt(Recent.Count - 1);
            }
        }
        if (p.Phase is ScanPhase.Done or ScanPhase.Cancelled or ScanPhase.Failed)
        {
            Stopping = false;
            RefreshInfo();
        }
    }

    [RelayCommand]
    private void Start()
    {
        Recent.Clear();
        lastShot = null;
        RefreshInfo();
        if (app.StartScan()) IsRunning = true;
    }

    [RelayCommand]
    private void Stop()
    {
        Stopping = true;
        app.Scanner.Cancel();
    }

    [RelayCommand] private void Review() => main.ShowReview();
    [RelayCommand] private void Encode() => main.ShowEncode();
    [RelayCommand] private void EditSettings() => main.ShowSettings();
    [RelayCommand] private void ShowFiles() => main.ShowFiles();
}

/// <summary>One phase of a scan: its own bar, percentage and what it is doing.</summary>
public partial class PhaseItem : ObservableObject
{
    public string Letter { get; }
    public string Name { get; }
    [ObservableProperty] private double percent;
    [ObservableProperty] private string percentText = "";
    [ObservableProperty] private bool unknownTotal;
    [ObservableProperty] private string detail = "";
    [ObservableProperty] private string left = "";
    [ObservableProperty] private bool isWaiting = true;
    [ObservableProperty] private bool isActive;
    [ObservableProperty] private bool isDone;

    public PhaseItem(string letter, string name) { Letter = letter; Name = name; }

    public void Update(PhaseState state, double fraction, bool unknown, string detail, string left)
    {
        IsWaiting = state == PhaseState.Waiting;
        IsActive = state == PhaseState.Active;
        IsDone = state == PhaseState.Done;
        UnknownTotal = unknown;
        Percent = fraction * 100;
        PercentText = IsWaiting || unknown ? "" : ScanText.Percent(fraction) + "%";
        Detail = detail;
        Left = left;
    }
}
