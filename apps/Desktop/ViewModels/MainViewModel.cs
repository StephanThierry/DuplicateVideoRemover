using CommunityToolkit.Mvvm.ComponentModel;
using deepduplicates;

namespace DuplicateVideoRemover.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public AppController App { get; }
    public ReviewViewModel Review { get; }
    public ScanViewModel Scan { get; }
    public SettingsViewModel Settings { get; }
    public EncodeViewModel Encode { get; }

    /// <summary>0 = Review, 1 = Scan, 2 = Re-encode, 3 = Settings.</summary>
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
        Encode = new EncodeViewModel(app, this);
        // Start where there is something to do
        SelectedTab = app.Session.Results.Groups.Count + app.Session.Results.Broken.Count > 0 ? 0 : app.IsFirstRun ? 3 : 1;
    }

    // The Re-encode list follows the saved settings, so load it when the screen is opened
    partial void OnSelectedTabChanged(int value)
    {
        if (value == 2) _ = Encode.Reload();
    }

    public void ShowReview() => SelectedTab = 0;
    public void ShowScan() => SelectedTab = 1;
    public void ShowEncode() => SelectedTab = 2;
    public void ShowSettings() => SelectedTab = 3;
}
