using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.VisualTree;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using deepduplicates;
using DuplicateVideoRemover.Desktop;
using DuplicateVideoRemover.Desktop.ViewModels;
using DuplicateVideoRemover.Desktop.Views;

// Usage: DesktopShots <data folder> <output folder> [scan]
string data = args[0], output = args[1];
bool scan = args.Length > 2 && args[2] == "scan";
Directory.CreateDirectory(output);
var controller = AppController.Open(data);
DuplicateVideoRemover.Desktop.Program.Controller = controller;

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();

var vm = new MainViewModel(controller);
var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
window.Show();

void Pump(int ms = 300)
{
    var end = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < end) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
}
void Shot(string name)
{
    Pump();
    window.CaptureRenderedFrame()?.Save(Path.Combine(output, name + ".png"));
    Console.WriteLine("saved " + name);
}

if (scan)
{
    vm.SelectedTab = 1;
    vm.Scan.StartCommand.Execute(null);
    Pump(2500);
    Shot("d_scan_mid");
    while (controller.Scanner.IsRunning) Pump(200);
    Shot("d_scan_done");
}
vm.SelectedTab = 0; Shot("d_review");
vm.Review.SelectAllDuplicatesCommand.Execute(null); Shot("d_review_selected");
vm.Review.ShowFolderCommand.Execute(Environment.GetEnvironmentVariable("SHOT_FOLDER") ?? "/tmp/lib/Downloads"); Shot("d_review_folder");
vm.Review.SelectNoneCommand.Execute(null);
vm.Review.SelectFolderCommand.Execute(null); Shot("d_review_folder_selected");
vm.Review.AskExecuteCommand.Execute(null); Shot("d_confirm");
if (args.Length > 2 && args[^1] == "execute")
{
    vm.Review.ExecuteCommand.Execute(null);
    Pump(2500);
    Shot("d_after_execute");
    Console.WriteLine("after: " + vm.Review.Summary + " | toast: " + vm.Review.Toast);
}
else vm.Review.CancelExecuteCommand.Execute(null);
if (Environment.GetEnvironmentVariable("SHOT_ENCODE") == "1")
{
    vm.SelectedTab = 2; Pump(500);
    var loadStart = DateTime.UtcNow;
    while (vm.Encode.Loading && DateTime.UtcNow - loadStart < TimeSpan.FromMinutes(5)) Pump(200);
    Console.WriteLine($"encode list: {vm.Encode.Rows.Count} rows after {(DateTime.UtcNow - loadStart).TotalSeconds:0} s");
    vm.Encode.RowClicked(vm.Encode.Rows[0], false);
    vm.Encode.RowClicked(vm.Encode.Rows[4], true); Pump(300);
    Console.WriteLine("shift range picked: " + vm.Encode.Rows.Count(r => r.IsPicked));
    vm.Encode.RowClicked(vm.Encode.Rows[2], false); Pump(200);
    Console.WriteLine("after one click: " + vm.Encode.Rows.Count(r => r.IsPicked) + " | " + vm.Encode.PickedSize);
    vm.Encode.RowClicked(vm.Encode.Rows[2], false); Shot("d_encode");
    int shownBefore = vm.Encode.Rows.Count;
    vm.Encode.Rows[0].ExcludeFolderCommand.Execute(null); Pump(300);
    Console.WriteLine($"hide folder: {shownBefore} -> {vm.Encode.Rows.Count} rows, {vm.Encode.UnhideText}"); Shot("d_encode_hidden");
    vm.Encode.UnhideFoldersCommand.Execute(null); Pump(300);
    Console.WriteLine($"unhide: {vm.Encode.Rows.Count} rows");
    vm.Encode.Rows[1].DeleteCommand.Execute(null); Shot("d_encode_delete_confirm"); vm.Encode.CancelDeleteCommand.Execute(null);
    vm.Encode.AskCreateCommand.Execute(null); Shot("d_encode_confirm");
    vm.Encode.CreateCommand.Execute(null); Pump(300);
    Console.WriteLine("encode: " + vm.Encode.ResultText);
    vm.SelectedTab = MainViewModel.SettingsTab; Pump(1200);
    var editor = window.GetVisualDescendants().OfType<EncodeSettingsEditor>().First();
    var scroller = editor.GetVisualAncestors().OfType<Avalonia.Controls.ScrollViewer>().First();
    var pos = editor.TranslatePoint(new Avalonia.Point(0, 0), scroller.Content as Avalonia.Visual ?? scroller) ?? new Avalonia.Point(0, 0);
    scroller.Offset = new Avalonia.Vector(0, pos.Y - 60); Pump(600);
    vm.Settings.Encoding.Codec = EncodeOptions.Codecs[1]; Pump(300);
    Console.WriteLine($"x264 crf: {vm.Settings.Encoding.CrfValue} {vm.Settings.Encoding.QualityLabel}");
    vm.Settings.Encoding.Codec = EncodeOptions.Codecs[0]; Pump(300);
    Shot("d_enc_settings");
    Console.WriteLine($"x265 crf: {vm.Settings.Encoding.CrfValue} {vm.Settings.Encoding.QualityLabel}");
}
vm.SelectedTab = MainViewModel.SettingsTab; Pump(1500); Shot("d_settings");
vm.SelectedTab = 1; Shot("d_scan_scope");
vm.Settings.OpenFalsePositivesCommand.Execute(null); vm.SelectedTab = MainViewModel.SettingsTab; Pump(1500); Shot("d_false_positives");
vm.Settings.CloseFalsePositivesCommand.Execute(null);
// SHOT_FP=1: mark a copy as not a duplicate, then check that comparing again leaves it out too
// (writes falsepositives.json and results.json in the data folder - use a copy)
if (Environment.GetEnvironmentVariable("SHOT_FP") == "1")
{
    vm.SelectedTab = 0;
    controller.ReanalyzeFromDatabase();
    bool Together(int a, int b) => controller.Session.Results.Groups.Any(x => x.Members.Any(m => m.Id == a) && x.Members.Any(m => m.Id == b));
    var g = controller.Session.Results.Groups.First(x => x.Members.Count == 2);
    var copy = g.Members.First(m => m.Id != controller.Session.KeepId(g));
    var keptId = controller.Session.KeepId(g);
    Console.WriteLine($"fp: before marking, grouped together: {Together(copy.Id, keptId)}");
    Console.WriteLine("fp: " + controller.MarkFalsePositive(copy.Id));
    Console.WriteLine($"fp: right after marking: {Together(copy.Id, keptId)}");
    controller.ReanalyzeFromDatabase();
    Console.WriteLine($"fp: after compare again: {Together(copy.Id, keptId)}, pairs {FalsePositives.Count}");
    Shot("d_review_after_fp");
    vm.SelectedTab = MainViewModel.SettingsTab; vm.Settings.OpenFalsePositivesCommand.Execute(null); Pump(2000); Shot("d_false_positives_one");
}
// SHOT_PHASES=1: the scan screen with made-up progress (both phases), without running a scan
if (Environment.GetEnvironmentVariable("SHOT_PHASES") == "1")
{
    vm.SelectedTab = 1;
    var apply = typeof(ScanViewModel).GetMethod("Apply", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
    void Show(ScanProgress p, string name)
    {
        apply.Invoke(vm.Scan, new object[] { p });
        Console.WriteLine($"{name}: A {p.FindFraction:P0} '{ScanText.FindDetail(p)}' '{ScanText.FindLeft(p)}' | B {p.ProcessFraction:P0} '{ScanText.ProcessDetail(p)}' '{ScanText.ProcessLeft(p)}'");
        Shot(name);
    }
    Show(new ScanProgress { Phase = ScanPhase.FindingFiles, Message = "Finding video files", FilesFound = 22300, ExpectedFiles = 67569,
        FindRemaining = TimeSpan.FromMinutes(6.5), Elapsed = TimeSpan.FromMinutes(3.2) }, "d_phase_a");
    Show(new ScanProgress { Phase = ScanPhase.ReadingVideos, Message = "Reading new videos", FilesFound = 67612, ExpectedFiles = 67569, FilesListed = true,
        FindTime = TimeSpan.FromMinutes(7.4), Checked = 30000, NewTotal = 282, NewDone = 94, Remaining = TimeSpan.FromMinutes(2), SecondsPerNewFile = 0.6,
        ReadShare = 0.74, CompareEstimate = TimeSpan.FromSeconds(57),
        Elapsed = TimeSpan.FromMinutes(8.5) }, "d_phase_b");
    Show(new ScanProgress { Phase = ScanPhase.Comparing, Message = "Comparing videos", FilesFound = 67612, ExpectedFiles = 67569, FilesListed = true,
        FindTime = TimeSpan.FromMinutes(7.4), Checked = 67612, NewTotal = 282, NewDone = 282, ReadShare = 0.74, CompareFraction = 0.4,
        CompareEstimate = TimeSpan.FromSeconds(57), CompareRemaining = TimeSpan.FromSeconds(35), Elapsed = TimeSpan.FromMinutes(11) }, "d_phase_compare");
    Show(new ScanProgress { Phase = ScanPhase.FindingFiles, Message = "Finding video files", FilesFound = 1800, Elapsed = TimeSpan.FromSeconds(40) }, "d_phase_first");
}
// SHOT_COMPARE=1: how long comparing the whole library takes (the second run has the database cached)
if (Environment.GetEnvironmentVariable("SHOT_COMPARE") == "1")
{
    for (int run = 1; run <= 2; run++)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        controller.ReanalyzeFromDatabase();
        Console.WriteLine($"compare run {run}: {sw.Elapsed.TotalSeconds:0.0} s, {controller.Session.Results.VideosInScope:N0} videos");
    }
    var media = ScanEngine.InScope(ScanEngine.LoadAllCached(), controller.Settings);
    var steps = new System.Collections.Generic.List<double>();
    var t = System.Diagnostics.Stopwatch.StartNew();
    Analyzer.Analyze(media, controller.Settings, f => steps.Add(f));
    Console.WriteLine($"analyze only: {t.Elapsed.TotalSeconds:0.0} s for {media.Count:N0} videos, {steps.Count} progress reports, last {(steps.Count > 0 ? steps[^1] : 0):P0}");
}
// SHOT_READONLY=1: a read-only file can be deleted (File.Delete alone says "Access to the path is denied")
if (Environment.GetEnvironmentVariable("SHOT_READONLY") == "1")
{
    string ro = Path.Combine(output, "readonly-test.mp4");
    File.WriteAllText(ro, "x");
    File.SetAttributes(ro, FileAttributes.ReadOnly);
    try { VideoFile.Delete(ro); Console.WriteLine("read-only delete: " + (File.Exists(ro) ? "still there" : "deleted")); }
    catch (Exception e) { Console.WriteLine("read-only delete failed: " + e.Message); }
}
// SHOT_FILES=1: the All files screen, a search, and how long searching takes over all files
if (Environment.GetEnvironmentVariable("SHOT_FILES") == "1")
{
    vm.SelectedTab = MainViewModel.FilesTab; Pump(500);
    var start = DateTime.UtcNow;
    while ((vm.Files.Loading || vm.Files.Rows.Count == 0) && DateTime.UtcNow - start < TimeSpan.FromMinutes(5)) Pump(200);
    Console.WriteLine($"all files: {vm.Files.ResultText} after {(DateTime.UtcNow - start).TotalSeconds:0} s");
    Shot("d_files");
    var index = controller.AllFiles();
    foreach (var q in new[] { "dji", "dji 2024 -0002", "\"lisa mc\" -cirkler", "mp4 -dji -3xfeed", "xyz-nothing" })
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int n = 0;
        for (int r = 0; r < 20; r++) n = index.Search(q).Count;
        Console.WriteLine($"search '{q}': {n:N0} of {index.Count:N0} in {sw.Elapsed.TotalMilliseconds / 20:0.0} ms");
    }
    vm.Files.Search = "dji 2024 -0002"; Pump(800);
    Console.WriteLine($"after typing: {vm.Files.ResultText}");
    Shot("d_files_search");
}
