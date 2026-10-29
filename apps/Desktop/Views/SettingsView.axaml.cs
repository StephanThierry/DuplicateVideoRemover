using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using DuplicateVideoRemover.Desktop.ViewModels;

namespace DuplicateVideoRemover.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm) vm.PickFolder = PickFolderAsync;
        };
    }

    // The system folder picker; network locations work when the system shows them
    private async Task<string?> PickFolderAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return null;
        var result = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder",
            AllowMultiple = false
        });
        return result.FirstOrDefault()?.TryGetLocalPath();
    }
}
