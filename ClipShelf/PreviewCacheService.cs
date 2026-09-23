using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClipShelf;

// Owned by the main window; only immutable decoded pages are shared between sessions.
internal sealed class PreviewCacheService : IDisposable
{
    internal const long MemoryLimit = 64 * 1024 * 1024, DiskLimit = 512 * 1024 * 1024;
    internal string Root { get; }
    internal PreviewRenderScheduler Scheduler { get; } = new();
    private readonly Dictionary<string, (object Value, long Size, long Access)> models = new();
    private long modelBytes;
    internal T? Model<T>(string key) where T : class { lock (sync) { if (!models.TryGetValue(key, out var value)) return null; models[key] = (value.Value, value.Size, ++clock); return value.Value as T; } }
    internal void Model(string key, object value, long size) { lock (sync) { if (models.Remove(key, out var old)) modelBytes -= old.Size; if (size > MemoryLimit) return; models[key] = (value, size, ++clock); modelBytes += size; while (modelBytes > MemoryLimit || models.Count > 64) { var victim = models.MinBy(x => x.Value.Access); modelBytes -= victim.Value.Size; models.Remove(victim.Key); } } }
    private readonly SemaphoreSlim diskWriter = new(1);
    private int diskPending;
    internal long Hits, Misses;
    internal sealed record PageHeader(string Id, int Page, int Count, bool CountFinal, bool Approximate, string[] Warnings);
    internal static string RenderKey(DocumentIdentity id, int page, int width, double dpi = 96, PreviewQuality quality = PreviewQuality.Normal, double zoom = 1) => $"{PreviewProviderRegistry.Version}:{id.Id}:{page}:{((width + 63) / 64) * 64}:{Math.Round(dpi / 24) * 24}:{quality}:z{Math.Round(Math.Clamp(zoom, .5, 4) * 1000)}";
    private string DiskPath(string key) => Path.Combine(Root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))));
    internal RenderedPage? ReadPage(string key, CancellationToken token) {
        using var timing = PreviewMetrics.Measure("cache-query"); token.ThrowIfCancellationRequested();
        if (Model<PageHeader>(key) is { } header && Get(key) is { } memory) { Interlocked.Increment(ref Hits); return new(header.Id, header.Page, header.Count, memory, header.CountFinal, IsApproximate: header.Approximate, Warnings: header.Warnings); }
        string path = DiskPath(key);
        try {
            var meta = new FileInfo(path + ".json"); var png = new FileInfo(path + ".png");
            if (!meta.Exists || !png.Exists || meta.Length > 65536 || png.Length > 32 * 1024 * 1024) { Interlocked.Increment(ref Misses); return null; }
            var h = JsonSerializer.Deserialize<PageHeader>(File.ReadAllText(meta.FullName)); if (h is null || h.Id.Length != 64 || !h.Id.All(Uri.IsHexDigit) || h.Page < 0 || h.Count is < 1 or > 10000 || (h.CountFinal && h.Page >= h.Count)) return null;
            var bitmap = PreviewSceneRenderer.Decode(File.ReadAllBytes(png.FullName), 4096, token);
            Put(key, bitmap); Model(key, h, 256); Interlocked.Increment(ref Hits); return new(h.Id, h.Page, h.Count, bitmap, h.CountFinal, IsApproximate: h.Approximate, Warnings: h.Warnings);
        } catch (OperationCanceledException) { throw; } catch { Interlocked.Increment(ref Misses); return null; }
    }
    internal void WritePage(string key, RenderedPage page) {
        Put(key, page.Image); var header = new PageHeader(page.DocumentId, page.Page, page.Count, page.CountFinal, page.IsApproximate, page.Warnings ?? Array.Empty<string>()); Model(key, header, 256);
        if (Interlocked.Increment(ref diskPending) > 4) { Interlocked.Decrement(ref diskPending); return; }
        _ = Task.Run(async () => {
            await diskWriter.WaitAsync(); string? temporary = null;
            try {
                using var timing = PreviewMetrics.Measure("cache-write"); Directory.CreateDirectory(Root); string destination = DiskPath(key); temporary = destination + "." + Guid.NewGuid().ToString("N") + ".pending";
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(page.Image)); using (var output = File.Create(temporary)) encoder.Save(output);
                File.Move(temporary, destination + ".png", true); File.WriteAllText(temporary, JsonSerializer.Serialize(header)); File.Move(temporary, destination + ".json", true); CleanDisk();
            } catch { /* Disk cache is optional, never fail a preview because of it. */ }
            finally { if (temporary is not null) TryDelete(temporary); diskWriter.Release(); Interlocked.Decrement(ref diskPending); }
        });
    }
    private readonly object sync = new();
    private readonly Dictionary<string, (BitmapSource Image, long Bytes, long Access)> pages = new();
    private long bytes, clock;
    private string? activeDocumentId;
    internal void SetActiveDocument(string? id) { lock (sync) activeDocumentId = id; }
    internal int PageCount { get { lock (sync) return pages.Count; } }
    internal long MemoryBytes { get { lock (sync) return bytes; } }
    internal PreviewCacheService(string? root = null) => Root = root ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipShelf-PreviewCache", "native-v1");
    internal BitmapSource? Get(string key)
    {
        lock (sync) { if (!pages.TryGetValue(key, out var entry)) return null; pages[key] = (entry.Image, entry.Bytes, ++clock); return entry.Image; }
    }
    internal void Put(string key, BitmapSource image)
    {
        long cost = (long)image.PixelWidth * image.PixelHeight * 4;
        if (!image.IsFrozen || cost > MemoryLimit) return;
        lock (sync) {
            if (pages.Remove(key, out var old)) bytes -= old.Bytes;
            pages[key] = (image, cost, ++clock); bytes += cost;
            while (bytes > MemoryLimit || pages.Count > 24) {
                var victim = pages.Where(x => activeDocumentId is null || !x.Key.Contains(":" + activeDocumentId + ":", StringComparison.Ordinal)).OrderBy(x => x.Value.Access).FirstOrDefault();
                if (victim.Key is null) victim = pages.MinBy(x => x.Value.Access);
                bytes -= victim.Value.Bytes; pages.Remove(victim.Key);
            }
        }
    }
    // Only our cache directory and recognized generated names are eligible for cleanup.
    internal void CleanDisk()
    {
        try {
            Directory.CreateDirectory(Root);
            var files = new DirectoryInfo(Root).GetFiles().Where(f => f.Extension is ".pdf" or ".png" or ".json" && Path.GetFileNameWithoutExtension(f.Name).Length == 64 && Path.GetFileNameWithoutExtension(f.Name).All(Uri.IsHexDigit)).OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
            long total = 0;
            foreach (var file in files) { total += file.Length; if (total > DiskLimit || file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-14)) TryDelete(file.FullName); }
            foreach (var file in new DirectoryInfo(Root).GetFiles("*.pending")) { var parts = file.Name.Split('.'); if (parts.Length == 3 && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit) && parts[1].Length == 32 && parts[1].All(Uri.IsHexDigit) && file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)) TryDelete(file.FullName); }
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    internal static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    internal async Task<CleanupResult> ClearGeneratedAsync(string updateRoot, CancellationToken token)
    {
        await diskWriter.WaitAsync(token);
        try
        {
            lock (sync) { pages.Clear(); bytes = 0; models.Clear(); modelBytes = 0; }
            return await Task.Run(() => CacheCleanupService.Clean(Root, updateRoot, token), token);
        }
        finally { diskWriter.Release(); }
    }
    public void Dispose() { lock (sync) { pages.Clear(); bytes = 0; models.Clear(); modelBytes = 0; } Scheduler.Dispose(); }
}
