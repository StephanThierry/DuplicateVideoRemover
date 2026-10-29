using System;
using System.IO;
using Avalonia;
using deepduplicates;

namespace DuplicateVideoRemover.Desktop;

public static class Program
{
    public static AppController Controller { get; set; } = null!;

    // Usage: DuplicateVideoRemover.Desktop [--data <folder>] [--db <folder>]
    //   --data  folder with settings.json and output (or DVR_DATA). Default: the current folder if it
    //           holds settings.json (like the command-line version), otherwise the user's app data folder.
    //   --db    folder with deepdupes.db and _screens (or DVR_DB), e.g. a database on the NAS that the
    //           Docker container also uses. Default: the data folder.
    [STAThread]
    public static void Main(string[] args)
    {
        Controller = AppController.Open(DataFolder(args), DataLocation.ResolveDatabaseFolder(args));
        Controller.Scanner.ProgramName = "the desktop app";
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static string DataFolder(string[] args)
    {
        var chosen = DataLocation.ResolveDataFolder(args, null);
        if (chosen != null) return chosen;
        string current = Directory.GetCurrentDirectory();
        if (File.Exists(Path.Combine(current, "settings.json"))) return current;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DuplicateVideoRemover");
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
