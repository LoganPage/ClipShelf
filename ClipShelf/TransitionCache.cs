using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace ClipShelf;

/// <summary>
/// Rasterizes a sheet once while it fades, scales or shifts, so its text and controls are
/// composed as one bitmap instead of being re-rendered on every frame. The cache is removed
/// when the motion ends, so scrolling and editing inside the settled sheet stay uncached.
/// </summary>
internal static class TransitionCache
{
    // Beyond this a cache can exceed GPU texture limits and fall back to slower composition.
    private const double MaximumPixels = 4096;
    private static readonly ConditionalWeakTable<UIElement, BitmapCache> owned = new();

    internal static bool IsActive(UIElement element)
        => owned.TryGetValue(element, out var cache) && ReferenceEquals(element.CacheMode, cache);

    internal static void Begin(UIElement element)
    {
        if (element.CacheMode is not null || (RenderCapability.Tier >> 16) == 0) return;
        DpiScale dpi = VisualTreeHelper.GetDpi(element);
        Size size = element.RenderSize;
        if (size.Width * dpi.DpiScaleX > MaximumPixels || size.Height * dpi.DpiScaleY > MaximumPixels) return;
        // The sheets have opaque surfaces, so ClearType text matches the settled rendering.
        var cache = new BitmapCache { RenderAtScale = dpi.DpiScaleX, EnableClearType = true, SnapsToDevicePixels = true };
        owned.AddOrUpdate(element, cache);
        element.CacheMode = cache;
    }

    internal static void End(UIElement element)
    {
        if (!owned.TryGetValue(element, out var cache)) return;
        owned.Remove(element);
        if (ReferenceEquals(element.CacheMode, cache)) element.ClearValue(UIElement.CacheModeProperty);
    }
}
