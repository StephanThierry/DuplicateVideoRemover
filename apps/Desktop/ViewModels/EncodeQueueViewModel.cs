using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using deepduplicates;

namespace DuplicateVideoRemover.Desktop.ViewModels;

/// <summary>The Encoding screen: the encode queue with the video being encoded, the ones waiting and the finished ones.</summary>
public partial class EncodeQueueViewModel : ObservableObject
{
    private readonly EncodeEngine engine;
    private readonly MainViewModel main;
    private readonly Dictionary<string, EncodeQueueRow> rows = new();
    private int applyQueued;
    private string logKey = "";

    public ObservableCollection<EncodeQueueRow> Waiting { get; } = new();
    public ObservableCollection<EncodeQueueRow> Finished { get; } = new();

    /// <summary>Set by the view: opens a folder in the file manager.</summary>
    public Func<string, Task>? OpenFolder { get; set; }

    [ObservableProperty] private EncodeQueueRow? current;
    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private bool canStart;
    [ObservableProperty] private bool hasWaiting;
    [ObservableProperty] private bool hasFinished;
    [ObservableProperty] private bool isEmpty = true;
    // An empty list shrinks to its header, so the other one gets the room
    [ObservableProperty] private GridLength waitingRowHeight = GridLength.Auto;
    [ObservableProperty] private GridLength finishedRowHeight = GridLength.Auto;
    [ObservableProperty] private string headline = "Nothing in the queue";
    [ObservableProperty] private string left = "";
    [ObservableProperty] private string totals = "";
    [ObservableProperty] private string saved = "";
    [ObservableProperty] private string elapsed = "0:00";
    [ObservableProperty] private double percent;
    [ObservableProperty] private string percentText = "";
    [ObservableProperty] private string error = "";
    [ObservableProperty] private string waitingTitle = "Waiting";
    [ObservableProperty] private string finishedTitle = "Finished";
    [ObservableProperty] private string log = "";
    /// <summary>The number shown on the sidebar item: videos waiting or being encoded (empty when none).</summary>
    [ObservableProperty] private string badge = "";
    [ObservableProperty] private bool hasBadge;

    public bool HasCurrent => Current != null;
    public bool HasError => Error.Length > 0;
    partial void OnCurrentChanged(EncodeQueueRow? value) => OnPropertyChanged(nameof(HasCurrent));
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    public EncodeQueueViewModel(AppController app, MainViewModel main) : this(app.Encoder, main) { }

    public EncodeQueueViewModel(EncodeEngine engine, MainViewModel main)
    {
        this.engine = engine;
        this.main = main;
        // Progress arrives a few times a second from a background thread: show only the latest, once per UI turn
        engine.ProgressChanged += _ =>
        {
            if (Interlocked.Exchange(ref applyQueued, 1) == 1) return;
            Dispatcher.UIThread.Post(() => { Volatile.Write(ref applyQueued, 0); Apply(engine.Current); });
        };
        Apply(engine.Current);
    }

    private void Apply(EncodeProgress p)
    {
        var items = p.Items;
        var present = new HashSet<string>();
        foreach (var i in items)
        {
            present.Add(i.Id);
            if (!rows.TryGetValue(i.Id, out var row)) rows[i.Id] = row = new EncodeQueueRow(i, this);
            row.Update(i);
        }
        foreach (var gone in rows.Keys.Where(k => !present.Contains(k)).ToList()) rows.Remove(gone);

        Sync(Waiting, items.Where(i => i.Status == EncodeItemStatus.Queued).Select(i => rows[i.Id]).ToList());
        Sync(Finished, items.Where(i => i.IsFinished).OrderByDescending(i => i.FinishedUtc).Select(i => rows[i.Id]).ToList());
        var running = p.Current;
        Current = running == null ? null : rows[running.Id];

        IsRunning = p.Running;
        CanStart = !p.Running && p.QueuedCount > 0;
        HasWaiting = p.QueuedCount > 0;
        HasFinished = p.FinishedCount > 0;
        WaitingRowHeight = p.QueuedCount > 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        FinishedRowHeight = p.FinishedCount > 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        IsEmpty = items.Count == 0;
        Headline = EncodeText.Headline(p);
        Left = EncodeText.Left(p);
        Totals = items.Count == 0 ? "" : EncodeText.Totals(p);
        Saved = EncodeText.Saved(p);
        Elapsed = Fmt.Time(p.Elapsed);
        Percent = p.Fraction * 100;
        PercentText = items.Count == 0 ? "" : (int)Math.Floor(p.Fraction * 100) + "%";
        Error = p.Error;
        WaitingTitle = $"Waiting ({p.QueuedCount:N0})";
        FinishedTitle = $"Finished ({p.FinishedCount:N0})";
        int pending = p.PendingCount;
        Badge = pending > 99 ? "99+" : pending.ToString();
        HasBadge = pending > 0;

        string key = p.Log.Count + "|" + (p.Log.Count > 0 ? p.Log[^1] : "");
        if (key != logKey) { logKey = key; Log = string.Join("\n", p.Log); }
    }

