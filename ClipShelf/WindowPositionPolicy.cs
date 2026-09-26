using System;
using System.Collections.Generic;
using System.Windows;

namespace ClipShelf;

internal static class WindowPositionPolicy
{
    internal const double MinimumVisibleTitleWidth = 120;
    internal const double TitleHeight = 32;

    internal static bool IsReachable(double left, double top, double width, IReadOnlyList<Rect> workAreas)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(width) || width <= 0) return false;
        foreach (Rect area in workAreas)
        {
            if (area.IsEmpty || area.Width < MinimumVisibleTitleWidth || area.Height < TitleHeight) continue;
            double visibleLeft = Math.Max(left, area.Left);
            double visibleRight = Math.Min(left + width, area.Right);
            if (visibleRight - visibleLeft >= MinimumVisibleTitleWidth && top >= area.Top && top + TitleHeight <= area.Bottom)
                return true;
        }
        return false;
    }
}
