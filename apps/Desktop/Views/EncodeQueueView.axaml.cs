using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Input;
using Avalonia.Media;
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
    // The grip captures the pointer. The card (picture and all) follows the pointer, lifted above the others; the cards it
    // passes slide out of the way, so the gap shows where it will land. Letting go moves the video there in the queue. Near the
    // top or bottom edge the list scrolls. Positions are taken in the list's items panel, which moves with the scrolling.

    private EncodeQueueRow? dragRow;
    private ListBox? dragList;
    private Control? dragPanel;
    private double startY, pointerY, viewY, dragTop, dragHeight;
    private EncodeQueueRow? dropBefore;      // lands just before this one (null: at the end)
    private bool dropKnown;
    private DispatcherTimer? scrollTimer;
    private Control? dragGrip;

    private void Grip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control grip || grip.DataContext is not EncodeQueueRow row || !e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
        dragList = grip.FindAncestorOfType<ListBox>();
        dragPanel = dragList?.ItemsPanelRoot;
        if (dragList == null || dragPanel == null || dragList.ContainerFromItem(row) is not Control card) return;
        dragRow = row;
        dragTop = card.Bounds.Y;
        dragHeight = card.Bounds.Height;
        startY = pointerY = e.GetPosition(dragPanel).Y;
        viewY = e.GetPosition(dragList).Y;
        dropKnown = false;
        row.IsDragged = true;
        dragGrip = grip;
        ToolTip.SetIsOpen(grip, false);
        ToolTip.SetServiceEnabled(grip, false);   // no "Drag up or down" note hanging on the card while it moves
        e.Pointer.Capture(grip);
        e.Handled = true;
        scrollTimer ??= new DispatcherTimer(System.TimeSpan.FromMilliseconds(30), DispatcherPriority.Input, (_, _) => AutoScroll());
        scrollTimer.Start();
        Arrange();
    }

    private void Grip_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (dragRow == null || dragPanel == null || dragList == null) return;
        pointerY = e.GetPosition(dragPanel).Y;
        viewY = e.GetPosition(dragList).Y;
        Arrange();
    }

    private void Grip_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (dragRow == null) return;
        var row = dragRow;
        var before = dropBefore;
        bool known = dropKnown;
        e.Pointer.Capture(null);
        EndDrag();
        if (!known || DataContext is not EncodeQueueViewModel vm) return;
        var waiting = vm.Waiting.ToList();
        int at = waiting.IndexOf(row);
        var next = at >= 0 && at + 1 < waiting.Count ? waiting[at + 1] : null;
        if (ReferenceEquals(before, next) || ReferenceEquals(before, row)) return;   // dropped where it was
        vm.MoveWaiting(row, before);
    }

    private void Grip_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndDrag();

    private void EndDrag()
    {
        scrollTimer?.Stop();
        if (dragRow != null) dragRow.IsDragged = false;
        if (dragGrip != null) { ToolTip.SetServiceEnabled(dragGrip, true); dragGrip = null; }
        if (dragList != null)
            foreach (var c in dragList.GetRealizedContainers()) { c.RenderTransform = null; c.ZIndex = 0; }   // all back in place, at once
        dragRow = null;
        dragList = null;
        dragPanel = null;
    }

    /// <summary>Moves the dragged card to the pointer and the others aside, and works out where it would land.</summary>
    private void Arrange()
    {
        if (dragList == null || dragRow == null || DataContext is not EncodeQueueViewModel vm) return;
        double dy = pointerY - startY;
        double center = dragTop + dragHeight / 2 + dy;
        var others = new System.Collections.Generic.List<(Control Card, EncodeQueueRow Row)>();
        foreach (var c in dragList.GetRealizedContainers())
        {
            if (c.DataContext is not EncodeQueueRow r) continue;
            if (ReferenceEquals(r, dragRow)) { Shift(c, dy, slide: false); c.ZIndex = 10; continue; }
            c.ZIndex = 0;
            double mid = c.Bounds.Y + c.Bounds.Height / 2;
            bool wasAbove = c.Bounds.Y < dragTop;
            // A card the dragged one has passed moves into the room it left
            Shift(c, wasAbove && mid > center ? dragHeight : !wasAbove && mid < center ? -dragHeight : 0, slide: true);
            others.Add((c, r));
        }
        others.Sort((a, b) => a.Card.Bounds.Y.CompareTo(b.Card.Bounds.Y));
        if (others.Count == 0) { dropKnown = false; return; }
        var below = others.FirstOrDefault(o => o.Card.Bounds.Y + o.Card.Bounds.Height / 2 > center);
        if (below.Row != null) dropBefore = below.Row;
        else
        {
            // Below every card shown: before the next one in the queue (scrolled out of view), or at the end
            var waiting = vm.Waiting.ToList();
            int last = waiting.IndexOf(others[^1].Row);
            dropBefore = waiting.Skip(last + 1).FirstOrDefault(r => !ReferenceEquals(r, dragRow));
        }
        dropKnown = true;
    }

    private static void Shift(Control card, double y, bool slide)
    {
        if (card.RenderTransform is not TranslateTransform t)
        {
            t = new TranslateTransform();
            card.RenderTransform = t;
        }
        // The cards that make room glide; the dragged one sticks to the pointer
        if (slide && t.Transitions == null)
            t.Transitions = new Transitions { new DoubleTransition { Property = TranslateTransform.YProperty, Duration = System.TimeSpan.FromMilliseconds(140), Easing = new CubicEaseOut() } };
        else if (!slide) t.Transitions = null;
        t.Y = y;
    }

    private void AutoScroll()
    {
        if (dragList == null || dragList.FindDescendantOfType<ScrollViewer>() is not ScrollViewer sv) return;
        const double edge = 36;
        double h = dragList.Bounds.Height, step = 0;
        if (viewY < edge) step = -(edge - viewY) / 2 - 2;
        else if (viewY > h - edge) step = (viewY - (h - edge)) / 2 + 2;
        if (step == 0) return;
        double before = sv.Offset.Y;
        sv.Offset = new Vector(sv.Offset.X, System.Math.Clamp(before + step, 0, System.Math.Max(0, sv.Extent.Height - sv.Viewport.Height)));
        pointerY += sv.Offset.Y - before;   // the pointer stays put on screen, so it moves down the list
        Arrange();
    }
}
