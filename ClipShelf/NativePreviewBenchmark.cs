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
        var metrics = new List<object>(); object? preheatComparison = null; string? error = null;
        double workingSetBeforeMiB = Process.GetCurrentProcess().WorkingSet64 / 1048576d;
        try {
            foreach (string ext in new[] { "docx", "pptx" }) foreach (int count in new[] { 3, 100 }) {
                string source = Path.Combine(root, $"fixture-{count}.{ext}");
                if (ext == "docx") NativePreviewTests.Docx(source, count);
                else NativePreviewTests.Pptx(source, count);
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
                metrics.Add(new { format = ext, fixtureUnits = count, unit = ext == "docx" ? "paragraphs" : "slides", initialKnownPages = first.CountFinal ? first.Count : (int?)null,
                    bytes = id.Size, coldMs = cold, hotMs = warm, uncachedPageMs = pageCold, cachedPageMs = pageWarm,
                    correctFinalTargets = correct, totalTargetTrials = 10, hitRatio = cache.Hits / (double)Math.Max(1, cache.Hits + cache.Misses), approximate = first.IsApproximate });
            }
            var adjacent = new[] { FilePreviewTests.Item(Path.Combine(root, "fixture-3.docx")), FilePreviewTests.Item(Path.Combine(root, "fixture-100.docx")), FilePreviewTests.Item(Path.Combine(root, "fixture-3.pptx")) };
            async Task<(double LatencyMs, double HitRatio, int WarmCount, double WorkingSetMiB)> MeasureAdjacent(bool enabled) {
                using var local = new PreviewCacheService(Path.Combine(root, enabled ? "adjacent-warm" : "adjacent-cold"));
                await using var session = new PreviewSession(adjacent, 1, local, prewarmAdjacent: enabled);
                session.Start(); await session.Pending; await session.PrefetchPending; await session.WarmupPending;
                int hits = 0;
                foreach (int index in new[] { 0, 2 }) {
                    var id = DocumentIdentity.Read(adjacent[index].FilePaths[0]);
                    if (local.ReadPage(PreviewCacheService.RenderKey(id, 0, session.PixelWidth, session.PixelDpi), default) is not null) hits++;
                }
                var clock = Stopwatch.StartNew(); session.NavigateRecord(-1); await session.Pending; double first = clock.Elapsed.TotalMilliseconds;
                session.NavigateRecord(1); await session.Pending;
                clock.Restart(); session.NavigateRecord(1); await session.Pending; double second = clock.Elapsed.TotalMilliseconds;
                return ((first + second) / 2, hits / 2d, session.WarmupCompleted, Process.GetCurrentProcess().WorkingSet64 / 1048576d);
            }
            var baseline = await MeasureAdjacent(false);
            var optimized = await MeasureAdjacent(true);
            preheatComparison = new { baselineNoPreheat = new { adjacentFirstScreenMs = baseline.LatencyMs, preheatHitRatio = baseline.HitRatio, workingSetMiB = baseline.WorkingSetMiB },
                optimizedPreheat = new { adjacentFirstScreenMs = optimized.LatencyMs, preheatHitRatio = optimized.HitRatio, warmCount = optimized.WarmCount, workingSetMiB = optimized.WorkingSetMiB } };
        } catch (Exception e) { error = e.ToString(); }
        if (error is null && (metrics.Count != 4 || preheatComparison is null)) error = "Benchmark did not complete every generated sample and adjacent-record comparison.";
        File.WriteAllText(Path.Combine(root, "benchmark.json"), JsonSerializer.Serialize(new { passed = error is null, metrics, preheatComparison, workingSetBeforeMiB,
            workingSetAfterMiB = Process.GetCurrentProcess().WorkingSet64 / 1048576d, peakWorkingSetMiB = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576d, error,
            scope = "Self-generated synthetic DOCX/PPTX fixtures; no Office process is started. Before/after are same-run no-preheat/preheat comparisons, not a historical version baseline. Timings are engine request-to-bitmap, not physical screen presentation or actual displayed FPS." }, new JsonSerializerOptions { WriteIndented = true }));
        Application.Current.Shutdown(error is null ? 0 : 1);
    }
    private static void Save(BitmapSource image, string path) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream); }
}
