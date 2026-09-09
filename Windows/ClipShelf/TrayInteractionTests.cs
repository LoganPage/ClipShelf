using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ClipShelf;

/// <summary>Isolated tray-menu layout and handler checks; never reads the system clipboard.</summary>
public static class TrayInteractionTests
{
    private const string FixtureMarker = "SYNTHETIC-TRAY-PRIVATE-BODY";

    public static async Task RunAsync(string reportDirectory)
    {
        reportDirectory = Path.GetFullPath(reportDirectory);
        Directory.CreateDirectory(reportDirectory);
        var checks = new List<object>();
        string? error = null;
        MainWindow? window = null;
        void Check(bool passed, string name)
        {
            checks.Add(new { name, passed });
            if (!passed) throw new InvalidOperationException(name);
        }
        try
        {
            var store = CreateFixture();
            window = new MainWindow(store, demo: true)
            {
                Left = -12000, Top = -12000, Width = 720, Height = 572,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = false, ShowInTaskbar = false
            };
            window.Show();
            async Task Idle()
            {
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
            }
            await Idle();
            Check(window.Integration is null, "Fixture does not connect clipboard, capture, paste, or global hotkey services");
            Check(store.Items.Count == 12 && store.Items.All(item => item.Text?.Contains(FixtureMarker) == true),
                "All twelve long multiline records are synthetic and isolated from daily history");

            var themeColors = new List<Color>();
            foreach (string theme in new[] { "Light", "Dark" })
            {
                store.Settings.Theme = theme;
                store.Settings.HistoryEnabled = true;
                window.ApplyPreferences();
                await Idle();
                var menu = window.BuildTrayContextMenu();
                Check(!menu.IsOpen && PresentationSource.FromVisual(menu) is null,
                    $"{theme}: menu fixture never opens a native popup");
                Check(ReferenceEquals(menu.Style, window.FindResource("FluentContextMenu")),
                    $"{theme}: tray shares the existing Fluent context-menu surface");
                Check(!menu.HasDropShadow, $"{theme}: no legacy native menu shadow is requested");
                var actions = menu.Items.OfType<MenuItem>().ToArray();
                var separators = menu.Items.OfType<Separator>().ToArray();
                Check(actions.Length == 5 && separators.Length == 2 && menu.Items.Count == 7,
                    $"{theme}: menu contains exactly five actions and two separators");
                Check(actions.Select(item => item.Header?.ToString()).SequenceEqual(new[]
                    { "打开 ClipShelf", "暂停历史记录", "选择截图文件夹…", "设置", "退出" }),
                    $"{theme}: tray exposes only concise application-level commands in the intended order");
                Check(actions.All(item => !item.HasItems), $"{theme}: no history submenus are embedded in the tray");
                Check(actions.All(item => item.Header is string label && !label.Contains('\n') && !label.Contains('\r')
                    && !label.Contains(FixtureMarker) && !store.Items.Any(clip => label.Contains(clip.Title))),
                    $"{theme}: no copied text, history title, or multiline record appears in the menu");
                Check(actions.All(item => item.Icon is TextBlock icon && !string.IsNullOrWhiteSpace(icon.Text)
                    && icon.FontFamily.Source.Contains("Segoe Fluent Icons")),
                    $"{theme}: every action uses a Segoe Fluent icon");
                Check(actions.All(item => ReferenceEquals(item.Style, window.FindResource("FluentMenuItem"))),
                    $"{theme}: every action shares the current Fluent row template");
                Check(actions.All(item => AutomationProperties.GetName(item) == item.Header?.ToString()),
                    $"{theme}: screen-reader names match the concise visible commands");
                Check(separators.All(item => ReferenceEquals(item.Style, window.FindResource("FluentMenuSeparator"))),
                    $"{theme}: both dividers share the Fluent separator template");

                Layout(menu);
                Check(menu.Template.FindName("MenuSurface", menu) is Border surface
                    && surface.CornerRadius == new CornerRadius(8) && surface.BorderThickness == new Thickness(1),
                    $"{theme}: menu surface has 8 DIP corners and one subtle outline");
                Check(actions.All(item => item.Template.FindName("MenuRow", item) is Border row
                    && row.CornerRadius == new CornerRadius(4)),
                    $"{theme}: action highlights have the shared 4 DIP corners");
                Check(separators.All(item => FindAll<Border>(item).Any(border => border.CornerRadius == new CornerRadius(0))),
                    $"{theme}: divider edges stay square");
                Check(actions.All(item => item.ActualHeight >= 34 && item.ActualHeight <= 44),
                    $"{theme}: each action retains a compact 34–44 DIP pointer target");
                Check(menu.ActualWidth >= 240 && menu.ActualWidth <= 400 && menu.ActualHeight >= 190 && menu.ActualHeight <= 270,
                    $"{theme}: complete tray menu remains compact rather than expanding with clipboard history");
                Check(FindAll<TextBlock>(menu).All(text => !text.Text.Contains(FixtureMarker)),
                    $"{theme}: realized visual tree also excludes the synthetic private history body");
                Check(menu.Background is SolidColorBrush, $"{theme}: menu surface resolves its theme brush");
                themeColors.Add(((SolidColorBrush)menu.Background).Color);
                foreach (double scale in new[] { 1.0, 1.5, 2.0 })
                {
                    string capture = Path.Combine(reportDirectory, $"tray-{theme.ToLowerInvariant()}-{scale * 100:0}.png");
                    Save(menu, capture, scale);
                    Check(File.Exists(capture) && new FileInfo(capture).Length > 1000,
                        $"{theme}/{scale * 100:0}%: detached menu raster generated successfully");
                }

                actions[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, actions[1]));
                await Idle();
                Check(!store.Settings.HistoryEnabled, $"{theme}: pause command updates only the fixture's history setting");
                var pausedMenu = window.BuildTrayContextMenu();
                var resume = pausedMenu.Items.OfType<MenuItem>().ElementAt(1);
                Check(resume.Header?.ToString() == "继续历史记录", $"{theme}: reopening after pause offers resume");
                resume.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, resume));
                await Idle();
                Check(store.Settings.HistoryEnabled, $"{theme}: resume command restores fixture history recording");
                Check(!menu.IsOpen && !pausedMenu.IsOpen && window.Integration is null,
                    $"{theme}: menu actions remain isolated and open no native popup or capture service");
            }
            Check(themeColors.Count == 2 && themeColors[0] != themeColors[1],
                "Light and dark tray menus resolve distinct theme-specific surfaces");

            window.Hide();
            await Idle();
            Check(!window.IsVisible, "Fixture is hidden before the single-left-click regression check");
            window.HandleTrayMouseClick(Forms.MouseButtons.Middle);
            await Idle();
            Check(!window.IsVisible, "Middle click is ignored rather than opening the application or a menu");
            window.HandleTrayMouseClick(Forms.MouseButtons.Left);
            await Idle();
            Check(window.IsVisible && window.WindowState == WindowState.Normal,
                "A single left-click handler call restores the hidden main page");
            window.HandleTrayMouseClick(Forms.MouseButtons.Left);
            await Idle();
            Check(window.IsVisible && window.WindowState == WindowState.Normal,
                "A second left click keeps the page open instead of toggling it back into the tray");
            window.WindowState = WindowState.Minimized;
            window.Hide();
            window.HandleTrayMouseClick(Forms.MouseButtons.Left);
            await Idle();
            Check(window.IsVisible && window.WindowState == WindowState.Normal,
                "Single left click also restores a previously minimized hidden window");
            Check(window.Left < -5000 && window.Top < -5000,
                "Handler checks keep the synthetic window outside the visible desktop");
            Check(window.Integration is null && store.Items.Count == 12,
                "Tray interactions neither attach native capture services nor mutate the synthetic history");
            await store.FlushAsync();
        }
        catch (Exception exception) { error = exception.ToString(); }
        finally
        {
            File.WriteAllText(Path.Combine(reportDirectory, "tray-results.json"), JsonSerializer.Serialize(new
            {
                passed = error is null, checks, error,
                scope = "Own-app off-screen WPF layout/raster and production tray-handler checks with twelve independently generated multiline records. No daily history, clipboard access, global input injection, right-click popup activation, or actual display frame-rate claim. Direct ShowShelf handler checks may focus the off-screen synthetic window."
            }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Quit();
            Application.Current.Shutdown(error is null ? 0 : 1);
        }
    }

    /// <summary>
    /// Real own-app popup/owner component checks. Framework events are raised only on
    /// this fixture's controls; this is not an end-to-end system-tray input test.
    /// </summary>
    public static async Task RunPopupAsync(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        var diagnostics = new List<object>();
        MainWindow? window = null;
        string? error = null;
        void Check(bool passed, string name)
        {
            checks.Add(new { name, passed });
            if (!passed) throw new InvalidOperationException(name);
        }
        try
        {
            var store = CreateFixture();
            store.Settings.Theme = "Light";
            window = new MainWindow(store, demo: true)
            {
                Left = -12000, Top = -12000, Width = 720, Height = 572,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = false, ShowInTaskbar = false
            };
            window.TrayMenuDiagnostic += message => diagnostics.Add(new { time = DateTimeOffset.Now, message });
            async Task Idle()
            {
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
            }
            async Task Settle(int milliseconds = 250)
            {
                await Task.Delay(milliseconds);
                await Idle();
            }
            ContextMenu? CurrentMenu() => ReadPrivate<ContextMenu>(window, "trayContextMenu");
            Window? CurrentHost() => ReadPrivate<Window>(window, "trayMenuHost");
            bool IsReleased(Window host) => !host.IsVisible && !Application.Current.Windows.Cast<Window>().Contains(host);
            async Task<(ContextMenu Menu, Window Host)> Open()
            {
                window.HandleTrayMouseClick(Forms.MouseButtons.Right);
                await Idle();
                var menu = CurrentMenu();
                var host = CurrentHost();
                Check(menu is { IsOpen: true } && host is { IsVisible: true },
                    "Right-click production handler creates its own real popup and live focus host");
                return (menu!, host!);
            }

            window.Show();
            await Idle();
            window.Hide();
            await Idle();
            Check(!window.IsVisible && window.Integration is null,
                "Main fixture starts hidden without clipboard, paste, capture, or hotkey integration");
            Check(ReadPrivate<Forms.NotifyIcon>(window, "tray") is null,
                "Native component fixture creates no actual notification icon or system-tray input path");

            var first = await Open();
            Check(first.Host.Owner is null && !first.Host.ShowInTaskbar && first.Host.Opacity == 0
                && first.Host.Width == 1 && first.Host.Height == 1,
                "Popup has an independent transparent 1 DIP owner, not the hidden main window");
            Check(first.Menu.Placement == System.Windows.Controls.Primitives.PlacementMode.MousePoint,
                "Native menu keeps cursor-based placement rather than anchoring to the tiny owner");
            await Settle(1000);
            Check(first.Menu.IsOpen && ReferenceEquals(first.Menu, CurrentMenu()),
                "Menu remains open for at least one second beyond the former 150ms dismissal race");
            Check(first.Menu.IsKeyboardFocusWithin,
                "Stable popup retains WPF keyboard focus without activating its own HWND");
            Check(!window.IsVisible && first.Host.IsVisible,
                "Opening and focusing the tray menu never reveals the hidden main page");
            Save(first.Menu, Path.Combine(directory, "tray-native-component.png"), 1.5);

            var openPage = first.Menu.Items.OfType<MenuItem>().First();
            Check(openPage.Header?.ToString() == "打开 ClipShelf", "Open-page command is the expected production menu action");
            openPage.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, openPage));
            await Settle();
            Check(window.IsVisible && window.WindowState == WindowState.Normal,
                "Framework click on the actual Open menu item restores the main page");
            Check(!first.Menu.IsOpen && CurrentMenu() is null && CurrentHost() is null && IsReleased(first.Host),
                "Open-page command closes its popup and fully releases the temporary host");
            Check(window.IsActive, "Delayed menu cleanup does not take activation back from the newly opened main page");

            window.Hide();
            await Idle();
            var escape = await Open();
            await Settle();
            Check(escape.Menu.IsKeyboardFocusWithin, "Reopened native menu has keyboard focus before Escape routing");
            var source = PresentationSource.FromVisual(escape.Menu);
            Check(source is not null, "Escape test is routed through the real menu's presentation source");
            var escapeKey = new KeyEventArgs(Keyboard.PrimaryDevice, source!, Environment.TickCount, Key.Escape)
            {
                RoutedEvent = Keyboard.KeyDownEvent
            };
            escape.Menu.RaiseEvent(escapeKey);
            await Settle();
            Check(escapeKey.Handled && !escape.Menu.IsOpen,
                "WPF menu KeyDown Escape path handles the event and dismisses the actual popup");
            Check(!window.IsVisible && CurrentMenu() is null && CurrentHost() is null && IsReleased(escape.Host),
                "Escape releases the owner while keeping the main page hidden");

            var old = await Open();
            await Settle();
            var replacement = await Open();
            Check(!ReferenceEquals(old.Menu, replacement.Menu) && !ReferenceEquals(old.Host, replacement.Host),
                "Repeated right click replaces the menu with its own independent owner");
            Check(!old.Menu.IsOpen && IsReleased(old.Host), "Replacement promptly releases the previous menu and owner");
            await Settle(300);
            Check(replacement.Menu.IsOpen && replacement.Host.IsVisible && replacement.Menu.IsKeyboardFocusWithin,
                "New menu survives the previous popup's real delayed-close interval");
            // Exercise the production Closed subscriber after a replacement is alive.
            // A late duplicate routed close is a component-level race fixture only.
            old.Menu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent, old.Menu));
            await Settle();
            Check(ReferenceEquals(replacement.Menu, CurrentMenu()) && ReferenceEquals(replacement.Host, CurrentHost())
                && replacement.Menu.IsOpen && replacement.Host.IsVisible && replacement.Menu.IsKeyboardFocusWithin,
                "Late Closed event from the old menu cannot clear, hide, or unfocus its replacement");
            Check(!window.IsVisible, "Replacement and late-close callbacks never reveal the main page");

            window.HandleTrayMouseClick(Forms.MouseButtons.Left);
            await Settle();
            Check(window.IsVisible && window.IsActive && CurrentMenu() is null && CurrentHost() is null
                && IsReleased(replacement.Host),
                "Left-click handler closes the live popup, releases its host, and activates the main page");
            window.HandleTrayMouseClick(Forms.MouseButtons.Right);
            window.HandleTrayMouseClick(Forms.MouseButtons.Left);
            await Settle();
            Check(window.IsVisible && CurrentMenu() is null && CurrentHost() is null,
                "Opening the main page cancels an older queued right-click menu request");
            Check(window.Integration is null && store.Items.Count == 12 && store.Items.All(item => item.Text?.Contains(FixtureMarker) == true),
                "Native popup component checks preserve the isolated synthetic records and never attach clipboard services");
            Check(window.Left < -5000 && window.Top < -5000,
                "Any restored synthetic main page remains outside the visible desktop");
            await store.FlushAsync();
        }
        catch (Exception exception) { error = exception.ToString(); }
        finally
        {
            File.WriteAllText(Path.Combine(directory, "tray-popup-results.json"), JsonSerializer.Serialize(new
            {
                passed = error is null, checks, diagnostics, error,
                scope = "Own-process native WPF ContextMenu/temporary-owner component tests. Uses synthetic isolated history and the production right/left handlers; opens real popup HWNDs and acquires native focus. Open-command Click, Escape KeyDown, and a delayed Closed race are framework events raised only on this fixture's controls. No SendInput, injected system input, actual notification-icon mouse/keyboard end-to-end claim, user clipboard, real history, screenshot watcher, or global hotkey. The main fixture is kept off-screen."
            }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Quit();
            Application.Current.Shutdown(error is null ? 0 : 1);
        }
    }

    private static T? ReadPrivate<T>(MainWindow window, string field) where T : class =>
        typeof(MainWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window) as T;

    /// <summary>Manual native-tray fixture. Opens only synthetic data and remains alive until its own Exit command.</summary>
    public static void RunDemo(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var store = CreateFixture();
        var window = new MainWindow(store, demo: true)
        {
            Title = "ClipShelf · 托盘界面测试", Width = 720, Height = 572,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        Application.Current.MainWindow = window;
        window.Show();
        window.InitializeTray("ClipShelf · 托盘界面测试");
        File.WriteAllText(Path.Combine(directory, "tray-demo.json"), JsonSerializer.Serialize(new
        {
            processId = Environment.ProcessId,
            fixtureDirectory = store.DirectoryPath,
            scope = "Manual UI fixture only. No clipboard listener, screenshot watcher, global hotkey, real history, or automatic input. Close this fixture using its tray Exit action."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static HistoryStore CreateFixture()
    {
        var store = new HistoryStore(Path.Combine(Path.GetTempPath(), "ClipShelf-tray-" + Guid.NewGuid().ToString("N")));
        store.Settings.WatchScreenshots = false;
        store.Settings.LaunchAtLogin = false;
        store.Settings.HistoryEnabled = true;
        store.Settings.ScreenshotFolder = Path.Combine(store.DirectoryPath, "synthetic-screenshots");
        for (int index = 0; index < 12; index++)
        {
            string text = $"{FixtureMarker}-{index}\n这是独立生成的测试内容，不来自用户剪贴板。\n"
                + string.Concat(Enumerable.Repeat("长记录不应撑开托盘菜单。", 20));
            store.Add(new ClipItem
            {
                Kind = ClipKind.Text, Title = text, Text = text,
                CreatedAt = new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero).AddSeconds(-index)
            });
        }
        return store;
    }

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T found) yield return found;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in FindAll<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static void Save(FrameworkElement element, string path, double scale)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * scale),
            (int)Math.Ceiling(element.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
