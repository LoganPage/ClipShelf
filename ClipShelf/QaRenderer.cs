using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClipShelf;

public static class QaRenderer
{
    public static async Task RunAsync(string output)
    {
        Directory.CreateDirectory(output);
        var store = new HistoryStore(Path.Combine(Path.GetTempPath(), "ClipShelf-render-" + Guid.NewGuid().ToString("N")));
        DemoContent.Add(store); store.Settings.Theme = "Light";
        await Task.WhenAll(store.Items.Where(item => item.ImagePath is not null).Select(item => ThumbnailLoader.LoadAsync(item.ImagePath!)));
        var window = new MainWindow(store, demo: true) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -12000, Top = -12000, ShowActivated = false };
        window.Show();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Capture(window, Path.Combine(output, "clipshelf-light.png"));
        store.Settings.Theme = "Dark"; window.ApplyPreferences();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Capture(window, Path.Combine(output, "clipshelf-dark.png"));
        window.OpenSettings();
        await Task.Delay(220);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Capture(window, Path.Combine(output, "clipshelf-settings-dark.png"));
        var settings = (ContentControl)window.FindName("SettingsContent");
        var scroll = FindScroll(settings);
        scroll?.ScrollToVerticalOffset(580);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Capture(window, Path.Combine(output, "clipshelf-settings-history-dark.png"));
        scroll?.ScrollToEnd();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Capture(window, Path.Combine(output, "clipshelf-settings-shortcuts-dark.png"));
        store.Settings.Theme = "Light"; window.ApplyPreferences();
        window.OpenSettings();
        await Task.Delay(220);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Capture(window, Path.Combine(output, "clipshelf-settings-light.png"));
        window.Quit();
    }
    private static ScrollViewer? FindScroll(DependencyObject obj)
    {
        if (obj is ScrollViewer scroll) return scroll;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++) if (FindScroll(VisualTreeHelper.GetChild(obj, i)) is ScrollViewer found) return found;
        return null;
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
