using System.IO;
using Avalonia.Controls;
using DuplicateVideoRemover.Desktop.ViewModels;

namespace DuplicateVideoRemover.Desktop.Views;

public partial class EncodeQueueView : UserControl
{
    public EncodeQueueView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is EncodeQueueViewModel vm)
                vm.OpenFolder = async folder =>
                {
                    var top = TopLevel.GetTopLevel(this);
                    if (top != null) await top.Launcher.LaunchUriAsync(new System.Uri(Path.GetFullPath(folder)));
                };
        };
    }
}
