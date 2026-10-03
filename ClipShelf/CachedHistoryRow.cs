using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClipShelf;

// Cache only the row contents, not selection backgrounds or the scrolling surface.
// Moving a realized row can reuse its text/icons at the monitor's native density.
public sealed class CachedHistoryRow : Grid
{
    internal static bool DiagnosticsDisableCache { get; set; }
    public CachedHistoryRow() => Loaded += (_, _) => UpdateCache(VisualTreeHelper.GetDpi(this));
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateCache(newDpi);
    }
    private void UpdateCache(DpiScale dpi)
    {
        bool enabled = !DiagnosticsDisableCache && !RuntimeFeatureSwitches.NoRowCache && (RenderCapability.Tier >> 16) > 0;
        if (!enabled)
        {
            if (CacheMode is not null) CacheMode = null;
            return;
        }
        if (CacheMode is BitmapCache current
            && Math.Abs(current.RenderAtScale - dpi.DpiScaleX) < .001
            && current.EnableClearType && current.SnapsToDevicePixels) return;
        CacheMode = new BitmapCache { RenderAtScale = dpi.DpiScaleX, EnableClearType = true, SnapsToDevicePixels = true };
    }
}
