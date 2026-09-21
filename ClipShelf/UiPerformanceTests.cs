using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClipShelf;

public static class UiPerformanceTests
{
    public static async Task RunAsync(string reportPath)
    {
        var checks = new List<string>(); var metrics = new Dictionary<string, double>();
        MainWindow? window = null; string? error = null;
        void Check(bool result, string name) { if (!result) throw new InvalidOperationException(name); checks.Add(name); }
        async Task Idle() { await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
        try
        {
            string directory = Path.Combine(Path.GetTempPath(), "ClipShelf-ui-performance-" + Guid.NewGuid().ToString("N"));
            var seed = Enumerable.Range(0, 1000).Select(i => new ClipItem { Title = $"记录 {i:D4} · 界面流畅度测试", Text = $"独立合成数据 {i:D4}", CreatedAt = DateTimeOffset.Now.AddSeconds(-i) }).ToArray();
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "history.json"), JsonSerializer.Serialize(seed));
            File.WriteAllText(Path.Combine(directory, "settings.json"), "{\"MaxItems\":1000}");
            var store = new HistoryStore(directory);
            window = new MainWindow(store, demo: true) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -12000, Top = -12000, ShowActivated = false };
            window.Show(); await Idle();
            var list = (HistoryListBox)window.FindName("HistoryList");
            var search = (TextBox)window.FindName("SearchBox");
            var scroll = Find<ScrollViewer>(list)!;
            var originalSource = list.ItemsSource;
            int resets = 0;
            ((INotifyCollectionChanged)list.ItemsSource).CollectionChanged += (_, e) => { if (e.Action == NotifyCollectionChangedAction.Reset) resets++; };
            Check(list.Items.Count == 1000, "1000 synthetic rows loaded");
            Check(VirtualizingPanel.GetScrollUnit(list) == ScrollUnit.Pixel, "pixel scrolling enabled with virtualization");
            Check(Enumerable.Range(0, 1000).Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) is not null) < 40, "1000 rows do not create 1000 visual containers");
            var range = (Action<int, int>)typeof(MainWindow).GetMethod("SelectRange", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate(typeof(Action<int, int>), window);
            int events = 0; list.SelectionChanged += (_, _) => events++;
            range(0, 10); range(0, 20); list.UnselectAll(); events = 0;
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 300; i++) { list.UnselectAll(); for (int j = 0; j <= 30 + (i / 10) % 20; j++) list.SelectedItems.Add(list.Items[j]); }
            watch.Stop(); metrics["baselineDragMs"] = watch.Elapsed.TotalMilliseconds; metrics["baselineSelectionEvents"] = events;
            list.UnselectAll(); events = 0; watch.Restart();
            for (int i = 0; i < 300; i++) range(0, 30 + (i / 10) % 20);
            watch.Stop(); metrics["optimizedDragMs"] = watch.Elapsed.TotalMilliseconds; metrics["optimizedSelectionEvents"] = events;
            Check(events <= 30, "drag changes emit at most one selection event per new endpoint");
            int before = events; range(0, 39); Check(events == before, "stationary drag does not rebuild selection");
            Check(list.SelectedItems.Count == 40, "drag range retains exact selection");
            range(39, 10); Check(list.SelectedItems.Count == 30 && list.SelectedItems.Contains(list.Items[10]) && list.SelectedItems.Contains(list.Items[39]), "reversed range remains correct");
            list.UnselectAll();
            var chosen = (ClipItem)list.Items[25]; list.ReplaceSelection(new[] { chosen });
            store.TogglePinned(new[] { chosen.Id }); await Idle();
            Check(ReferenceEquals(list.ItemsSource, originalSource) && resets == 0, "pin update preserves collection without resets");
            Check(list.SelectedItems.Cast<ClipItem>().Single().Id == chosen.Id && ((ClipItem)list.Items[0]).Id == chosen.Id, "pin moves row and preserves selection");
            scroll.ScrollToVerticalOffset(740 + 17); await Idle();
            var topId = ((ClipItem)list.Items[(int)(scroll.VerticalOffset / 74)]).Id;
            double remainder = scroll.VerticalOffset % 74;
            store.Add(new ClipItem { Title = "刚复制的新记录", Text = "new fixture", CreatedAt = DateTimeOffset.Now }); await Idle();
            Check(((ClipItem)list.Items[(int)(scroll.VerticalOffset / 74)]).Id == topId && Math.Abs(scroll.VerticalOffset % 74 - remainder) < 1, "incoming history preserves top row and pixel offset");
            Check(list.SelectedItems.Cast<ClipItem>().Single().Id == chosen.Id, "incoming history preserves selection");
            var held = (ClipItem)list.Items[15];
            typeof(MainWindow).GetField("pointerDown", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            store.Add(new ClipItem { Title = "按住期间进入的记录", Text = "pointer fixture" }); await Idle();
            Check(((ClipItem)list.Items[15]).Id == held.Id, "pointer-down freezes row identity before drag threshold");
            typeof(MainWindow).GetMethod("EndDrag", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { true }); await Idle();
            Check(list.Items.Cast<ClipItem>().Any(item => item.Text == "pointer fixture"), "pointer release applies deferred history changes");
            search.Text = "does-not-exist-unique"; search.Text = "刚复制"; await window.PendingSearch; await Idle();
            Check(list.Items.Count == 1 && ((ClipItem)list.Items[0]).Title == "刚复制的新记录", "latest search wins after rapid typing");
            Check(list.SelectedItems.Count == 0 && scroll.VerticalOffset == 0, "query change clears selection and starts at top");
            search.Clear(); await window.PendingSearch; await Idle();
            Check(list.Items.Count == 1000 && ReferenceEquals(list.ItemsSource, originalSource) && resets == 0, "clearing search restores rows without collection reset");
            var brush = Application.Current.Resources["SurfaceBrush"]; ThemeManager.Apply(store.Settings);
            Check(ReferenceEquals(brush, Application.Current.Resources["SurfaceBrush"]), "unchanged theme does not invalidate brushes");
            Check(ReferenceEquals(ThemeManager.Icon(1), ThemeManager.Icon(1)), "application icons use stable frozen cache");
            IconAssetTests.Verify(Check);
            for (int i = 0; i < 80; i++) { scroll.ScrollToVerticalOffset(i * 113.5); await Idle(); }
            Check(Enumerable.Range(0, 1000).Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) is not null) < 40, "scroll stress retains bounded recycled containers");
            Check(await ThumbnailLoader.LoadAsync(Path.Combine(directory, "missing.png")) is null, "missing thumbnail degrades without UI exception");
            foreach (var size in new[] { (Width: 300, Height: 12000), (Width: 12000, Height: 300), (Width: 32, Height: 32) }) {
                string path = Path.Combine(directory, $"thumbnail-{size.Width}-{size.Height}.png");
                var pixels = new byte[size.Width * size.Height * 4];
                var image = System.Windows.Media.Imaging.BitmapSource.Create(size.Width, size.Height, 96, 96, PixelFormats.Bgra32, null, pixels, size.Width * 4);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image)); using (var output = File.Create(path)) encoder.Save(output);
                var thumbnail = await ThumbnailLoader.LoadAsync(path);
                Check(thumbnail is not null && thumbnail.IsFrozen && thumbnail.PixelWidth <= 156 && thumbnail.PixelHeight <= 120, $"Thumbnail {size.Width}x{size.Height} has bounded decoded width AND height");
                Check(ReferenceEquals(thumbnail, await ThumbnailLoader.LoadAsync(path)), "Thumbnail cache avoids repeated decoding");
            }
            var caption = window.FindResource("CaptionButton") as Style;
            Check(caption is not null && (double)caption.Setters.OfType<Setter>().Single(s => s.Property == Control.FontSizeProperty).Value == 10, "Caption icons use a separate compact style");
            var historyBar = Find<System.Windows.Controls.Primitives.ScrollBar>(list)!;
            window.OpenSettings(); await Task.Delay(180); await Idle();
            var settingsScroller = Find<SmoothScrollViewer>((ContentControl)window.FindName("SettingsContent"))!;
            var settingsBar = (System.Windows.Controls.Primitives.ScrollBar)settingsScroller.Template.FindName("PART_VerticalScrollBar", settingsScroller);
            Check(historyBar.Width == settingsBar.Width && historyBar.Margin == settingsBar.Margin, "History and settings scrollbar width and margins match");
            var historyThumb = Find<System.Windows.Controls.Primitives.Thumb>(historyBar)!;
            var settingsThumb = Find<System.Windows.Controls.Primitives.Thumb>(settingsBar)!;
            Check(ReferenceEquals(historyThumb.Style, settingsThumb.Style) && Math.Abs(historyThumb.ActualWidth - settingsThumb.ActualWidth) < .1, "Both scrollbars share thumb style and actual width");
            var states = historyThumb.Template.Triggers.OfType<Trigger>().Select(t => t.Property).ToArray();
            Check(states.Contains(UIElement.IsMouseOverProperty) && states.Contains(System.Windows.Controls.Primitives.Thumb.IsDraggingProperty), "Thumb defines hover and active-drag feedback");
            foreach (var theme in new[] { "Light", "Dark" }) {
                ThemeManager.Apply(new AppSettings { Theme = theme });
                var colors = new[] { "ScrollThumbBrush", "ScrollThumbHoverBrush", "ScrollThumbDragBrush" }.Select(k => ((SolidColorBrush)Application.Current.FindResource(k)).Color).ToArray();
                Check(colors.Distinct().Count() == 3, theme + " scrollbar states have distinct colors");
            }
        }
        catch (Exception ex) { error = ex.ToString(); Environment.ExitCode = 1; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { passed = error is null, checks, metrics, error, scope = "isolated synthetic WPF fixtures; no desktop input or clipboard access" }, new JsonSerializerOptions { WriteIndented = true }));
        if (window is not null) window.Quit(); else Application.Current.Shutdown();
    }
    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T result) return result;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) if (Find<T>(VisualTreeHelper.GetChild(root, i)) is T found) return found;
        return null;
    }
}
