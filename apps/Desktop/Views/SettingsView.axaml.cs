using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
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
        Scroller.ScrollChanged += (_, _) => MarkCurrentSection();
    }

    // "On this page": scroll so the section's header is at the top
    private void Jump_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name } || this.FindControl<Control>(name) is not { } section) return;
        var top = section.TranslatePoint(new Point(0, 0), (Visual)Scroller.Content!);
        if (top is { } p) Scroller.Offset = new Vector(Scroller.Offset.X, System.Math.Max(0, p.Y - 4));
    }

    // Highlight the section at the top of the view (the last one whose header has scrolled past a third of the way down)
    private void MarkCurrentSection()
    {
        var buttons = Jumps.Children.OfType<Button>().ToList();
        string? current = null;
        foreach (var b in buttons)
        {
            if (b.Tag is not string name || this.FindControl<Control>(name) is not { } section) continue;
            var y = section.TranslatePoint(new Point(0, 0), Scroller);
            if (y is { } p && p.Y <= Scroller.Viewport.Height / 3) current = name;
        }
        // At the very bottom the last sections can never reach the top: then the last one is current
        if (Scroller.Offset.Y > 0 && Scroller.Offset.Y + Scroller.Viewport.Height >= Scroller.Extent.Height - 2)
            current = buttons.LastOrDefault()?.Tag as string;
        current ??= buttons.FirstOrDefault()?.Tag as string;
        foreach (var b in buttons) b.Classes.Set("current", Equals(b.Tag, current));
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
