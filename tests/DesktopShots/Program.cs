using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
// SHOT_SIZE=1320x860 sets the window size (layout bugs depend on it)
var size = (Environment.GetEnvironmentVariable("SHOT_SIZE") ?? "1440x900").Split('x');
var window = new MainWindow { DataContext = vm, Width = int.Parse(size[0]), Height = int.Parse(size[1]) };
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

// SHOT_JUMP=1 (with a data folder whose output/results.json has groups of 3 with long reasons, keep = the 2nd copy): selects and
// unselects the kept copy and prints how far its card moves in the list - it should not move at all
if (Environment.GetEnvironmentVariable("SHOT_JUMP") == "1")
{
    vm.SelectedTab = MainViewModel.ReviewTab; Pump(800);
    var list = window.GetVisualDescendants().OfType<Avalonia.Controls.ListBox>().First(l => l.Classes.Contains("rows"));
    var rows = vm.Review.Rows.OfType<VideoRow>().ToList();
    // A group in the middle of the list, scrolled to the middle of the view
    var target = rows.Where(r => r.IsKept).Skip(6).First();
    list.ScrollIntoView(vm.Review.Rows.IndexOf(target)); Pump(500);
    var group = rows.Where(r => controller.Session.Results.Groups.First(g => g.Members.Contains(target.Entry)).Members.Contains(r.Entry)).ToList();
    double Y(VideoRow r) => list.ContainerFromItem(r) is Avalonia.Controls.Control c ? c.TranslatePoint(new Point(0, 0), list)?.Y ?? double.NaN : double.NaN;
    double H(VideoRow r) => list.ContainerFromItem(r)?.Bounds.Height ?? double.NaN;
    void Step(string what, Action act)
    {
        double y0 = Y(target), lh0 = list.Bounds.Height; var h0 = group.Select(H).ToArray();
        act(); Pump(500);
        double y1 = Y(target), lh1 = list.Bounds.Height; var h1 = group.Select(H).ToArray();
        Console.WriteLine($"[jump] {what}: card moved {y1 - y0:+0.0;-0.0;0}px (y {y0:0} -> {y1:0}), list height {lh0:0} -> {lh1:0}, " +
            $"card heights {string.Join("/", h0.Select(h => h.ToString("0")))} -> {string.Join("/", h1.Select(h => h.ToString("0")))}, toast: {vm.Review.Toast ?? "-"}");
    }
    if (Environment.GetEnvironmentVariable("SHOT_PROBE") == "1")
    {
        var dividerItem = list.GetRealizedContainers().First(c => c.DataContext is MatchDividerRow);
        var button = dividerItem.GetVisualDescendants().OfType<Avalonia.Controls.Button>().First();
        Console.WriteLine($"[probe] divider item bounds {dividerItem.Bounds}, z {dividerItem.ZIndex}, button bounds {button.Bounds}, visible {button.IsEffectivelyVisible}");
        foreach (var v in button.GetVisualAncestors().TakeWhile(a => a != list)) Console.WriteLine($"[probe]   {v.GetType().Name} clip={v.ClipToBounds} bounds={v.Bounds}");
        Console.WriteLine($"[probe] colours: see App.axaml");
    }
    Shot("j_before");
    Step("select the kept copy (keep moves to the copy above)", () => target.IsSelected = true);
    Step("unselect it again", () => target.IsSelected = false);
    Step("select the other two", () => { foreach (var r in group.Where(r => r != target)) r.IsSelected = true; });
    Step("select the kept copy too (every copy)", () => target.IsSelected = true);
    Shot("j_doomed");
    Step("unselect it again", () => target.IsSelected = false);
    return;
}
// SHOT_HIDDEN=1 (with SHOT_FFMPEG=<folder with ffmpeg and ffprobe>, and an empty data folder): Manage hidden folders in Re-encode
// planning, on six different clips in nested folders - hide a folder, then its parent folder, unhide one, and the list after a restart
if (Environment.GetEnvironmentVariable("SHOT_HIDDEN") == "1")
{
    string ffSource4 = Environment.GetEnvironmentVariable("SHOT_FFMPEG")!;
    if (!File.Exists(FileHandler.FFmpegPath))
    {
        Directory.CreateDirectory(Path.Combine(FileHandler.AppFolder, "ffmpeg"));
        foreach (var f in Directory.GetFiles(ffSource4)) File.Copy(f, Path.Combine(FileHandler.AppFolder, "ffmpeg", Path.GetFileName(f)), true);
    }
    string lib = Path.Combine(FileHandler.AppFolder, "videos");
    var clips = new[] { ("Shows/Season 1", "testsrc"), ("Shows/Season 1", "testsrc2"), ("Shows/Season 2", "smptebars"), ("Shows/Season 2", "rgbtestsrc"), ("Movies", "mandelbrot"), ("Movies", "yuvtestsrc") };
    var maker4 = new ProcessEncodeRunner();
    for (int n = 0; n < clips.Length; n++)
    {
        string folder = Path.Combine(lib, clips[n].Item1.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(folder);
        string clip = Path.Combine(folder, $"{clips[n].Item2} {n + 1}.mp4");
        if (File.Exists(clip)) continue;
        Task.Run(() => maker4.RunAsync(FileHandler.FFmpegPath, new[] { "-hide_banner", "-y", "-f", "lavfi", "-i", $"{clips[n].Item2}=duration=30:size=640x360:rate=25",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-pix_fmt", "yuv420p", clip }, _ => { }, _ => { }, CancellationToken.None)).GetAwaiter().GetResult();
    }
    foreach (var f in Directory.GetFiles(lib, "*.mp4", SearchOption.AllDirectories)) File.SetLastWriteTime(f, DateTime.Now.AddHours(-2));
    var st4 = controller.Settings.Clone();
    st4.contentFolders = new[] { lib };
    controller.SaveSettings(st4, PathMapper.Mappings);
    controller.StartScan();
    var t4 = DateTime.UtcNow;
    while ((controller.Scanner.IsRunning || controller.Scanner.Current.Phase == ScanPhase.Idle) && DateTime.UtcNow - t4 < TimeSpan.FromMinutes(3)) Pump(200);
    vm.SelectedTab = MainViewModel.EncodeTab; Pump(800);
    var l4 = DateTime.UtcNow;
    while (vm.Encode.Loading && DateTime.UtcNow - l4 < TimeSpan.FromMinutes(2)) Pump(200);
    vm.Encode.MinMb = 0; Pump(300);
    Console.WriteLine($"[hidden] {vm.Encode.Rows.Count} videos shown, button: {vm.Encode.ManageHiddenText}");
    var season1 = vm.Encode.Rows.First(r => r.Folder.EndsWith("Season 1"));
    season1.ExcludeFolderCommand.Execute(null); Pump(300);
    Console.WriteLine($"[hidden] Exclude folder on {season1.Folder}: {vm.Encode.Rows.Count} shown, {vm.Encode.ManageHiddenText}");
    vm.Encode.ManageHiddenCommand.Execute(null);
    vm.Encode.NewHiddenFolder = Path.Combine(lib, "Shows");   // the parent folder: Season 2 goes too
    vm.Encode.AddHiddenCommand.Execute(null); Pump(400);
    Console.WriteLine($"[hidden] parent added: {vm.Encode.Rows.Count} shown ({string.Join(", ", vm.Encode.Rows.Select(r => r.FileName))}); list: {string.Join(" | ", vm.Encode.HiddenList.Select(h => h.Folder + " = " + h.Videos))}");
    Shot("h_manage");
    Console.WriteLine($"[hidden] after a restart the list holds: {string.Join(" | ", new HiddenFolders(Path.Combine(FileHandler.AppFolder, "output", "hiddenfolders.json")).List)}");
    vm.Encode.HiddenList.First(h => h.Folder.EndsWith("Shows")).UnhideCommand.Execute(null); Pump(300);
    Console.WriteLine($"[hidden] parent unhidden: {vm.Encode.Rows.Count} shown, {vm.Encode.ManageHiddenText}");
    vm.Encode.UnhideAllCommand.Execute(null); Pump(300);
    Console.WriteLine($"[hidden] unhide all: {vm.Encode.Rows.Count} shown, {vm.Encode.ManageHiddenText}");
    Shot("h_manage_empty");
    vm.Encode.CloseHiddenCommand.Execute(null);
    return;
}
// SHOT_FAILED=1 (with SHOT_FFMPEG=<folder with ffmpeg and ffprobe>, and an empty data folder): a scan with files that cannot be read -
// text named .mp4, a cut-off video - and a short one that can (it is not "could not read"); the list behind the "i"
if (Environment.GetEnvironmentVariable("SHOT_FAILED") == "1")
{
    string ff5 = Environment.GetEnvironmentVariable("SHOT_FFMPEG")!;
    if (!File.Exists(FileHandler.FFmpegPath))
    {
        Directory.CreateDirectory(Path.Combine(FileHandler.AppFolder, "ffmpeg"));
        foreach (var f in Directory.GetFiles(ff5)) File.Copy(f, Path.Combine(FileHandler.AppFolder, "ffmpeg", Path.GetFileName(f)), true);
    }
    string dir5 = Path.Combine(FileHandler.AppFolder, "videos");
    Directory.CreateDirectory(dir5);
    var maker5 = new ProcessEncodeRunner();
    void Make(string name, string src, int seconds) => Task.Run(() => maker5.RunAsync(FileHandler.FFmpegPath, new[] { "-hide_banner", "-y", "-f", "lavfi", "-i", $"{src}=duration={seconds}:size=640x360:rate=25",
        "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", Path.Combine(dir5, name) }, _ => { }, _ => { }, CancellationToken.None)).GetAwaiter().GetResult();
    Make("Good one.mp4", "testsrc", 30); Make("Good two.mp4", "smptebars", 30); Make("Short.mp4", "testsrc2", 2);
    Make("whole.mp4", "testsrc2", 30);
    var whole = File.ReadAllBytes(Path.Combine(dir5, "whole.mp4"));
    File.WriteAllBytes(Path.Combine(dir5, "Cut off download.mp4"), whole[..(whole.Length / 3)]);
    File.Delete(Path.Combine(dir5, "whole.mp4"));
    File.WriteAllText(Path.Combine(dir5, "Not really a video.mp4"), "This is a text file with a video extension.");
    foreach (var f in Directory.GetFiles(dir5)) File.SetLastWriteTime(f, DateTime.Now.AddHours(-2));
    var st5 = controller.Settings.Clone();
    st5.contentFolders = new[] { dir5 };
    controller.SaveSettings(st5, PathMapper.Mappings);
    vm.SelectedTab = MainViewModel.ScanTab;
    vm.Scan.StartCommand.Execute(null);
    var t5 = DateTime.UtcNow;
    while ((controller.Scanner.IsRunning || controller.Scanner.Current.Phase == ScanPhase.Idle) && DateTime.UtcNow - t5 < TimeSpan.FromMinutes(3)) Pump(200);
    Pump(800);
    Console.WriteLine($"[failed] could not read: {vm.Scan.Failed}, icon shown: {vm.Scan.HasFailedFiles}");
    foreach (var f in vm.Scan.FailedFiles) Console.WriteLine($"[failed]   {f.FileName}: {f.Reason}");
    Shot("f_scan");
    vm.Scan.ShowFailedCommand.Execute(null); Shot("f_list");
    vm.Scan.CloseFailedCommand.Execute(null);
    return;
}
// SHOT_DRAG=1 (with a data folder that SHOT_HIDDEN has scanned): the waiting videos are reordered by dragging their grip with the
// mouse, in the full and in the compact view; prints the engine's order after each drag
if (Environment.GetEnvironmentVariable("SHOT_DRAG") == "1")
{
    controller.Encoder.Stop();   // nothing starts: every video stays waiting
    controller.Encoder.ClearQueued();
    var cands = controller.EncodeCandidates();
    controller.EnqueueEncode(cands);
    vm.SelectedTab = MainViewModel.QueueTab; Pump(800);
    string Order() => string.Join(", ", controller.Encoder.Current.Items.Where(i => i.Status == EncodeItemStatus.Queued).Select(i => Path.GetFileNameWithoutExtension(i.Path)));
    Console.WriteLine($"[drag] start: {Order()}");
    void Drag(int from, int to, bool below, string shot)
    {
        var list = window.GetVisualDescendants().OfType<Avalonia.Controls.ListBox>().First(l => l.IsEffectivelyVisible && l.ItemsSource == vm.EncodeQueue.Waiting);
        var grips = list.GetVisualDescendants().OfType<Avalonia.Controls.Border>().Where(b => b.Classes.Contains("grip")).ToList();
        var rows = list.GetRealizedContainers().ToList();
        Point Center(Avalonia.Visual v) => v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), window)!.Value;
        var start = Center(grips[from]);
        var target = rows[to];
        var top = target.TranslatePoint(new Point(0, 0), window)!.Value;
        var end = new Point(start.X, top.Y + (below ? target.Bounds.Height * 0.8 : target.Bounds.Height * 0.2));
        window.MouseDown(start, Avalonia.Input.MouseButton.Left); Pump(50);
        for (int s = 1; s <= 8; s++) { window.MouseMove(new Point(start.X, start.Y + (end.Y - start.Y) * s / 8)); Pump(30); }
        Shot(shot);
        Pump(300);   // the cards that make room glide there
        Console.WriteLine($"[drag] while dragging: {string.Join(", ", list.GetRealizedContainers().Select(c => Path.GetFileNameWithoutExtension(((EncodeQueueRow)c.DataContext!).FileName) + ((c.RenderTransform as Avalonia.Media.TranslateTransform)?.Y is double y && Math.Abs(y) > 0.5 ? $" ({y:+0;-0})" : "") + (((EncodeQueueRow)c.DataContext!).IsDragged ? " [dragged]" : "")))}");
        window.MouseUp(end, Avalonia.Input.MouseButton.Left); Pump(400);
        Console.WriteLine($"[drag] row {from + 1} to {(below ? "below" : "above")} row {to + 1}: {Order()} | shown: {string.Join(", ", vm.EncodeQueue.Waiting.Select(r => Path.GetFileNameWithoutExtension(r.FileName)))}");
    }
    Drag(0, 2, below: true, "g_drag_full");
    Drag(4, 0, below: false, "g_drag_full2");
    vm.EncodeQueue.Compact = true; Pump(600);
    Drag(1, 3, below: true, "g_drag_compact");
    if (Environment.GetEnvironmentVariable("SHOT_DRAG_KEEP") != "1") controller.Encoder.ClearQueued();   // keep: for trying the web page on it
    controller.Encoder.Shutdown();
    return;
}
// SHOT_DOOMED=1 (with SHOT_FFMPEG=<folder with ffmpeg and ffprobe>, and an empty data folder): selecting BOTH copies of a video - the original
// and the duplicate - is allowed; both cards turn red, a warning toast comes, and the confirmation says every copy goes
if (Environment.GetEnvironmentVariable("SHOT_DOOMED") == "1")
{
    string ffSource3 = Environment.GetEnvironmentVariable("SHOT_FFMPEG")!;
    if (!File.Exists(FileHandler.FFmpegPath))
    {
        Directory.CreateDirectory(Path.Combine(FileHandler.AppFolder, "ffmpeg"));
        foreach (var f in Directory.GetFiles(ffSource3)) File.Copy(f, Path.Combine(FileHandler.AppFolder, "ffmpeg", Path.GetFileName(f)), true);
    }
    string pair = Path.Combine(FileHandler.AppFolder, "videos");
    Directory.CreateDirectory(pair);
    string original = Path.Combine(pair, "Holiday original.mp4"), copy = Path.Combine(pair, "Holiday original - copy.mp4");
    var maker3 = new ProcessEncodeRunner();
    Task.Run(() => maker3.RunAsync(FileHandler.FFmpegPath, new[] { "-hide_banner", "-y", "-f", "lavfi", "-i", "testsrc2=duration=30:size=1280x720:rate=25",
        "-f", "lavfi", "-i", "sine=duration=30", "-shortest", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "30", "-pix_fmt", "yuv420p", "-c:a", "aac", original },
        _ => { }, _ => { }, CancellationToken.None)).GetAwaiter().GetResult();
    File.Copy(original, copy, true);
    foreach (var f in Directory.GetFiles(pair)) File.SetLastWriteTime(f, DateTime.Now.AddHours(-2));
    var st3 = controller.Settings.Clone();
    st3.contentFolders = new[] { pair };
    controller.SaveSettings(st3, PathMapper.Mappings);
    controller.StartScan();
    var t3 = DateTime.UtcNow;
    while ((controller.Scanner.IsRunning || controller.Scanner.Current.Phase == ScanPhase.Idle) && DateTime.UtcNow - t3 < TimeSpan.FromMinutes(3)) Pump(200);
    Pump(800);
    vm.SelectedTab = MainViewModel.ReviewTab; Pump(800);
    var cards = vm.Review.Rows.OfType<VideoRow>().ToList();
    Console.WriteLine($"[doomed] {cards.Count} copies in the group: {string.Join(", ", cards.Select(c => c.FileName + (c.IsKept ? " (keep)" : "")))}");
    var keepCard = cards.First(c => c.IsKept); var dupeCard = cards.First(c => !c.IsKept);
    dupeCard.IsSelected = true; Pump(400);
    Console.WriteLine($"[doomed] only the duplicate selected: toast = {(vm.Review.Toast ?? "(none)")}, red cards = {cards.Count(c => c.IsDoomed)}, keep mark on the original = {keepCard.IsKept}");
    keepCard.IsSelected = true; Pump(500);
    Console.WriteLine($"[doomed] the original selected too: selected = {cards.Count(c => c.IsSelected)}, red cards = {cards.Count(c => c.IsDoomed)}, keep marks = {cards.Count(c => c.IsKept)}");
    Console.WriteLine($"[doomed] toast: {vm.Review.Toast}");
    Shot("d_doomed");
    vm.Review.AskExecuteCommand.Execute(null); Pump(400);
    Console.WriteLine($"[doomed] confirmation: {vm.Review.ConfirmTitle} | {vm.Review.ConfirmText}");
    Shot("d_doomed_confirm");
    // SHOT_DOOMED_DELETE=1: really delete both (they are the test copies in the data folder)
    if (Environment.GetEnvironmentVariable("SHOT_DOOMED_DELETE") == "1")
    {
        vm.Review.ExecuteCommand.Execute(null);
        var until = DateTime.UtcNow.AddSeconds(30);
        while ((vm.Review.Busy || Directory.GetFiles(pair).Length > 0) && DateTime.UtcNow < until) Pump(200);
        Pump(800);
        Console.WriteLine($"[doomed] deleted both: files left in the folder = {Directory.GetFiles(pair).Length}, groups left = {controller.Session.Results.Groups.Count}, toast = {vm.Review.Toast}");
        return;
    }
    vm.Review.CancelExecuteCommand.Execute(null); Pump(300);
    dupeCard.IsSelected = false; Pump(400);
    Console.WriteLine($"[doomed] the duplicate unselected again: red cards = {cards.Count(c => c.IsDoomed)}, keep marks = {cards.Count(c => c.IsKept)} (on {cards.FirstOrDefault(c => c.IsKept)?.FileName})");
    return;
}
// SHOT_SCANTHUMBS=1 (with SHOT_FFMPEG=<folder with ffmpeg and ffprobe>, and an empty data folder): the live pictures on the Scan screen -
// each has a play button (it opens the video) - made by really scanning a few clips
if (Environment.GetEnvironmentVariable("SHOT_SCANTHUMBS") == "1")
{
    string ffSource2 = Environment.GetEnvironmentVariable("SHOT_FFMPEG")!;
    if (!File.Exists(FileHandler.FFmpegPath))
    {
        Directory.CreateDirectory(Path.Combine(FileHandler.AppFolder, "ffmpeg"));
        foreach (var f in Directory.GetFiles(ffSource2)) File.Copy(f, Path.Combine(FileHandler.AppFolder, "ffmpeg", Path.GetFileName(f)), true);
    }
    string clips = Path.Combine(FileHandler.AppFolder, "videos");
    Directory.CreateDirectory(clips);
    var maker = new ProcessEncodeRunner();
    for (int n = 0; n < 4; n++)
    {
        string clip = Path.Combine(clips, $"Holiday clip {n + 1}.mp4");
        if (File.Exists(clip)) continue;
        int seconds = 20 + n * 5;
        Task.Run(() => maker.RunAsync(FileHandler.FFmpegPath, new[] { "-hide_banner", "-y", "-f", "lavfi", "-i", $"testsrc2=duration={seconds}:size=1280x720:rate=25",
            "-f", "lavfi", "-i", "sine=duration=" + seconds, "-shortest", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "30", "-pix_fmt", "yuv420p", "-c:a", "aac", clip },
            _ => { }, _ => { }, CancellationToken.None)).GetAwaiter().GetResult();
    }
    foreach (var f in Directory.GetFiles(clips)) File.SetLastWriteTime(f, DateTime.Now.AddHours(-2));
    var st = controller.Settings.Clone();
    st.contentFolders = new[] { clips };
    controller.SaveSettings(st, PathMapper.Mappings);
    vm.SelectedTab = MainViewModel.ScanTab;
    vm.Scan.StartCommand.Execute(null);
    var t0 = DateTime.UtcNow;
    while (vm.Scan.Recent.Count < 3 && controller.Scanner.IsRunning && DateTime.UtcNow - t0 < TimeSpan.FromMinutes(2)) Pump(200);
    Pump(500); Shot("d_scan_thumbs");
    while (controller.Scanner.IsRunning && DateTime.UtcNow - t0 < TimeSpan.FromMinutes(3)) Pump(200);
    Pump(500);
    Console.WriteLine($"[thumbs] the pictures: {vm.Scan.Recent.Count}; names: {string.Join(", ", vm.Scan.Recent.Select(t => t.Name))}");
    Console.WriteLine($"[thumbs] marked new: {string.Join(", ", vm.Scan.Recent.Select(t => t.IsNew))}");
    var first = vm.Scan.Recent[0];
    Console.WriteLine($"[thumbs] newest: {first.Name}, play button ready: {first.Play.CanExecute(null)}, path is the file on disk: {File.Exists(first.VideoPath)}");
    var shown = controller.Scanner.Current.LastVideo;
    var found = shown == null ? null : controller.FindEntry(shown.Id);
    Console.WriteLine($"[thumbs] the web player can ask for it (video/{shown?.Id}): {(found != null && found.Path == shown!.Path ? "OK" : "FAILED")}; {found?.FileName}, {found?.DurationSeconds} s");
    vm.SelectedTab = MainViewModel.ScanTab; Pump(500); Shot("d_scan_thumbs_done");
    // A second scan: every video is in the database already, none is read again - the Scan screen still shows a picture at once
    vm.Scan.StartCommand.Execute(null);
    var t1 = DateTime.UtcNow;
    while (controller.Scanner.IsRunning && DateTime.UtcNow - t1 < TimeSpan.FromMinutes(1)) Pump(100);
    Pump(500);
    Console.WriteLine($"[thumbs] second scan, nothing read again (new videos read: {controller.Scanner.Current.NewDone}): pictures on the Scan screen = {vm.Scan.Recent.Count} ({(vm.Scan.Recent.Count >= 1 ? "OK" : "FAILED")}), first: {vm.Scan.Recent.FirstOrDefault()?.Name}");
    vm.SelectedTab = MainViewModel.ScanTab; Pump(500); Shot("d_scan_thumbs_cached");
    return;
}
// SHOT_QUEUE=1 (with SHOT_FFMPEG=<folder with ffmpeg and ffprobe>, and an empty data folder): the whole encode queue for real -
// makes a few clips, scans them, adds some to the queue in the Re-encode planning screen, then adds more while it runs
if (Environment.GetEnvironmentVariable("SHOT_QUEUE") == "1")
{
    string ffSource = Environment.GetEnvironmentVariable("SHOT_FFMPEG")!;
    string ffTarget = Path.Combine(FileHandler.AppFolder, "ffmpeg");
    if (!File.Exists(FileHandler.FFmpegPath))
    {
        Directory.CreateDirectory(ffTarget);
        foreach (var f in Directory.GetFiles(ffSource)) File.Copy(f, Path.Combine(ffTarget, Path.GetFileName(f)), true);
    }
    string videos = Path.Combine(FileHandler.AppFolder, "videos");
    Directory.CreateDirectory(videos);
    var gen = new ProcessEncodeRunner();
    int[] lengths = { 140, 120, 100, 110, 130, 105 };
    for (int n = 0; n < lengths.Length; n++)
    {
        string clip = Path.Combine(videos, $"Holiday clip {n + 1}.mp4");
        if (File.Exists(clip)) continue;
        int seconds = lengths[n];
        // Off the UI thread: waiting for an async call on it would deadlock under Avalonia's synchronization context
        Task.Run(() => gen.RunAsync(FileHandler.FFmpegPath, new[] { "-hide_banner", "-y", "-f", "lavfi", "-i", $"testsrc2=duration={seconds}:size=1280x720:rate=25",
            "-f", "lavfi", "-i", "sine=duration=" + seconds, "-shortest", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "30", "-pix_fmt", "yuv420p", "-c:a", "aac", clip },
            _ => { }, _ => { }, CancellationToken.None)).GetAwaiter().GetResult();
    }
    string bad = Path.Combine(videos, "Broken download.mp4");
    File.WriteAllText(bad, "this is not a video");
    // The scan leaves out files that were changed in the last minutes ("still being written")
    foreach (var f in Directory.GetFiles(videos)) File.SetLastWriteTime(f, DateTime.Now.AddHours(-2));
    Console.WriteLine("clips made");

    var settings = controller.Settings.Clone();
    settings.contentFolders = new[] { videos };
    settings.encodeSettings = new EncodeSettings { codec = "x264", preset = "veryfast", resolution = 720, crf = 24, container = "mp4" };
    controller.SaveSettings(settings, PathMapper.Mappings);
    controller.StartScan();
    var scanStart = DateTime.UtcNow;
    while ((controller.Scanner.IsRunning || controller.Scanner.Current.Phase == ScanPhase.Idle) && DateTime.UtcNow - scanStart < TimeSpan.FromMinutes(3)) Pump(200);
    Console.WriteLine("scan: " + controller.Scanner.Current.Phase + " " + controller.Scanner.Current.Message);

    // SHOT_CHECK=1: earlier results are there already - two good ones, one cut off - plus a video that is encoded now.
    // The new files are checked (length, pictures like the scan's), then the original of a good one is deleted and the cut-off one replaced.
    if (Environment.GetEnvironmentVariable("SHOT_CHECK") == "1")
    {
        var cands = controller.EncodeCandidates();
        var es = settings.EncodeOrDefault();
        for (int n = 0; n < 3; n++)
        {
            string target = EncodePlanner.OutputPath(cands[n].Path, es);
            var made = Task.Run(() => gen.RunAsync(FileHandler.FFmpegPath, EncodePlanner.Arguments(cands[n].Path, es, target), _ => { }, _ => { }, CancellationToken.None)).GetAwaiter().GetResult();
            Console.WriteLine($"earlier result {n}: ffmpeg {made}, {new FileInfo(target).Length:N0} bytes");
        }
        string cutOff = EncodePlanner.OutputPath(cands[2].Path, es);
        var all = File.ReadAllBytes(cutOff);
        File.WriteAllBytes(cutOff, all[..(all.Length * 6 / 10)]);
        foreach (var f in Directory.GetFiles(videos, "*_720p_x264.mp4")) File.SetLastWriteTime(f, DateTime.Now.AddHours(-2));

        vm.SelectedTab = MainViewModel.EncodeTab; Pump(500);
        var waitLoad = DateTime.UtcNow;
        while ((vm.Encode.Loading || vm.Encode.Rows.Count == 0) && DateTime.UtcNow - waitLoad < TimeSpan.FromMinutes(1)) Pump(200);
        vm.Encode.MinMb = 0; vm.Encode.MinKbps = 0; Pump(300);
        vm.Encode.RowClicked(vm.Encode.Rows[0], false); vm.Encode.RowClicked(vm.Encode.Rows[3], true); Pump(300);
        vm.Encode.PrimaryCommand.Execute(null);
        var wait = DateTime.UtcNow;
        while (controller.Encoder.Current.PendingCount > 0 && DateTime.UtcNow - wait < TimeSpan.FromMinutes(3)) Pump(100);
        Pump(800);
        vm.SelectedTab = MainViewModel.QueueTab; Pump(500); Shot("c_finished");
        foreach (var i in controller.Encoder.Current.Items) Console.WriteLine($"  {i.FileName}: {EncodeText.Status(i)} | check: {i.Check}");

        var q = vm.EncodeQueue;
        var good = q.Finished.First(r => r.CanDeleteOriginal);
        good.AskDeleteOriginalCommand.Execute(null); Pump(300); Shot("c_confirm_delete");
        Console.WriteLine("confirm: " + q.ConfirmTitle + " | " + q.ConfirmText.Replace("\n", " / ") + " | button " + q.ConfirmButton);
        q.ConfirmYesCommand.Execute(null); Pump(1500);
        Console.WriteLine($"after delete: toast '{q.Toast}', original still there: {File.Exists(good.OriginalPath)}, new file there: {File.Exists(good.NewPath)}");
        Shot("c_after_delete");

        var incomplete = q.Finished.FirstOrDefault(r => r.CanReplace);
        Console.WriteLine("incomplete row found: " + (incomplete != null));
        if (incomplete != null)
        {
            incomplete.AskReplaceCommand.Execute(null); Pump(300); Shot("c_confirm_replace");
            q.ConfirmYesCommand.Execute(null);
            wait = DateTime.UtcNow;
            while (controller.Encoder.Current.PendingCount > 0 || controller.Encoder.IsRunning) { Pump(100); if (DateTime.UtcNow - wait > TimeSpan.FromMinutes(2)) break; }
            Pump(800); Shot("c_after_replace");
        }
        // Delete the NEW file of another video (the original is kept); the row whose original was deleted must not offer it
        var originalGone = q.Finished.First(r => r.StatusText.Contains("original has been deleted"));
        Console.WriteLine($"row whose original was deleted offers delete-new: {originalGone.CanDeleteNew}");
        var other = q.Finished.First(r => r.CanDeleteNew && r.CanDeleteOriginal);
        Console.WriteLine($"saving shown for {other.FileName}: {other.SavingText} / {other.SavingDetail}");
        other.AskDeleteNewCommand.Execute(null); Pump(300); Shot("c_confirm_delete_new");
        Console.WriteLine("confirm: " + q.ConfirmTitle + " | " + q.ConfirmText.Replace("\n", " / "));
        q.ConfirmYesCommand.Execute(null); Pump(1500);
        Console.WriteLine($"after delete new: toast '{q.Toast}', new file there: {File.Exists(other.NewPath)}, original there: {File.Exists(other.OriginalPath)}, still listed: {q.Finished.Contains(other)}");
        Shot("c_after_delete_new");
        // The original of "originalGone" was deleted: its new file must still have play + delete. Delete that last copy too.
        Console.WriteLine($"after deleting the original: original available {originalGone.OriginalAvailable}, can delete original {originalGone.CanDeleteOriginal}; new file available {originalGone.NewAvailable}, can delete new {originalGone.CanDeleteNew}");
        Shot("c_one_copy_left");
        originalGone.AskDeleteNewCommand.Execute(null); Pump(300); Shot("c_confirm_last_copy");
        Console.WriteLine("last copy dialog: " + q.ConfirmTitle + " | button " + q.ConfirmButton);
        q.ConfirmYesCommand.Execute(null); Pump(1500);
        Console.WriteLine($"after deleting the last copy: toast '{q.Toast}', row still listed: {q.Finished.Contains(originalGone)}, can delete original {originalGone.CanDeleteOriginal}, can delete new {originalGone.CanDeleteNew}, both greyed: {originalGone.OriginalDeleted && originalGone.NewDeleted}");
        Shot("c_both_gone");
        q.Compact = true; Pump(500); Shot("c_compact_deleted"); q.Compact = false; Pump(300);
        // "Confirm before deleting" off: no dialog, the file goes at once
        Console.WriteLine($"confirm before deleting is on by default: {q.ConfirmDeletes}");
        q.ConfirmDeletes = false; Pump(300); Shot("c_confirm_option_off");
        var quick = q.Finished.First(r => r.CanDeleteNew);
        quick.AskDeleteNewCommand.Execute(null); Pump(1500);
        Console.WriteLine($"with confirmation off: dialog shown {q.ConfirmVisible}, new file gone {!File.Exists(quick.NewPath)}, toast '{q.Toast}'");
        q.ConfirmDeletes = true;
        var again = q.Finished.First(r => r.CanDeleteNew);
        again.AskDeleteNewCommand.Execute(null); Pump(300);
        Console.WriteLine($"with confirmation on again: dialog shown {q.ConfirmVisible}");
        q.ConfirmNoCommand.Execute(null);
        foreach (var i in controller.Encoder.Current.Items) Console.WriteLine($"  final {i.FileName}: {EncodeText.Status(i)}");
        vm.SelectedTab = MainViewModel.EncodeTab; Pump(800); Shot("c_picker_after");
        Console.WriteLine($"picker rows after deleting an original: {vm.Encode.Rows.Count}");
        controller.Encoder.Shutdown();
        return;
    }
    vm.SelectedTab = MainViewModel.QueueTab; Pump(500); Shot("q_empty");
    vm.SelectedTab = MainViewModel.EncodeTab; Pump(500);
    var loadWait = DateTime.UtcNow;
    while ((vm.Encode.Loading || vm.Encode.Rows.Count == 0) && DateTime.UtcNow - loadWait < TimeSpan.FromMinutes(1)) Pump(200);
    vm.Encode.MinMb = 0; vm.Encode.MinKbps = 0; Pump(300);
    Console.WriteLine($"picker: {vm.Encode.Rows.Count} videos, button '{vm.Encode.PrimaryText}'");
    vm.Encode.RowClicked(vm.Encode.Rows[0], false);
    vm.Encode.RowClicked(vm.Encode.Rows[2], true); Pump(300); Shot("q_picked");

    vm.Encode.PrimaryCommand.Execute(null); Pump(400);
    Console.WriteLine("after add: " + vm.Encode.Toast + " | " + vm.Encode.QueueSummary + " | queued rows " + vm.Encode.Rows.Count(r => r.IsQueued));
    // A click on a queued row does nothing
    vm.Encode.RowClicked(vm.Encode.Rows[0], false);
    Console.WriteLine("click on queued row picks it: " + vm.Encode.Rows[0].IsPicked);
    Shot("q_added");

    // More, while the first batch runs: a video that cannot be encoded, and the rest of the list
    controller.Encoder.Enqueue(new[] { new EncodeCandidate { Id = 99999, Path = bad, SizeBytes = 20, DurationSeconds = 30 } }, settings.EncodeOrDefault());
    foreach (var row in vm.Encode.Rows.Where(r => !r.IsQueued)) vm.Encode.RowClicked(row, false);
    vm.Encode.PrimaryCommand.Execute(null); Pump(400);
    Console.WriteLine("second add: " + vm.Encode.Toast);

    var run = DateTime.UtcNow;
    while (!(controller.Encoder.Current.Current?.Fraction > 0.3) && DateTime.UtcNow - run < TimeSpan.FromMinutes(2)) Pump(100);
    vm.SelectedTab = MainViewModel.QueueTab; Pump(300); Shot("q_running");
    // Compact view (a table, no pictures; the running video keeps its progress bar), and a collapsed list
    vm.EncodeQueue.Compact = true; Pump(400); Shot("q_running_compact");
    vm.EncodeQueue.WaitingExpanded = false; Pump(400); Shot("q_running_collapsed");
    vm.EncodeQueue.WaitingExpanded = true; vm.EncodeQueue.Compact = false; Pump(300);
    vm.Encode.HideQueued = true; vm.SelectedTab = MainViewModel.EncodeTab; Pump(400);
    Console.WriteLine($"hide queued: {vm.Encode.Rows.Count} rows left"); Shot("q_picker_hidden");
    vm.Encode.HideQueued = false; Pump(300); Shot("q_picker_greyed");

    run = DateTime.UtcNow;
    while (controller.Encoder.Current.Items.Count(i => i.IsFinished) < 3 && DateTime.UtcNow - run < TimeSpan.FromMinutes(3)) Pump(200);
    vm.SelectedTab = MainViewModel.QueueTab; Pump(300); Shot("q_midway");

    run = DateTime.UtcNow;
    while (controller.Encoder.Current.PendingCount > 0 && DateTime.UtcNow - run < TimeSpan.FromMinutes(5)) Pump(200);
    Pump(500); Shot("q_done");
    vm.EncodeQueue.Compact = true; Pump(500); Shot("q_done_compact"); vm.EncodeQueue.Compact = false; Pump(300);
    var final = controller.Encoder.Current;
    Console.WriteLine($"final: {EncodeText.Headline(final)} | {EncodeText.Totals(final)} | {EncodeText.Saved(final)}");
    foreach (var i in final.Items) Console.WriteLine($"  {i.FileName}: {EncodeText.Status(i)}");
    Console.WriteLine("part files left: " + Directory.GetFiles(videos, "*.part").Length);
    // Stop in the middle: the running video goes back in the queue, nothing is left half done, Start carries on
    controller.Encoder.ClearFinished();
    foreach (var f in Directory.GetFiles(videos, "*_720p_x264.mp4")) File.Delete(f);
    controller.EnqueueEncode(controller.EncodeCandidates().Take(4));
    run = DateTime.UtcNow;
    while (!(controller.Encoder.Current.Current?.Fraction > 0.2) && DateTime.UtcNow - run < TimeSpan.FromMinutes(2)) Pump(20);
    controller.Encoder.Stop();
    while (controller.Encoder.IsRunning) Pump(50);
    Pump(500); vm.SelectedTab = MainViewModel.QueueTab; Shot("q_stopped");
    var stopped = controller.Encoder.Current;
    Console.WriteLine($"stopped: {EncodeText.Headline(stopped)} | halted {stopped.Halted}, waiting {stopped.QueuedCount}, part files {Directory.GetFiles(videos, "*.part").Length}");
    vm.EncodeQueue.StartCommand.Execute(null);
    run = DateTime.UtcNow;
    while (controller.Encoder.Current.PendingCount > 0 && DateTime.UtcNow - run < TimeSpan.FromMinutes(3)) Pump(100);
    Pump(500); Shot("q_after_restart");
    Console.WriteLine($"after start: {EncodeText.Headline(controller.Encoder.Current)} | {EncodeText.Totals(controller.Encoder.Current)}");
    controller.Encoder.Shutdown();
    return;
}
// SHOT_LEGACY=1: opens the Re-encode queue on a copy of real data and waits until the finished videos from before the check are checked
if (Environment.GetEnvironmentVariable("SHOT_LEGACY") == "1")
{
    vm.SelectedTab = MainViewModel.QueueTab;
    var begun = DateTime.UtcNow;
    Pump(800); Shot("l_before");
    while (controller.Encoder.Current.Items.Any(i => i.CheckPending) && DateTime.UtcNow - begun < TimeSpan.FromMinutes(4)) Pump(300);
    Pump(1500); Shot("l_after");
    foreach (var i in controller.Encoder.Current.Items) Console.WriteLine($"  {i.FileName}: {i.Status} | {EncodeText.Status(i)} | check: {i.Check}{i.Message} | delete original {i.CanDeleteOriginal}");
    controller.Encoder.Shutdown();
    return;
}
// SHOT_PLANNING=1: opens Re-encode planning on the given data (use a copy) and waits for the list - for finding crashes there
if (Environment.GetEnvironmentVariable("SHOT_PLANNING") == "1")
{
    vm.SelectedTab = MainViewModel.EncodeTab;
    var started = DateTime.UtcNow;
    Pump(500); Shot("p_loading");
    while (vm.Encode.Loading && DateTime.UtcNow - started < TimeSpan.FromMinutes(3)) Pump(200);
    Pump(1500);
    Console.WriteLine($"planning: loading {vm.Encode.Loading}, {vm.Encode.Rows.Count} rows, has videos {vm.Encode.HasVideos}, after {(DateTime.UtcNow - started).TotalSeconds:0} s");
    Shot("p_loaded");
    return;
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
    Console.WriteLine($"hide folder: {shownBefore} -> {vm.Encode.Rows.Count} rows, {vm.Encode.ManageHiddenText}"); Shot("d_encode_hidden");
    // Manage hidden folders: hide a parent folder too, then unhide one by one and all
    vm.Encode.ManageHiddenCommand.Execute(null);
    string? parent = System.IO.Path.GetDirectoryName(vm.Encode.Rows[0].Folder);
    if (parent != null) { vm.Encode.NewHiddenFolder = parent; vm.Encode.AddHiddenCommand.Execute(null); }
    Pump(300);
    Console.WriteLine($"parent hidden ({parent}): {vm.Encode.Rows.Count} rows, list: {string.Join(" | ", vm.Encode.HiddenList.Select(h => h.Folder + " = " + h.Videos))}"); Shot("d_encode_manage_hidden");
    vm.Encode.HiddenList[0].UnhideCommand.Execute(null); Pump(300);
    Console.WriteLine($"one unhidden: {vm.Encode.Rows.Count} rows, {vm.Encode.ManageHiddenText}");
    vm.Encode.UnhideAllCommand.Execute(null); vm.Encode.CloseHiddenCommand.Execute(null); Pump(300);
    Console.WriteLine($"unhide all: {vm.Encode.Rows.Count} rows, {vm.Encode.ManageHiddenText}");
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
// SHOT_LOADTIME=1: how long opening All files takes (database load + saved file list)
if (Environment.GetEnvironmentVariable("SHOT_LOADTIME") == "1")
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var all = ScanEngine.LoadAllCached();
    Console.WriteLine($"load database (with fingerprints): {sw.Elapsed.TotalSeconds:0.0} s, {all.Count:N0} rows");
    sw.Restart();
    var idx = controller.AllFiles();
    Console.WriteLine($"All files index (first time): {sw.Elapsed.TotalSeconds:0.0} s, {idx.Count:N0} files");
}
