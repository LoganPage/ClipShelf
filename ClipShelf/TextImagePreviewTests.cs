using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipShelf;

internal static class TextImagePreviewTests
{
    internal static async Task RunAsync(string root, PreviewCacheService cache, Action<bool, string> check)
    {
        string path = Path.Combine(root, "restored-image.png");
        WriteImage(path, 320, 180);
        var text = new ClipItem { Kind = ClipKind.Text, Title = "文字快速预览", Text = "文字与图片已恢复\nUnicode: 中文、emoji 🌿\n" + string.Join('\n', Enumerable.Repeat("仅供预览，不会自动粘贴。", 30)) };
        var picture = new ClipItem { Kind = ClipKind.Image, ImagePath = path, Title = "剪贴板图片" };
        var file = FilePreviewTests.Item(path);
        check(PreviewFormatRegistry.Supports(text) && PreviewFormatRegistry.Supports(picture) && PreviewFormatRegistry.Supports(file), "Clipboard text, clipboard image and raster file are supported");
        var service = new TextImagePreviewService(cache);
        var id = DocumentIdentity.Read(path);
        var first = await service.RenderImageAsync(id, 1000, default);
        var warm = await service.RenderImageAsync(id, 1000, default);
        check(first.Image.PixelWidth == 320 && first.Image.PixelHeight == 180 && first.Image.IsFrozen, "Image decode preserves aspect ratio and freezes pixels");
        check(ReferenceEquals(first.Image, warm.Image), "Warm image preview uses bounded shared cache");
        WriteImage(path, 640, 360);
        var modified = DocumentIdentity.Read(path);
        var changed = await service.RenderImageAsync(modified, 1000, default);
        check(id.Id != modified.Id && changed.Image.PixelWidth == 640 && !ReferenceEquals(changed.Image, first.Image), "Modified image invalidates cache");
        string large = Path.Combine(root, "large-image.png"); WriteImage(large, 4000, 3000);
        var reduced = await service.RenderImageAsync(DocumentIdentity.Read(large), 1000, default);
        check(reduced.Image.PixelWidth == 1000 && reduced.Image.PixelHeight == 750, "Large images decode at display size instead of retaining original pixels");
        using (var cancelled = new CancellationTokenSource()) {
            cancelled.Cancel(); bool stopped = false;
            try { await service.RenderImageAsync(modified, 1000, cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
            check(stopped, "Cancelled image request never returns a cached or decoded result");
        }
        var huge = new ClipItem { Kind = ClipKind.Text, Text = new string('文', TextImagePreviewService.TextLimit - 1) + "🌿尾部" };
        var bounded = await TextImagePreviewService.ReadTextAsync(huge, default);
        check(bounded.Truncated && bounded.Text.Length < TextImagePreviewService.TextLimit && !char.IsHighSurrogate(bounded.Text[^1]) && huge.Text.EndsWith("尾部"), "Long text preview is bounded without splitting Unicode or modifying original");
        var items = new[] { text, FilePreviewTests.Item(Path.Combine(root, "unsupported.xlsx")), picture, FilePreviewTests.Item(Path.Combine(root, "small.pdf")) };
        await using (var session = new PreviewSession(items, 0, cache)) {
            session.Start(); await session.Pending;
            check(session.PresentedText?.Text == text.Text && session.Error is null && session.Presented is null, "Text session presents original text without PDF conversion");
            session.NavigatePage(100); check(session.Page == 0 && session.Count == 1, "Text has no accidental document pagination");
            var stamp = session.CurrentRequest;
            session.NavigateRecord(1); await session.Pending;
            check(session.Index == 1 && session.Error?.Code == "Unsupported" && !session.Accepts(stamp), "Navigation stops on an unsupported file after text");
            session.NavigateRecord(1); await session.Pending;
            check(session.Index == 2 && session.PresentedText is null && session.Presented?.Image.PixelWidth == 640 && session.Error is null, "Navigation continues from unsupported file to image");
            session.NavigateRecord(1); await session.Pending;
            check(session.Presented?.Count == 4, "Image to PDF navigation keeps document pagination");
            for (int i = 0; i < 15; i++) { session.NavigateRecord(-1); session.NavigateRecord(-1); session.NavigateRecord(-1); session.NavigateRecord(1); session.NavigateRecord(-1); }
            await session.Pending;
            check(session.Index == 0 && session.PresentedText?.Text == text.Text && session.Presented is null, "Rapid mixed navigation settles on latest text without stale image");
            session.NavigateRecord(1); session.NavigateRecord(1); session.Cancel(); await session.Pending;
            check(session.PresentedText?.Text == text.Text, "Close during image loading cannot replace visible text");
        }
        string corrupt = Path.Combine(root, "corrupt.png"); File.WriteAllText(corrupt, "not an image");
        await using (var session = new PreviewSession(new[] { FilePreviewTests.Item(corrupt), text }, 0, cache)) {
            session.Start(); await session.Pending; check(session.Error is not null, "Corrupt image reports inline error");
            session.NavigateRecord(1); await session.Pending; check(session.Error is null && session.PresentedText is not null, "Text navigation recovers from corrupt image");
        }
        await using (var session = new PreviewSession(new[] { new ClipItem { Kind = ClipKind.Image } }, 0, cache)) {
            session.Start(); await session.Pending; check(session.Error?.Code == "MissingFile", "Missing clipboard image has explicit error");
        }
        // Exercise the actual controls and routed keyboard boundary with read-only clipboard observation.
        uint sequence = NativeMethods.GetClipboardSequenceNumber();
        var window = new PreviewWindow(items, 0, cache) { ShowActivated = false, ShowInTaskbar = false };
        try {
            window.Show(); await FilePreviewTests.Idle(); await window.PendingRender; window.UpdateLayout();
            check(window.PresentedContent is TextBox { IsReadOnly: true } box && box.Text == text.Text, "Text is selectable in a read-only preview control");
            double width = window.ActualWidth, height = window.ActualHeight;
            Capture(window, Path.Combine(root, "restored-text-light.png"));
            void Key(Key key) => window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Key(System.Windows.Input.Key.Down); await window.PendingRender; await Task.Delay(160); window.UpdateLayout();
            check(window.RecordIndex == 1 && window.Session.Error?.Code == "Unsupported" && window.DisplayedLocation.Contains("unsupported.xlsx"), "Routed Down opens the adjacent unsupported preview page");
            Key(System.Windows.Input.Key.Down); await window.PendingRender; await Task.Delay(160); window.UpdateLayout();
            check(window.RecordIndex == 2 && window.PresentedContent is Image { Source: not null }, "A second routed Down continues from unsupported to image");
            Capture(window, Path.Combine(root, "restored-image-light.png"));
            var scroll = FilePreviewTests.All<ScrollViewer>(window).First();
            scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.MouseWheelEvent });
            check(window.RecordIndex == 2 && window.PageIndex == 0, "Image wheel cannot switch records or pages");
            Key(System.Windows.Input.Key.Up); await window.PendingRender;
            check(window.RecordIndex == 1 && window.Session.Error?.Code == "Unsupported", "Routed Up returns to the adjacent unsupported preview page");
            Key(System.Windows.Input.Key.Up); await window.PendingRender;
            ThemeManager.Apply(new AppSettings { Theme = "Dark" }); await Task.Delay(220); window.UpdateLayout();
            Capture(window, Path.Combine(root, "restored-text-dark.png"));
            check(window.ActualWidth == width && window.ActualHeight == height && window.PresentedContent is TextBox, "Mixed previews keep fixed window layout across theme and content changes");
            check(sequence == NativeMethods.GetClipboardSequenceNumber(), "Text/image opening and navigation never touch clipboard");
            Key(System.Windows.Input.Key.Space); await Task.Delay(210); await window.Cleanup;
            check(!window.IsVisible, "Space closes restored text preview and releases session");
        } finally { if (window.IsVisible) await window.CloseAndReleaseAsync(); ThemeManager.Apply(new AppSettings { Theme = "Light" }); }
    }
    private static void WriteImage(string path, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 160; pixels[i + 1] = 110; pixels[i + 2] = 35; pixels[i + 3] = 255; }
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
        using var stream = File.Create(path); png.Save(stream);
    }
    private static void Capture(PreviewWindow window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); png.Save(stream);
    }
}
