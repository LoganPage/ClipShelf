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
        internal required Color Target;
        internal int Version;
        internal int Pending;
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
        state.Target = color;
        state.Version++;
        state.Pending = 4;
        int version = state.Version;
        StartComponent(name, state, version, "a", state.Brush.Color.A, color.A);
        StartComponent(name, state, version, "r", state.Brush.Color.R, color.R);
        StartComponent(name, state, version, "g", state.Brush.Color.G, color.G);
        StartComponent(name, state, version, "b", state.Brush.Color.B, color.B);
    }

    private static void StartComponent(string name, TransitionState state, int version, string component, byte from, byte to)
    {
        MotionDriver.Current.Animate(state.Brush, "theme-" + component, from, to, MotionTokens.Theme,
            value => ApplyComponent(name, state, version, component, value),
            () => CompleteComponent(name, state, version));
    }

    private static void ApplyComponent(string name, TransitionState state, int version, string component, double value)
    {
        if (!active.TryGetValue(name, out var currentState) || !ReferenceEquals(currentState, state) || state.Version != version) return;
        byte channel = (byte)Math.Clamp(Math.Round(value), 0, 255);
        Color current = state.Brush.Color;
        state.Brush.Color = component switch
        {
            "a" => Color.FromArgb(channel, current.R, current.G, current.B),
            "r" => Color.FromArgb(current.A, channel, current.G, current.B),
            "g" => Color.FromArgb(current.A, current.R, channel, current.B),
            _ => Color.FromArgb(current.A, current.R, current.G, channel)
        };
    }

    private static void CompleteComponent(string name, TransitionState state, int version)
    {
        if (!active.TryGetValue(name, out var currentState) || !ReferenceEquals(currentState, state) || state.Version != version) return;
        if (--state.Pending != 0) return;
        active.Remove(name);
        MotionDriver.Current.Cancel(state.Brush);
        var final = new SolidColorBrush(state.Target); final.Freeze();
        Application.Current.Resources[name] = final;
    }
}
