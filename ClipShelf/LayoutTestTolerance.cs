using System;
using System.Windows.Media;

namespace ClipShelf;

/// <summary>Test-only tolerance for layout rounding: one device pixel plus a small floating-point margin.</summary>
internal static class LayoutTestTolerance
{
    internal static double Dip(Visual visual)
    {
        double scale = VisualTreeHelper.GetDpi(visual).DpiScaleX;
        return 1.0 / Math.Max(scale, .01) + .05;
    }

    internal static bool Near(double actual, double expected, Visual visual) =>
        Math.Abs(actual - expected) <= Dip(visual);

    internal static bool NearAtScale(double actual, double expected, double scale) =>
        Math.Abs(actual - expected) <= 1.0 / Math.Max(scale, .01) + .05;
}
