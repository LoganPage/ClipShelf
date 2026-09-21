using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace ClipShelf;

internal abstract class OoxmlPreviewProvider(PreviewCacheService cache) : IPreviewProvider
{
    protected PreviewCacheService Cache { get; } = cache;
    protected SafeOoxmlPackage Package = null!;
    protected DocumentIdentity Identity = null!;
    protected PreviewDocumentInfo Info = null!;
    public abstract bool CanPreview(string file);
    public virtual Task OpenAsync(DocumentIdentity file, CancellationToken token) => Cache.Scheduler.Run(() => {
        Identity = file; Package = new(file.Path); ReadInfo(token); return true;
    }, token);
    protected abstract void ReadInfo(CancellationToken token);
    public Task<PreviewDocumentInfo> GetDocumentInfoAsync(CancellationToken token) => Task.FromResult(Info);
    public virtual Task<int?> GetPageCountAsync(bool complete, CancellationToken token) => Task.FromResult(Info.PageCount);
    public async Task<PreviewPageResult?> GetThumbnailAsync(CancellationToken token) => await Cache.Scheduler.Run(() => {
        string? path = new[] { "docProps/thumbnail.jpeg", "docProps/thumbnail.jpg", "docProps/thumbnail.png" }.FirstOrDefault(Package.Has);
        if (path is null) return null;
        try { var image = PreviewSceneRenderer.Decode(Package.Bytes(path, token, 4 * 1024 * 1024), 900, token); return new PreviewPageResult(Identity.Id, 0, 0, image, image.PixelWidth, image.PixelHeight, PreviewQuality.Thumbnail, true, new[] { "内嵌缩略图" }); }
        catch (OperationCanceledException) { throw; } catch { return null; }
    }, token);
    protected abstract Task<PreviewScene> GetSceneAsync(int page, CancellationToken token);
    public virtual async Task<PreviewPageResult> RenderPageAsync(int page, int width, double dpi, PreviewQuality quality, long version, CancellationToken token) {
        var scene = await GetSceneAsync(page, token);
        var warnings = new HashSet<string>(scene.Warnings);
        var image = await Cache.Scheduler.Run(() => PreviewSceneRenderer.Render(scene, width, token, warnings), token);
        int index = Info.PageCount is int count ? Math.Clamp(page, 0, count - 1) : page;
        return new(Identity.Id, index, version, image, image.PixelWidth, image.PixelHeight, quality, true, warnings.ToArray());
    }
    public virtual Task CloseAsync() => Cache.Scheduler.Run(() => { Package?.Dispose(); return true; }, CancellationToken.None);
    public async ValueTask DisposeAsync() => await CloseAsync();
}
