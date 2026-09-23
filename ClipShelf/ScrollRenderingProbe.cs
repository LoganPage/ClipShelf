using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClipShelf;

/// <summary>Explicit synthetic scroll probe. Callback cadence is NOT displayed FPS.</summary>
public static class ScrollRenderingProbe
{
    public static async Task RunAsync(string reportPath, bool previewStress = false, bool multiSelection = false, bool fineWheel = false)
    {
        var checks = new List<string>(); var intervals = new List<double>(); var renderIntervals = new List<double>();
        MainWindow? window = null; HistoryListBox? list = null; EventHandler? handler = null;
        string? error = null; int frames = 0, movedFrames = 0, packets = 0, handledPackets = 0, resets = 0, realized = 0;
        int tier = RenderCapability.Tier >> 16; bool animations = SystemParameters.ClientAreaAnimation;
        double distance = 0; long lastTick = 0; TimeSpan previousRendering = TimeSpan.MinValue;
        PreviewCacheService? previewCache = null; PreviewSession? previewSession = null; using var previewCancel = new CancellationTokenSource(); Task previewWork = Task.CompletedTask;
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
        try
        {
            RunMotionChecks(Check);
            string fixture = Path.Combine(Path.GetTempPath(), "ClipShelf-scroll-probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixture);
            string longImage = Path.Combine(fixture, "long-screenshot.png");
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(300, 12000, 96, 96, PixelFormats.Bgra32, null, new byte[300 * 12000 * 4], 300 * 4);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap)); using (var output = File.Create(longImage)) encoder.Save(output);
            var seed = Enumerable.Range(0, 1000).Select(index => new ClipItem { Kind = index % 3 == 0 ? ClipKind.Image : ClipKind.Text, ImagePath = index % 3 == 0 ? longImage : null, Text = $"独立示例记录 {index:D4} · 连续滚动测试，不包含你的剪贴板内容。", CreatedAt = DateTimeOffset.Now.AddSeconds(-index) }).ToArray();
            File.WriteAllText(Path.Combine(fixture, "history.json"), JsonSerializer.Serialize(seed));
            File.WriteAllText(Path.Combine(fixture, "settings.json"), "{\"MaxItems\":1000}");
            window = new MainWindow(new HistoryStore(fixture), demo: true) { ShowInTaskbar = false, Width = 720, Height = 572 };
            list = (HistoryListBox)window.FindName("HistoryList");
            window.Show();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(700);
            if (previewStress) {
                string document = Path.Combine(fixture, "scroll-preview-load.docx"); NativePreviewTests.Docx(document, 3000);
                string before = Path.Combine(fixture, "scroll-preview-before.pptx"); NativePreviewTests.Pptx(before, 10);
                string after = Path.Combine(fixture, "scroll-preview-after.docx"); NativePreviewTests.Docx(after, 20);
                previewCache = new PreviewCacheService(Path.Combine(fixture, "preview-cache"));
                previewSession = new PreviewSession(new[] { FilePreviewTests.Item(before), FilePreviewTests.Item(document), FilePreviewTests.Item(after) }, 1, previewCache, prewarmAdjacent: true);
                previewSession.Start();
                var stressSession = previewSession;
                previewWork = Task.Run(async () => { await stressSession.Pending; previewCancel.Token.ThrowIfCancellationRequested(); stressSession.SetZoomFactor(2.3);
                    await stressSession.Pending; await stressSession.PrefetchPending; await stressSession.WarmupPending; }, previewCancel.Token);
            }
            Check(window.Integration is null && list.Items.Count == 1000, "Visible probe contains only 1000 synthetic records and no native integration");
            if (multiSelection) { list.SelectAll(); Check(list.SelectedItems.Count == 1000, "Scroll probe retains 1000 selected rows"); }
            var panel = Find<VirtualizingStackPanel>(list) ?? throw new InvalidOperationException("No virtualizing panel");
            var scroll = Find<ScrollViewer>(list) ?? throw new InvalidOperationException("No ScrollViewer");
            var scrollbar = (ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
            var thumb = Find<Thumb>(scrollbar) ?? throw new InvalidOperationException("No scroll thumb");
            var grip = (Border)thumb.Template.FindName("Grip", thumb);
            var rowContent = Find<CachedHistoryRow>(list) ?? throw new InvalidOperationException("No row content");
            double gapStart = rowContent.TranslatePoint(new Point(rowContent.ActualWidth, 0), scroll).X;
            double gripCenter = grip.TranslatePoint(new Point(grip.ActualWidth / 2, 0), scroll).X;
            Check(Math.Abs(gripCenter - (gapStart + scroll.ActualWidth) / 2) <= 1,
                "Scrollbar grip is centered in the gap between row content and the right edge");
            Check(scrollbar.ActualWidth == 13 && grip.ActualWidth == 5, "Scrollbar hit area and visible width are unchanged");
            ((INotifyCollectionChanged)list.ItemsSource).CollectionChanged += (_, args) => { if (args.Action == NotifyCollectionChangedAction.Reset) resets++; };
            void Packet(int delta)
            {
                var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = list };
                list.RaiseEvent(args); packets++; if (args.Handled) handledPackets++;
            }
            double lastOffset = panel.VerticalOffset;
            handler = (_, args) => {
                if (args is not RenderingEventArgs frame || frame.RenderingTime == previousRendering) return;
                long now = Stopwatch.GetTimestamp();
                if (lastTick != 0) { intervals.Add((now - lastTick) * 1000.0 / Stopwatch.Frequency); renderIntervals.Add((frame.RenderingTime - previousRendering).TotalMilliseconds); }
                lastTick = now; previousRendering = frame.RenderingTime; frames++;
                if (Math.Abs(panel.VerticalOffset - lastOffset) > 0.01) movedFrames++;
                distance += Math.Abs(panel.VerticalOffset - lastOffset); lastOffset = panel.VerticalOffset;
            };
            CompositionTarget.Rendering += handler;
            int wheelDelta = fineWheel ? -15 : -120;
            for (int index = 0; index < 50; index++) { Packet(wheelDelta); await Task.Delay(80); }
            CompositionTarget.Rendering -= handler; handler = null;
            await Task.Delay(400);
            Check(handledPackets == packets, "Every synthetic wheel packet entered the production wheel handler");
            Check(panel.VerticalOffset > WheelScrollMotion.WheelDistance(wheelDelta, SystemParameters.WheelScrollLines, panel.ViewportHeight) * 40, "Production wheel input moves the real virtualized offset continuously");
            Check(resets == 0 && VirtualizingPanel.GetScrollUnit(list) == ScrollUnit.Pixel, "Scrolling retains the same pixel-virtualized collection");
            realized = Enumerable.Range(0, 1000).Count(index => list.ItemContainerGenerator.ContainerFromIndex(index) is not null);
            Check(realized < 40, "Continuous scroll keeps fewer than 40 realized containers for 1000 records");
            Packet(-120); await Task.Delay(30); list.CancelWheelMotion();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            double stopped = panel.VerticalOffset; await Task.Delay(150);
            Check(Math.Abs(panel.VerticalOffset - stopped) < 0.1, "Cancelling stops all subsequent wheel movement");
            list.CancelWheelMotion(); ((IScrollInfo)panel).SetVerticalOffset(1000);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            double beforeFine = panel.VerticalOffset; Packet(-15);
            Check(!SystemParameters.ClientAreaAnimation || list.IsWheelAnimating, "Small wheel packets use the ordinary frame driver, not per-packet jumps");
            await Task.Delay(200);
            Check(Math.Abs(panel.VerticalOffset - beforeFine - WheelScrollMotion.WheelDistance(-15, SystemParameters.WheelScrollLines, panel.ViewportHeight)) < 1,
                "Small wheel deltas are not promoted to a whole-notch jump");
            // Exercise handoffs in the same window, not independent process-only tests.
            for (int round = 0; round < 3; round++)
            {
                list.CancelWheelMotion(); ((IScrollInfo)panel).SetVerticalOffset(1000);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Packet(-120); await Task.Delay(25);
                window.BeginRowPointer(15, new Point(20, 40), ModifierKeys.None);
                Check(!list.IsWheelAnimating, "Starting selection cancels only the previous wheel gesture");
                typeof(MainWindow).GetField("dragging", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(window, true);
                for (int frame = 0; frame < 24; frame++) { window.AdvanceDragFrame(new Point(20, list.ActualHeight + 1), 1d / 144); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
                window.CompleteRowPointer(new Point(20, list.ActualHeight - 5));
                var selectedIds = list.SelectedItems.Cast<ClipItem>().Select(item => item.Id).ToHashSet();
                double afterDrag = panel.VerticalOffset; Packet(-15); await Task.Delay(200);
                Check(panel.VerticalOffset > afterDrag && selectedIds.SetEquals(list.SelectedItems.Cast<ClipItem>().Select(item => item.Id)), "Wheel after drag release moves normally and preserves selection");
                list.UnselectAll(); Packet(-120);
                Check(!SystemParameters.ClientAreaAnimation || list.IsWheelAnimating, "Clearing multiselection does not disable ordinary smooth scrolling");
            }
            Packet(-120); list.CancelWheelMotion(); scroll.ScrollToVerticalOffset(222);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(140);
            Check(Math.Abs(panel.VerticalOffset - 222) < 1, "Programmatic navigation is not undone by residual wheel motion");
        }
        catch (Exception exception) { error = exception.ToString(); }
        finally
        {
            if (handler is not null) CompositionTarget.Rendering -= handler;
            previewCancel.Cancel(); previewSession?.Cancel(); try { await previewWork; } catch (OperationCanceledException) { } catch (Exception e) { error ??= e.GetType().Name; }
            if (previewSession is not null) await previewSession.DisposeAsync(); previewCache?.Dispose();
            list?.CancelWheelMotion();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { passed = error is null, checks, error, renderTier = tier, clientAreaAnimation = animations,
                previewStress, multiSelection, fineWheel, frames, movedFrames, offsetChangedRatio = frames > 0 ? (double)movedFrames / frames : 0, packets, handledPackets, traveledDip = distance,
                realizedContainers = realized, collectionResets = resets,
                callbackIntervalMs = Summary(intervals), scheduledRenderIntervalMs = Summary(renderIntervals),
                scope = "Visible synthetic WPF fixture with optional adjacent-record warmup and 2.3x high-resolution preview stress; app-internal routed events, no injected system input or real clipboard. Callback intervals and offset changes are NOT measured displayed frames or a 160 FPS guarantee." }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Quit(); Application.Current.Shutdown(error is null ? 0 : 1);
        }
    }

    private static object Summary(List<double> samples)
    {
        var sorted = samples.OrderBy(value => value).ToArray();
        double Percentile(double p) => sorted.Length == 0 ? 0 : sorted[(int)Math.Clamp(Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
        return new { count = sorted.Length, p50 = Percentile(0.5), p95 = Percentile(0.95), max = Percentile(1) };
    }
    private static void RunMotionChecks(Action<bool, string> check)
    {
        check(!HistoryListBox.UsesNativeWheel(ModifierKeys.None, false) && !HistoryListBox.UsesNativeWheel(ModifierKeys.Control, false)
            && !HistoryListBox.UsesNativeWheel(ModifierKeys.Shift | ModifierKeys.Control, false), "Normal, Ctrl and Shift selection modes share wheel handling");
        check(HistoryListBox.UsesNativeWheel(ModifierKeys.Alt, false) && HistoryListBox.UsesNativeWheel(ModifierKeys.None, true), "System/stylus input keeps its native route");
        check(WheelScrollMotion.WheelDistance(-120, 3, 500) == 48, "Wheel line setting preserves the familiar full-notch distance");
        check(WheelScrollMotion.WheelDistance(-15, 3, 500) == 6 && WheelScrollMotion.WheelDistance(-240, 3, 500) == 96, "Fractional and multi-notch packets retain their true magnitude");
        check(WheelScrollMotion.WheelDistance(-120, 0, 500) == 0 && WheelScrollMotion.WheelDistance(-120, -1, 500) == 500, "Zero-line and page-scroll system settings are honored");
        check(WheelScrollMotion.IsFractionalWheelDelta(-15) && !WheelScrollMotion.IsFractionalWheelDelta(240), "Precision packets are distinguished without truncating them");
        double? reference = null;
        foreach (int rate in new[] { 60, 120, 144, 160, 240 })
        {
            var motion = new WheelScrollMotion(); motion.Reset(100, 10000); motion.AddDistance(480, 10000);
            for (int index = 0; index < rate / 4; index++) motion.Advance(1.0 / rate, 10000);
            reference ??= motion.Position;
            check(Math.Abs(motion.Position - reference.Value) < 0.00001, $"Quarter-second trajectory is equal at {rate} Hz time steps");
            for (int index = 0; index < rate; index++) motion.Advance(1.0 / rate, 10000);
            check(!motion.IsActive && motion.Position == 580, $"Motion settles without an endless callback at {rate} Hz time steps");
        }
        var reverse = new WheelScrollMotion(); reverse.Reset(200, 10000); reverse.AddDistance(480, 10000); reverse.Advance(0.04, 10000);
        double current = reverse.Position; reverse.AddDistance(-48, 10000);
        check(Math.Abs(reverse.Target - (current - 48)) < 0.00001, "Reversing cancels old-direction scroll backlog from the current position");
        reverse.Advance(2, 10000); check(!reverse.IsActive && Math.Abs(reverse.Position - (current - 48)) < 0.00001, "Reversed motion reaches the newly requested target");
        reverse.Reset(0, 500); reverse.AddDistance(-200, 500); check(!reverse.IsActive && reverse.Position == 0, "Top edge does not overscroll");
        reverse.Reset(495, 500); reverse.AddDistance(200, 500); reverse.Advance(2, 500); check(reverse.Position == 500 && !reverse.IsActive, "Bottom edge clamps and stops");
        reverse.Reset(100, 500); reverse.AddDistance(200, 500); reverse.Advance(0.1, 500); reverse.Reset(reverse.Position, 500);
        current = reverse.Position; reverse.Advance(1, 500); check(reverse.Position == current && !reverse.IsActive, "Explicit interruption retains the presentation position");
    }
    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) return value;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (Find<T>(VisualTreeHelper.GetChild(root, index)) is T found) return found;
        return null;
    }
}
