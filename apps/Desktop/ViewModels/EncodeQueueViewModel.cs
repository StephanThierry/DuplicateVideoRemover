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

/// <summary>The Re-encode queue screen: the encode queue with the video being encoded, the ones waiting and the finished ones.</summary>
public partial class EncodeQueueViewModel : ObservableObject
{
    private readonly AppController app;
    private readonly EncodeEngine engine;
    private readonly MainViewModel main;
    private Action? confirmed;                       // what "yes" does in the open confirmation
    private CancellationTokenSource? toastTimer;
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
    // An empty or collapsed list shrinks to its header, so the other one gets the room
    [ObservableProperty] private GridLength waitingRowHeight = GridLength.Auto;
    [ObservableProperty] private GridLength finishedRowHeight = GridLength.Auto;

    // Compact view: no pictures anywhere, the lists are tables. The sections can be collapsed to their header.
    [ObservableProperty] private bool compact;
    [ObservableProperty] private bool waitingExpanded = true;
    [ObservableProperty] private bool finishedExpanded = true;
    public bool ShowCurrentFull => HasCurrent && !Compact;
    public bool ShowCurrentCompact => HasCurrent && Compact;
    public bool ShowWaitingFull => HasWaiting && WaitingExpanded && !Compact;
    public bool ShowWaitingCompact => HasWaiting && WaitingExpanded && Compact;
    public bool ShowWaitingEmpty => !HasWaiting && WaitingExpanded;
    public bool ShowFinishedFull => HasFinished && FinishedExpanded && !Compact;
    public bool ShowFinishedCompact => HasFinished && FinishedExpanded && Compact;
    public bool ShowFinishedEmpty => !HasFinished && FinishedExpanded;

    partial void OnCompactChanged(bool value) => ViewChanged();
    partial void OnWaitingExpandedChanged(bool value) => ViewChanged();
    partial void OnFinishedExpandedChanged(bool value) => ViewChanged();
    partial void OnHasWaitingChanged(bool value) => ViewChanged();
    partial void OnHasFinishedChanged(bool value) => ViewChanged();

    // Which parts are shown follows these few switches
    private void ViewChanged()
    {
        foreach (var name in new[] { nameof(ShowCurrentFull), nameof(ShowCurrentCompact), nameof(ShowWaitingFull), nameof(ShowWaitingCompact),
                                     nameof(ShowWaitingEmpty), nameof(ShowFinishedFull), nameof(ShowFinishedCompact), nameof(ShowFinishedEmpty) })
            OnPropertyChanged(name);
        WaitingRowHeight = HasWaiting && WaitingExpanded ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        FinishedRowHeight = HasFinished && FinishedExpanded ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
    }

    [RelayCommand] private void ToggleWaiting() => WaitingExpanded = !WaitingExpanded;
    [RelayCommand] private void ToggleFinished() => FinishedExpanded = !FinishedExpanded;
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
    partial void OnCurrentChanged(EncodeQueueRow? value) { OnPropertyChanged(nameof(HasCurrent)); ViewChanged(); }
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    // A confirmation before deleting the original or replacing an incomplete new file
    [ObservableProperty] private bool confirmVisible;
    [ObservableProperty] private string confirmTitle = "";
    [ObservableProperty] private string confirmText = "";
    [ObservableProperty] private string confirmButton = "";
    [ObservableProperty] private string? toast;

