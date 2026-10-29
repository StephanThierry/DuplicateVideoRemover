using System;
using System.IO;
using System.Threading;
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
var window = new MainWindow { DataContext = vm, Width = 1280, Height = 860 };
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
vm.Review.ShowFolderCommand.Execute("/tmp/lib/Downloads"); Shot("d_review_folder");
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
vm.SelectedTab = 2; Pump(1500); Shot("d_settings");
vm.Settings.Match.ShapeMatch = 396; Pump(1500); Shot("d_settings_strict");
Console.WriteLine("preview strict: " + vm.Settings.PreviewDuplicates);
