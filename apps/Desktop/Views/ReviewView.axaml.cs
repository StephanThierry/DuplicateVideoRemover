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

    // Each row's container (they are reused while scrolling): the band of every second set, and the "False positive"
    // button drawn above the cards it sits between
    private void Rows_ContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        bool shade = e.Container.DataContext switch
        {
            VideoRow v => v.Shade,
            MatchDividerRow d => d.Shade,
            GroupHeaderRow h => h.Shade,
            _ => false,
        };
        e.Container.Classes.Set("shade", shade);
        bool divider = e.Container.DataContext is MatchDividerRow;
        e.Container.ZIndex = divider ? 1 : 0;
        e.Container.ClipToBounds = !divider;   // the button reaches over the cards above and below
    }
}
