using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClipShelf;

/// <summary>
/// Smoothness diagnostics probe. Adds the measurements the scroll probe lacks:
/// UI-thread responsiveness (background sampler), GC pause duration and collection
/// counts, and a correlation between long stalls and GC activity.
///
/// Callback intervals are NOT displayed frames and NOT a refresh-rate guarantee.
/// </summary>
public static class SmoothnessProbe
{
    /// <summary>One process run == one pass. Repeat the process externally for distribution.</summary>
    public static async Task RunAsync(string reportPath, bool multiSelection = false, bool fineWheel = false, string? variant = null)
    {
        object? result = null;
        string? error = null;
        try { result = await OnePassAsync(multiSelection, fineWheel, variant); }
        catch (Exception exception) { error = exception.ToString(); }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            {
                passed = error is null,
                error,
                multiSelection,
                fineWheel,
                variant = variant ?? "default",
                warmup = (variant ?? "").ToLowerInvariant().Contains("warm"),
                result,
                scope = "In-process synthetic WPF fixture, 1000 records with 1/3 image rows. " +
                        "uiLatencyMs is a Dispatcher round-trip at Send priority from a background sampler, " +
                        "i.e. how long the UI thread took to answer. GC counters and pause duration are read " +
                        "in-process. Callback intervals are NOT displayed frames and NOT a refresh-rate guarantee."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { Application.Current?.Shutdown(error is null ? 0 : 1); }
    }

    private static async Task<object> OnePassAsync(bool multiSelection, bool fineWheel, string? variantName)
    {
        var intervals = new List<double>();
        var latencies = new List<double>();
        var stalls = new List<object>();

        string requested = (variantName ?? "default").ToLowerInvariant();
        bool warmup = requested.Contains("warm");
        string variant = requested.Replace("-warm", "").Replace("warm", "").Trim('-');
        if (variant.Length == 0) variant = "default";
        string fixture = Path.Combine(Path.GetTempPath(), "ClipShelf-smoothness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        string? picture = variant == "text" ? null : Path.Combine(fixture, "screenshot.png");
        if (picture is not null)
        {
            int height = variant == "small" ? 300 : 12000;
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(300, height, 96, 96, PixelFormats.Bgra32, null, new byte[300 * height * 4], 300 * 4);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var output = File.Create(picture)) encoder.Save(output);
        }

        Func<int, bool> isImageRow = index => picture is not null && index % 3 == 0;
        var seed = Enumerable.Range(0, 1000).Select(index => new ClipItem
        {
            Kind = isImageRow(index) ? ClipKind.Image : ClipKind.Text,
            ImagePath = isImageRow(index) ? picture : null,
            Text = $"独立示例记录 {index:D4} · 平滑度诊断，不包含你的剪贴板内容。",
            CreatedAt = DateTimeOffset.Now.AddSeconds(-index)
        }).ToArray();
        File.WriteAllText(Path.Combine(fixture, "history.json"), JsonSerializer.Serialize(seed));
        File.WriteAllText(Path.Combine(fixture, "settings.json"), "{\"MaxItems\":1000}");

        MainWindow? window = null;
        EventHandler? handler = null;
        var sampling = new CancellationTokenSource();
        Task? sampler = null;
        try
        {
            window = new MainWindow(new HistoryStore(fixture), demo: true) { ShowInTaskbar = false, Width = 720, Height = 572 };
            var list = (HistoryListBox)window.FindName("HistoryList")!;
            window.Show();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(700);

            var panel = Find<VirtualizingStackPanel>(list) ?? throw new InvalidOperationException("No virtualizing panel");
            if (multiSelection) list.SelectAll();

            double lastOffset = panel.VerticalOffset;
            double distance = 0;
            int frames = 0, movedFrames = 0, packets = 0;
            TimeSpan previousRendering = TimeSpan.MinValue;
            long lastTick = 0;

            void Packet(int delta)
            {
                var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = list };
                list.RaiseEvent(args); packets++;
            }

            // Optional warm-up pass: scroll once, settle, return to the top, then measure.
            // Separates a one-off "first realize of the viewport" cost from a per-crossing cost.
            if (warmup)
            {
                for (int index = 0; index < 12; index++) { Packet(-120); await Task.Delay(70); }
                await Task.Delay(700);
                list.CancelWheelMotion();
                ((IScrollInfo)panel).SetVerticalOffset(0);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                await Task.Delay(300);
                packets = 0; frames = 0; movedFrames = 0; distance = 0;
                lastOffset = panel.VerticalOffset;
            }

            var dispatcher = window.Dispatcher;
            sampler = Task.Run(async () =>
            {
                var watch = new Stopwatch();
                while (!sampling.IsCancellationRequested)
                {
                    watch.Restart();
                    try { await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Send, sampling.Token); }
                    catch (OperationCanceledException) { break; }
                    latencies.Add(watch.Elapsed.TotalMilliseconds);
                    await Task.Delay(1);
                }
            }, sampling.Token);

            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            long allocatedBefore = GC.GetTotalAllocatedBytes(true);
            TimeSpan pauseBefore = GC.GetTotalPauseDuration();
            long memoryBefore = GC.GetTotalMemory(false);

            handler = (_, args) =>
            {
                if (args is not RenderingEventArgs frame || frame.RenderingTime == previousRendering) return;
                long now = Stopwatch.GetTimestamp();
                if (lastTick != 0)
                {
                    double interval = (now - lastTick) * 1000.0 / Stopwatch.Frequency;
                    intervals.Add(interval);
                    if (interval > 33.4)
                        stalls.Add(new
                        {
                            intervalMs = interval,
                            offset = panel.VerticalOffset,
                            realized = Enumerable.Range(0, list.Items.Count)
                                .Count(index => list.ItemContainerGenerator.ContainerFromIndex(index) is not null),
                            thumbnails = ThumbnailCacheCount(),
                            allocatedMb = (GC.GetTotalAllocatedBytes(false) - allocatedBefore) / 1048576.0,
                            gc0 = GC.CollectionCount(0) - g0,
                            gc1 = GC.CollectionCount(1) - g1,
                            gc2 = GC.CollectionCount(2) - g2
                        });
                }
                lastTick = now; previousRendering = frame.RenderingTime; frames++;
                if (Math.Abs(panel.VerticalOffset - lastOffset) > 0.01) movedFrames++;
                distance += Math.Abs(panel.VerticalOffset - lastOffset);
                lastOffset = panel.VerticalOffset;
            };
            CompositionTarget.Rendering += handler;

            int wheelDelta = fineWheel ? -15 : -120;
            for (int index = 0; index < 50; index++) { Packet(wheelDelta); await Task.Delay(80); }

            CompositionTarget.Rendering -= handler; handler = null;
            await Task.Delay(400);

            int dg0 = GC.CollectionCount(0) - g0, dg1 = GC.CollectionCount(1) - g1, dg2 = GC.CollectionCount(2) - g2;
            long allocatedAfter = GC.GetTotalAllocatedBytes(true);
            TimeSpan pauseAfter = GC.GetTotalPauseDuration();
            long memoryAfter = GC.GetTotalMemory(false);

            sampling.Cancel();
            try { await sampler; } catch (OperationCanceledException) { }
            sampler = null;

            list.CancelWheelMotion();

            return new
            {
                frames, movedFrames, packets, traveledDip = distance,
                callbackIntervalMs = Summary(intervals),
                uiLatencyMs = Summary(latencies),
                uiBlocked = Blocked(latencies),
                gc = new
                {
                    gen0 = dg0, gen1 = dg1, gen2 = dg2,
                    allocatedBytes = allocatedAfter - allocatedBefore,
                    pauseDurationMs = (pauseAfter - pauseBefore).TotalMilliseconds,
                    memoryBefore, memoryAfter
                },
                stallCount = stalls.Count,
                stalls
            };
        }
        finally
        {
            if (handler is not null) CompositionTarget.Rendering -= handler;
            sampling.Cancel();
            if (sampler is not null) { try { await sampler; } catch (OperationCanceledException) { } }
            sampling.Dispose();
            window?.Quit();
        }
    }

    private static object Summary(List<double> samples)
    {
        var sorted = samples.OrderBy(value => value).ToArray();
        double Percentile(double p) => sorted.Length == 0 ? 0 : sorted[(int)Math.Clamp(Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
        return new
        {
            count = sorted.Length,
            p50 = Percentile(0.5),
            p90 = Percentile(0.9),
            p95 = Percentile(0.95),
            p99 = Percentile(0.99),
            max = Percentile(1)
        };
    }

    private static object Blocked(List<double> latencies)
    {
        int Over(double ms) => latencies.Count(value => value > ms);
        double Ratio(double ms) => latencies.Count == 0 ? 0 : (double)Over(ms) / latencies.Count;
        return new
        {
            over8 = Over(8),
            over16_7 = Over(16.7),
            over33_3 = Over(33.3),
            over50 = Over(50),
            ratio8 = Ratio(8),
            ratio16_7 = Ratio(16.7),
            ratio33_3 = Ratio(33.3)
        };
    }

    /// <summary>Reads ThumbnailLoader's private LRU size to see whether a stall coincides with decode completion.</summary>
    private static int ThumbnailCacheCount()
    {
        try
        {
            var field = typeof(ThumbnailLoader).GetField("cache",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            return (field?.GetValue(null) as System.Collections.ICollection)?.Count ?? -1;
        }
        catch { return -1; }
    }

    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) return value;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (Find<T>(VisualTreeHelper.GetChild(root, index)) is T found) return found;
        return null;
    }
}
