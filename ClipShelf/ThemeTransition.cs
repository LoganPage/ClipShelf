using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ClipShelf;

// Animate only flat surfaces. Text, glyphs and cached rows must not redraw every tick.
internal static class ThemeTransition
{
    private sealed class TransitionState
    {
        internal required SolidColorBrush Brush;
        internal Color From;
        internal required Color Target;
        internal int Version;
    }

    private static readonly HashSet<string> surfaces = new() {
        "BackgroundBrush", "SurfaceBrush", "SearchBrush", "SettingsBrush",
        "SettingsCanvasBrush", "SettingsCardBrush", "SettingsControlBrush", "MenuSurfaceBrush"
    };
    private static readonly Dictionary<string, TransitionState> active = new();
    internal static int ActiveCount => active.Count;
    internal static void Set(string name, Color color, bool animate)
    {
        var resources = Application.Current.Resources;
        var current = resources[name] as SolidColorBrush;
        if (animate && active.TryGetValue(name, out var running) && running.Target == color) return;
        if (current is not null && !active.ContainsKey(name) && current.Color == color) return;
        if (!animate || !surfaces.Contains(name) || current is null)
        {
            if (active.Remove(name, out var prior)) MotionDriver.Current.Cancel(prior.Brush);
            var final = new SolidColorBrush(color); final.Freeze(); resources[name] = final; return;
        }

        TransitionState state;
        if (active.TryGetValue(name, out var existing) && ReferenceEquals(existing.Brush, current)) state = existing;
        else
        {
            if (existing is not null) MotionDriver.Current.Cancel(existing.Brush);
            var brush = new SolidColorBrush(current.Color);
            // Shared dynamic-resource brushes may be frozen by WPF as soon as they
            // are published. A constant binding keeps this short-lived transition
            // brush mutable without introducing an AnimationClock.
            BindingOperations.SetBinding(brush, Brush.OpacityProperty, new Binding { Source = 1d });
            resources[name] = brush;
            state = new TransitionState { Brush = brush, Target = color };
            active[name] = state;
        }
        // One channel per brush: the color changes once per frame instead of once per
        // ARGB component, so every surface using it is invalidated a single time.
        MotionDriver.Current.Cancel(state.Brush);
        state.From = state.Brush.Color;
        state.Target = color;
        int version = ++state.Version;
        // Progress runs in byte units of the largest component change, so the shared
        // Theme tolerances keep meaning "within one color step" for every transition.
        double distance = Math.Max(Math.Max(Math.Abs(color.A - state.From.A), Math.Abs(color.R - state.From.R)),
            Math.Max(Math.Abs(color.G - state.From.G), Math.Abs(color.B - state.From.B)));
        MotionDriver.Current.Animate(state.Brush, "theme-color", 0, Math.Max(distance, 1), MotionTokens.Theme,
            value => ApplyProgress(name, state, version, value / Math.Max(distance, 1)),
            () => Complete(name, state, version));
    }

    private static void ApplyProgress(string name, TransitionState state, int version, double progress)
    {
        if (!active.TryGetValue(name, out var currentState) || !ReferenceEquals(currentState, state) || state.Version != version) return;
        static byte Mix(byte from, byte to, double t) => (byte)Math.Clamp(Math.Round(from + (to - from) * t), 0, 255);
        Color from = state.From, to = state.Target;
        state.Brush.Color = Color.FromArgb(Mix(from.A, to.A, progress), Mix(from.R, to.R, progress),
            Mix(from.G, to.G, progress), Mix(from.B, to.B, progress));
    }

    private static void Complete(string name, TransitionState state, int version)
    {
        if (!active.TryGetValue(name, out var currentState) || !ReferenceEquals(currentState, state) || state.Version != version) return;
        active.Remove(name);
        MotionDriver.Current.Cancel(state.Brush);
        var final = new SolidColorBrush(state.Target); final.Freeze();
        Application.Current.Resources[name] = final;
    }
}
