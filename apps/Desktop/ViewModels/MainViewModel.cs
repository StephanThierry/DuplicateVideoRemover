using CommunityToolkit.Mvvm.ComponentModel;
using deepduplicates;

namespace DuplicateVideoRemover.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public AppController App { get; }
    public ReviewViewModel Review { get; }
    public ScanViewModel Scan { get; }
    public SettingsViewModel Settings { get; }

    /// <summary>0 = Review, 1 = Scan, 2 = Settings.</summary>
    [ObservableProperty] private int selectedTab;

    public string DataFolder => App.DatabaseFolder == App.DataFolder
        ? App.DataFolder
        : $"{App.DataFolder}   (database: {App.DatabaseFolder})";

    public MainViewModel(AppController app)
    {
        App = app;
        Review = new ReviewViewModel(app, this);
        Scan = new ScanViewModel(app, this);
        Settings = new SettingsViewModel(app);
        // Start where there is something to do
        SelectedTab = app.Session.Results.Groups.Count + app.Session.Results.Broken.Count > 0 ? 0 : app.IsFirstRun ? 2 : 1;
    }

    public void ShowReview() => SelectedTab = 0;
    public void ShowScan() => SelectedTab = 1;
    public void ShowSettings() => SelectedTab = 2;
}
