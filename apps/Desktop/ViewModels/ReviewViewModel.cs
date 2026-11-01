using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using deepduplicates;

namespace DuplicateVideoRemover.Desktop.ViewModels;

/// <summary>The review list: a flat list of rows (section titles, group headers, videos) so it can be virtualized.</summary>
public partial class ReviewViewModel : ObservableObject
{
    private const int PageSize = 60;
    private readonly AppController app;
    private readonly MainViewModel main;
    private ResultSession session;
    private int shownGroups = PageSize;
    private CancellationTokenSource? toastTimer;

    public ObservableCollection<FolderItem> Folders { get; } = new();
    public ObservableCollection<object> Rows { get; } = new();
    public ObservableCollection<ExecuteFailure> Failures { get; } = new();

    [ObservableProperty] private string summary = "";
    [ObservableProperty] private bool hasResults;
    [ObservableProperty] private string? folderFilter;
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string selectionCount = "";
    [ObservableProperty] private string selectionSize = "";
    [ObservableProperty] private bool hasSelection;
    [ObservableProperty] private bool confirmVisible;
    [ObservableProperty] private string confirmTitle = "";
    [ObservableProperty] private string confirmText = "";
    [ObservableProperty] private bool busy;
    [ObservableProperty] private string? toast;
    [ObservableProperty] private bool failuresVisible;
    [ObservableProperty] private bool hasFailures;
    [ObservableProperty] private string failuresTitle = "";
    [ObservableProperty] private string failuresSummary = "";
    // Progress while deleting
    [ObservableProperty] private double deletePercent;
    [ObservableProperty] private string deleteText = "";
    [ObservableProperty] private string deleteFailedText = "";

    public string ExecuteLabel => "Delete " + Fmt.Plural(session.SelectedCount, "file", "files");
    public bool FolderFilterActive => FolderFilter != null;

    public ReviewViewModel(AppController app, MainViewModel main)
    {
        this.app = app;
        this.main = main;
        session = app.Session;
        session.Changed += OnSessionChanged;
        app.SessionReplaced += () => Dispatcher.UIThread.Post(() =>
        {
            session.Changed -= OnSessionChanged;
            session = app.Session;
            session.Changed += OnSessionChanged;
            FolderFilter = null;
            shownGroups = PageSize;
            Rebuild();
        });
        Rebuild();
    }

    partial void OnFolderFilterChanged(string? value) { OnPropertyChanged(nameof(FolderFilterActive)); shownGroups = PageSize; Rebuild(); }
    partial void OnSearchChanged(string value) { shownGroups = PageSize; Rebuild(); }

    private void OnSessionChanged() => Dispatcher.UIThread.Post(RefreshState);

    // --- Building the list ---

    /// <summary>Rebuilds rows for the current filter (after filtering or removing files).</summary>
    public void Rebuild()
    {
        var results = session.Results;
        HasResults = results.Groups.Count + results.Broken.Count > 0;
        Summary = HasResults
            ? $"{Fmt.Plural(results.DuplicateCount, "duplicate", "duplicates")} in {Fmt.Plural(results.Groups.Count, "group", "groups")}, " +
              $"{Fmt.Plural(results.Broken.Count, "broken or short video", "broken or short videos")}. Found {Fmt.Ago(results.CreatedUtc)}."
            : "Nothing to review yet.";

        Rows.Clear();
        var groups = results.Groups.Where(g => g.Members.Any(Matches)).ToList();
        var broken = results.Broken.Where(Matches).ToList();

        if (groups.Count > 0)
        {
            Rows.Add(new SectionRow("Duplicates", Fmt.Plural(groups.Count, "group", "groups")));
            foreach (var g in groups.Take(shownGroups))
            {
                Rows.Add(new GroupHeaderRow($"{g.Members.Count} copies, {Fmt.Duration(g.Members[0].DurationSeconds)} long"));
                for (int i = 0; i < g.Members.Count; i++)
                {
                    var row = new VideoRow(g.Members[i], this, inGroup: true);
                    // Each copy was matched with the first video: the divider takes the copy below out of the group
                    if (i > 0) Rows.Add(new MatchDividerRow(row));
                    Rows.Add(row);
                }
            }
            if (groups.Count > shownGroups)
                Rows.Add(new MoreRow($"Show {Math.Min(PageSize, groups.Count - shownGroups)} more groups", () => { shownGroups += PageSize; Rebuild(); }));
        }
        if (broken.Count > 0)
        {
            Rows.Add(new SectionRow("Broken, incomplete or too short", Fmt.Plural(broken.Count, "video", "videos")));
            foreach (var b in broken) Rows.Add(new VideoRow(b, this, inGroup: false));
        }
        if (HasResults && groups.Count == 0 && broken.Count == 0)
            Rows.Add(new SectionRow("No matches", "Nothing matches this filter."));

        RefreshState();
    }

