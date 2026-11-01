using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using DuplicateVideoRemover.Desktop.ViewModels;

namespace DuplicateVideoRemover.Desktop.Views;

public partial class EncodeView : UserControl
{
    public EncodeView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is EncodeViewModel vm)
                vm.OpenFolder = async folder =>
                {
                    var top = TopLevel.GetTopLevel(this);
                    if (top != null) await top.Launcher.LaunchUriAsync(new System.Uri(Path.GetFullPath(folder)));
                };
            if (DataContext is EncodeViewModel model)
                model.CopyText = async text =>
                {
                    var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                    if (clipboard == null) return false;
                    await clipboard.SetTextAsync(text);
                    return true;
                };
        };
    }

    // Click picks or unpicks a video; Shift-click picks (or unpicks) everything since the last click
    private void Row_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: EncodeRow row } && DataContext is EncodeViewModel vm
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            vm.RowClicked(row, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
        }
    }
}
