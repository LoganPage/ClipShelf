using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClipShelf;

// Cache only the row contents, not selection backgrounds or the scrolling surface.
// Moving a realized row can reuse its text/icons at the monitor's native density.
public sealed class CachedHistoryRow : Grid
{
    public CachedHistoryRow() => Loaded += (_, _) => UpdateCache(VisualTreeHelper.GetDpi(this));
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateCache(newDpi);
    }
    private void UpdateCache(DpiScale dpi)
    {
        CacheMode = (RenderCapability.Tier >> 16) > 0
            ? new BitmapCache { RenderAtScale = dpi.DpiScaleX, EnableClearType = true, SnapsToDevicePixels = true }
            : null;
    }
}
