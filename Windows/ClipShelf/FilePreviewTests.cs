using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipShelf;

internal static class FilePreviewTests
{
    private static List<string> Fixture(string root)
    {
        Directory.CreateDirectory(root);
        string text = Path.Combine(root, "说明.txt"), image = Path.Combine(root, "示例.png"), pdf = Path.Combine(root, "两页示例.pdf");
        File.WriteAllText(text, "文件预览测试\nUnicode 中文 😀\n" + new string('A', FilePreviewLoader.TextChunk + 100), Encoding.UTF8);
        var pixels = Enumerable.Range(0, 480 * 270).SelectMany(_ => new byte[] { 180, 120, 45, 255 }).ToArray();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(480, 270, 96, 96, PixelFormats.Bgra32, null, pixels, 480 * 4)));
        using (var stream = File.Create(image)) encoder.Save(stream);
        var objects = new[] {
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 500] /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 500] /Contents 6 0 R >>",
            PdfStream("0.2 0.5 0.8 rg 40 40 320 420 re f\n"), PdfStream("0.8 0.3 0.2 rg 40 40 320 420 re f\n") };
        var doc = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++) { offsets.Add(doc.Length); doc.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        int xref = doc.Length; doc.Append("xref\n0 7\n0000000000 65535 f \n");
        foreach (int offset in offsets) doc.Append(offset.ToString("D10") + " 00000 n \n");
        doc.Append($"trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(pdf, doc.ToString(), Encoding.ASCII);
        return new() { text, image, pdf, Path.Combine(root, "missing.docx") };
    }
    private static string PdfStream(string text) => $"<< /Length {text.Length} >>\nstream\n{text}endstream";
    public static void RunDemo(string root)
    {
        var paths = Fixture(Path.GetFullPath(root));
        var preview = new PreviewWindow(new[] { new ClipItem { Kind = ClipKind.File, FilePaths = paths } }, 0);
        preview.Closed += (_, _) => Application.Current.Shutdown(); preview.Show();
    }
    public static async Task RunAsync(string root)
    {
        root = Path.GetFullPath(root); var files = Fixture(root);
        var checks = new List<string>(); string? error = null; PreviewWindow? window = null;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        try
        {
            var hashes = files.Take(3).Select(p => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
            var text = await FilePreviewLoader.LoadAsync(files[0], 0, FilePreviewLoader.TextChunk, default);
            Check(text.Kind == "文本" && text.Text!.Contains("中文 😀") && text.HasMore && text.Text.Length == FilePreviewLoader.TextChunk, "Unicode text is decoded and initially bounded");
            text = await FilePreviewLoader.LoadAsync(files[0], 0, FilePreviewLoader.TextChunk * 2, default);
            Check(!text.HasMore && text.Text!.Length > FilePreviewLoader.TextChunk, "Next text segment loads without truncating the source");
            string utf16 = Path.Combine(root, "utf16.txt"); File.WriteAllText(utf16, "中文 UTF16", Encoding.Unicode);
            Check((await FilePreviewLoader.LoadAsync(utf16, 0, 0, default)).Text == "中文 UTF16", "UTF16 BOM is supported");
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            string gb = Path.Combine(root, "gb.txt"); File.WriteAllText(gb, "中文旧编码", Encoding.GetEncoding(54936));
            Check((await FilePreviewLoader.LoadAsync(gb, 0, 0, default)).Text == "中文旧编码", "GB18030 text has a readable fallback");
            var img = await FilePreviewLoader.LoadAsync(files[1], 0, 0, default);
            Check(img.Image is { IsFrozen: true, PixelWidth: 480, PixelHeight: 270 }, "Image file decodes on a worker and is transferable");
            var pdf = await FilePreviewLoader.LoadAsync(files[2], 0, 0, default);
            Check(pdf.Kind == "PDF" && pdf.PageCount == 2 && pdf.Image is { IsFrozen: true, PixelHeight: 1800 }, $"Windows PDF renderer displays first page at bounded resolution ({pdf.Kind}/{pdf.PageCount}/{pdf.Image?.PixelWidth}x{pdf.Image?.PixelHeight})");
            byte[] Pixel(BitmapSource source) { var b = new byte[4]; source.CopyPixels(new Int32Rect(source.PixelWidth / 2, source.PixelHeight / 2, 1, 1), b, 4, 0); return b; }
            var second = await FilePreviewLoader.LoadAsync(files[2], 1, 0, default);
            Check(!Pixel(pdf.Image!).SequenceEqual(Pixel(second.Image!)), "PDF next page has distinct rendered content");
            using (File.Open(files[2], FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Check(true, "PDF preview releases source file handles");
            Check(!(await FilePreviewLoader.LoadAsync(files[3], 0, 0, default)).Exists, "Missing files return a readable unavailable state");
            Check((await FilePreviewLoader.LoadAsync(root, 0, 0, default)).Kind == "文件夹", "Folder uses information fallback");
            Check(!(await FilePreviewLoader.LoadAsync(@"\\server\share\file.txt", 0, 0, default)).Exists, "Network paths are not automatically opened");
            Check(!FilePreviewLoader.CanOpen("a.exe") && !FilePreviewLoader.CanOpen("a.lnk") && !FilePreviewLoader.CanOpen("a.ps1"), "Executable and shortcut types have no launch action");
            using (var cancel = new CancellationTokenSource()) {
                cancel.Cancel(); bool cancelled = false;
                try { await FilePreviewLoader.LoadAsync(files[2], 0, 0, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled, "Cancelled requests stop before decoding");
            }
            window = new PreviewWindow(new[] { new ClipItem { Kind = ClipKind.File, FilePaths = files } }, 0);
            window.Show(); await window.PendingRender; window.UpdateLayout();
            Check(All<TextBox>(window).Any(t => t.Text.StartsWith("文件预览测试")), "Window shows file content rather than a path list");
            window.NavigateFile(1); await window.PendingRender; window.UpdateLayout();
            Check(window.FileIndex == 1 && All<Image>(window).Any(i => i.Source is not null), "Right navigation previews the next file in the same record");
            window.NavigateFile(1); await window.PendingRender; window.UpdateLayout();
            Check(All<Button>(window).Any(b => b.Content?.ToString() == "下一页" && b.IsEnabled), "PDF has an enabled next-page control");
            var firstPdfBitmap = All<Image>(window).Single(i => i.Source is not null).Source;
            var pdfSurface = window.PresentedContent;
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Right) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); await window.PendingRender; window.UpdateLayout();
            Check(window.PageIndex == 1 && All<Button>(window).Any(b => b.Content?.ToString() == "下一页" && !b.IsEnabled), "PDF page navigation ends at last page");
            Check(ReferenceEquals(pdfSurface, window.PresentedContent), "PDF page swap retains its visual surface");
            window.NavigatePage(-1); await window.PendingRender; window.UpdateLayout();
            Check(ReferenceEquals(firstPdfBitmap, All<Image>(window).Single(i => i.Source is not null).Source), "Returning to PDF page reuses decoded cached bitmap");
            window.NavigatePage(1); window.NavigatePage(-1); window.NavigatePage(1); await window.PendingRender; window.UpdateLayout();
            Check(window.PageIndex == 1, "Rapid alternating paging presents the latest request");
            var screenshot = new RenderTargetBitmap(820, 620, 96, 96, PixelFormats.Pbgra32); screenshot.Render(window);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(screenshot)); using (var stream = File.Create(Path.Combine(root, "pdf-preview.png"))) png.Save(stream);
            foreach (string theme in new[] { "Light", "Dark" }) foreach (int width in new[] { 620, 820 }) {
                ThemeManager.Apply(new AppSettings { Theme = theme }); window.Width = width; window.UpdateLayout();
                Check(All<Button>(window).All(b => b.TransformToAncestor(window).TransformBounds(new Rect(b.RenderSize)).Right <= window.ActualWidth + 1), $"{theme}/{width}: preview actions fit the window");
                var shot = new RenderTargetBitmap(width, 620, 96, 96, PixelFormats.Pbgra32); shot.Render(window);
                var encoded = new PngBitmapEncoder(); encoded.Frames.Add(BitmapFrame.Create(shot)); using var output = File.Create(Path.Combine(root, $"pdf-{theme}-{width}.png")); encoded.Save(output);
            }
            ThemeManager.Apply(new AppSettings { Theme = "Light" });
            window.NavigateFile(-1); window.NavigateFile(-1); await window.PendingRender; window.UpdateLayout();
            Check(window.FileIndex == 0 && All<TextBox>(window).Any(t => t.Text.StartsWith("文件预览测试")), "Rapid navigation cannot replace the latest content with an older result");
            var textBox = All<TextBox>(window).Single(); textBox.ScrollToVerticalOffset(200); window.UpdateLayout(); double textOffset = textBox.VerticalOffset;
            All<Button>(window).Single(b => b.Content?.ToString() == "再加载一段").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await window.PendingRender; window.UpdateLayout();
            Check(ReferenceEquals(textBox, All<TextBox>(window).Single()) && textBox.Text.Length > FilePreviewLoader.TextChunk && Math.Abs(textBox.VerticalOffset - textOffset) < 1,
                "Loading more text preserves the control and reading position");
            window.NavigateFile(3); await window.PendingRender; window.UpdateLayout();
            Check(All<Button>(window).Where(b => b.Content?.ToString() is "用默认应用打开" or "在文件夹中显示").All(b => !b.IsEnabled), "Missing-file actions are disabled");
            window.NavigateFile(-1); var pending = window.PendingRender; window.Close(); await pending; window = null;
            Check(true, "Closing during PDF decoding safely cancels work");
            string corrupt = Path.Combine(root, "corrupt.pdf"); File.WriteAllText(corrupt, "not a PDF");
            window = new PreviewWindow(new[] { new ClipItem { Kind = ClipKind.File, FilePaths = new() { corrupt } } }, 0); window.Show(); await window.PendingRender; window.UpdateLayout();
            Check(All<TextBox>(window).Any(t => t.Text.Contains("无法预览")), "Corrupt PDF displays a recoverable error in the preview");
            var openButton = All<Button>(window).Single(b => b.Content?.ToString() == "用默认应用打开"); openButton.Focus();
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            openButton.RaiseEvent(key);
            Check(key.Handled && !window.IsVisible, "Space closes even when the external-open button has focus"); window = null;
            Check(hashes.SequenceEqual(files.Take(3).Select(p => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))))), "All source files remain byte-for-byte unchanged");
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally { window?.Close(); File.WriteAllText(Path.Combine(root, "file-preview-results.json"), JsonSerializer.Serialize(new { passed = error is null, checks, error }, new JsonSerializerOptions { WriteIndented = true })); Application.Current.Shutdown(error is null ? 0 : 1); }
    }
    private static IEnumerable<T> All<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in All<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
