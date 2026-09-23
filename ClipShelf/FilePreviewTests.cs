using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClipShelf;

internal static class FilePreviewTests
{
    internal static ClipItem Item(string path) => new() { Kind = ClipKind.File, FilePaths = new() { path }, Title = Path.GetFileName(path) };
    internal static void MakePdf(string path, int count, int padding = 0)
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", $"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(0, count).Select(i => $"{3 + i * 2} 0 R"))}] /Count {count} >>" };
        for (int i = 0; i < count; i++) {
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 550] /Contents {4 + i * 2} 0 R >>");
            string contents = (i % 2 == 0 ? "0.2 0.5 0.8" : "0.8 0.3 0.2") + " rg 40 40 320 470 re f\n%" + new string('x', padding) + "\n";
            objects.Add($"<< /Length {contents.Length} >>\nstream\n{contents}endstream");
        }
        var doc = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++) { offsets.Add(doc.Length); doc.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        int xref = doc.Length; doc.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) doc.Append(offset.ToString("D10") + " 00000 n \n");
        doc.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, doc.ToString(), Encoding.ASCII);
    }
    public static void RunDemo(string root)
    {
        Directory.CreateDirectory(root); string path = Path.GetFullPath(Path.Combine(root, "demo.pdf")); MakePdf(path, 12);
        var window = new PreviewWindow(new[] { Item(path) }, 0); window.Closed += (_, _) => Application.Current.Shutdown(); window.Show();
    }
    public static async Task RunAsync(string root)
    {
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown; root = Path.GetFullPath(root); Directory.CreateDirectory(root);
        var checks = new List<string>(); var timing = new Dictionary<string, double>(); string? error = null; PreviewWindow? window = null;
        void Check(bool value, string label) { if (!value) throw new Exception(label); checks.Add(label); }
        using var cache = new PreviewCacheService(Path.Combine(root, "cache"));
        try {
            string small = Path.Combine(root, "small.pdf"), large = Path.Combine(root, "large.pdf"), unsupported = Path.Combine(root, "other.xlsx"), otherUnsupported = Path.Combine(root, "other.zip");
            MakePdf(small, 4); MakePdf(large, 180, 65536); File.WriteAllText(unsupported, "Never parse this as a spreadsheet"); File.WriteAllText(otherUnsupported, "Never parse this as an archive");
            foreach (string ext in new[] { ".pdf", ".docx", ".PPTX", ".png", ".JPG", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".ico", ".txt", ".md", ".json", ".cs", ".ps1" }) Check(PreviewFormatRegistry.Get("file" + ext) != PreviewFormat.Unsupported, "Allow " + ext);
            foreach (string ext in new[] { ".doc", ".ppt", ".xlsx", ".xls", ".mp3", ".mp4", ".zip", ".docm", ".pptm", ".exe", "" }) Check(PreviewFormatRegistry.Get("file" + ext) == PreviewFormat.Unsupported, "Reject " + ext);
            await TextImagePreviewTests.RunAsync(root, cache, Check);
            var folder = Item(small); folder.IsDirectory = true; Check(!PreviewFormatRegistry.Supports(folder), "Directories cannot masquerade as PDFs");
            Check(PreviewSession.BoundPage(-10, 12) == 0 && PreviewSession.BoundPage(200, 12) == 11 && PreviewSession.BoundPage(1, 0) == 0, "Page boundaries");
            var id = DocumentIdentity.Read(small); Check(id.Id == DocumentIdentity.Read(small).Id, "Stable identity");
            string sameStamp = Path.Combine(root, "same-stamp.pdf"); File.Copy(small, sameStamp, true); var before = DocumentIdentity.Read(sameStamp);
            using (var edit = new FileStream(sameStamp, FileMode.Open, FileAccess.Write)) { edit.Position = 10; edit.WriteByte(88); }
            File.SetLastWriteTimeUtc(sameStamp, new DateTime(before.Modified, DateTimeKind.Utc));
            Check(before.Id != DocumentIdentity.Read(sameStamp).Id, "Sample hash invalidates equal-size / equal-time changed content");
            string mutable = Path.Combine(root, "mutable.pdf"); File.Copy(small, mutable, true); var original = DocumentIdentity.Read(mutable); File.AppendAllText(mutable, "% changed"); Check(original.Id != DocumentIdentity.Read(mutable).Id, "Modification invalidates cache");
            var watch = Stopwatch.StartNew();
            await using (var renderer = new PdfPageRenderService(cache)) {
                var first = await renderer.RenderAsync(id, 0, 1000, default); timing["pdf_first_ms"] = watch.Elapsed.TotalMilliseconds;
                Check(first.Count == 4 && first.Image.IsFrozen, "PDF pages are immutable decoded bitmaps");
                watch.Restart(); var warm = await renderer.RenderAsync(id, 0, 1000, default); timing["pdf_warm_ms"] = watch.Elapsed.TotalMilliseconds;
                Check(ReferenceEquals(first.Image, warm.Image), "Warm open reuses cached page");
                await renderer.RenderAsync(id, 3, 1000, default); Check(renderer.OpenCount == 1, "Page changes do not reopen document");
                watch.Restart(); var big = await renderer.RenderAsync(DocumentIdentity.Read(large), 0, 1200, default); timing["large_pdf_first_ms"] = watch.Elapsed.TotalMilliseconds; Check(big.Count == 180, "180-page / 11MB PDF first page");
                using var cancel = new CancellationTokenSource(); cancel.Cancel(); bool cancelled = false;
                try { await renderer.RenderAsync(id, 0, 1000, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled, "Cancelled render exits without UI results");
            }
            var records = new[] { Item(small), Item(unsupported), Item(large), Item(Path.Combine(root, "missing.pdf")) };
            await using (var session = new PreviewSession(records, 0, cache)) {
                session.Start(); await session.Pending; Check(session.Presented?.Count == 4, "Session opens first page");
                var stale = session.CurrentRequest;
                session.NavigatePage(1); session.NavigatePage(1); session.NavigatePage(1); await session.Pending;
                Check(session.Page == 3 && session.Presented?.Page == 3 && !session.Accepts(stale), "Rapid paging commits only latest document/page/version");
                session.NavigateRecord(1); await session.Pending; Check(session.Index == 1 && session.Error?.Code == "Unsupported", "Down stops on the adjacent unsupported record");
                session.NavigateRecord(1); await session.Pending; Check(session.Index == 2 && session.Error is null, "Down continues from unsupported to the next previewable record");
                session.SetPage(9999); await session.Pending; Check(session.Page == 179, "End clamps to last page");
                session.NavigateRecord(-1); await session.Pending; Check(session.Index == 1 && session.Error?.Code == "Unsupported", "Up stops on the adjacent unsupported record");
                session.NavigateRecord(-1); await session.Pending; Check(session.Page == 3, "Session restores visited page after crossing an unsupported record");
                session.HandleKey(Key.Home); await session.Pending; Check(session.Page == 0, "Home goes to first page");
                session.NavigateRecord(1); session.NavigateRecord(1); session.NavigateRecord(1); await session.Pending; Check(session.Error?.Code == "MissingFile", "Missing document is an inline error");
                session.Cancel(); var closedStamp = session.CurrentRequest; session.NavigateRecord(-1); Check(!session.Accepts(closedStamp), "Closed session rejects every result");
            }
            var adjacent = new[] { Item(unsupported), Item(small), Item(otherUnsupported) };
            await using (var session = new PreviewSession(adjacent, 1, cache)) {
                session.Start(); await session.Pending;
                session.NavigateRecord(-1); await session.Pending; Check(session.Index == 0 && session.Error?.Code == "Unsupported", "Up reaches an unsupported record immediately before a previewable record");
                session.NavigateRecord(1); await session.Pending; Check(session.Index == 1 && session.Presented?.Count == 4 && session.Error is null, "Down returns from unsupported to the supported record");
                session.NavigateRecord(1); await session.Pending; Check(session.Index == 2 && session.Error?.Code == "Unsupported", "Down reaches an unsupported record immediately after a previewable record");
                session.NavigateRecord(-1); await session.Pending; Check(session.Index == 1 && session.Presented?.Count == 4 && session.Error is null, "Up returns from the following unsupported record");
            }
            // A deliberately non-cooperative renderer verifies stale completion rejection independently of cancellation.
            var delayed = new DelayedRenderer();
            await using (var session = new PreviewSession(records, 0, cache, delayed)) {
                session.Start(); await delayed.Started.Task; var obsolete = session.Pending;
                session.NavigateRecord(1); session.NavigateRecord(1); await session.Pending;
                delayed.Release.TrySetResult(); await obsolete;
                Check(session.Presented?.DocumentId == DocumentIdentity.Read(large).Id, "Old file result cannot overwrite new file even when cancellation is ignored");
            }
            string broken = Path.Combine(root, "broken.pdf"); File.WriteAllText(broken, "not a PDF");
            await using (var session = new PreviewSession(new[] { Item(broken) }, 0, cache)) { session.Start(); await session.Pending; Check(session.Error?.Code == "InvalidDocument", "Corrupt PDF has explicit error"); }
            using (var locked = new FileStream(small, FileMode.Open, FileAccess.Read, FileShare.None)) {
                await using var session = new PreviewSession(new[] { Item(small) }, 0, cache); session.Start(); await session.Pending; Check(session.Error?.Code == "FileBusy", "Exclusively locked file fails safely");
            }
            Check(PreviewException.From(new UnauthorizedAccessException()).Code == "AccessDenied", "Permission errors are distinct");
            string deniedPath = Path.Combine(root, "denied.pdf"); File.Copy(small, deniedPath, true); var deniedFile = new FileInfo(deniedPath); var originalAcl = deniedFile.GetAccessControl();
            try {
                var deniedAcl = deniedFile.GetAccessControl(); deniedAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadData, AccessControlType.Deny)); deniedFile.SetAccessControl(deniedAcl);
                await using var deniedSession = new PreviewSession(new[] { Item(deniedPath) }, 0, cache); deniedSession.Start(); await deniedSession.Pending;
                Check(deniedSession.Error?.Code == "AccessDenied", "Actual NTFS read-denied file yields inline permission error");
            } finally { deniedFile.SetAccessControl(originalAcl); }
            Check(!typeof(PreviewSession).Assembly.GetTypes().Any(t => t.Name is "OfficeConversionWorker" or "DocumentConversionService"), "Office conversion worker and service are removed from the application");
            window = new PreviewWindow(records, 0, cache) { ShowActivated = false, ShowInTaskbar = false }; window.Show(); await Idle(); await window.PendingRender; window.UpdateLayout();
            double width = window.ActualWidth, height = window.ActualHeight; var content = window.PresentedContent;
            window.NavigatePage(1); Check(ReferenceEquals(content, window.PresentedContent), "Loading retains the same content container"); await window.PendingRender;
            var scroll = All<ScrollViewer>(window).First(); int recordBefore = window.RecordIndex, pageBefore = window.PageIndex;
            scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.MouseWheelEvent }); await Idle();
            Check(window.RecordIndex == recordBefore && window.PageIndex == pageBefore, "Mouse wheel cannot change document or page");
            void SendKey(PreviewWindow target, System.Windows.Input.Key key) => target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            for (int i = 0; i < 20; i++) { SendKey(window, System.Windows.Input.Key.Right); SendKey(window, System.Windows.Input.Key.Left); }
            SendKey(window, System.Windows.Input.Key.Down); SendKey(window, System.Windows.Input.Key.Up); await window.PendingRender;
            Check(window.RecordIndex == 0 && window.Session.Presented?.DocumentId == id.Id,
                $"Routed rapid arrows stay inside preview and settle on latest document (index={window.RecordIndex}, error={window.Session.Error?.Code})");
            foreach (bool dark in new[] { false, true }) {
                ThemeManager.Apply(new AppSettings { Theme = dark ? "Dark" : "Light" }); await Task.Delay(220); window.UpdateLayout();
                foreach (double dpi in new[] { 1.25, 1.5, 2.0 }) {
                    var capture = new RenderTargetBitmap((int)(width * dpi), (int)(height * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32); capture.Render(window);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(capture)); using var file = File.Create(Path.Combine(root, $"preview-{dark}-{dpi}.png")); png.Save(file);
                    Check(window.ActualWidth == width && window.ActualHeight == height, $"Stable layout at simulated {dpi * 100}% DPI / dark={dark}");
                }
            }
            window.Session.NavigateRecord(1); window.Close(); await Task.Delay(210); await window.Cleanup; Check(!window.IsVisible, "Close cancels work and cannot reopen preview"); window = null;
            window = new PreviewWindow(new[] { Item(unsupported) }, 0, cache) { ShowActivated = false, ShowInTaskbar = false }; window.Show(); await Idle(); await window.PendingRender;
            Check(window.IsVisible && window.Session.Error?.Code == "Unsupported" && window.Session.Presented is null, "Unsupported file opens an honest no-preview window");
            SendKey(window, System.Windows.Input.Key.Space); await Task.Delay(210); await window.Cleanup; Check(!window.IsVisible, "Space closes even an unsupported preview"); window = null;
            window = new PreviewWindow(new[] { Item(small) }, 0, cache) { ShowActivated = false, ShowInTaskbar = false }; window.Show(); await Idle(); SendKey(window, System.Windows.Input.Key.Escape); await Task.Delay(210); await window.Cleanup;
            Check(!window.IsVisible, "Esc cancels loading and closes preview"); window = null;
            Check(cache.MemoryBytes <= PreviewCacheService.MemoryLimit, "Decoded memory is bounded");
            for (int i = 0; i < 40; i++) { var bitmap = BitmapSource.Create(1000, 1000, 96, 96, PixelFormats.Bgra32, null, new byte[4000000], 4000); bitmap.Freeze(); cache.Put("eviction-" + i, bitmap); }
            Check(cache.MemoryBytes <= PreviewCacheService.MemoryLimit && cache.Get("eviction-0") is null && cache.Get("eviction-39") is not null, "LRU actually evicts above 64 MiB");
            Directory.CreateDirectory(cache.Root); string expired = Path.Combine(cache.Root, new string('A', 64) + ".pdf"); File.WriteAllText(expired, "expired"); File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-15));
            string unrelated = Path.Combine(cache.Root, "keep.txt"); File.WriteAllText(unrelated, "not a generated cache entry"); cache.CleanDisk();
            Check(!File.Exists(expired) && File.Exists(unrelated), "Disk cleanup expires only recognized cache entries");
        } catch (Exception e) { error = e.ToString(); }
        finally { window?.Close(); File.WriteAllText(Path.Combine(root, "file-preview-results.json"), JsonSerializer.Serialize(new { passed = error is null, checks, timing, error }, new JsonSerializerOptions { WriteIndented = true })); Application.Current.Shutdown(error is null ? 0 : 1); }
    }
    private sealed class DelayedRenderer : IPreviewPageRenderer
    {
        internal readonly TaskCompletionSource Started = new(), Release = new(); private int calls;
        public async Task<RenderedPage> RenderAsync(DocumentIdentity id, int page, int width, CancellationToken token)
        {
            if (Interlocked.Increment(ref calls) == 1) { Started.TrySetResult(); await Release.Task; }
            var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4); image.Freeze(); return new(id.Id, page, 4, image);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    internal static async Task Idle() { await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(30); }
    internal static IEnumerable<T> All<T>(DependencyObject root) where T : DependencyObject { if (root is T match) yield return match; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in All<T>(VisualTreeHelper.GetChild(root, i))) yield return child; }
}
internal static class CommonPreviewTests
{
    // The old all-format entry point exercises supported documents plus restored text/images.
    internal static Task RunAsync(string root) => FilePreviewTests.RunAsync(root);
}
