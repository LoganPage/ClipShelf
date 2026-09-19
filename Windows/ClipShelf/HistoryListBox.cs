using System;
using System.Collections;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ClipShelf;

public sealed class HistoryListBox : ListBox
{
    private readonly WheelScrollMotion wheelMotion = new();
    private ScrollViewer? wheelViewer;
    private VirtualizingStackPanel? wheelPanel;
    private bool rendering, hasRequestedOffset;
    private double requestedOffset;
    private double? dragScrollOffset;
    private long lastFrameTimestamp;
    private TimeSpan lastRenderingTime = TimeSpan.MinValue;

    public HistoryListBox()
    {
        Loaded += (_, _) => FindScrollParts();
        Unloaded += (_, _) => DetachScrollParts();
        IsVisibleChanged += (_, _) => { if (!IsVisible) CancelWheelMotion(); };
        IsEnabledChanged += (_, _) => { if (!IsEnabled) CancelWheelMotion(); };
        PreviewMouseDown += (_, _) => CancelWheelMotion();
        PreviewKeyDown += (_, _) => CancelWheelMotion();
        PreviewTouchDown += (_, _) => CancelWheelMotion();
        ManipulationStarting += (_, _) => CancelWheelMotion();
    }

    // WPF batches this replacement into one selection transaction instead of
    // clearing then notifying once per row on every pointer movement.
    public void ReplaceSelection(IEnumerable items)
    {
        var desired = items.Cast<object>().ToHashSet();
        var existing = SelectedItems.Cast<object>().ToHashSet();
        var removed = existing.Except(desired).ToArray();
        var added = desired.Except(existing).ToArray();
        if (removed.Length + added.Length == 0) return;
        // The common drag step crosses one row: avoid rebuilding WPF's selection.
        if (removed.Length == 1 && added.Length == 0) SelectedItems.Remove(removed[0]);
        else if (added.Length == 1 && removed.Length == 0) SelectedItems.Add(added[0]);
        else SetSelectedItems(desired);
    }

    /// <summary>Stop at the current scroll position before a new selection/navigation intent.</summary>
    public void CancelWheelMotion()
    {
        dragScrollOffset = null;
        StopRendering();
        double current = wheelPanel?.VerticalOffset ?? 0;
        // InvalidateMeasure may not have presented the last request yet. Stop at the
        // last laid-out position instead of letting that old request move a clicked row.
        if (hasRequestedOffset && wheelPanel is not null) ((IScrollInfo)wheelPanel).SetVerticalOffset(current);
        hasRequestedOffset = false;
        wheelMotion.Reset(current, MaximumOffset);
    }

    public override void OnApplyTemplate()
    {
        DetachScrollParts(); base.OnApplyTemplate(); FindScrollParts();
    }

    protected override void OnItemsChanged(NotifyCollectionChangedEventArgs e)
    {
        CancelWheelMotion(); base.OnItemsChanged(e);
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (e.Handled || e.Delta == 0) return;
        if (UsesNativeWheel(Keyboard.Modifiers, e.StylusDevice is not null))
        { CancelWheelMotion(); return; }
        if (!FindScrollParts() || wheelViewer is null) return;
        // A future nested editor/scroll viewer keeps its own wheel behavior.
        var nearest = Ancestor<ScrollViewer>(e.OriginalSource as DependencyObject);
        if (nearest is not null && !ReferenceEquals(nearest, wheelViewer)) return;
        if (HandleWheelDelta(e.Delta, IsMouseCaptureWithin)) e.Handled = true;
    }

    // Ctrl/Shift belong to selection; holding them must not switch wheel physics.
    internal static bool UsesNativeWheel(ModifierKeys modifiers, bool stylus) => stylus || (modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) != 0;

    // Also used by the isolated rendering probe; never synthesizes a system input event.
    internal bool HandleWheelDelta(int delta, bool directInput = false)
    {
        dragScrollOffset = null;
        if (delta == 0 || !FindScrollParts() || wheelPanel is null || wheelViewer is null
            || !wheelViewer.CanContentScroll || VirtualizingPanel.GetScrollUnit(this) != ScrollUnit.Pixel) return false;
        double distance = WheelScrollMotion.WheelDistance(delta, SystemParameters.WheelScrollLines, wheelPanel.ViewportHeight);
        if (distance == 0) { CancelWheelMotion(); return true; }
        double maximum = MaximumOffset;
        long now = Stopwatch.GetTimestamp();
        // A small delta does not identify a touchpad: high-resolution mouse wheels
        // produce them too. All ordinary wheel packets share the same frame driver.
        // A quicker response keeps small packets responsive without per-packet jumps.
        wheelMotion.ResponseFrequency = WheelScrollMotion.IsFractionalWheelDelta(delta) ? 56 : 28;
        bool immediate = !SystemParameters.ClientAreaAnimation || directInput;
        double current = hasRequestedOffset ? requestedOffset : wheelPanel.VerticalOffset;
        if (immediate)
        {
            StopRendering(); wheelMotion.Reset(current, maximum);
            wheelMotion.Reset(current + distance, maximum);
            RequestOffset(wheelMotion.Position);
        }
        else
        {
            if (!wheelMotion.IsActive) wheelMotion.Reset(current, maximum);
            wheelMotion.AddDistance(distance, maximum);
            if (wheelMotion.IsActive && !rendering)
            {
                lastFrameTimestamp = now; lastRenderingTime = TimeSpan.MinValue;
                CompositionTarget.Rendering += RenderWheelFrame; rendering = true;
            }
        }
        // Also honor an OS setting of zero lines: never fall through to a whole-notch jump.
        return true;
    }

