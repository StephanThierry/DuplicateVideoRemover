using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using deepduplicates;

namespace DuplicateVideoRemover.Desktop.ViewModels;

/// <summary>
/// The All files screen: every video the last scan found, with a fast search over the paths
/// ("vacation lisbon 2026 -ocean"). The list is virtualized, so only visible rows load screenshots.
/// </summary>
public partial class AllFilesViewModel : ObservableObject
{
    private readonly AppController app;
    private readonly MainViewModel main;
    private FileIndex? index;
    private readonly Dictionary<int, FileRow> rowsById = new();
    private readonly HashSet<string> hidden = new(StringComparer.OrdinalIgnoreCase);   // "Exclude folder"
    private readonly HashSet<int> deleted = new();
    private CancellationTokenSource? debounce, toastTimer;
    private bool stale = true;

    /// <summary>Set by the view: puts text on the clipboard (false when there is none).</summary>
    public Func<string, Task<bool>>? CopyText { get; set; }

    /// <summary>The rows that match the search (a new list each time - the list control is virtualized).</summary>
    [ObservableProperty] private IReadOnlyList<FileRow> rows = Array.Empty<FileRow>();
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string resultText = "";
    [ObservableProperty] private string sourceText = "";
    [ObservableProperty] private bool loading;
    [ObservableProperty] private bool hasFiles = true;
    [ObservableProperty] private bool hasMatches = true;
    [ObservableProperty] private bool hasHidden;
    [ObservableProperty] private string unhideText = "";
    [ObservableProperty] private string? toast;
    [ObservableProperty] private FileRow? deleting;
    [ObservableProperty] private string deleteText = "";
    public bool DeleteVisible => Deleting != null;

    public AllFilesViewModel(AppController app, MainViewModel main)
    {
        this.app = app;
        this.main = main;
        // After a scan or a delete elsewhere: load again the next time the screen is shown
        app.DataChanged += () => Dispatcher.UIThread.Post(() => { stale = true; if (main.SelectedTab == MainViewModel.FilesTab) _ = Reload(); });
    }

    /// <summary>Loads the list when the screen is opened (only when the data changed since).</summary>
    public async Task Reload()
    {
        if (!stale || Loading) return;
        stale = false;
        Loading = true;
        index = await Task.Run(app.AllFiles);
        // Where the list comes from: no scan is needed to open this screen
        SourceText = LastScan.FoundUtc is DateTime t
            ? $"Every video the scan from {Fmt.When(t)} found (the saved file list - nothing is scanned)."
            : "Every video found on disk.";
        rowsById.Clear();
        deleted.Clear();
        foreach (var e in index.All) rowsById[e.Id] = new FileRow(e, this);
        HasFiles = index.Count > 0;
        Loading = false;
        await Filter();
    }

    // Searches a moment after the last key press
    partial void OnSearchChanged(string value)
    {
        debounce?.Cancel();
        var cts = debounce = new CancellationTokenSource();
        Task.Delay(150, cts.Token).ContinueWith(t => { if (!t.IsCanceled) Dispatcher.UIThread.Post(() => _ = Filter()); });
    }

    private async Task Filter()
    {
        if (index == null) return;
        var query = SearchQuery.Parse(Search);
        var hiddenNow = hidden.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deletedNow = deleted.ToHashSet();
        var found = await Task.Run(() => index.Search(query, f => !hiddenNow.Contains(f.Folder) && !deletedNow.Contains(f.Id)));
        if (!SameQuery(query, SearchQuery.Parse(Search))) return;   // typed on meanwhile: that search shows instead
        Rows = found.Select(f => rowsById[f.Id]).ToList();
        HasMatches = Rows.Count > 0 || index.Count == 0;
        int total = index.Count - deleted.Count;
        ResultText = query.IsEmpty && hidden.Count == 0 ? $"{total:N0} files" : $"{Rows.Count:N0} of {total:N0} files";
    }

    private static bool SameQuery(SearchQuery a, SearchQuery b) => a.Include.SequenceEqual(b.Include) && a.Exclude.SequenceEqual(b.Exclude);

