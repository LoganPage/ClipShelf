using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
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

public enum ScrollProbeDirection { Down, Up }
public enum ScrollProbeMode { Directional, Reverse, DownOnly, UpOnly }

/// <summary>
/// Smoothness diagnostics probe. Adds the measurements the scroll probe lacks:
/// UI-thread responsiveness (background sampler), GC pause duration and collection
/// counts, and a correlation between long stalls and GC activity.
///
/// Callback intervals are NOT displayed frames and NOT a refresh-rate guarantee.
/// </summary>
public static class SmoothnessProbe
{
    public static async Task RunAsync(string reportPath, bool multiSelection = false, bool fineWheel = false, string? variant = null,
        ScrollProbeDirection direction = ScrollProbeDirection.Down, bool pauseBeforeMeasurement = false, bool settings = false,
        bool undoBeforeMeasurement = false, string? scenario = null, int repeat = 1, ScrollProbeMode mode = ScrollProbeMode.Directional)
    {
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var samples = new List<JsonElement>();
        string? error = null;
        repeat = Math.Clamp(repeat, 1, 20);
        mode = ResolveMode(scenario, mode);
        try
        {
            for (int run = 1; run <= repeat; run++)
            {
                object sample = await OnePassAsync(multiSelection, fineWheel, variant, direction, pauseBeforeMeasurement,
                    settings, undoBeforeMeasurement, scenario, mode, run);
                samples.Add(JsonSerializer.SerializeToElement(sample));
            }
        }
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
                direction = direction.ToString(),
                pauseBeforeMeasurement,
                settings,
                undoBeforeMeasurement,
                repeat,
                mode = mode.ToString(),
                scenario = scenario ?? "default",
                variant = variant ?? "default",
                warmup = (variant ?? "").ToLowerInvariant().Contains("warm"),
                build = BuildFingerprint(),
                result = samples.Count == 0 ? null : (object)samples[0],
                samples,
                summary = DistributionSummary(samples),
                scope = ((scenario ?? "").StartsWith("real", StringComparison.OrdinalIgnoreCase)
                        ? "In-process read-only copy of the current local ClipShelf history; source files and user data are not modified. "
                        : "In-process synthetic WPF fixture, 1000 records with 1/3 image rows. ") +
                        "uiLatencyMs is a Dispatcher round-trip at Send priority from a background sampler, " +
                        "i.e. how long the UI thread took to answer. GC counters and pause duration are read " +
                        "in-process. Callback intervals are NOT displayed frames and NOT a refresh-rate guarantee."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception reportException)
        {
            error = string.Join(Environment.NewLine, new[] { error, "Report serialization failed: " + reportException }.Where(value => !string.IsNullOrWhiteSpace(value)));
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { passed = false, error }));
        }
        finally { Application.Current?.Shutdown(error is null ? 0 : 1); }
    }

    private static async Task<object> OnePassAsync(bool multiSelection, bool fineWheel, string? variantName,
        ScrollProbeDirection direction, bool pauseBeforeMeasurement, bool settings, bool undoBeforeMeasurement,
        string? scenarioName, ScrollProbeMode requestedMode, int sampleIndex)
    {
        var intervals = new List<double>();
        var latencies = new List<double>();
        var stalls = new List<object>();

        string requested = (variantName ?? "default").ToLowerInvariant();
        bool warmup = requested.Contains("warm");
        string variant = requested.Replace("-warm", "").Replace("warm", "").Trim('-');
        if (variant.Length == 0) variant = "default";
        string scenario = (scenarioName ?? "default").ToLowerInvariant();
        bool scenarioFine = scenario.EndsWith("-fine", StringComparison.Ordinal);
        bool realFixture = scenario.StartsWith("real", StringComparison.Ordinal);
        bool realPinnedOffscreen = scenario == "real-pinned-offscreen";
        ScrollProbeMode mode = ResolveMode(scenario, requestedMode);
        string fixture = Path.Combine(Path.GetTempPath(), "ClipShelf-smoothness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        string? picture = realFixture || variant == "text" ? null : Path.Combine(fixture, "screenshot.png");
        if (picture is not null)
        {
            int height = variant == "small" ? 300 : 12000;
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(300, height, 96, 96, PixelFormats.Bgra32, null, new byte[300 * height * 4], 300 * 4);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var output = File.Create(picture)) encoder.Save(output);
        }

        bool filtered = scenario is "filtered-pinned" or "filtered-pinned-fine" or "filtered-unpinned";
        bool pinned = scenario is "filtered-pinned" or "filtered-pinned-fine" or "all-pinned";
        if (realFixture)
        {
            string source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipShelf");
            string sourceHistory = Path.Combine(source, "history.json");
            if (!File.Exists(sourceHistory)) throw new InvalidOperationException("Real-data probe skipped: history.json does not exist.");
            File.Copy(sourceHistory, Path.Combine(fixture, "history.json"));
            string sourceSettings = Path.Combine(source, "settings.json");
            if (File.Exists(sourceSettings)) File.Copy(sourceSettings, Path.Combine(fixture, "settings.json"));
            else File.WriteAllText(Path.Combine(fixture, "settings.json"), "{\"MaxItems\":10000}");
        }
        else
        {
            Func<int, bool> isImageRow = index => picture is not null && index % 3 == 0;
            var seed = Enumerable.Range(0, 1000).Select(index => new ClipItem
            {
                Kind = isImageRow(index) ? ClipKind.Image : ClipKind.Text,
                ImagePath = isImageRow(index) ? picture : null,
                Text = $"独立示例记录 {index:D4} · 平滑度诊断，不包含你的剪贴板内容。",
                CreatedAt = DateTimeOffset.Now.AddSeconds(-index),
                IsPinned = pinned && index % 17 == 1
            }).ToArray();
            File.WriteAllText(Path.Combine(fixture, "history.json"), JsonSerializer.Serialize(seed));
            File.WriteAllText(Path.Combine(fixture, "settings.json"), "{\"MaxItems\":1000}");
        }

        MainWindow? window = null;
        EventHandler? handler = null;
        var sampling = new CancellationTokenSource();
        Task? sampler = null;
        try
        {
            var store = new HistoryStore(fixture);
            if (realFixture && store.Items.Count < 3)
                throw new InvalidOperationException("Real-data probe skipped: copied history contains fewer than three records.");
            window = new MainWindow(store, demo: true) { ShowInTaskbar = false, Width = 720, Height = 572 };
            window.SuppressApplicationShutdownForDiagnostics = true;
            var list = (HistoryListBox)window.FindName("HistoryList")!;
            window.Show();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(700);
            if (realFixture)
            {
                ClipItem pinnedItem;
                if (realPinnedOffscreen)
                {
                    var group = store.Items.GroupBy(item => item.Kind).OrderByDescending(items => items.Count()).First();
                    var candidates = group.Take(12).ToArray();
                    foreach (ClipItem item in candidates.Where(item => !item.IsPinned).ToArray()) store.TogglePinned([item.Id]);
                    pinnedItem = candidates[^1];
                }
                else
                {
                    pinnedItem = store.Items.FirstOrDefault(item => item.IsPinned) ?? store.Items[0];
                    if (!pinnedItem.IsPinned) store.TogglePinned([pinnedItem.Id]);
                }
                store.Settings.HistoryTypeFilter = pinnedItem.Kind switch
                {
                    ClipKind.File => HistoryTypeFilter.File,
                    ClipKind.Image => HistoryTypeFilter.Image,
                    _ => HistoryTypeFilter.Text
                };
                window.Refresh();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                await Task.Delay(250);
            }
            if (filtered)
            {
                store.Settings.HistoryTypeFilter = HistoryTypeFilter.Text;
                window.Refresh();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                await Task.Delay(250);
            }

            IScrollInfo panel;
            UIElement wheelTarget;
            if (settings)
            {
                var content = (ContentControl)window.FindName("SettingsContent")!;
                var overlay = (Grid)window.FindName("SettingsOverlay")!;
                content.Content = new SettingsPanel(window);
                overlay.Visibility = Visibility.Visible;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                var settingsScroll = Find<SmoothScrollViewer>(content) ?? throw new InvalidOperationException("No settings scroller");
                panel = Find<IScrollInfo>(settingsScroll) ?? throw new InvalidOperationException("No settings scroll presenter");
                wheelTarget = settingsScroll;
            }
            else
            {
                panel = Find<VirtualizingStackPanel>(list) ?? throw new InvalidOperationException("No virtualizing panel");
                wheelTarget = list;
            }
            if (multiSelection) list.SelectAll();

            if (undoBeforeMeasurement)
            {
                store.Remove(store.Items.Skip(12).Take(8).Select(item => item.Id));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                store.UndoDelete();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                await Task.Delay(300);
            }

            double lastOffset = panel.VerticalOffset;
            double distance = 0;
            int frames = 0, movedFrames = 0, packets = 0;
            int currentPacketIndex = 0;
            string currentPacketDirection = "none";
            TimeSpan previousRendering = TimeSpan.MinValue;
            long lastTick = 0;

            void Packet(int delta)
            {
                currentPacketIndex++;
                currentPacketDirection = delta < 0 ? "down" : "up";
                var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = wheelTarget };
                wheelTarget.RaiseEvent(args); packets++;
            }

            if (direction == ScrollProbeDirection.Up || mode == ScrollProbeMode.UpOnly)
            {
                panel.SetVerticalOffset(Math.Min(Math.Max(0, panel.ExtentHeight - panel.ViewportHeight), settings ? 760 : 1200));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                await Task.Delay(300);
            }

            if (pauseBeforeMeasurement)
            {
                for (int index = 0; index < 8; index++) { Packet(direction == ScrollProbeDirection.Up ? 120 : -120); await Task.Delay(70); }
                await Task.Delay(1500);
                packets = 0;
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
                            packetIndex = currentPacketIndex,
                            direction = currentPacketDirection,
                            offset = panel.VerticalOffset,
                            realized = settings ? -1 : Enumerable.Range(0, list.Items.Count)
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

            if (scenario == "filter-animation")
            {
                ((Button)window.FindName("FilterImageButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(420);
            }
            else
            {
                int magnitude = fineWheel || scenarioFine ? 15 : 120;
                int wheelDelta = magnitude * (direction == ScrollProbeDirection.Up ? 1 : -1);
                for (int index = 0; index < 50; index++)
                {
                    int packet = mode switch
                    {
                        ScrollProbeMode.Reverse => (index < 25 ? -magnitude : magnitude),
                        ScrollProbeMode.DownOnly => -magnitude,
                        ScrollProbeMode.UpOnly => magnitude,
                        _ => wheelDelta
                    };
                    Packet(packet); await Task.Delay(80);
                }
            }

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
            if (wheelTarget is SmoothScrollViewer smooth) smooth.CancelWheelMotion();

            return new
            {
                sampleIndex,
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
            if (window is not null)
            {
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed += (_, _) => closed.TrySetResult();
                window.Quit();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    private static ScrollProbeMode ResolveMode(string? scenarioName, ScrollProbeMode requestedMode)
    {
        string scenario = (scenarioName ?? "default").ToLowerInvariant();
        if (scenario == "real-down") return ScrollProbeMode.DownOnly;
        if (requestedMode != ScrollProbeMode.Directional) return requestedMode;
        return scenario is "filtered-pinned" or "filtered-pinned-fine" or "filtered-unpinned" or "all-pinned"
            or "unfiltered-unpinned" or "real" or "real-fine" or "real-pinned-offscreen"
            ? ScrollProbeMode.Reverse : ScrollProbeMode.Directional;
    }

    private static object BuildFingerprint()
    {
        string path = Assembly.GetExecutingAssembly().Location;
        var file = new FileInfo(path);
        using var stream = File.OpenRead(path);
        return new
        {
            sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(),
            fileSize = file.Length,
            buildTimestampUtc = file.LastWriteTimeUtc,
            version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown"
        };
    }

    private static object DistributionSummary(IReadOnlyList<JsonElement> samples)
    {
        double[] Values(Func<JsonElement, double> selector) => samples.Select(selector).ToArray();
        return new
        {
            callbackMaxMs = Distribution(Values(sample => sample.GetProperty("callbackIntervalMs").GetProperty("max").GetDouble())),
            callbackP99Ms = Distribution(Values(sample => sample.GetProperty("callbackIntervalMs").GetProperty("p99").GetDouble())),
            stallCount = Distribution(Values(sample => sample.GetProperty("stallCount").GetDouble())),
            uiOver33Count = Distribution(Values(sample => sample.GetProperty("uiBlocked").GetProperty("over33_3").GetDouble())),
            uiLatencyMaxMs = Distribution(Values(sample => sample.GetProperty("uiLatencyMs").GetProperty("max").GetDouble()))
        };
    }

    private static object Distribution(double[] values)
    {
        double[] sorted = values.OrderBy(value => value).ToArray();
        double median = sorted.Length == 0 ? 0 : sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
        return new
        {
            min = sorted.Length == 0 ? 0 : sorted[0],
            median,
            max = sorted.Length == 0 ? 0 : sorted[^1],
            all = values
        };
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

    private static T? Find<T>(DependencyObject root) where T : class
    {
        if (root is T value) return value;
        if (root is not Visual && root is not System.Windows.Media.Media3D.Visual3D) return null;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (Find<T>(VisualTreeHelper.GetChild(root, index)) is T found) return found;
        return null;
    }
}
