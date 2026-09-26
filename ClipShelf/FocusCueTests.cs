using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClipShelf;

public static class FocusCueTests
{
    private static MainWindow Fixture(string directory)
    {
        var store = new HistoryStore(Path.Combine(directory, "fixture-" + Guid.NewGuid().ToString("N")));
        store.Settings.HistoryEnabled = store.Settings.WatchScreenshots = store.Settings.LaunchAtLogin = false;
        for (int i = 0; i < 15; i++) store.Add(new ClipItem { Title = $"独立焦点验证记录 {i + 1:D2}", Text = $"Synthetic focus item {i}", CreatedAt = DateTimeOffset.Now.AddSeconds(-i) });
        return new MainWindow(store, demo: true) { Width = 900, Height = 800, Title = "ClipShelf · 焦点提示验证" };
    }
    public static async Task RunAsync(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        var checks = new List<string>(); string? error = null; MainWindow? window = null;
        double layoutDpiX = 0, layoutDpiY = 0;
        void Check(bool passed, string name) { if (!passed) throw new InvalidOperationException(name); checks.Add(name); }
        async Task Idle() => await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        try
        {
            window = Fixture(directory); window.Left = window.Top = -12000;
            window.WindowStartupLocation = WindowStartupLocation.Manual; window.ShowInTaskbar = false;
            window.Show(); window.Activate(); await Idle();
            var layoutDpi = VisualTreeHelper.GetDpi(window); layoutDpiX = layoutDpi.PixelsPerInchX; layoutDpiY = layoutDpi.PixelsPerInchY;
            var list = (HistoryListBox)window.FindName("HistoryList");
            var settings = (ContentControl)window.FindName("SettingsContent");
            var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            Check(window.Integration is null && FocusCuePolicy.GetIsEnabled(window), "Fixture is isolated; main window installs the shared input-intent policy");
            Check(!FocusCuePolicy.GetShowKeyboardFocus(window), "Opening/activation does not opt into keyboard outlines");
            foreach (var key in new[] { Key.LeftCtrl, Key.LeftShift, Key.S, Key.A, Key.Enter })
            {
                FocusCuePolicy.RecordKey(window, key, ModifierKeys.None);
                Check(!FocusCuePolicy.GetShowKeyboardFocus(row), $"{key} alone does not enable a spurious outline");
            }
            foreach (var key in new[] { Key.Tab, Key.Left, Key.Right, Key.Up, Key.Down, Key.Home, Key.End, Key.PageUp, Key.PageDown, Key.Apps })
            {
                FocusCuePolicy.Reset(window); FocusCuePolicy.RecordKey(window, key, ModifierKeys.None);
                Check(FocusCuePolicy.GetShowKeyboardFocus(row), $"{key} deliberately enables inherited keyboard focus cues");
            }
            FocusCuePolicy.RecordKey(window, Key.S, ModifierKeys.Windows | ModifierKeys.Shift);
            Check(!FocusCuePolicy.GetShowKeyboardFocus(row), "Screenshot shortcut intent hides the outline without clearing focus");
            FocusCuePolicy.RecordKey(window, Key.Tab, ModifierKeys.None);
            FocusCuePolicy.RecordKey(window, Key.Snapshot, ModifierKeys.None);
            Check(!FocusCuePolicy.GetShowKeyboardFocus(row), "PrintScreen also clears stale navigation intent");
            FocusCuePolicy.RecordKey(window, Key.Tab, ModifierKeys.None);
            FocusCuePolicy.RecordKey(window, Key.Tab, ModifierKeys.Alt);
            Check(!FocusCuePolicy.GetShowKeyboardFocus(row), "Switching apps does not retain navigation outlines");

            window.ApplyRowSelection(0, ModifierKeys.None); row.Focus(); await Idle();
            Check(row.IsKeyboardFocused, "The actual synthetic row owns keyboard focus");
            var probes = new List<(AdornerLayer Layer, ProbeAdorner Adorner)>();
            Border Probe(UIElement target, string resource)
            {
                var layer = AdornerLayer.GetAdornerLayer(target) ?? throw new InvalidOperationException("Missing adorner layer");
                var adorner = new ProbeAdorner(target, (Style)window.FindResource(resource));
                probes.Add((layer, adorner)); layer.Add(adorner);
                adorner.Measure(target.RenderSize); adorner.Arrange(new Rect(target.RenderSize)); adorner.UpdateLayout();
                return Descendants<Border>(adorner).Single();
            }
            ListBoxItem Row(int index) => (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index);
            async Task Navigate(Key key, ModifierKeys modifiers)
            {
                FocusCuePolicy.RecordKey(window, key, modifiers);
                Check(await window.HandleHistoryKeyAsync(key, modifiers, Keyboard.FocusedElement as DependencyObject),
                    $"The production history handler accepts {modifiers}+{key}");
                await Idle();
            }
            var marker = Probe(row, "RowKeyboardFocusVisual"); await Idle();
            Check(marker.Visibility == Visibility.Collapsed, "A mouse/programmatic-focused row has no extra focus decoration");
            Check(marker.Width == 2 && marker.HorizontalAlignment == HorizontalAlignment.Left
                && marker.BorderThickness == new Thickness(0) && marker.BorderBrush is null,
                "The row focus template is only a 2 DIP left marker, never a stroked row rectangle");

            await Navigate(Key.Down, ModifierKeys.None);
            var second = Row(1); var secondMarker = Probe(second, "RowKeyboardFocusVisual"); await Idle();
            Check(second.IsKeyboardFocused && second.IsSelected && list.SelectedItems.Count == 1,
                "Ordinary Down moves both selection and actual focus to the next row");
            Check(secondMarker.Visibility == Visibility.Collapsed && marker.Visibility == Visibility.Collapsed,
                "Ordinary Down shows only the selected fill, with no full-row outline or side marker");
            await Navigate(Key.Up, ModifierKeys.None);
            Check(row.IsKeyboardFocused && row.IsSelected && marker.Visibility == Visibility.Collapsed,
                "Ordinary Up selects the previous row without adding a focus outline");

            await Navigate(Key.Down, ModifierKeys.Shift);
            Check(row.IsSelected && second.IsSelected && list.SelectedItems.Count == 2
                && second.IsKeyboardFocused && secondMarker.Visibility == Visibility.Collapsed,
                "Shift+Down extends selection without framing its selected focused row");
            await Navigate(Key.Down, ModifierKeys.Shift);
            var third = Row(2); var thirdMarker = Probe(third, "RowKeyboardFocusVisual"); await Idle();
            Check(third.IsSelected && third.IsKeyboardFocused && list.SelectedItems.Count == 3
                && new[] { marker, secondMarker, thirdMarker }.All(visual => visual.Visibility == Visibility.Collapsed),
                "A three-row Shift range retains seamless selected fills and no row focus rectangles");

            var selectedBeforeControlNavigation = list.SelectedItems.Cast<ClipItem>().Select(item => item.Id).ToHashSet();
            await Navigate(Key.Down, ModifierKeys.Control);
            var fourth = Row(3); var fourthMarker = Probe(fourth, "RowKeyboardFocusVisual"); await Idle();
            Check(fourth.IsKeyboardFocused && !fourth.IsSelected
                && selectedBeforeControlNavigation.SetEquals(list.SelectedItems.Cast<ClipItem>().Select(item => item.Id)),
                "Ctrl+Down moves focus to an unselected row without changing the selected range");
            Check(fourthMarker.Visibility == Visibility.Visible && LayoutTestTolerance.Near(fourthMarker.ActualWidth, 2, fourthMarker)
                && fourthMarker.HorizontalAlignment == HorizontalAlignment.Left
                && fourthMarker.ActualHeight < fourth.ActualHeight
                && fourthMarker.BorderThickness == new Thickness(0) && fourthMarker.BorderBrush is null,
                "An unselected keyboard-focused row has only a short 2 DIP side marker, not an enclosing border");
            Check(ReferenceEquals(fourthMarker.Background, window.FindResource("MutedBrush")),
                "The unselected focus marker uses the neutral muted brush rather than accent blue");

            await Navigate(Key.Space, ModifierKeys.Control);
            Check(fourth.IsSelected && fourth.IsKeyboardFocused && fourthMarker.Visibility == Visibility.Collapsed,
                "Ctrl+Space selects the focused row and removes its now-redundant side marker");
            await Navigate(Key.Space, ModifierKeys.Control);
            Check(!fourth.IsSelected && fourth.IsKeyboardFocused && fourthMarker.Visibility == Visibility.Visible,
                "Ctrl+Space can deselect while preserving a minimal visible keyboard focus location");
            FocusCuePolicy.RecordKey(window, Key.Snapshot, ModifierKeys.None); await Idle();
            Check(fourthMarker.Visibility == Visibility.Collapsed && fourth.IsKeyboardFocused
                && selectedBeforeControlNavigation.SetEquals(list.SelectedItems.Cast<ClipItem>().Select(item => item.Id)),
                "Screenshot intent hides the side marker without clearing focus or selected records");
            await Navigate(Key.Down, ModifierKeys.None);
            var fifth = Row(4); var fifthMarker = Probe(fifth, "RowKeyboardFocusVisual"); await Idle();
            Check(fifth.IsKeyboardFocused && fifth.IsSelected && list.SelectedItems.Count == 1
                && fifthMarker.Visibility == Visibility.Collapsed && fourthMarker.Visibility == Visibility.Collapsed,
                "Returning to ordinary arrow selection removes all row focus markers and outlines");

            var copyButton = (Button)window.FindName("CopyButton");
            FocusCuePolicy.Reset(window); copyButton.Focus();
            var buttonOutline = Probe(copyButton, "KeyboardFocusVisual"); await Idle();
            Check(buttonOutline.Visibility == Visibility.Collapsed, "A mouse/programmatic-focused toolbar button has no spurious outline");
            FocusCuePolicy.RecordKey(window, Key.Tab, ModifierKeys.None); await Idle();
            Check(copyButton.IsKeyboardFocused && buttonOutline.Visibility == Visibility.Visible
                && buttonOutline.BorderThickness == new Thickness(2) && buttonOutline.CornerRadius == new CornerRadius(4),
                "Tab navigation still shows the normal four-sided focus outline on toolbar buttons");
            ((TextBox)window.FindName("SearchBox")).Focus(); await Idle();
            Check(buttonOutline.Visibility == Visibility.Collapsed && fifthMarker.Visibility == Visibility.Collapsed,
                "Stale button and row adorners remain hidden after their targets lose actual keyboard focus");
            foreach (var probe in probes) probe.Layer.Remove(probe.Adorner);

            FocusCuePolicy.Reset(window); window.OpenSettings(); await Idle();
            Check(settings.IsKeyboardFocusWithin, "Opening settings moves actual focus into the modal content");
            var focusedSetting = Keyboard.FocusedElement;
            window.Store.Add(new ClipItem { Title = "Synthetic screenshot arrival", Text = "No clipboard touched" }); await Idle();
            Check(settings.IsKeyboardFocusWithin && ReferenceEquals(focusedSetting, Keyboard.FocusedElement), "A screenshot-like history arrival cannot steal settings focus");
            window.ShowShelf(); await Idle();
            Check(settings.IsKeyboardFocusWithin, "Calling ShowShelf while settings is open keeps the modal focus boundary");
            row.Focus(); await Idle();
            Check(settings.IsKeyboardFocusWithin && !row.IsKeyboardFocused, "Focus restoration into background history is redirected into settings");
            window.CloseSettings(); await Idle();
            Check(((Grid)window.FindName("SettingsOverlay")).Visibility == Visibility.Collapsed, "Closing settings releases its modal boundary");
            FocusCuePolicy.RecordKey(window, Key.Tab, ModifierKeys.None);
            window.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = window });
            Check(!FocusCuePolicy.GetShowKeyboardFocus(window), "Mouse press immediately hides keyboard outlines");
            FocusCuePolicy.RecordKey(window, Key.Tab, ModifierKeys.None); window.Hide();
            Check(!FocusCuePolicy.GetShowKeyboardFocus(window), "Hiding the window clears outline intent");
            using var preview = new PreviewFixture();
            Check(FocusCuePolicy.GetIsEnabled(preview.Window), "Other windows sharing the focus styles also install the policy");
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            File.WriteAllText(Path.Combine(directory, "focus-results.json"), JsonSerializer.Serialize(new { passed = error is null, checks, error, layoutDpiX, layoutDpiY,
                scope = "Synthetic own-app WPF focus and adorner checks, including production arrow/Shift/Ctrl selection handlers; no clipboard access or system input. Shortcut lifecycle is simulated at policy level, not real Win+Shift+S." }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Quit(); if (window is null) Application.Current.Shutdown(error is null ? 0 : 1);
        }
    }

