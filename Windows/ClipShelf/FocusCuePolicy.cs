using System.Windows;
using System.Windows.Input;

namespace ClipShelf;

/// <summary>
/// Focus stays accessible, but its outline follows deliberate navigation rather
/// than WPF's last input device (which also includes screenshot shortcuts).
/// </summary>
public static class FocusCuePolicy
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(FocusCuePolicy), new PropertyMetadata(false, EnabledChanged));
    private static readonly DependencyPropertyKey ShowKeyboardFocusPropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "ShowKeyboardFocus", typeof(bool), typeof(FocusCuePolicy),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static readonly DependencyProperty ShowKeyboardFocusProperty = ShowKeyboardFocusPropertyKey.DependencyProperty;
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    public static bool GetShowKeyboardFocus(DependencyObject element) => (bool)element.GetValue(ShowKeyboardFocusProperty);

    private static void EnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not Window window) return;
        if ((bool)e.NewValue)
        {
            window.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnKeyDown), true);
            window.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnPointer), true);
            window.AddHandler(UIElement.PreviewTouchDownEvent, new System.EventHandler<TouchEventArgs>(OnTouch), true);
            window.Deactivated += OnDeactivated;
            window.IsVisibleChanged += OnVisibilityChanged;
        }
        else
        {
            window.RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnKeyDown));
            window.RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnPointer));
            window.RemoveHandler(UIElement.PreviewTouchDownEvent, new System.EventHandler<TouchEventArgs>(OnTouch));
            window.Deactivated -= OnDeactivated;
            window.IsVisibleChanged -= OnVisibilityChanged;
        }
        Reset(window);
    }

    internal static void Reset(Window window) => window.SetValue(ShowKeyboardFocusPropertyKey, false);

    internal static void RecordKey(Window window, Key key, ModifierKeys modifiers)
    {
        if ((modifiers & ModifierKeys.Windows) != 0 || key is Key.LWin or Key.RWin or Key.Snapshot
            || (key == Key.Tab && modifiers.HasFlag(ModifierKeys.Alt)))
        { Reset(window); return; }
        if ((modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return;
        bool navigation = key is Key.Tab or Key.Left or Key.Right or Key.Up or Key.Down
            or Key.Home or Key.End or Key.PageUp or Key.PageDown
            || (key == Key.Apps && modifiers == ModifierKeys.None)
            || (key == Key.F10 && modifiers == ModifierKeys.Shift);
        if (navigation) window.SetValue(ShowKeyboardFocusPropertyKey, true);
        // Modifier presses, ordinary typing, activation and data arrival do not
        // opt a mouse user into focus outlines. Existing keyboard intent persists.
    }

    private static void OnKeyDown(object sender, KeyEventArgs e) => RecordKey((Window)sender,
        e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key, Keyboard.Modifiers);
    private static void OnPointer(object sender, MouseButtonEventArgs e) => Reset((Window)sender);
    private static void OnTouch(object? sender, TouchEventArgs e) { if (sender is Window window) Reset(window); }
    private static void OnDeactivated(object? sender, System.EventArgs e) { if (sender is Window window) Reset(window); }
    private static void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    { if (!(bool)e.NewValue) Reset((Window)sender); }
}