    // Brings a list in line with the wanted rows, touching only what differs (no reset, so scrolling stays put)
    private static void Sync(ObservableCollection<EncodeQueueRow> list, List<EncodeQueueRow> wanted)
    {
        if (list.Count == wanted.Count && list.SequenceEqual(wanted)) return;
        var keep = new HashSet<EncodeQueueRow>(wanted);
        for (int i = list.Count - 1; i >= 0; i--) if (!keep.Contains(list[i])) list.RemoveAt(i);
        for (int i = 0; i < wanted.Count; i++)
        {
            if (i < list.Count && ReferenceEquals(list[i], wanted[i])) continue;
            int at = list.IndexOf(wanted[i]);
            if (at >= 0) list.Move(at, i); else list.Insert(i, wanted[i]);
        }
    }

    [RelayCommand] private void Start() => engine.Start();
    [RelayCommand] private void Stop() => engine.Stop();
    [RelayCommand] private void SkipCurrent() => engine.SkipCurrent();
    [RelayCommand] private void ClearWaiting() => engine.ClearQueued();
    [RelayCommand] private void ClearFinished() => engine.ClearFinished();
    [RelayCommand] private void GoToEncode() => main.ShowEncode();

    internal void Remove(EncodeQueueRow row) => engine.Remove(row.Id);
    internal void Retry(EncodeQueueRow row) => engine.Retry(row.Id);
    internal async Task ShowFolder(string folder) { if (OpenFolder != null) await OpenFolder(folder); }
}

/// <summary>One video in the queue.</summary>
public partial class EncodeQueueRow : ObservableObject
{
    private readonly EncodeQueueViewModel owner;
    private Bitmap? shot;
    private bool shotLoaded;

    public string Id { get; }
    public int VideoId { get; }
    public string FileName { get; }
    public string Folder { get; }
    public string Size { get; }
    public string Length { get; }
    public string Summary { get; }

    [ObservableProperty] private string statusText = "";
    [ObservableProperty] private string detail = "";
    [ObservableProperty] private string resultText = "";
    [ObservableProperty] private double percent;
    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private bool isDone;
    [ObservableProperty] private bool isFailed;
    [ObservableProperty] private bool isSkipped;
    [ObservableProperty] private bool canRetry;
    [ObservableProperty] private string whenText = "";

    public bool IsProblem => IsFailed;

    /// <summary>A screenshot from the middle of the video (loaded when first shown).</summary>
    public Bitmap? Shot
    {
        get
        {
            if (!shotLoaded) { shotLoaded = true; shot = Shots.Get(VideoId, 2); }
            return shot;
        }
    }

    public EncodeQueueRow(EncodeItemInfo i, EncodeQueueViewModel owner)
    {
        this.owner = owner;
        Id = i.Id; VideoId = i.VideoId;
        FileName = i.FileName; Folder = i.Folder;
        Size = Fmt.Size(i.SizeBytes); Length = Fmt.Duration(i.DurationSeconds); Summary = i.Summary;
    }

    public void Update(EncodeItemInfo i)
    {
        StatusText = EncodeText.Status(i);
        Detail = i.Status == EncodeItemStatus.Running ? EncodeText.CurrentDetail(i) : "";
        Percent = i.Fraction * 100;
        IsRunning = i.Status == EncodeItemStatus.Running;
        IsDone = i.Status == EncodeItemStatus.Done;
        IsFailed = i.Status == EncodeItemStatus.Failed;
        IsSkipped = i.Status == EncodeItemStatus.Skipped;
        CanRetry = IsFailed || IsSkipped;
        ResultText = IsDone ? $"{Size} became {Fmt.Size(i.OutputBytes)}" : "";
        WhenText = i.FinishedUtc is DateTime t ? Fmt.When(t) : "";
        OnPropertyChanged(nameof(IsProblem));
    }

    [RelayCommand] private void Remove() => owner.Remove(this);
    [RelayCommand] private void Retry() => owner.Retry(this);
    [RelayCommand] private Task ShowFolder() => owner.ShowFolder(Folder);
}
