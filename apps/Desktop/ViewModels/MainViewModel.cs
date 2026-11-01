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
    public AllFilesViewModel Files { get; }

    public const int ReviewTab = 0, ScanTab = 1, EncodeTab = 2, FilesTab = 3, SettingsTab = 4;

    /// <summary>0 = Review, 1 = Scan, 2 = Re-encode, 3 = All files, 4 = Settings.</summary>
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
        Files = new AllFilesViewModel(app, this);
        // Start where there is something to do
        SelectedTab = app.Session.Results.Groups.Count + app.Session.Results.Broken.Count > 0 ? ReviewTab : app.IsFirstRun ? SettingsTab : ScanTab;
    }

    // The Re-encode list follows the saved settings, so load it when the screen is opened
    partial void OnSelectedTabChanged(int value)
    {
        if (value == EncodeTab) _ = Encode.Reload();
        if (value == FilesTab) _ = Files.Reload();
    }

    public void ShowReview() => SelectedTab = ReviewTab;
    public void ShowScan() => SelectedTab = ScanTab;
    public void ShowEncode() => SelectedTab = EncodeTab;
    public void ShowSettings() => SelectedTab = SettingsTab;
}