    public static void RunDemo(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        var window = Fixture(directory); Application.Current.MainWindow = window;
        var list = (HistoryListBox)window.FindName("HistoryList");
        var events = new List<object>();
        void Record(string kind) => events.Add(new { kind, cue = FocusCuePolicy.GetShowKeyboardFocus(window),
            focused = (Keyboard.FocusedElement as FrameworkElement)?.GetType().Name,
            settingsFocus = ((ContentControl)window.FindName("SettingsContent")).IsKeyboardFocusWithin,
            selected = list.SelectedItems.Count });
        var button = new Button { Content = "模拟截图返回", Style = (Style)window.FindResource("SoftButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(8,0,0,0) };
        var header = (StackPanel)window.FindName("HeaderPanel");
        header.Children.Insert(1, button);
        button.Click += (_, _) => { FocusCuePolicy.RecordKey(window, Key.Snapshot, ModifierKeys.None); FocusCuePolicy.Reset(window);
            window.Store.Add(new ClipItem { Title = "模拟截图 · 未访问系统剪贴板", Text = "Synthetic arrival " + Guid.NewGuid() }); Record("simulated-capture-return"); };
        window.PreviewKeyUp += (_, _) => Record("key-up");
        window.PreviewMouseUp += (_, _) => Record("pointer-up");
        window.PreviewKeyDown += (_, e) => { if (e.Key == Key.F12) { e.Handled = true; window.Quit(); } };
        window.Closed += (_, _) => File.WriteAllText(Path.Combine(directory, "focus-ui-events.json"), JsonSerializer.Serialize(events, new JsonSerializerOptions { WriteIndented = true }));
        window.Show();
    }

    private sealed class ProbeAdorner : Adorner
    {
        private readonly Control visual;
        internal ProbeAdorner(UIElement target, Style style) : base(target)
        { visual = new Control { Style = style, IsHitTestVisible = false }; AddVisualChild(visual); }
        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => visual;
        protected override Size MeasureOverride(Size size) { visual.Measure(AdornedElement.RenderSize); return AdornedElement.RenderSize; }
        protected override Size ArrangeOverride(Size size) { visual.Arrange(new Rect(size)); return size; }
    }
    private sealed class PreviewFixture : IDisposable
    {
        internal PreviewWindow Window { get; } = new(new[] { new ClipItem { Title = "Synthetic preview", Text = "Preview policy" } }, 0);
        internal PreviewFixture() { Window.Left = Window.Top = -12000; Window.ShowActivated = false; Window.ShowInTaskbar = false; Window.Show(); }
        public void Dispose() => Window.Close();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
}
