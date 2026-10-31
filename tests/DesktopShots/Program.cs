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
    vm.SelectedTab = 2; Pump(2500);
    vm.Encode.SelectTopCommand.Execute(null); Pump(300);
    vm.Encode.CreateCommand.Execute(null); Shot("d_encode");
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
vm.Settings.Match.ShapeMatch = 396; Pump(1500); Shot("d_settings_strict");
Console.WriteLine("preview strict: " + vm.Settings.PreviewDuplicates);
