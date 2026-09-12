using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ClipShelf;
internal static class NativePreviewBenchmark
{
    internal static async Task RunAsync(string root) {
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown; root = Path.GetFullPath(root); Directory.CreateDirectory(root);
        var metrics = new List<object>(); string? error = null;
        try {
            // Read-only reuse of the synthetic Office-produced samples measured by the previous engine.
            string original = Path.Combine(Environment.CurrentDirectory, "windows", "artifacts", "v1.0.26-release-office");
            foreach (string ext in new[] { "docx", "pptx" }) foreach (int count in new[] { 3, 100 }) {
                string source = Path.Combine(original, $"fixture-{count}.{ext}"); if (!File.Exists(source)) continue;
                using var cache = new PreviewCacheService(Path.Combine(root, $"cache-{ext}-{count}"));
                await using var renderer = new PdfPageRenderService(cache); var id = DocumentIdentity.Read(source); var timer = Stopwatch.StartNew();
                var first = await renderer.RenderAsync(id, 0, 1000, default); double cold = timer.Elapsed.TotalMilliseconds;
                Save(first.Image, Path.Combine(root, $"{ext}-{count}.png"));
                timer.Restart(); await renderer.RenderAsync(id, 0, 1000, default); double warm = timer.Elapsed.TotalMilliseconds;
                timer.Restart(); await renderer.RenderAsync(id, 1, 1000, default); double pageCold = timer.Elapsed.TotalMilliseconds;
                timer.Restart(); await renderer.RenderAsync(id, 1, 1000, default); double pageWarm = timer.Elapsed.TotalMilliseconds;
                var records = new[] { FilePreviewTests.Item(source) }; await using var session = new PreviewSession(records, 0, cache); session.Start(); await session.Pending;
                int correct = 0;
                for (int run = 0; run < 10; run++) { for (int i = 0; i < 10; i++) { session.NavigatePage(1); session.NavigatePage(-1); } await session.Pending; if (session.Presented?.Page == 0 && session.Presented.DocumentId == id.Id) correct++; }
                metrics.Add(new { format = ext, sourcePages = count, bytes = id.Size, coldMs = cold, hotMs = warm, uncachedPageMs = pageCold, cachedPageMs = pageWarm,
                    correctFinalTargets = correct, totalTargetTrials = 10, hitRatio = cache.Hits / (double)Math.Max(1, cache.Hits + cache.Misses), approximate = first.IsApproximate });
            }
        } catch (Exception e) { error = e.ToString(); }
        File.WriteAllText(Path.Combine(root, "benchmark.json"), JsonSerializer.Serialize(new { passed = error is null, metrics, peakWorkingSetMiB = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576d, error,
            scope = "Read-only original synthetic samples from v1.0.26 baseline; no Office process is started. Timings are engine request-to-bitmap, not physical screen presentation. Historical baseline was recorded in another run, not simultaneous controlled hardware benchmarking." }, new JsonSerializerOptions { WriteIndented = true }));
        Application.Current.Shutdown(error is null ? 0 : 1);
    }
    private static void Save(BitmapSource image, string path) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream); }
}