    private bool Matches(VideoEntry e) =>
        (FolderFilter == null || e.Folder == FolderFilter) &&
        (string.IsNullOrWhiteSpace(Search) || e.Path.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Updates selection, kept marks, folder counts and the action bar from the session.</summary>
    private void RefreshState()
    {
        foreach (var row in Rows.OfType<VideoRow>()) row.Refresh();

        var folders = session.Folders();
        int n = session.SelectedCount;
        Folders.Clear();
        Folders.Add(FolderItem.All(folders.Sum(f => f.Count), n, FolderFilter == null));
        foreach (var f in folders) Folders.Add(new FolderItem(f, f.Folder == FolderFilter));

        HasSelection = n > 0;
        SelectionCount = n == 0 ? "nothing selected for deleting" : $"selected for deleting in {Fmt.Plural(n, "file", "files")}";
        SelectionSize = Fmt.Size(session.SelectedBytes);
        OnPropertyChanged(nameof(ExecuteLabel));
    }

    // --- Commands ---

    [RelayCommand] private void SelectAllDuplicates() => session.SelectAllDuplicates();
    [RelayCommand] private void SelectBroken() => session.SelectAllBroken();
    [RelayCommand] private void SelectNone() => session.SelectNone();
    [RelayCommand] private void ShowAllFolders() => FolderFilter = null;
    [RelayCommand] private void ShowFolder(string? folder) => FolderFilter = folder;
    [RelayCommand] private void UnselectFolder() { if (FolderFilter != null) session.UnselectFolder(FolderFilter); }
    [RelayCommand] private void GoToScan() => main.ShowScan();
    [RelayCommand] private void GoToEncode() => main.ShowEncode();

    [RelayCommand]
    private void SelectFolder()
    {
        if (FolderFilter == null) return;
        int n = session.SelectFolder(FolderFilter);
        ShowToast(n == 0 ? "Nothing more to select in this folder." : $"Selected {Fmt.Plural(n, "file", "files")} in this folder.");
    }

    [RelayCommand]
    private void Reanalyze()
    {
        app.ReanalyzeFromDatabase();
        ShowToast("Compared again with the saved settings.");
    }

    [RelayCommand]
    private void AskExecute()
    {
        int n = session.SelectedCount;
        if (n == 0) return;
        string size = Fmt.Size(session.SelectedBytes);
        ConfirmTitle = $"Delete {Fmt.Plural(n, "file", "files")} permanently?";
        ConfirmText = $"{size} will be deleted. This can't be undone.";
        ConfirmVisible = true;
    }

    [RelayCommand] private void CancelExecute() => ConfirmVisible = false;

    [RelayCommand]
    private async Task Execute()
    {
        ConfirmVisible = false;
        DeletePercent = 0;
        DeleteText = "Starting...";
        DeleteFailedText = "";
        Busy = true;
        var lastShown = DateTime.MinValue;
        // Reported from the worker thread; shown at most ~8 times a second, and always for the last step
        var progress = new SyncProgress<DeleteProgress>(p =>
        {
            if (!p.UpdatingDatabase && DateTime.UtcNow - lastShown < TimeSpan.FromMilliseconds(120)) return;
            lastShown = DateTime.UtcNow;
            Dispatcher.UIThread.Post(() =>
            {
                DeletePercent = p.Fraction * 100;
                DeleteText = p.UpdatingDatabase ? "Updating the database..." : $"{p.Done + 1:N0} of {p.Total:N0}: {p.CurrentFile}";
                DeleteFailedText = p.Failed > 0 ? $"{Fmt.Plural(p.Failed, "file", "files")} could not be deleted so far" : "";
            });
        });
        ExecuteReport report;
        try { report = await Task.Run(() => app.Execute(progress)); }
        finally { Busy = false; }

        Failures.Clear();
        foreach (var f in report.Failed) Failures.Add(f);
        HasFailures = report.Failed.Count > 0;
        FailuresSummary = $"{(report.Removed.Count > 0 ? $"Deleted {Fmt.Plural(report.Removed.Count, "file", "files")} ({Fmt.Size(report.BytesFreed)}). " : "")}"
                        + "The files below are still selected. Close any program using them, check that you may change the folder, and try again.";
        FailuresTitle = $"{Fmt.Plural(report.Failed.Count, "file was", "files were")} not deleted";
        // Failures are shown straight away, with the reason for each file
        FailuresVisible = HasFailures;

        var text = $"Deleted {Fmt.Plural(report.Removed.Count, "file", "files")}, {Fmt.Size(report.BytesFreed)}.";
        if (HasFailures) text += $" {Fmt.Plural(report.Failed.Count, "file", "files")} could not be deleted.";
        // The folder may be empty now - then show everything again
        if (FolderFilter != null && !session.Folders().Any(f => f.Folder == FolderFilter)) FolderFilter = null;
        else Rebuild();
        ShowToast(text, HasFailures ? 12000 : 6000);
    }

    [RelayCommand] private void ShowFailures() => FailuresVisible = true;
    [RelayCommand] private void CloseFailures() => FailuresVisible = false;

    internal void Select(VideoRow row, bool select)
    {
        var message = session.SetSelected(row.Entry.Id, select);
        if (message != null) { ShowToast(message); row.Refresh(); }
    }


    internal void MarkFalsePositive(VideoRow row)
    {
        ShowToast(app.MarkFalsePositive(row.Entry.Id), 6000);
        Rebuild();
    }

    /// <summary>Opens the video in the program the system uses for it.</summary>
    internal void Play(VideoRow row)
    {
        try
        {
            if (!System.IO.File.Exists(row.Entry.Path)) { ShowToast("The file is not there any more: " + row.Entry.Path); return; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(row.Entry.Path) { UseShellExecute = true });
        }
        catch (Exception e) { ShowToast("Could not open the video: " + e.Message); }
    }
    internal bool IsSelected(int id) => session.IsSelected(id);
    internal bool IsKept(VideoEntry e) => session.IsKept(e);

    private void ShowToast(string text, int ms = 4000)
    {
        Toast = text;
        toastTimer?.Cancel();
        toastTimer = new CancellationTokenSource();
        var token = toastTimer.Token;
        Task.Delay(ms, token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Dispatcher.UIThread.Post(() => Toast = null);
        });
    }
}

