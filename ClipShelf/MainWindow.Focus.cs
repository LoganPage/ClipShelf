using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace ClipShelf;

public partial class MainWindow
{
    private bool redirectingSettingsFocus;
    private int settingsFocusRequest;
    private UIElement? lastSettingsFocus;

    // An overlay is a modal focus boundary even though it shares the main HWND.
    // Restore keyboard focus to a real settings control, never its background owner.
    private bool FocusSettingsContent()
    {
        if (quitting || !IsVisible || !IsEnabled || !IsActive
            || SettingsOverlay.Visibility != Visibility.Visible) return false;
        if (SettingsContent.IsKeyboardFocusWithin) return true;
        if (redirectingSettingsFocus) return false;
        redirectingSettingsFocus = true;
        try
        {
            if (lastSettingsFocus is { IsVisible: true, IsEnabled: true, Focusable: true } previous
                && IsWithin(previous, SettingsContent))
                return SettingsContent.Content is SettingsPanel panel
                    ? panel.RestoreFocusWithoutScrolling(previous) : previous.Focus();
            // The footer is first in the DockPanel tree; begin in the appearance
            // section inside the scroller instead of accidentally focusing Done.
            var scroller = Descendant<ScrollViewer>(SettingsContent);
            var firstAppearanceChoice = scroller is null ? null : Descendant<Button>(scroller);
            return firstAppearanceChoice is { IsVisible: true, IsEnabled: true, Focusable: true }
                && firstAppearanceChoice.Focus() && SettingsContent.IsKeyboardFocusWithin;
        }
        finally { redirectingSettingsFocus = false; }
    }

    private void EnsureSettingsFocus()
    {
        if (SettingsOverlay.Visibility != Visibility.Visible || FocusSettingsContent()) return;
        int request = ++settingsFocusRequest;
        var content = SettingsContent.Content;
        Dispatcher.BeginInvoke(() => {
            // A closed/replaced overlay or a user switch to another application
            // must invalidate this delayed fallback instead of stealing focus.
            if (request == settingsFocusRequest && ReferenceEquals(content, SettingsContent.Content))
                FocusSettingsContent();
        }, DispatcherPriority.Input);
    }

    private void KeepFocusInsideSettings(KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is UIElement inside && IsWithin(inside, SettingsContent))
        { lastSettingsFocus = inside; return; }
        if (SettingsOverlay.Visibility != Visibility.Visible || redirectingSettingsFocus) return;
        // Only repair focus in this window's background. Separate ComboBox/menu
        // popup trees keep their normal keyboard interaction and focus ownership.
        if (e.NewFocus is DependencyObject target && IsWithin(target, this)
            && !IsWithin(target, SettingsContent)) EnsureSettingsFocus();
    }
}
