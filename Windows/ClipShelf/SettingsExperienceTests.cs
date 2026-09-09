using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClipShelf;

/// <summary>Isolated pin presentation, settings layout and real scroll-offset regression checks.</summary>
public static class SettingsExperienceTests
{
    public static void RunDemo(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        var store = new HistoryStore(Path.Combine(directory, "isolated"));
        store.Settings.WatchScreenshots = store.Settings.HistoryEnabled = store.Settings.LaunchAtLogin = false;
        store.Settings.ScreenshotFolder = @"C:\Pictures\Screenshots";
        var window = new MainWindow(store, demo: true) { Width = 900, Height = 760, Title = "ClipShelf · 设置连续性验证" };
        var events = new List<object>();
        void Record(string kind, object? target = null, int? delta = null)
        {
            var content = (ContentControl)window.FindName("SettingsContent");
            var scroll = FindAll<SmoothScrollViewer>(content).FirstOrDefault();
            events.Add(new { kind, delta, timestamp = Stopwatch.GetTimestamp(), target = target?.GetType().Name, offset = scroll?.VerticalOffset,
                focus = (Keyboard.FocusedElement as FrameworkElement)?.GetType().Name,
                focusName = (Keyboard.FocusedElement as FrameworkElement)?.Name });
        }
        window.AddHandler(FrameworkElement.RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler((_, e) => Record("bring-into-view", e.TargetObject)), true);
        window.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) => Record("scroll", e.OriginalSource)), true);
        window.GotKeyboardFocus += (_, e) => Record("focus", e.NewFocus);
        window.AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler((_, e) => Record("wheel", e.OriginalSource, e.Delta)), true);
        window.PreviewMouseUp += (_, _) => Record("pointer-up");
        window.PreviewKeyDown += (_, e) => { if (e.Key == Key.F12) { e.Handled = true; window.Quit(); } };
        window.Closed += (_, _) => File.WriteAllText(Path.Combine(directory, "settings-ui-events.json"), JsonSerializer.Serialize(events, new JsonSerializerOptions { WriteIndented = true }));
        window.Show();
    }

    public static async Task RunAsync(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        var checks = new List<string>(); var offsets = new List<double>();
        var openMilliseconds = new List<double>();
        string? error = null; MainWindow? window = null; SmoothScrollViewer? scroll = null;
        void Check(bool passed, string name) { if (!passed) throw new InvalidOperationException(name); checks.Add(name); }
        try
        {
            var store = new HistoryStore(Path.Combine(Path.GetTempPath(), "ClipShelf-settings-test-" + Guid.NewGuid().ToString("N")));
            store.Settings.WatchScreenshots = false; store.Settings.LaunchAtLogin = false;
            store.Settings.ScreenshotFolder = @"C:\Pictures\Screenshots";
            var first = new ClipItem { Kind = ClipKind.Text, Text = "独立示例 · 置顶图标", Title = "独立示例 · 置顶图标" };
            var second = new ClipItem { Kind = ClipKind.Text, Text = "独立示例 · 普通记录", Title = "独立示例 · 普通记录" };
            store.Add(first); store.Add(second);
            var restoredPin = new ClipItem { Kind = ClipKind.Text, Text = "启动前已置顶的独立示例", IsPinned = true }; store.Add(restoredPin);
            window = new MainWindow(store, demo: true) { Width = 900, Height = 780, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            async Task Idle() { await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
            await Idle();
            Check(window.Integration is null, "Fixture never connects to the user's clipboard or native services");
            var list = (HistoryListBox)window.FindName("HistoryList");
            var toolbar = (Button)window.FindName("PinButton");
            Button Pin(ClipItem item) => FindAll<Button>((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(item)).Single(b => b.Name == "RowPinButton");
            Check(ReferenceEquals(Pin(restoredPin).Foreground, window.FindResource("AccentBrush")), "Previously pinned record is accented on initial realization");
            foreach (var theme in new[] { "Light", "Dark" })
            {
                store.Settings.Theme = theme; window.ApplyPreferences(); await Idle();
                list.ReplaceSelection(new[] { first });
                var unpinned = Pin(first);
                Check(ReferenceEquals(unpinned.Foreground, window.FindResource("TextBrush")), theme + ": unpinned icon remains neutral");
                store.TogglePinned(new[] { first.Id }); await Idle();
                var pinned = Pin(first);
                Check(pinned.Content?.ToString() == "\uE718" && ReferenceEquals(pinned.Foreground, window.FindResource("AccentBrush")), theme + ": same pushpin glyph changes to accent immediately");
                Check(FindAll<TextBlock>(pinned).Any(t => t.Text == "\uE718" && ReferenceEquals(t.Foreground, window.FindResource("AccentBrush"))), theme + ": rendered pin glyph uses accent, not the implicit text brush");
                Check(AutomationProperties.GetName(pinned) == "取消置顶此记录" && pinned.ToolTip?.ToString() == "取消置顶此记录", theme + ": pin status retains accessible and hover descriptions");
                Check(!FindAll<TextBlock>(list).Any(t => t.Text == "已置顶"), theme + ": history rows have no redundant pinned label");
                Check(ReferenceEquals(toolbar.Foreground, window.FindResource("AccentBrush")), theme + ": selected pinned record also colors toolbar pin");
                Check(FindAll<TextBlock>(toolbar).Any(t => t.Text == "\uE718" && ReferenceEquals(t.Foreground, window.FindResource("AccentBrush"))), theme + ": rendered toolbar glyph also uses accent");
                Save((FrameworkElement)window.Content, Path.Combine(directory, "pin-" + theme.ToLowerInvariant() + ".png"));
                list.ReplaceSelection(new[] { first, second }); await Idle();
                Check(ReferenceEquals(toolbar.Foreground, window.FindResource("TextBrush")), theme + ": mixed selection does not claim all records are pinned");
                store.TogglePinned(new[] { first.Id }); await Idle();
                Check(ReferenceEquals(Pin(first).Foreground, window.FindResource("TextBrush")), theme + ": unpin restores neutral glyph");

                var overlay = (Grid)window.FindName("SettingsOverlay");
                var content = (ContentControl)window.FindName("SettingsContent");
                var settings = new SettingsPanel(window); content.Content = settings; overlay.Visibility = Visibility.Visible;
                await Idle(); scroll = FindAll<SmoothScrollViewer>(settings).Single();
                Check(scroll.Template.FindName("PART_VerticalScrollBar", scroll) is System.Windows.Controls.Primitives.ScrollBar { ActualWidth: <= 10 }, theme + ": settings scrollbar does not inherit the oversized system minimum width");
                Check(scroll.ScrollableHeight > 500 && !scroll.CanContentScroll, theme + ": settings uses physical continuous scrolling");
                DependencyObject? ancestor = scroll; bool noEffect = true;
                while (ancestor is not null) { if (ancestor is UIElement element && element.Effect is not null) noEffect = false; ancestor = VisualTreeHelper.GetParent(ancestor); }
                Check(noEffect && ((Border)window.FindName("SettingsShadow")).Effect is not null, theme + ": scrolling content has no effect surface; shadow is a static sibling");
                var toggle = FindAll<CheckBox>(settings).Single(c => c.Name == "DeselectOnRepeatedClickToggle");
                toggle.ApplyTemplate();
                var peer = new CheckBoxAutomationPeer(toggle);
                var provider = (IToggleProvider)peer.GetPattern(PatternInterface.Toggle);
                Check(provider.ToggleState == ToggleState.Off && toggle.Template.FindName("state", toggle) is TextBlock { Text: "关" }, theme + ": switch retains accessible off state and a visible label");
                provider.Toggle(); await Idle();
                Check(store.Settings.DeselectOnRepeatedClick && provider.ToggleState == ToggleState.On && toggle.Template.FindName("state", toggle) is TextBlock { Text: "开" }, theme + ": accessible toggle applies preference immediately");
                provider.Toggle(); await Idle();
                Check(!store.Settings.DeselectOnRepeatedClick, theme + ": switching off restores the original behavior");
                var buttons = FindAll<Button>(settings).ToArray();
                buttons.Single(b => b.Content?.ToString() == "浅色").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
                Check(buttons.SequenceEqual(FindAll<Button>(settings)), theme + ": changing theme does not rebuild the settings controls");
                buttons.Single(b => b.Content?.ToString() == (theme == "Light" ? "浅色" : "深色")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
                foreach (double width in new[] { 680.0, 900.0, 1200.0 })
                {
                    window.Width = width; scroll.ScrollToTop(); await Idle();
                    var cards = FindAll<Border>(settings).Where(b => b.Name == "SettingsGroupCard").ToArray();
                    Check(cards.Length >= 8 && cards.All(b => b.CornerRadius == new CornerRadius(8)), $"{theme}/{width}: grouped cards consistently use 8 DIP corners");
                    Check(cards.All(b => b.TransformToAncestor(scroll).TransformBounds(new Rect(b.RenderSize)).Right <= scroll.ActualWidth), $"{theme}/{width}: cards fit the viewport without horizontal clipping");
                    foreach (var button in buttons)
                    {
                        Check(button.TransformToAncestor(settings).TransformBounds(new Rect(button.RenderSize)).Right <= settings.ActualWidth + .1, $"{theme}/{width}: button fits inside settings");
                    }
                    Save((FrameworkElement)window.Content, Path.Combine(directory, $"settings-{theme.ToLowerInvariant()}-{width:0}.png"));
                    scroll.ScrollToVerticalOffset(700); await Idle();
                    Save((FrameworkElement)window.Content, Path.Combine(directory, $"settings-history-{theme.ToLowerInvariant()}-{width:0}.png"));
                }
                scroll.ScrollToVerticalOffset(200); await Idle();
                double before = scroll.VerticalOffset;
                scroll.HandleWheelDelta(-120);
                for (int i = 0; i < 35; i++) { await Task.Delay(16); offsets.Add(scroll.VerticalOffset); }
                Check(scroll.VerticalOffset > before && !scroll.IsWheelMotionActive, theme + ": notch wheel moves the actual settings offset and settles");
                Check(!SystemParameters.ClientAreaAnimation || offsets.Distinct().Count() > 3, theme + ": animated wheel exposes multiple intermediate physical offsets");
                // Route through the actual editor templates. Calling HandleWheelDelta
                // alone misses the native wheel fallback when the pointer crosses an editor.
                var wheelSources = FindAll<TextBox>(settings).Cast<UIElement>()
                    .Concat(FindAll<ComboBox>(settings))
                    .Concat(FindAll<ScrollViewer>(settings).Where(s => !ReferenceEquals(s, scroll) && s.ScrollableHeight == 0))
                    .Concat(new UIElement[] { toggle, FindAll<Button>(scroll).First() }).ToArray();
                foreach (var source in wheelSources)
                {
                    scroll.CancelWheelMotion(); scroll.ScrollToVerticalOffset(200); await Idle();
                    var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                        { RoutedEvent = Mouse.PreviewMouseWheelEvent };
                    source.RaiseEvent(wheel);
                    Check(wheel.Handled, theme + ": outer settings owns wheel over " + source.GetType().Name);
                    Check(!SystemParameters.ClientAreaAnimation || scroll.IsWheelMotionActive,
                        theme + ": editor crossing does not switch to native line jumps");
                    await Task.Delay(600); await Idle();
                    Check(Math.Abs(scroll.VerticalOffset - 200 - WheelScrollMotion.WheelDistance(-120, SystemParameters.WheelScrollLines, scroll.ViewportHeight)) < 1,
                        theme + ": routed editor wheel preserves full distance exactly once");
                }
                scroll.CancelWheelMotion(); scroll.ScrollToVerticalOffset(200); await Idle();
                scroll.HandleWheelDelta(-560); window.UpdateLayout();
                Check(!SystemParameters.ClientAreaAnimation || (scroll.IsWheelMotionActive && Math.Abs(scroll.VerticalOffset - 200) < .1),
                    theme + ": a large non-notch packet cannot jump directly by several lines");
                await Task.Delay(600); await Idle();
                Check(Math.Abs(scroll.VerticalOffset - 200 - WheelScrollMotion.WheelDistance(-560, SystemParameters.WheelScrollLines, scroll.ViewportHeight)) < 1,
                    theme + ": large packet keeps its full distance while settling");
                scroll.CancelWheelMotion(); scroll.ScrollToVerticalOffset(200); await Idle();
                scroll.HandleWheelDelta(-15); await Idle();
                double fineOffset = scroll.VerticalOffset;
                scroll.HandleWheelDelta(-120); window.UpdateLayout();
                Check(!SystemParameters.ClientAreaAnimation || (scroll.IsWheelMotionActive && Math.Abs(scroll.VerticalOffset - fineOffset) < .1),
                    theme + ": a fine packet does not make the next notch jump instantly");
                scroll.HandleWheelDelta(-15);
                await Task.Delay(600); await Idle();
                Check(Math.Abs(scroll.VerticalOffset - 200 - WheelScrollMotion.WheelDistance(-150, SystemParameters.WheelScrollLines, scroll.ViewportHeight)) < 1,
                    theme + ": mixed fine/notch packets keep the in-flight target and total distance");
                var choice = FindAll<ComboBox>(settings).First();
                int choiceBefore = choice.SelectedIndex;
                scroll.CancelWheelMotion(); scroll.ScrollToVerticalOffset(200); await Idle();
                foreach (var source in wheelSources.Take(4).Append(choice))
                {
                    source.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                        { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                }
                await Task.Delay(600); await Idle();
                Check(choice.SelectedIndex == choiceBefore, theme + ": rolling over a closed dropdown never changes the preference");
                Check(Math.Abs(scroll.VerticalOffset - 200 - WheelScrollMotion.WheelDistance(-600, SystemParameters.WheelScrollLines, scroll.ViewportHeight)) < 1,
                    theme + ": moving across editors during one wheel burst keeps its accumulated target");
                scroll.HandleWheelDelta(-120); await Task.Delay(30); scroll.CancelWheelMotion(); await Idle();
                double stopped = scroll.VerticalOffset; await Task.Delay(180);
                Check(Math.Abs(scroll.VerticalOffset - stopped) < .1 && !scroll.IsWheelMotionActive, theme + ": interruption stops residual scrolling");
                scroll.HandleWheelDelta(-15); await Idle();
                Check(Math.Abs(scroll.VerticalOffset - stopped - WheelScrollMotion.WheelDistance(-15, SystemParameters.WheelScrollLines, scroll.ViewportHeight)) < 1 && !scroll.IsWheelMotionActive, theme + ": precision wheel preserves small direct deltas");
                scroll.CancelWheelMotion(); scroll.HandleWheelDelta(-120); await Task.Delay(30);
                scroll.ScrollToVerticalOffset(123); await Idle(); await Task.Delay(180);
                Check(Math.Abs(scroll.VerticalOffset - 123) < 1 && !scroll.IsWheelMotionActive, theme + ": external navigation cancels the old scroll target");
                scroll.ScrollToBottom(); await Idle(); double bottom = scroll.VerticalOffset;
                scroll.HandleWheelDelta(-120); await Task.Delay(80);
                Check(Math.Abs(scroll.VerticalOffset - bottom) < 1 && !scroll.IsWheelMotionActive, theme + ": bottom edge does not overscroll");
                scroll.ScrollToTop(); await Idle(); scroll.HandleWheelDelta(120); await Task.Delay(80);
                Check(scroll.VerticalOffset == 0 && !scroll.IsWheelMotionActive, theme + ": top edge does not overscroll");
                scroll.HandleWheelDelta(-120); overlay.Visibility = Visibility.Collapsed; await Idle();
                Check(!scroll.IsWheelMotionActive, theme + ": hiding settings detaches the render callback");
                content.Content = null; await Idle(); Check(!scroll.IsWheelMotionActive, theme + ": unloading settings leaves no render callback");
                scroll = null;
            }
            // Exercise the production lifecycle and real focus boundary, not only
            // isolated check-box state changes with an inactive test window.
            window.Activate(); await Idle();
            var preloaded = new SettingsPanel(window);
            ((ContentControl)window.FindName("SettingsContent")).Content = preloaded;
            store.Settings.DeselectOnRepeatedClick = !store.Settings.DeselectOnRepeatedClick;
            preloaded.RefreshFromSettings();
            Check(!preloaded.IsLoaded, "Cached preferences can refresh safely before templates are loaded");
            window.OpenSettings(); await Idle();
            var liveContent = (ContentControl)window.FindName("SettingsContent");
            var livePanel = (SettingsPanel)liveContent.Content;
            var liveScroll = FindAll<SmoothScrollViewer>(livePanel).Single();
            var liveToggles = FindAll<CheckBox>(livePanel).ToArray();
            var background = (TextBox)window.FindName("SearchBox");
            foreach (var control in liveToggles)
            {
                control.BringIntoView(); await Idle(); control.Focus(); await Idle();
                double before = liveScroll.VerticalOffset;
                bool? initial = control.IsChecked;
                var provider = (IToggleProvider)new CheckBoxAutomationPeer(control).GetPattern(PatternInterface.Toggle);
                for (int i = 0; i < 4; i++)
                {
                    provider.Toggle(); await Idle();
                    background.Focus(); await Idle(); // Reproduce framework restoration to the modal's background.
                    Check(control.IsKeyboardFocused && Math.Abs(liveScroll.VerticalOffset - before) < .1,
                        $"{control.Content}/{i}: focus repair returns to the operated switch without jumping to the theme section");
                }
                Check(control.IsChecked == initial && ReferenceEquals(livePanel, liveContent.Content), $"{control.Content}: repeated switching does not replace the settings page");
                var knob = (FrameworkElement)control.Template.FindName("knob", control);
                // Dispatcher/composition clocks may start late on an occluded or locked
                // desktop. Assert eventual state, not a displayed frame-rate deadline.
                var settle = Stopwatch.StartNew();
                while (knob.RenderTransform is TranslateTransform moving && Math.Abs(moving.X - (initial == true ? 20 : 0)) >= .01 && settle.ElapsedMilliseconds < 1500)
                    await Task.Delay(25);
                Check(knob.RenderTransform is TranslateTransform shift && Math.Abs(shift.X - (initial == true ? 20 : 0)) < .01,
                    $"{control.Content}: interrupted switch motion settles to the real checked state (actual {(knob.RenderTransform as TranslateTransform)?.X}, checked {initial})");
            }
            double rememberedOffset = liveScroll.VerticalOffset;
            for (int i = 0; i < 6; i++)
            {
                window.CloseSettings(); await Idle();
                var timer = Stopwatch.StartNew(); window.OpenSettings(); timer.Stop(); openMilliseconds.Add(timer.Elapsed.TotalMilliseconds);
                await Idle();
                Check(ReferenceEquals(livePanel, liveContent.Content) && Math.Abs(liveScroll.VerticalOffset - rememberedOffset) < .1,
                    $"Open {i}: cached controls and scroll position survive close/reopen");
            }
            window.OpenSettings(); await Idle();
            Check(ReferenceEquals(livePanel, liveContent.Content) && Math.Abs(liveScroll.VerticalOffset - rememberedOffset) < .1,
                "Repeated OpenSettings while already open is idempotent");
            window.CloseSettings(); window.OpenSettings(); window.CloseSettings(); await Task.Delay(200); await Idle();
            var liveOverlay = (Grid)window.FindName("SettingsOverlay");
            Check(liveOverlay.Visibility == Visibility.Collapsed && !liveOverlay.HasAnimatedProperties && !liveScroll.IsWheelMotionActive,
                "Closing during the entrance transition removes animations and wheel callbacks; delayed focus cannot reopen it");
            store.Settings.DeselectOnRepeatedClick = true; store.Settings.ScreenshotFolder = @"C:\Pictures\NewScreenshots";
            window.OpenSettings(); await Idle();
            Check(liveToggles.Single(t => t.Name == "DeselectOnRepeatedClickToggle").IsChecked == true &&
                FindAll<TextBlock>(livePanel).Any(t => t.Text == store.Settings.ScreenshotFolder) &&
                ReferenceEquals(livePanel, liveContent.Content) && Math.Abs(liveScroll.VerticalOffset - rememberedOffset) < .1,
                "Cached controls refresh changed preferences and folder labels in place without resetting position");
            window.CloseSettings();
        }
        catch (Exception exception) { error = exception.ToString(); }
        finally
        {
            scroll?.CancelWheelMotion();
            File.WriteAllText(Path.Combine(directory, "settings-results.json"), JsonSerializer.Serialize(new { passed = error is null, checks, error, offsets, openMilliseconds,
                scope = "Synthetic isolated WPF settings and pin tests. Offset observations are not displayed frame-rate measurements. No real user history, clipboard or injected desktop input." }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Quit(); Application.Current.Shutdown(error is null ? 0 : 1);
        }
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T result) yield return result;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in FindAll<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void Save(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * 1.5), (int)Math.Ceiling(element.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
        bitmap.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