    public EncodeQueueViewModel(AppController app, MainViewModel main)
    {
        this.app = app;
        engine = app.Encoder;
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

    // --- Delete the original (new file checked) / replace an incomplete new file: always asked first ---

    internal void AskDeleteOriginal(EncodeQueueRow row)
    {
        bool recoverable = app.CanRecover(row.OriginalPath);
        ConfirmTitle = recoverable ? "Move the original to the Recycle Bin?" : "Delete the original permanently?";
        ConfirmText = $"{row.FileName}\n{row.Folder}\n\nOriginal: {row.Size}. New file: {row.NewSize}, checked: {row.CheckText}.\n"
                    + (recoverable ? "You can restore the original from the Recycle Bin." : "This location has no Recycle Bin (a network share, for example), so this can't be undone.");
        ConfirmButton = recoverable ? "Move to Recycle Bin" : "Delete permanently";
        Ask(() => Report(app.DeleteOriginalAfterEncode(row.Id)));
    }

    internal void AskDeleteNew(EncodeQueueRow row)
    {
        bool recoverable = app.CanRecover(row.NewPath);
        ConfirmTitle = recoverable ? "Move the new file to the Recycle Bin?" : "Delete the new file permanently?";
        ConfirmText = $"{row.NewFileName} ({row.NewSize})\n{row.Folder}\n\n"
                    + (row.CheckOk ? $"Checked: {row.CheckText}.\n" : $"It is incomplete: {row.CheckText}.\n")
                    + $"The original ({row.Size}) is kept, and the video can be planned for re-encoding again.\n"
                    + (recoverable ? "You can restore the new file from the Recycle Bin." : "This location has no Recycle Bin (a network share, for example), so this can't be undone.");
        ConfirmButton = recoverable ? "Move to Recycle Bin" : "Delete permanently";
        Ask(() => Report(app.DeleteNewFile(row.Id)));
    }

    internal void AskReplace(EncodeQueueRow row)
    {
        bool recoverable = app.CanRecover(row.NewPath);
        ConfirmTitle = "Replace the incomplete new file?";
        ConfirmText = $"{row.NewFileName} ({row.NewSize}) is incomplete: {row.CheckText}.\n\nIt will be "
                    + (recoverable ? "moved to the Recycle Bin" : "deleted permanently (this location has no Recycle Bin)")
                    + " and the video encoded again. The original is not touched.";
        ConfirmButton = "Replace and encode again";
        Ask(() => Report(app.ReplaceIncomplete(row.Id)));
    }

    private void Ask(Action yes) { confirmed = yes; ConfirmVisible = true; }

    private void Report(AppController.EncodeActionResult result) => ShowToast(result.Ok ? result.Message : result.Error, result.Ok ? 6000 : 12000);

    [RelayCommand] private void ConfirmYes() { var yes = confirmed; ConfirmVisible = false; confirmed = null; yes?.Invoke(); }
    [RelayCommand] private void ConfirmNo() { ConfirmVisible = false; confirmed = null; }

    private void ShowToast(string text, int ms)
    {
        Toast = text;
        toastTimer?.Cancel();
        toastTimer = new CancellationTokenSource();
        var token = toastTimer.Token;
        Task.Delay(ms, token).ContinueWith(t => { if (!t.IsCanceled) Dispatcher.UIThread.Post(() => Toast = null); });
    }

    /// <summary>Opens a video in the program the system uses for it.</summary>
    internal void Play(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path)) { ShowToast("The file is not there: " + path, 6000); return; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e) { ShowToast("Could not open the video: " + e.Message, 6000); }
    }

    internal string NewShotPath(string id, int n) => engine.CheckShotPath(id, n);

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
    public string OriginalPath { get; }
    public string NewPath { get; }
    public string NewFileName { get; }
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
    [ObservableProperty] private bool isIncomplete;
    [ObservableProperty] private bool isSkipped;
    [ObservableProperty] private bool canRetry;
    [ObservableProperty] private string whenText = "";
    // The check of the new file (length and pictures, like the scan): shown with the original's pictures
    [ObservableProperty] private bool hasCheck;
    [ObservableProperty] private bool checkOk;
    [ObservableProperty] private bool checkPending;
    [ObservableProperty] private string checkText = "";
    [ObservableProperty] private string newSize = "";
    [ObservableProperty] private bool canDeleteOriginal;
    [ObservableProperty] private bool canDeleteNew;
    [ObservableProperty] private bool canReplace;
    // What this job saved (shown right of the pictures)
    [ObservableProperty] private bool hasSaving;
    [ObservableProperty] private bool savingIsGood;
    [ObservableProperty] private string savingText = "";
    [ObservableProperty] private string savingDetail = "";
    // The compact table
    [ObservableProperty] private string shortStatus = "";
    [ObservableProperty] private string savedShort = "";
    [ObservableProperty] private Bitmap? newShot1;
    [ObservableProperty] private Bitmap? newShot2;
    [ObservableProperty] private Bitmap? newShot3;
    private string? newShotsKey;

    public bool IsProblem => IsFailed || IsIncomplete;
    public bool CheckFailed => HasCheck && !CheckOk && !CheckPending;
    public bool NoCheck => !HasCheck;

    // The original's pictures from the scan
    public Bitmap? Shot1 => Shots.Get(VideoId, 1);
    public Bitmap? Shot2 => Shots.Get(VideoId, 2);
    public Bitmap? Shot3 => Shots.Get(VideoId, 3);

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
        OriginalPath = i.Path; NewPath = i.OutputPath; NewFileName = System.IO.Path.GetFileName(i.OutputPath);
        Size = Fmt.Size(i.SizeBytes); Length = Fmt.Duration(i.DurationSeconds); Summary = i.Summary;
    }

    public void Update(EncodeItemInfo i)
    {
        StatusText = EncodeText.Status(i);
        Detail = i.Status == EncodeItemStatus.Running ? EncodeText.CurrentDetail(i) : "";
        Percent = i.Fraction * 100;
        IsRunning = i.Status == EncodeItemStatus.Running;
        IsDone = i.Status is EncodeItemStatus.Done or EncodeItemStatus.AlreadyEncoded;
        IsIncomplete = i.Status == EncodeItemStatus.Incomplete;
        IsFailed = i.Status == EncodeItemStatus.Failed;
        IsSkipped = i.Status == EncodeItemStatus.Skipped;
        CanRetry = IsFailed || IsSkipped;
        ResultText = IsDone ? $"{Size} became {Fmt.Size(i.OutputBytes)}" : "";
        WhenText = i.FinishedUtc is DateTime t ? Fmt.When(t) : "";
        HasCheck = i.HasCheck;
        CheckOk = i.CheckOk;
        CheckPending = i.CheckPending;
        CheckText = i.Status == EncodeItemStatus.Incomplete && i.Message.Length > 0 ? i.Message : i.CheckPending ? "Waiting to be checked..." : i.Check;
        NewSize = Fmt.Size(i.OutputBytes);
        CanDeleteOriginal = i.CanDeleteOriginal;
        CanDeleteNew = i.CanDeleteNew;
        CanReplace = i.CanReplace;
        HasSaving = i.Status is EncodeItemStatus.Done or EncodeItemStatus.AlreadyEncoded;
        SavingIsGood = i.Saved >= 0;
        SavingText = i.Saved >= 0 ? (i.OriginalDeleted ? "Saved " : "Saves ") + Fmt.Size(i.Saved) : Fmt.Size(-i.Saved) + " bigger";
        SavingDetail = i.Saved >= 0 ? EncodeText.Percent(i.SizeBytes, i.OutputBytes) + " smaller" : "no saving";
        SavedShort = !HasSaving ? "" : i.Saved >= 0 ? $"{Fmt.Size(i.Saved)} ({EncodeText.Percent(i.SizeBytes, i.OutputBytes)})" : Fmt.Size(-i.Saved) + " bigger";
        ShortStatus = i.Status switch
        {
            EncodeItemStatus.Done => "Done",
            EncodeItemStatus.AlreadyEncoded => "Already encoded",
            EncodeItemStatus.Incomplete => "Incomplete",
            EncodeItemStatus.Failed => "Failed",
            EncodeItemStatus.Skipped => "Skipped",
            EncodeItemStatus.Running => "Encoding",
            _ => "Waiting",
        };
        LoadNewShots(i);
        OnPropertyChanged(nameof(IsProblem));
        OnPropertyChanged(nameof(CheckFailed));
        OnPropertyChanged(nameof(NoCheck));
    }

    // The new file's pictures were made when the check ran: load them again only when it ran again
    private void LoadNewShots(EncodeItemInfo i)
    {
        string key = i.HasCheckShots ? i.Id + "|" + i.FinishedUtc?.Ticks : "";
        // Same check and the pictures are on screen: nothing to do. (Pictures that were not there yet, or were made again, are looked for again.)
        if (key == newShotsKey && (key.Length == 0 || NewShot1 != null)) return;
        newShotsKey = key;
        Bitmap? Load(int n)
        {
            if (key.Length == 0) return null;
            try
            {
                string path = owner.NewShotPath(Id, n);
                if (!System.IO.File.Exists(path)) return null;
                using var stream = System.IO.File.OpenRead(path);
                return Bitmap.DecodeToWidth(stream, 240);
            }
            catch { return null; }
        }
        NewShot1 = Load(1); NewShot2 = Load(2); NewShot3 = Load(3);
    }

    [RelayCommand] private void AskDeleteOriginal() => owner.AskDeleteOriginal(this);
    [RelayCommand] private void AskDeleteNew() => owner.AskDeleteNew(this);
    [RelayCommand] private void AskReplace() => owner.AskReplace(this);
    [RelayCommand] private void PlayOriginal() => owner.Play(OriginalPath);
    [RelayCommand] private void PlayNew() => owner.Play(NewPath);
    [RelayCommand] private void Remove() => owner.Remove(this);
    [RelayCommand] private void Retry() => owner.Retry(this);
    [RelayCommand] private Task ShowFolder() => owner.ShowFolder(Folder);
}
