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

    /// <summary>Set by the view: puts text on the clipboard (false when there is none).</summary>
    public Func<string, Task<bool>>? CopyText { get; set; }

    // Folders hidden with "Exclude folder", until "Unhide folders"
    private readonly HashSet<string> hidden = new(StringComparer.OrdinalIgnoreCase);
    private System.Threading.CancellationTokenSource? toastTimer;
    [ObservableProperty] private bool hasHidden;
    [ObservableProperty] private string unhideText = "";
    [ObservableProperty] private string? toast;
    // "Delete" confirmation for one video
    [ObservableProperty] private EncodeRow? deleting;
    [ObservableProperty] private string deleteTitle = "";
    [ObservableProperty] private string deleteText = "";
    public bool DeleteVisible => Deleting != null;

    [ObservableProperty] private string settingsSummary = "";
    [ObservableProperty] private string qualitySummary = "";
    [ObservableProperty] private string search = "";
    [ObservableProperty] private decimal minKbps;
    [ObservableProperty] private decimal minMb = 100;
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

    // What the main button does: add to the encode queue, or create a script. Starts as the saved default;
    // a choice made here is kept until the default is changed in Settings.
    public ChoiceOption[] RunModes { get; } = EncodeOptions.RunModes;
    [ObservableProperty] private ChoiceOption runMode = EncodeOptions.RunModes[0];
    private string lastDefaultMode = "";
    public bool IsRunMode => RunMode.Id == "run";
    public string PrimaryText => IsRunMode ? "Add to queue" : "Create encoding script";
    partial void OnRunModeChanged(ChoiceOption value) { OnPropertyChanged(nameof(IsRunMode)); OnPropertyChanged(nameof(PrimaryText)); }

    // Videos in the encode queue are greyed out here (and can be hidden)
    [ObservableProperty] private bool hideQueued;
    [ObservableProperty] private bool hasQueueSummary;
    [ObservableProperty] private string queueSummary = "";
    private string queueSignature = "";
    private int queueRefreshQueued;
    partial void OnHideQueuedChanged(bool value) => Filter();

    public EncodeViewModel(AppController app, MainViewModel main)
    {
        this.app = app;
        this.main = main;
        app.DataChanged += () => Dispatcher.UIThread.Post(() => _ = Reload());
        // The queue changes while it runs: grey out what was added, show what finished (progress ticks change nothing here)
        app.Encoder.ProgressChanged += _ =>
        {
            if (System.Threading.Interlocked.Exchange(ref queueRefreshQueued, 1) == 1) return;
            Dispatcher.UIThread.Post(() => { System.Threading.Volatile.Write(ref queueRefreshQueued, 0); RefreshQueue(); });
        };
    }

    /// <summary>Marks the rows that are in the queue (waiting, running or done). Only when the queue's membership changed.</summary>
    private void RefreshQueue(bool force = false)
    {
        var p = app.Encoder.Current;
        string signature = $"{p.Items.Count}|{p.QueuedCount}|{p.Current?.Id}|{p.DoneCount}";
        if (!force && signature == queueSignature) return;
        queueSignature = signature;
        var map = p.StatusByPath();
        bool changed = false;
        foreach (var r in all) changed |= r.SetQueueState(map.TryGetValue(r.Candidate.Path, out var st) ? st : null);
        int waiting = p.QueuedCount, running = p.Current != null ? 1 : 0, done = p.DoneCount;
        HasQueueSummary = waiting + running + done > 0;
        QueueSummary = "Encode queue: " + string.Join(", ", new[] { running > 0 ? "1 encoding" : null, waiting > 0 ? $"{waiting:N0} waiting" : null, done > 0 ? $"{done:N0} done" : null }.Where(x => x != null)) + ".";
        if (changed && HideQueued) Filter(); else if (changed) UpdatePicked();
    }

    partial void OnScriptPathChanged(string? value) => OnPropertyChanged(nameof(HasResult));
    partial void OnSearchChanged(string value) => Filter();
    partial void OnMinKbpsChanged(decimal value) => Filter();
    partial void OnMinMbChanged(decimal value) => Filter();

    /// <summary>Loads the list again (when the screen is opened, after a scan or new settings).</summary>
    public async Task Reload()
    {
        var e = settings = app.Settings.EncodeOrDefault();
        if (e.runMode != lastDefaultMode) { lastDefaultMode = e.runMode; RunMode = RunModes.First(m => m.Id == e.runMode); }
        double target = EncodeEstimator.TargetKbps(e);
        SettingsSummary = $"{EncodeOptions.Codec(e.codec).Label}, {e.resolution}p, quality {e.crf}";
        QualitySummary = $"{EncodeOptions.QualityLabel(e.codec, e.crf ?? 0)}. Speed: {e.preset}, {(e.audio == "copy" ? "original sound" : "AAC sound")}, {e.container.ToUpperInvariant()}";
        Loading = true;
        var picked = all.Where(r => r.IsPicked).Select(r => r.Candidate.Id).ToHashSet();
        var candidates = await Task.Run(app.EncodeCandidates);
        all = candidates.Select(c => new EncodeRow(c, picked.Contains(c.Id), EncodeEstimator.EstimatedBytes(c, e, target), UpdatePicked, this)).ToList();
        anchor = null;
        HasVideos = all.Count > 0;
        Loading = false;
        RefreshQueue(force: true);
        Filter();
    }

    private IEnumerable<EncodeRow> Visible() => all.Where(r =>
        !(HideQueued && r.IsQueued) && r.Candidate.AverageKbps >= (double)MinKbps && r.Candidate.SizeBytes >= (double)MinMb * 1024 * 1024 && !hidden.Contains(r.Candidate.Folder) &&
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
        if (row.IsQueued) return;   // already in the queue
        if (shift && anchor != null)
        {
            int a = Rows.IndexOf(anchor), b = Rows.IndexOf(row);
            if (a >= 0 && b >= 0)
            {
                for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) if (!Rows[i].IsQueued) Rows[i].IsPicked = anchorState;
                return;
            }
        }
        row.IsPicked = !row.IsPicked;
        anchorState = row.IsPicked;
        anchor = row;
    }

    [RelayCommand] private void SelectAll() { foreach (var r in Visible().Where(r => !r.IsQueued)) r.IsPicked = true; }
    [RelayCommand] private void SelectTop() { foreach (var r in Visible().Where(r => !r.IsQueued).Take(20)) r.IsPicked = true; }
    [RelayCommand] private void SelectAllShown() { foreach (var r in Visible().Where(r => !r.IsQueued)) r.IsPicked = true; }
    [RelayCommand] private void SelectNone() { foreach (var r in all) r.IsPicked = false; }
    [RelayCommand] private void EditSettings() => main.ShowSettings();
    [RelayCommand] private void GoToScan() => main.ShowScan();
    [RelayCommand] private void OpenQueue() => main.ShowQueue();

    /// <summary>The main button: adds the picked videos to the encode queue, or asks before creating the script.</summary>
    [RelayCommand]
    private void Primary()
    {
        if (IsRunMode) AddToQueue(); else AskCreate();
    }

    private void AddToQueue()
    {
        var videos = all.Where(r => r.IsPicked && !r.IsQueued).Select(r => r.Candidate).ToList();   // in list order: highest bitrate first
        if (videos.Count == 0) return;
        var result = app.EnqueueEncode(videos);
        // Files that are gone since the scan are not queued and leave the list
        foreach (var gone in result.Missing.Select(m => all.FirstOrDefault(r => r.Candidate.Id == m.Id)).Where(r => r != null).ToList())
        {
            gone!.IsPicked = false;
            all.Remove(gone);
            Rows.Remove(gone);
        }
        RefreshQueue(force: true);   // greys out what was added (and unpicks it)
        var parts = new List<string> { $"Added {Fmt.Plural(result.Added, "video", "videos")} to the queue." };
        if (result.AlreadyThere > 0) parts.Add($"{Fmt.Plural(result.AlreadyThere, "was", "were")} in it already.");
        if (result.Missing.Count > 0) parts.Add($"{Fmt.Plural(result.Missing.Count, "file was", "files were")} not found any more and left out.");
        if (app.Encoder.Current.Halted && app.Encoder.Current.QueuedCount > 0) parts.Add("The queue is stopped: press Start on the Encoding screen.");
        ShowToast(string.Join(" ", parts), result.Missing.Count > 0 ? 10000 : 5000);
    }

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

    // --- Per video: exclude its folder, copy its path, delete it ---

    /// <summary>Hides every video in the folder (they are unpicked, so the script only holds what can be seen).</summary>
    internal void HideFolder(EncodeRow row)
    {
        string folder = row.Candidate.Folder;
        hidden.Add(folder);
        foreach (var r in all.Where(r => string.Equals(r.Candidate.Folder, folder, StringComparison.OrdinalIgnoreCase))) r.IsPicked = false;
        UpdateHidden();
        Filter();
        ShowToast($"Hid the videos in {folder}.");
    }

    [RelayCommand]
    private void UnhideFolders()
    {
        hidden.Clear();
        UpdateHidden();
        Filter();
    }

    private void UpdateHidden()
    {
        HasHidden = hidden.Count > 0;
        UnhideText = $"Unhide folders ({hidden.Count})";
    }

    /// <summary>Opens the video in the program the system uses for it.</summary>
    internal void Play(EncodeRow row)
    {
        try
        {
            if (!System.IO.File.Exists(row.Candidate.Path)) { ShowToast("The file is not there any more: " + row.Candidate.Path); return; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(row.Candidate.Path) { UseShellExecute = true });
        }
        catch (Exception e) { ShowToast("Could not open the video: " + e.Message); }
    }

    internal async Task CopyPath(EncodeRow row)
    {
        try
        {
            bool ok = CopyText != null && await CopyText(row.Candidate.Path);
            ShowToast(ok ? "Copied: " + row.Candidate.Path : "Could not reach the clipboard.");
        }
        catch (Exception e) { ShowToast("Could not copy: " + e.Message); }
    }

    partial void OnDeletingChanged(EncodeRow? value) => OnPropertyChanged(nameof(DeleteVisible));

    internal void AskDelete(EncodeRow row)
    {
        Deleting = row;
        DeleteTitle = "Delete this video permanently?";
        DeleteText = $"{row.FileName}\n{row.Folder}\n{row.Size} will be deleted. This can't be undone.";
    }

    [RelayCommand] private void CancelDelete() => Deleting = null;

    [RelayCommand]
    private void ConfirmDelete()
    {
        var row = Deleting;
        Deleting = null;
        if (row == null) return;
        var error = app.DeleteVideoFile(row.Candidate.Id, row.Candidate.Path);
        if (error != null) { ShowToast($"Could not delete {row.FileName}: {error}", 10000); return; }
        row.IsPicked = false;
        all.Remove(row);
        Rows.Remove(row);
        HasVideos = all.Count > 0;
        UpdatePicked();
        ShowToast($"Deleted {row.FileName} ({row.Size}).");
    }

    private void ShowToast(string text, int ms = 4000)
    {
        Toast = text;
        toastTimer?.Cancel();
        toastTimer = new System.Threading.CancellationTokenSource();
        var token = toastTimer.Token;
        Task.Delay(ms, token).ContinueWith(t => { if (!t.IsCanceled) Dispatcher.UIThread.Post(() => Toast = null); });
    }

    [RelayCommand]
    private void Create()
    {
        ConfirmVisible = false;
        // In list order: highest bitrate first
        var videos = all.Where(r => r.IsPicked).Select(r => r.Candidate).ToList();
        if (videos.Count == 0) return;
        var result = app.WriteEncodeScript(videos);
        // Files that are gone since the scan are left out of the script and the list
        foreach (var gone in result.Missing.Select(m => all.FirstOrDefault(r => r.Candidate.Id == m.Id)).Where(r => r != null).ToList())
        {
            gone!.IsPicked = false;
            all.Remove(gone);
            Rows.Remove(gone);
        }
        string missingText = result.Missing.Count == 0 ? "" : $" {Fmt.Plural(result.Missing.Count, "file was", "files were")} not found any more and left out.";
        if (result.Path == null)
        {
            ShowToast("No script created:" + missingText, 10000);
            return;
        }
        ScriptPath = result.Path;
        ResultText = $"Created a script for {Fmt.Plural(result.Videos, "video", "videos")}: {ScriptPath}.{missingText}";
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

    // In the encode queue: waiting, being encoded or done. Such a row is greyed out and cannot be picked.
    [ObservableProperty] private EncodeItemStatus? queueState;
    public bool IsQueued => QueueState != null;
    public string QueueLabel => QueueState switch
    {
        EncodeItemStatus.Queued => "In the queue",
        EncodeItemStatus.Running => "Encoding now",
        EncodeItemStatus.Done => "Encoded",
        _ => "",
    };

    /// <summary>Returns true when the state changed.</summary>
    public bool SetQueueState(EncodeItemStatus? state)
    {
        if (QueueState == state) return false;
        QueueState = state;
        if (state != null) IsPicked = false;
        OnPropertyChanged(nameof(IsQueued));
        OnPropertyChanged(nameof(QueueLabel));
        return true;
    }

    private readonly EncodeViewModel owner;

    public EncodeRow(EncodeCandidate candidate, bool picked, long estimatedBytes, Action changed, EncodeViewModel owner)
    {
        Candidate = candidate;
        isPicked = picked;
        EstimatedBytes = estimatedBytes;
        this.changed = changed;
        this.owner = owner;
    }

    partial void OnIsPickedChanged(bool value) => changed();

    [RelayCommand] private void ExcludeFolder() => owner.HideFolder(this);
    [RelayCommand] private Task CopyPath() => owner.CopyPath(this);
    [RelayCommand] private void Delete() => owner.AskDelete(this);
    [RelayCommand] private void Play() => owner.Play(this);
}
