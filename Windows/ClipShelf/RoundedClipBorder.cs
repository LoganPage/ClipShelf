using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClipShelf;

/// <summary>One retained clip, updated during arrange so content and rounded edges share a frame.</summary>
public sealed class RoundedClipBorder : Border
{
    private readonly RectangleGeometry roundedClip = new();
    private readonly RectangleGeometry contentClip = new();
    public RoundedClipBorder() => Clip = roundedClip;

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        var rect = new Rect(size);
        double radius = Math.Min(CornerRadius.TopLeft, Math.Min(size.Width, size.Height) / 2);
        if (roundedClip.Rect != rect) roundedClip.Rect = rect;
        if (roundedClip.RadiusX != radius) roundedClip.RadiusX = roundedClip.RadiusY = radius;
        if (Child is { } child)
        {
            // Match the inner contour to the border stroke; otherwise a selected square
            // row can paint over the curved border, leaving a differently shaped sliver.
            if (!ReferenceEquals(child.Clip, contentClip)) child.Clip = contentClip;
            var contentRect = new Rect(child.RenderSize);
            if (contentClip.Rect != contentRect) contentClip.Rect = contentRect;
            double radiusX = Math.Max(0, radius - BorderThickness.Left - Padding.Left);
            double radiusY = Math.Max(0, radius - BorderThickness.Top - Padding.Top);
            if (contentClip.RadiusX != radiusX) contentClip.RadiusX = radiusX;
            if (contentClip.RadiusY != radiusY) contentClip.RadiusY = radiusY;
        }
        return size;
    }
}
