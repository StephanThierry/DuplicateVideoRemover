using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

    // --- Dragging a waiting video by its grip to change the order ---
    // The grip captures the pointer; while it moves, the row under it shows a line where the video would land (above or below
    // it, by which half the pointer is in), and the list scrolls when the pointer is near its top or bottom edge.

    private EncodeQueueRow? dragRow, dropRow;
    private bool dropAfter;
    private ListBox? dragList;
    private double pointerY;
    private DispatcherTimer? scrollTimer;

    private void Grip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control grip || grip.DataContext is not EncodeQueueRow row || !e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
        dragList = grip.FindAncestorOfType<ListBox>();
        if (dragList == null) return;
        dragRow = row;
        row.IsDragged = true;
        pointerY = e.GetPosition(dragList).Y;
        e.Pointer.Capture(grip);
        e.Handled = true;
        scrollTimer ??= new DispatcherTimer(System.TimeSpan.FromMilliseconds(30), DispatcherPriority.Input, (_, _) => AutoScroll());
        scrollTimer.Start();
    }

    private void Grip_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (dragRow == null || dragList == null) return;
        pointerY = e.GetPosition(dragList).Y;
        ShowDropLine();
    }

    private void Grip_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (dragRow == null) return;
        var row = dragRow;
        var target = dropRow;
        bool after = dropAfter;
        e.Pointer.Capture(null);
        EndDrag();
        if (target == null || DataContext is not EncodeQueueViewModel vm) return;
        // Below a row = before the one after it (or the end of the queue)
        var waiting = vm.Waiting.ToList();
        EncodeQueueRow? before = target;
        if (after)
        {
            int at = waiting.IndexOf(target);
            before = at >= 0 && at + 1 < waiting.Count ? waiting[at + 1] : null;
        }
        if (ReferenceEquals(before, row) || (before == null && waiting.LastOrDefault() == row)) return;   // dropped where it was
        vm.MoveWaiting(row, before);
    }

    private void Grip_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndDrag();

    private void EndDrag()
    {
        scrollTimer?.Stop();
        if (dragRow != null) dragRow.IsDragged = false;
        SetDrop(null, false);
        dragRow = null;
        dragList = null;
    }

    private void ShowDropLine()
    {
        if (dragList == null) return;
        EncodeQueueRow? target = null;
        bool after = false;
        Control? first = null, last = null;
        foreach (var c in dragList.GetRealizedContainers())
        {
            if (c.DataContext is not EncodeQueueRow r || c.TranslatePoint(new Point(0, 0), dragList) is not Point top) continue;
            first ??= c; last = c;
            double h = c.Bounds.Height;
            if (pointerY >= top.Y && pointerY < top.Y + h) { target = r; after = pointerY > top.Y + h / 2; break; }
        }
        // Above the first or below the last row shown: before the first, after the last
        if (target == null && first?.DataContext is EncodeQueueRow f && pointerY < (first.TranslatePoint(new Point(0, 0), dragList)?.Y ?? 0)) target = f;
        else if (target == null && last?.DataContext is EncodeQueueRow l) { target = l; after = true; }
        if (ReferenceEquals(target, dragRow)) target = null;   // over itself: it stays
        SetDrop(target, after);
    }

    private void SetDrop(EncodeQueueRow? target, bool after)
    {
        if (dropRow != null) { dropRow.DropAbove = false; dropRow.DropBelow = false; }
        dropRow = target;
        dropAfter = after;
        if (target != null) { target.DropAbove = !after; target.DropBelow = after; }
    }

    private void AutoScroll()
    {
        if (dragList == null || dragList.FindDescendantOfType<ScrollViewer>() is not ScrollViewer sv) return;
        const double edge = 36;
        double h = dragList.Bounds.Height, step = 0;
        if (pointerY < edge) step = -(edge - pointerY) / 2 - 2;
        else if (pointerY > h - edge) step = (pointerY - (h - edge)) / 2 + 2;
        if (step == 0) return;
        sv.Offset = new Vector(sv.Offset.X, System.Math.Clamp(sv.Offset.Y + step, 0, System.Math.Max(0, sv.Extent.Height - sv.Viewport.Height)));
        ShowDropLine();
    }
}
