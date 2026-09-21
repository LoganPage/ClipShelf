using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ClipShelf;

/// <summary>Physical settings scrolling, driven by elapsed time rather than a 60 Hz timer.</summary>
public sealed class SmoothScrollViewer : ScrollViewer
{
    private readonly WheelScrollMotion motion = new();
    private IScrollInfo? scrollInfo;
    private Window? owner;
    private bool rendering, hasRequest;
    private double requestedOffset;
    private long lastTimestamp;
    private TimeSpan lastRenderingTime = TimeSpan.MinValue;
    internal bool IsWheelMotionActive => rendering;

    public SmoothScrollViewer()
    {
        CanContentScroll = false;
        Focusable = false;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        PanningMode = PanningMode.VerticalOnly;
        Loaded += (_, _) => {
            FindScrollPart();
            var window = Window.GetWindow(this);
            if (!ReferenceEquals(owner, window))
            {
                if (owner is not null) owner.Deactivated -= OwnerDeactivated;
                owner = window;
                if (owner is not null) owner.Deactivated += OwnerDeactivated;
            }
        };
        Unloaded += (_, _) => {
            CancelWheelMotion(); scrollInfo = null;
            if (owner is not null) owner.Deactivated -= OwnerDeactivated;
            owner = null;
        };
        IsVisibleChanged += (_, _) => { if (!IsVisible) CancelWheelMotion(); };
        IsEnabledChanged += (_, _) => { if (!IsEnabled) CancelWheelMotion(); };
        PreviewMouseDown += (_, _) => CancelWheelMotion();
        PreviewKeyDown += (_, _) => CancelWheelMotion();
        PreviewTouchDown += (_, _) => CancelWheelMotion();
        ManipulationStarting += (_, _) => CancelWheelMotion();
        ScrollChanged += HandleScrollChanged;
    }

    public override void OnApplyTemplate()
    {
        CancelWheelMotion(); scrollInfo = null;
        base.OnApplyTemplate(); FindScrollPart();
    }

    private void OwnerDeactivated(object? sender, EventArgs e) => CancelWheelMotion();
    private double MaximumOffset => scrollInfo is null ? 0 : Math.Max(0, scrollInfo.ExtentHeight - scrollInfo.ViewportHeight);
    private bool FindScrollPart()
    {
        scrollInfo ??= GetTemplateChild("PART_ScrollContentPresenter") as IScrollInfo;
        return scrollInfo is not null && ReferenceEquals(scrollInfo.ScrollOwner, this) && !CanContentScroll;
    }

    public void CancelWheelMotion()
    {
        StopRendering();
        double current = scrollInfo?.VerticalOffset ?? VerticalOffset;
        if (hasRequest) scrollInfo?.SetVerticalOffset(current);
        hasRequest = false;
        motion.Reset(current, MaximumOffset);
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (e.Handled || e.Delta == 0) return;
        if (Keyboard.Modifiers != ModifierKeys.None || e.StylusDevice is not null)
        { CancelWheelMotion(); return; }
        // Closed choices and single-line editors belong to this page's wheel route.
        // Only a genuinely scrollable inner viewport (or an open choice) owns it.
        var item = e.OriginalSource as DependencyObject;
        while (item is not null && !ReferenceEquals(item, this))
        {
            if (item is ScrollViewer { ScrollableHeight: > 0 } or ComboBox { IsDropDownOpen: true }
                || item is TextBoxBase { ExtentHeight: > 0 } editor && editor.ExtentHeight > editor.ViewportHeight + .5)
            { CancelWheelMotion(); return; }
            item = item is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item);
        }
        if (HandleWheelDelta(e.Delta, IsMouseCaptureWithin || Mouse.LeftButton == MouseButtonState.Pressed)) e.Handled = true;
    }

    // Isolated tests exercise this without injecting desktop input.
    internal bool HandleWheelDelta(int delta, bool directInput = false)
    {
        if (delta == 0 || !FindScrollPart() || scrollInfo is null) return false;
        double distance = WheelScrollMotion.WheelDistance(delta, SystemParameters.WheelScrollLines, scrollInfo.ViewportHeight);
        if (distance == 0) { CancelWheelMotion(); return true; }
        long now = Stopwatch.GetTimestamp();
        // Non-multiples of 120 are not necessarily fine input: drivers can combine
        // several notches into a large packet. Never turn that into an instant jump.
        // Small packets join an active wheel motion instead of discarding its target.
        bool fineInput = Math.Abs((long)delta) < 120 && !motion.IsActive;
        double current = hasRequest ? requestedOffset : scrollInfo.VerticalOffset;
        if (fineInput || directInput || !SystemParameters.ClientAreaAnimation)
        {
            StopRendering(); motion.Reset(current + distance, MaximumOffset); RequestOffset(motion.Position);
        }
        else
        {
            if (!motion.IsActive) motion.Reset(current, MaximumOffset);
            motion.AddDistance(distance, MaximumOffset);
            if (motion.IsActive && !rendering)
            {
                lastTimestamp = now; lastRenderingTime = TimeSpan.MinValue;
                CompositionTarget.Rendering += RenderFrame; rendering = true;
            }
        }
        return true;
    }

    private void RenderFrame(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs frame)
        {
            if (frame.RenderingTime == lastRenderingTime) return;
            lastRenderingTime = frame.RenderingTime;
        }
        if (!IsVisible || !IsEnabled || !IsLoaded || scrollInfo is null || !SystemParameters.ClientAreaAnimation
            || IsMouseCaptureWithin || Mouse.LeftButton == MouseButtonState.Pressed)
        { CancelWheelMotion(); return; }
        long now = Stopwatch.GetTimestamp();
        double elapsed = (now - lastTimestamp) / (double)Stopwatch.Frequency;
        lastTimestamp = now;
        RequestOffset(motion.Advance(elapsed, MaximumOffset));
        if (!motion.IsActive) StopRendering();
    }

    private void RequestOffset(double value)
    {
        if (scrollInfo is null) return;
        hasRequest = true; requestedOffset = value;
        // The presenter arranges translated content with valid hit testing. There is no
        // per-frame ScrollViewer command queue, UpdateLayout or render-transform illusion.
        scrollInfo.SetVerticalOffset(value);
    }

    private void HandleScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, this)) return;
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0 || e.ViewportWidthChange != 0)
        { CancelWheelMotion(); return; }
        if (e.VerticalChange == 0 || scrollInfo is null) return;
        if (hasRequest && Math.Abs(scrollInfo.VerticalOffset - requestedOffset) <= 1)
        { hasRequest = false; return; }
        if (motion.IsActive) CancelWheelMotion();
        hasRequest = false;
    }

    private void StopRendering()
    {
        if (!rendering) return;
        CompositionTarget.Rendering -= RenderFrame; rendering = false;
        lastRenderingTime = TimeSpan.MinValue;
    }
}
