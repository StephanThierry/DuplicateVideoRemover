using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DuplicateVideoRemover.Desktop.ViewModels;
using DuplicateVideoRemover.Desktop.Views;

namespace DuplicateVideoRemover.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = new MainViewModel(Program.Controller) };
            // Closing the app stops a running scan and frees scan.lock, so other programs can scan right away
            desktop.Exit += (_, _) =>
            {
                Program.Controller.Scanner.Cancel();
                // Stops FFmpeg and deletes its partial file; the rest of the queue is kept for the next start
                Program.Controller.Encoder.Shutdown();
                deepduplicates.ScanLock.ReleaseAll();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
