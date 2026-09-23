using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace ClipShelf;

internal enum PreviewQuality { Thumbnail, Draft, Normal }
internal sealed record PreviewDocumentInfo(string DocumentId, string FileType, string DisplayName, int? PageCount,
    double PageWidth, double PageHeight, bool HasEmbeddedThumbnail, string ProviderVersion);
internal sealed record PreviewPageResult(string DocumentId, int PageIndex, long RequestVersion, BitmapSource RenderedBitmap,
    int Width, int Height, PreviewQuality Quality, bool IsApproximate, string[] Warnings);
internal interface IPreviewProvider : IAsyncDisposable
{
    bool CanPreview(string file);
    Task OpenAsync(DocumentIdentity file, CancellationToken token);
    Task<PreviewDocumentInfo> GetDocumentInfoAsync(CancellationToken token);
    Task<int?> GetPageCountAsync(bool complete, CancellationToken token);
    Task<PreviewPageResult> RenderPageAsync(int page, int width, double dpi, PreviewQuality quality, long version, CancellationToken token);
    Task<PreviewPageResult?> GetThumbnailAsync(CancellationToken token);
    Task CloseAsync();
}
internal static class PreviewProviderRegistry
{
    internal const string Version = "native-2";
    internal static IPreviewProvider Create(string path, PreviewCacheService cache) => PreviewFormatRegistry.Get(path) switch {
        PreviewFormat.Pdf => new PdfPreviewProvider(), PreviewFormat.Word => new DocxPreviewProvider(cache),
        PreviewFormat.PowerPoint => new PptxPreviewProvider(cache), _ => throw new PreviewException("Unsupported", "当前格式暂不支持快速预览，可使用默认应用打开。") };
}
internal static class PreviewMetrics
{
    private static readonly System.Collections.Concurrent.ConcurrentQueue<(string Stage, double Milliseconds)> samples = new();
    internal static IDisposable Measure(string stage) => new Timer(stage);
    internal static (string Stage, double Milliseconds)[] Snapshot() => samples.ToArray();
    private sealed class Timer(string stage) : IDisposable {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        public void Dispose() { samples.Enqueue((stage, watch.Elapsed.TotalMilliseconds)); while (samples.Count > 1000) samples.TryDequeue(out _); }
    }
}
internal sealed class PdfPreviewProvider : IPreviewProvider
{
    private FileStream? file; private IRandomAccessStream? stream; private PdfDocument? pdf; private DocumentIdentity identity = null!;
    private PreviewDocumentInfo info = null!;
    public bool CanPreview(string path) => PreviewFormatRegistry.Get(path) == PreviewFormat.Pdf;
    public Task OpenAsync(DocumentIdentity id, CancellationToken token) => Task.Run(async () => {
        using var timing = PreviewMetrics.Measure("pdf-open"); identity = id;
        try {
            file = new FileStream(id.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); stream = file.AsRandomAccessStream();
            pdf = await PdfDocument.LoadFromStreamAsync(stream).AsTask(token);
            if (pdf.PageCount is 0 or > 10000) throw new PreviewException("SafetyLimit", "PDF 页数为空或超过 10,000 页安全上限。");
            using var first = pdf.GetPage(0);
            info = new(id.Id, "PDF", Path.GetFileName(id.Path), (int)pdf.PageCount, first.Size.Width, first.Size.Height, false, PreviewProviderRegistry.Version);
        } catch (OperationCanceledException) { await CloseAsync(); throw; }
        catch (Exception e) { await CloseAsync(); if (e.HResult == unchecked((int)0x8007052B)) throw new PreviewException("Encrypted", "PDF 受密码保护，请使用默认应用打开。"); throw; }
    }, token);
    public Task<PreviewDocumentInfo> GetDocumentInfoAsync(CancellationToken token) => Task.FromResult(info);
    public Task<int?> GetPageCountAsync(bool complete, CancellationToken token) => Task.FromResult(info.PageCount);
    public Task<PreviewPageResult?> GetThumbnailAsync(CancellationToken token) => Task.FromResult<PreviewPageResult?>(null);
    public Task<PreviewPageResult> RenderPageAsync(int index, int width, double dpi, PreviewQuality quality, long version, CancellationToken token) => Task.Run<PreviewPageResult>(async () => {
        using var timing = PreviewMetrics.Measure("pdf-draw"); token.ThrowIfCancellationRequested();
        index = Math.Clamp(index, 0, (int)pdf!.PageCount - 1);
        using var page = pdf.GetPage((uint)index); double ratio = page.Size.Height / Math.Max(1, page.Size.Width);
        int w = Math.Max(1, (int)Math.Min(Math.Clamp(width, 240, 4096), Math.Min(5600 / Math.Max(.01, ratio), Math.Sqrt(14_000_000 / Math.Max(.01, ratio)))));
        using var output = new InMemoryRandomAccessStream();
        try { await page.RenderToStreamAsync(output, new PdfPageRenderOptions { DestinationWidth = (uint)w, DestinationHeight = (uint)Math.Max(1, w * ratio) }).AsTask(token); }
        catch (OperationCanceledException) { throw; } catch { throw new PreviewException("PdfRenderFailed", "PDF 当前页无法渲染，请重试或使用默认应用打开。"); }
        using var input = output.AsStreamForRead(); var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = input; image.EndInit(); image.Freeze();
        token.ThrowIfCancellationRequested(); return new(identity.Id, index, version, image, image.PixelWidth, image.PixelHeight, quality, false, Array.Empty<string>());
    }, token);
    public Task CloseAsync() { pdf = null; stream?.Dispose(); stream = null; file?.Dispose(); file = null; return Task.CompletedTask; }
    public async ValueTask DisposeAsync() => await CloseAsync();
}
