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
    vm.SelectedTab = 3; Pump(1200);
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
vm.SelectedTab = 3; Pump(1500); Shot("d_settings");
vm.SelectedTab = 1; Shot("d_scan_scope");
vm.Settings.OpenFalsePositivesCommand.Execute(null); vm.SelectedTab = 3; Pump(1500); Shot("d_false_positives");
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
    vm.SelectedTab = 3; vm.Settings.OpenFalsePositivesCommand.Execute(null); Pump(2000); Shot("d_false_positives_one");
}
