using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClipShelf;

/// <summary>FLIP-style motion for only the realized history containers.</summary>
internal sealed class VisibleRowLayoutTransition
{
    internal sealed record Snapshot(Dictionary<Guid, double> Positions, HashSet<Guid> Ids, bool Enabled);
    private static readonly DependencyProperty CleanupHookedProperty = DependencyProperty.RegisterAttached(
        "CleanupHooked", typeof(bool), typeof(VisibleRowLayoutTransition), new PropertyMetadata(false));
    private int version;

    internal Snapshot Capture(ListBox list, bool enabled)
    {
        var positions = new Dictionary<Guid, double>();
        var ids = new HashSet<Guid>();
        if (!enabled || !list.IsLoaded || !list.IsVisible || !MotionPolicy.Allows(MotionDomain.ListLayout))
            return new(positions, ids, false);
        foreach (ListBoxItem row in RealizedRows(list))
        {
            if (row.DataContext is not ClipItem item) continue;
            ids.Add(item.Id);
            try { positions[item.Id] = row.TranslatePoint(new Point(), list).Y; } catch (InvalidOperationException) { }
        }
        return new(positions, ids, true);
    }

    internal void Play(ListBox list, Snapshot snapshot)
    {
        int request = ++version;
        if (!snapshot.Enabled)
        {
            foreach (ListBoxItem row in RealizedRows(list)) Reset(row);
            return;
        }
        list.Dispatcher.BeginInvoke(() =>
        {
            if (request != version || !list.IsLoaded || !list.IsVisible) return;
            foreach (ListBoxItem row in RealizedRows(list))
            {
                if (row.DataContext is not ClipItem item) continue;
                HookCleanup(row);
                var shift = EnsureShift(row);
                MotionDriver.Current.Cancel(row);
                double newY;
                try { newY = row.TranslatePoint(new Point(), list).Y - shift.Y; } catch (InvalidOperationException) { continue; }
                if (snapshot.Positions.TryGetValue(item.Id, out double oldY))
                {
                    double offset = Math.Clamp(oldY - newY, -list.ActualHeight, list.ActualHeight);
                    shift.Y = offset;
                    row.Opacity = 1;
                    MotionDriver.Current.Animate(row, "row-layout-y", offset, 0, MotionTokens.ListLayout, value => shift.Y = value);
                }
                else
                {
                    shift.Y = MotionTokens.ListEnterOffset;
                    row.Opacity = 0;
                    MotionDriver.Current.Animate(row, "row-layout-y", shift.Y, 0, MotionTokens.Insert, value => shift.Y = value);
                    MotionDriver.Current.Animate(row, "row-layout-opacity", row.Opacity, 1, MotionTokens.Insert, value => row.Opacity = value);
                }
            }
        // Wait until collection notifications, container recycling and the normal
        // render/layout pass have established the new coordinates. Forcing
        // UpdateLayout here would put the whole list back on the mutation path.
        }, DispatcherPriority.ContextIdle);
    }

    internal void Cancel(ListBox list)
    {
        version++;
        foreach (ListBoxItem row in RealizedRows(list)) Reset(row);
    }

    internal void CaptureExits(ListBox list, IEnumerable<Guid> ids, Canvas overlay)
    {
        if (!MotionPolicy.Allows(MotionDomain.ListLayout) || !list.IsLoaded || !list.IsVisible) return;
        var wanted = new HashSet<Guid>(ids);
        foreach (ListBoxItem row in RealizedRows(list))
        {
            if (row.DataContext is not ClipItem item || !wanted.Contains(item.Id)
                || row.ActualWidth <= 0 || row.ActualHeight <= 0) continue;
            try
            {
                DpiScale dpi = VisualTreeHelper.GetDpi(row);
                var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(row.ActualWidth * dpi.DpiScaleX)),
                    Math.Max(1, (int)Math.Ceiling(row.ActualHeight * dpi.DpiScaleY)), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                bitmap.Render(row); bitmap.Freeze();
                Point point = row.TranslatePoint(new Point(), list);
                var image = new Image { Source = bitmap, Width = row.ActualWidth, Height = row.ActualHeight, Opacity = 1,
                    Stretch = Stretch.Fill, RenderTransform = new TranslateTransform() };
                Canvas.SetLeft(image, point.X); Canvas.SetTop(image, point.Y); overlay.Children.Add(image);
                var shift = (TranslateTransform)image.RenderTransform;
                MotionDriver.Current.Animate(image, "exit-y", 0, -6, MotionTokens.Remove, value => shift.Y = value);
                MotionDriver.Current.Animate(image, "exit-opacity", 1, 0, MotionTokens.Remove, value => image.Opacity = value,
                    () => { overlay.Children.Remove(image); image.Source = null; });
            }
            catch (InvalidOperationException) { }
        }
    }

    private static TranslateTransform EnsureShift(ListBoxItem row)
    {
        if (row.RenderTransform is TranslateTransform existing) return existing;
        var shift = new TranslateTransform(); row.RenderTransform = shift; return shift;
    }

    private static IEnumerable<ListBoxItem> RealizedRows(DependencyObject root)
    {
        // Walk only the current visual subtree. ContainerFromIndex across the full
        // item count does not generate rows, but it still turns each refresh into
        // O(history size) work and defeats the purpose of a virtualized transition.
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is ListBoxItem row) yield return row;
            else foreach (ListBoxItem nested in RealizedRows(child)) yield return nested;
        }
    }

    private static void HookCleanup(ListBoxItem row)
    {
        if ((bool)row.GetValue(CleanupHookedProperty)) return;
        row.SetValue(CleanupHookedProperty, true);
        row.Unloaded += (_, _) => Reset(row);
        row.DataContextChanged += (_, _) => Reset(row);
    }

    private static void Reset(ListBoxItem row)
    {
        MotionDriver.Current.Cancel(row);
        if (row.RenderTransform is TranslateTransform shift) shift.Y = 0;
        row.Opacity = 1;
    }
}
