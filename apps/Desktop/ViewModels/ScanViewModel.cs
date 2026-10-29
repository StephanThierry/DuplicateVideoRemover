using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using deepduplicates;

namespace DuplicateVideoRemover.Desktop.ViewModels;

public partial class ScanViewModel : ObservableObject
{
    private readonly AppController app;
    private readonly MainViewModel main;
    private int? lastShot;

    public ObservableCollection<Bitmap> Recent { get; } = new();
    public ObservableCollection<StepItem> Steps { get; } = new()
    {
        new StepItem(ScanPhase.FindingFiles, "1  Find videos"),
        new StepItem(ScanPhase.ReadingVideos, "2  Read new videos"),
        new StepItem(ScanPhase.Comparing, "3  Compare"),
        new StepItem(ScanPhase.Done, "4  Done")
    };

    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private bool stopping;
    [ObservableProperty] private bool isIdle = true;
    [ObservableProperty] private double percent;
    [ObservableProperty] private string percentText = "0%";
    [ObservableProperty] private string message = "";
    [ObservableProperty] private string remaining = "";
    [ObservableProperty] private string found = "0";
    [ObservableProperty] private string alreadyRead = "-";
    [ObservableProperty] private string newRead = "-";
    [ObservableProperty] private string failed = "-";
    [ObservableProperty] private string elapsed = "0:00";
    [ObservableProperty] private string perVideo = "-";
    [ObservableProperty] private string currentFile = "";
    [ObservableProperty] private bool isDone;
    [ObservableProperty] private bool isProblem;
    [ObservableProperty] private string scopeText = "";
    [ObservableProperty] private string lastRunText = "";
    [ObservableProperty] private string lastRunDetail = "";

    public ScanViewModel(AppController app, MainViewModel main)
    {
        this.app = app;
        this.main = main;
        app.Scanner.ProgressChanged += p => Dispatcher.UIThread.Post(() => Apply(p));
        RefreshInfo();
        Apply(app.Scanner.Current);
    }

    /// <summary>Scope and last run, shown beside the progress.</summary>
    public void RefreshInfo()
    {
        var s = app.Settings;
        ScopeText = s.contentFolders.Length == 0
            ? "No folders yet. Add the folders with your videos in Settings."
            : string.Join(Environment.NewLine, s.contentFolders) +
              (s.excludePaths.Length > 0 ? Environment.NewLine + "Skipping paths that contain: " + string.Join(", ", s.excludePaths) : "");
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
        IsRunning = app.Scanner.IsRunning;
        IsIdle = p.Phase == ScanPhase.Idle;
        Percent = p.Fraction * 100;
        PercentText = $"{(int)Percent}%";
        Message = p.Message;
        Found = p.FilesFound.ToString("N0");
        if (p.Phase >= ScanPhase.ReadingVideos)
        {
            AlreadyRead = Math.Max(p.FilesFound - p.NewTotal, 0).ToString("N0");
            NewRead = $"{p.NewDone:N0} of {p.NewTotal:N0}";
            Failed = p.Failed.ToString("N0");
        }
        Elapsed = Fmt.Time(p.Elapsed);
        PerVideo = p.SecondsPerNewFile is double s ? $"{s:0.0} s" : "-";
        CurrentFile = p.Phase == ScanPhase.ReadingVideos && p.CurrentFile.Length > 0 ? "Reading " + p.CurrentFile : "";
        Remaining = p.Phase switch
        {
            ScanPhase.Preparing or ScanPhase.FindingFiles => "Counting videos...",
            ScanPhase.ReadingVideos when p.NewTotal == 0 => "No new videos to read",
            ScanPhase.ReadingVideos when p.Remaining is TimeSpan r => Fmt.Approx(r) + " left" + (p.EstimateFromHistory ? " (from earlier scans)" : ""),
            ScanPhase.ReadingVideos => "Estimating time left...",
            _ => ""
        };
        IsDone = p.Phase == ScanPhase.Done;
        IsProblem = p.Phase is ScanPhase.Cancelled or ScanPhase.Failed;
        if (p.Phase == ScanPhase.Failed) Message = "The scan stopped with an error: " + p.Message;

        var current = p.Phase == ScanPhase.Preparing ? ScanPhase.FindingFiles : p.Phase;
        foreach (var step in Steps)
        {
            bool active = current is not (ScanPhase.Idle or ScanPhase.Cancelled or ScanPhase.Failed);
            step.State = !active ? "" : current == ScanPhase.Done || step.Phase < current ? "done" : step.Phase == current ? "current" : "";
        }

        if (p.LastScreenshotId is int id && id != lastShot)
        {
            lastShot = id;
            var shot = Shots.Get(id, 2);
            if (shot != null)
            {
                Recent.Insert(0, shot);
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
    [RelayCommand] private void EditSettings() => main.ShowSettings();
}

public partial class StepItem : ObservableObject
{
    public ScanPhase Phase { get; }
    public string Label { get; }
    [ObservableProperty] private string state = "";
    public bool IsDone => State == "done";
    public bool IsCurrent => State == "current";

    public StepItem(ScanPhase phase, string label) { Phase = phase; Label = label; }

    partial void OnStateChanged(string value)
    {
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsCurrent));
    }
}