    private double MaximumOffset => wheelPanel is null ? 0 : Math.Max(0, wheelPanel.ExtentHeight - wheelPanel.ViewportHeight);
    internal bool IsWheelAnimating => rendering;

    private void RenderWheelFrame(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs frame)
        {
            if (frame.RenderingTime == lastRenderingTime) return;
            lastRenderingTime = frame.RenderingTime;
        }
        if (!IsVisible || !IsEnabled || !IsLoaded || wheelPanel is null || !SystemParameters.ClientAreaAnimation
            || IsMouseCaptureWithin)
        { CancelWheelMotion(); return; }
        long now = Stopwatch.GetTimestamp();
        double elapsed = (now - lastFrameTimestamp) / (double)Stopwatch.Frequency;
        lastFrameTimestamp = now;
        RequestOffset(wheelMotion.Advance(elapsed, MaximumOffset));
        if (!wheelMotion.IsActive) StopRendering();
    }

    private void RequestOffset(double value)
    {
        if (wheelPanel is null) return;
        requestedOffset = value; hasRequestedOffset = true;
        // Use the public IScrollInfo path directly, just like WPF's native wheel handler.
        // This avoids a per-frame ScrollViewer command queue while retaining recycling,
        // real content offsets and correct hit testing. No RenderTransform or UpdateLayout.
        ((IScrollInfo)wheelPanel).SetVerticalOffset(value);
    }

    internal double ScrollDragBy(double distance)
    {
        if (!FindScrollParts() || wheelPanel is null) return 0;
        // Keep the fractional remainder: WPF rounds its laid-out offset to pixels.
        // Re-reading that rounded value each frame slows motion on high-Hz screens.
        double current = dragScrollOffset is double previous && Math.Abs(previous - wheelPanel.VerticalOffset) <= 1
            ? previous : wheelPanel.VerticalOffset;
        double target = Math.Clamp(current + distance, 0, MaximumOffset);
        dragScrollOffset = target;
        RequestOffset(target);
        return target;
    }

    private void OnWheelScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, wheelViewer)) return;
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0 || e.ViewportWidthChange != 0)
        { CancelWheelMotion(); return; }
        if (e.VerticalChange == 0 || wheelPanel is null) return;
        // A thumb drag, keyboard/bring-into-view request, or app command owns the new
        // position. Small layout rounding differences in our own request are harmless.
        if (hasRequestedOffset && Math.Abs(wheelPanel.VerticalOffset - requestedOffset) <= 1)
        { hasRequestedOffset = false; return; }
        if (wheelMotion.IsActive) CancelWheelMotion();
        hasRequestedOffset = false;
    }

    private bool FindScrollParts()
    {
        if (wheelViewer is null)
        {
            wheelViewer = Descendants<ScrollViewer>(this).FirstOrDefault();
            if (wheelViewer is not null) wheelViewer.ScrollChanged += OnWheelScrollChanged;
        }
        if (wheelPanel is null)
            wheelPanel = Descendants<VirtualizingStackPanel>(this).FirstOrDefault(panel => ReferenceEquals(ItemsControl.GetItemsOwner(panel), this) && panel.Orientation == Orientation.Vertical);
        return wheelViewer is not null && wheelPanel is not null && ReferenceEquals(wheelPanel.ScrollOwner, wheelViewer);
    }

    private void StopRendering()
    {
        if (!rendering) return;
        CompositionTarget.Rendering -= RenderWheelFrame; rendering = false;
        lastRenderingTime = TimeSpan.MinValue;
    }

    private void DetachScrollParts()
    {
        CancelWheelMotion();
        if (wheelViewer is not null) wheelViewer.ScrollChanged -= OnWheelScrollChanged;
        wheelViewer = null; wheelPanel = null;
    }

    private static T? Ancestor<T>(DependencyObject? item) where T : DependencyObject
    {
        while (item is not null)
        {
            if (item is T match) return match;
            item = item is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item);
        }
        return null;
    }

    private static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
