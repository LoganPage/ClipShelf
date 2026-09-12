using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClipShelf;

// Animate only flat surfaces. Text, glyphs and cached rows must not redraw every tick.
internal static class ThemeTransition
{
    private static readonly HashSet<string> surfaces = new() {
        "BackgroundBrush", "SurfaceBrush", "SearchBrush", "SettingsBrush",
        "SettingsCanvasBrush", "SettingsCardBrush", "SettingsControlBrush", "MenuSurfaceBrush"
    };
    private static readonly Dictionary<string, Color> targets = new();
    internal static int ActiveCount => targets.Count;
    internal static void Set(string name, Color color, bool animate)
    {
        var resources = Application.Current.Resources;
        var current = resources[name] as SolidColorBrush;
        if (animate && targets.TryGetValue(name, out var target) && target == color) return;
        if (current is not null && !current.HasAnimatedProperties && current.Color == color) return;
        if (!animate || !surfaces.Contains(name) || current is null)
        {
            targets.Remove(name);
            var final = new SolidColorBrush(color); final.Freeze(); resources[name] = final; return;
        }
        var brush = new SolidColorBrush(color);
        var animation = new ColorAnimation(current.Color, color, TimeSpan.FromMilliseconds(200)) {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        animation.Completed += (_, _) => {
            if (!ReferenceEquals(resources[name], brush)) return;
            targets.Remove(name);
            var final = new SolidColorBrush(color); final.Freeze(); resources[name] = final;
        };
        targets[name] = color;
        // Begin before publishing: templates can otherwise freeze a shared brush.
        brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
        resources[name] = brush;
    }
}
