using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClipShelf;

internal static class HistoryTypeFilterTests
{
    internal static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var checks = new List<string>(); string? error = null; MainWindow? window = null;
        void Check(bool success, string name) { if (!success) throw new InvalidOperationException(name); checks.Add(name); }
        try
        {
            string data = Path.Combine(directory, "data");
            var store = new HistoryStore(data);
            var textAlpha = new ClipItem { Kind = ClipKind.Text, Title = "项目 Alpha", Text = "项目 Alpha" };
            var textBeta = new ClipItem { Kind = ClipKind.Text, Title = "备忘 Beta", Text = "备忘 Beta" };
            var fileAlpha = new ClipItem { Kind = ClipKind.File, Title = "Alpha 文件", FilePaths = new() { Path.Combine(directory, "alpha.txt") } };
            var image = new ClipItem { Kind = ClipKind.Image, Title = "示例图片", ImagePath = Path.Combine(directory, "image.png") };
            store.Add(textAlpha); store.Add(textBeta); store.Add(fileAlpha); store.Add(image);
            window = new MainWindow(store, demo: true) { Width = 900, Height = 720, Left = -12000, Top = -12000, ShowActivated = false, ShowInTaskbar = false };
            window.Show(); await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var list = (HistoryListBox)window.FindName("HistoryList");
            var search = (TextBox)window.FindName("SearchBox");
            var emptyTitle = (TextBlock)window.FindName("EmptyTitle");
            Button Filter(string name) => (Button)window.FindName(name);
            void Click(string name) => Filter(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ClipItem[] Visible() => list.Items.Cast<ClipItem>().ToArray();
            async Task Idle() => await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            async Task KeyboardActivate(string name)
            {
                Button button = Filter(name); button.Focus(); await Idle();
                PresentationSource source = PresentationSource.FromVisual(button) ?? throw new InvalidOperationException("Missing presentation source");
                button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent });
                button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.KeyUpEvent });
                await Idle();
            }

            Check(Visible().Length == 4, "All filter initially shows every record");
            Click("FilterFileButton");
            Check(Visible().Length == 1 && Visible()[0].Kind == ClipKind.File, "File filter shows only file records");
            Click("FilterAllButton");
            Check(Visible().Length == 4, "Returning to All is equivalent to no type filter");
            list.ReplaceSelection(new[] { textAlpha, fileAlpha });
            Click("FilterTextButton");
            Check(Visible().Length == 2 && Visible().All(item => item.Kind == ClipKind.Text), "Text filter shows only text records");
            Check(list.SelectedItems.Count == 1 && ReferenceEquals(list.SelectedItems[0], textAlpha), "Filtering removes hidden selections and preserves visible selections");
            search.Text = "Alpha"; await window.PendingSearch;
            Check(Visible().Length == 1 && Visible()[0].Id == textAlpha.Id, "Type filter and keyword search use AND semantics");
            search.Clear(); await window.PendingSearch;
            Check(Visible().Length == 2 && Visible().All(item => item.Kind == ClipKind.Text), "Clearing the query keeps the active type filter");

            Click("FilterImageButton");
            Check(Visible().Length == 1 && Visible()[0].Kind == ClipKind.Image, "Image filter works on the empty-query fast path");
            store.Remove(new[] { image.Id }); window.Refresh();
            Check(Visible().Length == 0 && emptyTitle.Text == "没有此类型的记录", "Type-only empty state is distinct from empty history");
            Click("FilterAllButton"); search.Text = "绝不匹配"; await window.PendingSearch;
            Check(Visible().Length == 0 && emptyTitle.Text == "没有匹配的记录", "Search empty state is distinct from type filtering");
            search.Clear(); store.Clear(); window.Refresh();
            Check(Visible().Length == 0 && emptyTitle.Text == "还没有历史记录", "Empty history has its own message");

            for (int i = 0; i < 72; i++) store.Add(new ClipItem {
                Kind = i % 3 == 0 ? ClipKind.File : i % 3 == 1 ? ClipKind.Image : ClipKind.Text,
                Title = $"滚动筛选 Alpha {i:D2}", Text = $"Alpha {i:D2}",
                FilePaths = i % 3 == 0 ? new() { Path.Combine(directory, $"scroll-{i:D2}.txt") } : new(),
                ImagePath = i % 3 == 1 ? Path.Combine(directory, $"scroll-{i:D2}.png") : null
            });
            Click("FilterAllButton"); window.Refresh(); await Idle();
            var scroll = Descendants<ScrollViewer>(list).First();
            scroll.ScrollToVerticalOffset(700); await Idle();
            Check(scroll.VerticalOffset > 0, "Long filter fixture can be scrolled away from the top");
            await KeyboardActivate("FilterFileButton");
            Check(StoreFilter(window) == HistoryTypeFilter.File && scroll.VerticalOffset == 0, "Keyboard filter change returns the list to the top");
            scroll.ScrollToVerticalOffset(350); await Idle(); double repeatedOffset = scroll.VerticalOffset;
            await KeyboardActivate("FilterFileButton");
            Check(repeatedOffset > 0 && Math.Abs(scroll.VerticalOffset - repeatedOffset) < .01, "Keyboard activation of the selected filter preserves scroll position");
            search.Text = "Alpha"; await window.PendingSearch; await Idle();
            Check(scroll.VerticalOffset == 0, "Keyword search still resets the filtered list to the top");
            scroll.ScrollToVerticalOffset(250); await Idle();
            await KeyboardActivate("FilterImageButton"); await Task.Delay(100); await window.PendingSearch; await Idle();
            Check(StoreFilter(window) == HistoryTypeFilter.Image && scroll.VerticalOffset == 0,
                $"Changing type while a keyword is active also returns to the top (filter={StoreFilter(window)}, offset={scroll.VerticalOffset:0.###})");
            search.Clear(); await window.PendingSearch;

            Click("FilterFileButton"); store.Flush();
            var reloaded = new HistoryStore(data);
            Check(reloaded.Settings.HistoryTypeFilter == HistoryTypeFilter.File, "Selected type filter persists across restart");
            string invalid = Path.Combine(directory, "invalid"); Directory.CreateDirectory(invalid);
            File.WriteAllText(Path.Combine(invalid, "settings.json"), "{\"HistoryTypeFilter\":\"Unknown\"}");
            Check(new HistoryStore(invalid).Settings.HistoryTypeFilter == HistoryTypeFilter.All, "Unknown persisted filter falls back to All");
            Check(Filter("FilterFileButton").Tag as string == "Selected", "Selected segment exposes a visual and accessibility state");
        }
        catch (Exception ex) { error = ex.ToString(); }
        File.WriteAllText(Path.Combine(directory, "type-filter-results.json"), JsonSerializer.Serialize(new { passed = error is null, checks, error }, new JsonSerializerOptions { WriteIndented = true }));
        if (window is not null) window.Quit(); else Application.Current.Shutdown(error is null ? 0 : 1);
    }

    private static string StoreFilter(MainWindow window) => HistoryTypeFilter.Normalize(window.Store.Settings.HistoryTypeFilter);

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }
}