    internal void HideFolder(FileRow row)
    {
        hidden.Add(row.Folder);
        UpdateHidden();
        _ = Filter();
        ShowToast($"Hid the videos in {row.Folder}.");
    }

    [RelayCommand]
    private void UnhideFolders()
    {
        hidden.Clear();
        UpdateHidden();
        _ = Filter();
    }

    private void UpdateHidden()
    {
        HasHidden = hidden.Count > 0;
        UnhideText = $"Unhide folders ({hidden.Count})";
    }

    internal void Play(FileRow row)
    {
        try
        {
            if (!System.IO.File.Exists(row.Entry.Path)) { ShowToast("The file is not there any more: " + row.Entry.Path); return; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(row.Entry.Path) { UseShellExecute = true });
        }
        catch (Exception e) { ShowToast("Could not open the video: " + e.Message); }
    }

    internal async Task CopyPath(FileRow row)
    {
        try
        {
            bool ok = CopyText != null && await CopyText(row.Entry.Path);
            ShowToast(ok ? "Copied: " + row.Entry.Path : "Could not reach the clipboard.");
        }
        catch (Exception e) { ShowToast("Could not copy: " + e.Message); }
    }

    partial void OnDeletingChanged(FileRow? value) => OnPropertyChanged(nameof(DeleteVisible));

    internal void AskDelete(FileRow row)
    {
        Deleting = row;
        DeleteText = $"{row.FileName}\n{row.Folder}\n{Fmt.Size(row.Entry.SizeBytes)} will be deleted. This can't be undone.";
    }

    [RelayCommand] private void CancelDelete() => Deleting = null;

    [RelayCommand]
    private void ConfirmDelete()
    {
        var row = Deleting;
        Deleting = null;
        if (row == null) return;
        var error = app.DeleteVideoFile(row.Entry.Id, row.Entry.Path);
        if (error != null) { ShowToast($"Could not delete {row.FileName}: {error}", 10000); return; }
        // Kept out of the list; the index is built again after the next change in the data
        deleted.Add(row.Entry.Id);
        Rows = Rows.Where(r => r != row).ToList();
        HasMatches = Rows.Count > 0;
        ShowToast($"Deleted {row.FileName} ({Fmt.Size(row.Entry.SizeBytes)}).");
    }

    [RelayCommand] private void GoToScan() => main.ShowScan();
    [RelayCommand] private void ScanAgain() => main.ShowScan();

    private void ShowToast(string text, int ms = 4000)
    {
        Toast = text;
        toastTimer?.Cancel();
        var cts = toastTimer = new CancellationTokenSource();
        Task.Delay(ms, cts.Token).ContinueWith(t => { if (!t.IsCanceled) Dispatcher.UIThread.Post(() => Toast = null); });
    }
}

/// <summary>One video in the All files list.</summary>
public partial class FileRow
{
    private readonly AllFilesViewModel owner;

    public VideoEntry Entry { get; }
    public string FileName => Entry.FileName;
    public string Folder => Entry.Folder;
    public string Meta { get; }

    // Full-size screenshots (240 x 160), from the shared cache when the row is shown: rows don't keep them,
    // so scrolling through 60,000 files doesn't fill the memory
    public Bitmap? Shot1 => Shots.Get(Entry.Id, 1);
    public Bitmap? Shot2 => Shots.Get(Entry.Id, 2);
    public Bitmap? Shot3 => Shots.Get(Entry.Id, 3);

    public FileRow(VideoEntry entry, AllFilesViewModel owner)
    {
        Entry = entry;
        this.owner = owner;
        Meta = string.Join("     ", new[] { Fmt.Size(entry.SizeBytes), Fmt.Duration(entry.DurationSeconds), Fmt.Bitrate(entry.Bitrate) }.Where(s => s.Length > 0));
    }

    [RelayCommand] private void Play() => owner.Play(this);
    [RelayCommand] private void ExcludeFolder() => owner.HideFolder(this);
    [RelayCommand] private Task CopyPath() => owner.CopyPath(this);
    [RelayCommand] private void Delete() => owner.AskDelete(this);
}