public sealed record SectionRow(string Title, string Subtitle);
public sealed record GroupHeaderRow(string Text);

/// <summary>The line between two matched copies, with "False positive" for the copy below it.</summary>
public sealed class MatchDividerRow
{
    public VideoRow Below { get; }
    public string Tip { get; }
    public IRelayCommand Command { get; }
    public MatchDividerRow(VideoRow below)
    {
        Below = below;
        Tip = $"These videos only look alike: take {below.FileName} out of the group and never match it with these copies again";
        Command = new RelayCommand(() => below.MarkFalsePositive());
    }
}

public sealed class MoreRow
{
    public string Text { get; }
    public IRelayCommand Command { get; }
    public MoreRow(string text, Action action) { Text = text; Command = new RelayCommand(action); }
}

public sealed class FolderItem
{
    public string? Folder { get; }
    public string ShortName { get; }
    public string Count { get; }
    public string SelectedText { get; }
    public bool HasSelected { get; }
    public bool IsActive { get; }

    private FolderItem(string? folder, string shortName, int count, int selected, bool active)
    {
        Folder = folder;
        ShortName = shortName;
        Count = count.ToString("N0");
        HasSelected = selected > 0;
        SelectedText = $"{selected} selected";
        IsActive = active;
    }

    /// <summary>The first item: every folder.</summary>
    public static FolderItem All(int count, int selected, bool active) => new(null, "All folders", count, selected, active);

    public FolderItem(FolderSummary f, bool active)
    {
        Folder = f.Folder;
        var parts = f.Folder.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        ShortName = parts.Length <= 2 ? f.Folder : "…" + System.IO.Path.DirectorySeparatorChar + string.Join(System.IO.Path.DirectorySeparatorChar, parts[^2..]);
        Count = f.Count.ToString("N0");
        HasSelected = f.SelectedCount > 0;
        SelectedText = $"{f.SelectedCount} selected";
        IsActive = active;
    }
}

/// <summary>One video in the review list.</summary>
public partial class VideoRow : ObservableObject
{
    private readonly ReviewViewModel owner;
    private bool refreshing;
    private Bitmap? shot1, shot2, shot3;
    private bool shotsLoaded;

    public VideoEntry Entry { get; }
    public bool InGroup { get; }
    public string FileName => Entry.FileName;
    public string Folder => Entry.Folder;
    public string Meta { get; }
    public string Reason { get; }
    public bool HasReason => Reason.Length > 0;

    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private bool isKept;

    public bool ShowReason => HasReason && !IsKept;

    // Screenshots load when the row is first shown (the list is virtualized)
    public Bitmap? Shot1 { get { LoadShots(); return shot1; } }
    public Bitmap? Shot2 { get { LoadShots(); return shot2; } }
    public Bitmap? Shot3 { get { LoadShots(); return shot3; } }

    public VideoRow(VideoEntry entry, ReviewViewModel owner, bool inGroup)
    {
        Entry = entry;
        this.owner = owner;
        InGroup = inGroup;
        Meta = string.Join("     ", new[] { Fmt.Size(entry.SizeBytes), Fmt.Duration(entry.DurationSeconds), Fmt.Bitrate(entry.Bitrate) }.Where(s => s.Length > 0));
        Reason = Regex.Replace(entry.Reason ?? "", "<!--.*?-->", "").Trim(' ', '-');
    }

    private void LoadShots()
    {
        if (shotsLoaded) return;
        shotsLoaded = true;
        shot1 = Shots.Get(Entry.Id, 1);
        shot2 = Shots.Get(Entry.Id, 2);
        shot3 = Shots.Get(Entry.Id, 3);
    }

    internal void Refresh()
    {
        refreshing = true;
        IsSelected = owner.IsSelected(Entry.Id);
        IsKept = InGroup && owner.IsKept(Entry);
        refreshing = false;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (!refreshing) owner.Select(this, value);
    }

    partial void OnIsKeptChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowReason));
    }

    [RelayCommand] private void Toggle() => IsSelected = !IsSelected;
    [RelayCommand] private void Play() => owner.Play(this);
    internal void MarkFalsePositive() => owner.MarkFalsePositive(this);
    [RelayCommand] private void ShowFolder() => owner.ShowFolderCommand.Execute(Folder);
}
