using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ClipShelf;

/// <summary>Immediate, cancellable pointer and keyboard feedback without changing ButtonBase semantics.</summary>
internal static class PressableMotion
{
    private sealed class State
    {
        internal required ScaleTransform Scale;
        internal required TranslateTransform Offset;
        internal Transform? OriginalTransform;
        internal Point OriginalOrigin;
        internal Window? Window;
        internal bool PointerPressed;
        internal bool PointerInside;
        internal bool KeyboardPressed;
    }

    private static readonly ConditionalWeakTable<ButtonBase, State> states = new();

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(PressableMotion), new PropertyMetadata(false, Changed));

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    private static void Changed(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not ButtonBase button) return;
        if ((bool)args.NewValue) Attach(button); else Detach(button);
    }

    private static void Attach(ButtonBase button)
    {
        if (states.TryGetValue(button, out _)) return;
        Transform originalTransform = button.RenderTransform;
        Point originalOrigin = button.RenderTransformOrigin;
        var scale = new ScaleTransform(1, 1);
        var offset = new TranslateTransform();
        var transforms = new TransformGroup(); transforms.Children.Add(scale); transforms.Children.Add(offset);
        button.RenderTransformOrigin = new Point(.5, .5);
        button.RenderTransform = transforms;
        states.Add(button, new State { Scale = scale, Offset = offset,
            OriginalTransform = originalTransform, OriginalOrigin = originalOrigin });
        button.Loaded += Loaded;
        button.Unloaded += Unloaded;
        button.MouseEnter += MouseEnter;
        button.MouseLeave += MouseLeave;
        button.PreviewMouseLeftButtonDown += PointerDown;
        button.PreviewMouseLeftButtonUp += PointerUp;
        button.LostMouseCapture += LostCapture;
        button.PreviewKeyDown += KeyDown;
        button.PreviewKeyUp += KeyUp;
        button.LostKeyboardFocus += LostKeyboardFocus;
        button.IsEnabledChanged += EnabledChanged;
    }

    private static void Detach(ButtonBase button)
    {
        if (!states.TryGetValue(button, out var state)) return;
        DetachWindow(button, state);
        Reset(button, state);
        button.RenderTransform = state.OriginalTransform ?? Transform.Identity;
        button.RenderTransformOrigin = state.OriginalOrigin;
        button.Loaded -= Loaded; button.Unloaded -= Unloaded;
        button.MouseEnter -= MouseEnter; button.MouseLeave -= MouseLeave;
        button.PreviewMouseLeftButtonDown -= PointerDown; button.PreviewMouseLeftButtonUp -= PointerUp;
        button.LostMouseCapture -= LostCapture; button.PreviewKeyDown -= KeyDown; button.PreviewKeyUp -= KeyUp;
        button.LostKeyboardFocus -= LostKeyboardFocus;
        button.IsEnabledChanged -= EnabledChanged;
        states.Remove(button);
    }

    private static void Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ButtonBase button || !states.TryGetValue(button, out var state)) return;
        state.Window = Window.GetWindow(button);
        if (state.Window is not null) state.Window.Deactivated += WindowDeactivated;
        Apply(button, false);
    }

    private static void Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ButtonBase button && states.TryGetValue(button, out var state))
        { DetachWindow(button, state); Reset(button, state); }
    }

    private static void DetachWindow(ButtonBase button, State state)
    {
        if (state.Window is not null) state.Window.Deactivated -= WindowDeactivated;
        state.Window = null;
    }

    private static void WindowDeactivated(object? sender, EventArgs e)
    {
        if (sender is not Window window) return;
        foreach (ButtonBase button in LogicalDescendants<ButtonBase>(window))
            if (states.TryGetValue(button, out var state)) Reset(button, state);
    }

    private static System.Collections.Generic.IEnumerable<T> LogicalDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is T match) yield return match;
            if (child is DependencyObject dependency)
                foreach (T descendant in LogicalDescendants<T>(dependency)) yield return descendant;
        }
    }

    private static void MouseEnter(object sender, MouseEventArgs e) { if (sender is ButtonBase button && states.TryGetValue(button, out var state)) { state.PointerInside = true; Apply(button, true); } }
    private static void MouseLeave(object sender, MouseEventArgs e) { if (sender is ButtonBase button && states.TryGetValue(button, out var state)) { state.PointerInside = false; Apply(button, true); } }
    private static void PointerDown(object sender, MouseButtonEventArgs e)
    { if (sender is ButtonBase button && states.TryGetValue(button, out var state)) { state.PointerInside = true; state.PointerPressed = true; Apply(button, true); } }
    private static void PointerUp(object sender, MouseButtonEventArgs e)
    { if (sender is ButtonBase button && states.TryGetValue(button, out var state)) { state.PointerPressed = false; Apply(button, true); } }
    private static void LostCapture(object sender, MouseEventArgs e)
    { if (sender is ButtonBase button && states.TryGetValue(button, out var state)) { state.PointerPressed = false; Apply(button, true); } }
    private static void KeyDown(object sender, KeyEventArgs e)
    {
        if (e.IsRepeat || e.Key is not (Key.Space or Key.Enter) || sender is not ButtonBase button || !states.TryGetValue(button, out var state)) return;
        state.KeyboardPressed = true; Apply(button, true);
    }
    private static void KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter) || sender is not ButtonBase button || !states.TryGetValue(button, out var state)) return;
        state.KeyboardPressed = false; Apply(button, true);
    }
    private static void LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is ButtonBase button && states.TryGetValue(button, out var state) && state.KeyboardPressed)
        { state.KeyboardPressed = false; Apply(button, true); }
    }
    private static void EnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    { if (sender is ButtonBase button && !button.IsEnabled && states.TryGetValue(button, out var state)) Reset(button, state); }

    private static void Reset(ButtonBase button, State state)
    {
        state.PointerPressed = state.PointerInside = state.KeyboardPressed = false;
        Apply(button, false);
    }

    private static void Apply(ButtonBase button, bool animate)
    {
        if (!states.TryGetValue(button, out var state)) return;
        bool pressed = button.IsEnabled && (state.PointerPressed && state.PointerInside || state.KeyboardPressed);
        bool hovered = button.IsEnabled && (state.PointerInside || button.IsMouseOver) && !pressed;
        double scale = pressed ? MotionTokens.PressScale : hovered ? 1.004 : 1;
        double offset = pressed ? MotionTokens.PressOffset : 0;
        if (!animate || !button.IsLoaded || !MotionPolicy.Allows(MotionDomain.MicroInteraction))
        {
            MotionDriver.Current.Snap(button, "press-x", scale, value => state.Scale.ScaleX = value);
            MotionDriver.Current.Snap(button, "press-y", scale, value => state.Scale.ScaleY = value);
            MotionDriver.Current.Snap(button, "press-offset", offset, value => state.Offset.Y = value);
            return;
        }
        MotionSpec spec = pressed ? MotionTokens.Press : MotionTokens.Hover;
        MotionDriver.Current.Animate(button, "press-x", state.Scale.ScaleX, scale, spec, value => state.Scale.ScaleX = value);
        MotionDriver.Current.Animate(button, "press-y", state.Scale.ScaleY, scale, spec, value => state.Scale.ScaleY = value);
        MotionDriver.Current.Animate(button, "press-offset", state.Offset.Y, offset, spec, value => state.Offset.Y = value);
    }
}
