using Avalonia.Controls;
using Avalonia.Input.Platform;
using DuplicateVideoRemover.Desktop.ViewModels;

namespace DuplicateVideoRemover.Desktop.Views;

public partial class ReviewView : UserControl
{
    public ReviewView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ReviewViewModel vm)
                vm.CopyText = async text =>
                {
                    var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                    if (clipboard == null) return false;
                    await clipboard.SetTextAsync(text);
                    return true;
                };
        };
    }
}
