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
    [ObservableProperty] private string elapsed = "0:00";
    [ObservableProperty] private string perVideo = "-";
    [ObservableProperty] private string currentFile = "";
    [ObservableProperty] private bool isDone;
    [ObservableProperty] private bool isProblem;
    [ObservableProperty] private string scopeText = "";
    [ObservableProperty] private string filterRulesText = "";
    /// <summary>The folders to scan, shown as tags.</summary>
    public ObservableCollection<string> ScopeFolders { get; } = new();
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
        ScopeFolders.Clear();
        foreach (var f in s.contentFolders) ScopeFolders.Add(f);
        ScopeText = s.contentFolders.Length == 0 ? "No folders yet. Add the folders with your videos in Settings." : "";
        int rules = s.excludePaths.Length;
        FilterRulesText = rules == 0 ? "" : $"There {(rules == 1 ? "is" : "are")} {Fmt.Plural(rules, "active path filtering rule", "active path filtering rules")}.";
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
        FindPhase.Update(ScanText.FindState(p), p.FindFraction, ScanText.FindUnknownTotal(p), ScanText.FindDetail(p), ScanText.FindLeft(p));
        ProcessPhase.Update(ScanText.ProcessState(p), p.ProcessFraction, false, ScanText.ProcessDetail(p), ScanText.ProcessLeft(p));
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
        IsDone = p.Phase == ScanPhase.Done;
        IsProblem = p.Phase is ScanPhase.Cancelled or ScanPhase.Failed;
        if (p.Phase == ScanPhase.Failed) Message = "The scan stopped with an error: " + p.Message;


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
    [RelayCommand] private void Encode() => main.ShowEncode();
    [RelayCommand] private void EditSettings() => main.ShowSettings();
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
