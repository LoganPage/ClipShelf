using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace ClipShelf;

internal sealed record RenderedPage(string DocumentId, int Page, int Count, BitmapSource Image, bool CountFinal = true,
    PreviewQuality Quality = PreviewQuality.Normal, bool IsApproximate = false, string[]? Warnings = null, long RequestVersion = 0);
internal interface IPreviewPageRenderer : IAsyncDisposable
{
    Task<RenderedPage> RenderAsync(DocumentIdentity identity, int page, int width, CancellationToken token);
    async Task<RenderedPage> RenderViewportAsync(DocumentIdentity identity, int page, int width, double dpi, long version, CancellationToken token, double zoom = 1) => (await RenderAsync(identity, page, width, token)) with { RequestVersion = version };
    Task<RenderedPage?> ThumbnailAsync(DocumentIdentity identity, CancellationToken token) => Task.FromResult<RenderedPage?>(null);
    Task<int?> CompletePaginationAsync(DocumentIdentity identity, CancellationToken token) => Task.FromResult<int?>(null);
}
// Compatibility facade for the session. Conversion is no longer part of this path.
internal sealed class PdfPageRenderService(PreviewCacheService cache) : IPreviewPageRenderer
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<string, IPreviewProvider> documents = new();
    internal int OpenCount { get; private set; }
    private async Task<IPreviewProvider> Open(DocumentIdentity id, CancellationToken token) {
        if ((await Task.Run(() => DocumentIdentity.Read(id.Path), token)).Id != id.Id) throw new PreviewException("Changed", "文件已修改，请重新加载预览。");
        if (documents.Remove(id.Id, out var provider)) { documents.Add(id.Id, provider); return provider; }
        provider = PreviewProviderRegistry.Create(id.Path, cache);
        try { await provider.OpenAsync(id, token); documents.Add(id.Id, provider); OpenCount++; }
        catch { await provider.DisposeAsync(); throw; }
        if (documents.Count > 3) { string first = documents.Keys.First(k => k != id.Id); var old = documents[first]; documents.Remove(first); await old.DisposeAsync(); }
        return provider;
    }
    public async Task<RenderedPage?> ThumbnailAsync(DocumentIdentity id, CancellationToken token) {
        await gate.WaitAsync(token);
        try { var p = await Open(id, token); var thumb = await p.GetThumbnailAsync(token); var info = await p.GetDocumentInfoAsync(token);
            return thumb is null ? null : new(id.Id, 0, info.PageCount ?? 1, thumb.RenderedBitmap, info.PageCount is not null, PreviewQuality.Thumbnail, true, thumb.Warnings); }
        finally { gate.Release(); }
    }
    public Task<RenderedPage> RenderAsync(DocumentIdentity id, int page, int width, CancellationToken token) => RenderViewportAsync(id, page, width, 96, 0, token);
    public Task<RenderedPage> RenderViewportAsync(DocumentIdentity id, int page, int width, double dpi, long version, CancellationToken token, double zoom = 1) => Task.Run<RenderedPage>(async () => {
        await gate.WaitAsync(token);
        try {
            token.ThrowIfCancellationRequested();
            if (DocumentIdentity.Read(id.Path).Id != id.Id) throw new PreviewException("Changed", "文件已修改，请重新加载预览。");
            string earlyKey = PreviewCacheService.RenderKey(id, page, width, dpi, zoom: zoom);
            if (cache.ReadPage(earlyKey, token) is { } early && early.DocumentId == id.Id && early.Page == page) return early with { RequestVersion = version };
            var provider = await Open(id, token); var info = await provider.GetDocumentInfoAsync(token);
            cache.Model(id.Id + ":info", info, 512);
            page = info.PageCount is int count ? Math.Clamp(page, 0, count - 1) : Math.Max(0, page);
            string key = PreviewCacheService.RenderKey(id, page, width, dpi, zoom: zoom);
            if (cache.ReadPage(key, token) is { } hit && hit.DocumentId == id.Id && hit.Page == page) return hit with { RequestVersion = version };
            var result = await provider.RenderPageAsync(page, width, dpi, PreviewQuality.Normal, version, token);
            info = await provider.GetDocumentInfoAsync(token);
            if (DocumentIdentity.Read(id.Path).Id != id.Id) throw new PreviewException("Changed", "文件已修改，请重新加载预览。");
            var rendered = new RenderedPage(id.Id, result.PageIndex, info.PageCount ?? result.PageIndex + 1, result.RenderedBitmap, info.PageCount is not null, result.Quality, result.IsApproximate, result.Warnings, version);
            cache.WritePage(PreviewCacheService.RenderKey(id, result.PageIndex, width, dpi, zoom: zoom), rendered); return rendered;
        } finally { gate.Release(); }
    }, token);
    public async Task<int?> CompletePaginationAsync(DocumentIdentity id, CancellationToken token) {
        await gate.WaitAsync(token); try { return await (await Open(id, token)).GetPageCountAsync(true, token); } finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync() {
        await gate.WaitAsync(); try { foreach (var p in documents.Values) await p.DisposeAsync(); documents.Clear(); } finally { gate.Release(); }
    }
}
