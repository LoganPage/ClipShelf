using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Threading.Tasks;

namespace ClipShelf;

/// <summary>Isolated drag-selection fixtures. The manual fixture observes input; it never injects it.</summary>
public static class DragSelectionTests
{
    /// <summary>Deterministic own-app regression checks; no system mouse or keyboard input is injected.</summary>
    public static async Task RunAsync(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var checks = new List<string>();
        var metrics = new Dictionary<string, double>();
        var captureDiagnostics = new List<object>();
        var watch = Stopwatch.StartNew();
        MainWindow? window = null;
        string? error = null;
        bool checkingSyntheticCapture = false;
        int suppressedUnheldSynchronizationMoves = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Drag-selection regression: " + name);
            checks.Add("PASS " + name);
        }
        async Task Idle() => await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        try
        {
            string fixtureDirectory = Path.Combine(directory, "regression-fixture-" + Guid.NewGuid().ToString("N"));
            var store = new HistoryStore(fixtureDirectory, deferredPersistence: true);
            store.Settings.Theme = "Light";
            store.Settings.MaxItems = 1000;
            store.Settings.HistoryEnabled = false;
            store.Settings.WatchScreenshots = false;
            store.Settings.LaunchAtLogin = false;
            for (int index = 0; index < 100; index++) store.Add(new ClipItem
            {
                Kind = ClipKind.Text,
                Title = $"Drag regression {index:D3}", Text = $"Independent drag fixture {index:D3}",
                CreatedAt = DateTimeOffset.Now.AddSeconds(-index)
            });
            Check(await store.FlushAsync(), "Synthetic fixture is persisted only in its isolated directory");
            window = new MainWindow(store, demo: true)
            {
                Title = "ClipShelf · 拖选隔离回归",
                Left = -12000, Top = -12000, Width = 900, Height = 800,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = false, ShowInTaskbar = false
            };
            window.Show();
            await Idle();
            var list = (HistoryListBox)window.FindName("HistoryList");
            var surface = (Border)window.FindName("HistoryBorder");
            var scroll = Descendant<ScrollViewer>(list) ?? throw new InvalidOperationException("Missing history scroll viewer.");
            bool DragFramesRunning() => Field(window, "dragRendering") is true;
            var nativeTimerField = typeof(ListBox).GetField("_autoScrollTimer", BindingFlags.Instance | BindingFlags.NonPublic);
            Check(nativeTimerField is not null, "This WPF runtime exposes the native ListBox auto-scroll timer for the control comparison");
            bool NativeTimerRunning() => nativeTimerField!.GetValue(list) is DispatcherTimer { IsEnabled: true };
            bool sawControlTimerStart = false;
            string capturePhase = "setup";
            void CaptureState(string owner, bool captured)
            {
                bool nativeTimerRunning = NativeTimerRunning();
                if (capturePhase == "old-owner-control" && owner == "HistoryList" && captured && nativeTimerRunning)
                    sawControlTimerStart = true;
                captureDiagnostics.Add(new
                {
                    milliseconds = watch.Elapsed.TotalMilliseconds,
                    phase = capturePhase,
                    owner,
                    captured,
                    currentOwner = Mouse.Captured?.GetType().Name,
                    listCaptured = list.IsMouseCaptured,
                    borderCaptured = surface.IsMouseCaptured,
                    nativeTimerExists = nativeTimerField!.GetValue(list) is not null,
                    nativeTimerRunning,
                    physicalLeftButton = Mouse.LeftButton.ToString()
                });
            }
            list.IsMouseCapturedChanged += (_, e) => CaptureState("HistoryList", (bool)e.NewValue);
            surface.IsMouseCapturedChanged += (_, e) => CaptureState("HistoryBorder", (bool)e.NewValue);
            // An unheld, off-screen capture asks WPF to synchronize the pointer. Its
            // generated MouseMove carries the real Released state and immediately
            // releases capture, unlike a physical drag. Suppress only that move
            // during explicitly scoped capture assertions. Do not fake button state,
            // modify production behavior, intercept release, or enable this in RunDemo.
            window.PreviewMouseMove += (_, e) =>
            {
                if (!checkingSyntheticCapture || e.LeftButton != MouseButtonState.Released) return;
                e.Handled = true;
                suppressedUnheldSynchronizationMoves++;
            };
            bool PointerDown() => Field(window, "pointerDown") is true;
            bool Dragging() => Field(window, "dragging") is true;
            bool SelectedExactly(params int[] indices) => list.SelectedItems.Cast<ClipItem>().ToHashSet()
                .SetEquals(indices.Select(index => (ClipItem)list.Items[index]));
            void End() => Invoke(window, "EndDrag", true);
            void Begin(int index, ModifierKeys modifiers = ModifierKeys.None)
                => window.BeginRowPointer(index, new Point(20, (index + 0.5) * 74 - scroll.VerticalOffset), modifiers);
            void DragTo(int index) => Invoke(window, "UpdateDragSelection", new Point(20, (index + 0.5) * 74 - scroll.VerticalOffset));
            void Edge(Point point) => Invoke(window, "AutoScrollDragAt", point);
            MouseButtonEventArgs ButtonEvent(DependencyObject source, RoutedEvent routedEvent) => new(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = routedEvent, Source = source };
            int selectionEvents = 0, resets = 0;
            list.SelectionChanged += (_, _) => selectionEvents++;
            var source = list.ItemsSource;
            ((INotifyCollectionChanged)source).CollectionChanged += (_, e) => { if (e.Action == NotifyCollectionChangedAction.Reset) resets++; };
            Check(window.Integration is null && list.Items.Count == 100, "Regression window has 100 synthetic rows and no clipboard integration");

            // This intentional old-owner comparison uses only this fixture's own WPF
            // capture API, never SendInput or an event posted to another application.
            checkingSyntheticCapture = true; capturePhase = "old-owner-control";
            Check(list.CaptureMouse(), "Control comparison can acquire capture on the old ListBox owner");
            CaptureState("control-after-capture", list.IsMouseCaptured);
            Check(list.IsMouseCaptured && NativeTimerRunning() && sawControlTimerStart,
                "The old ListBox capture lifecycle demonstrably starts WPF's competing native auto-scroll timer");
            list.ReleaseMouseCapture();
            Check(!NativeTimerRunning(), "Releasing the old ListBox owner stops its native auto-scroll timer");
            capturePhase = "production-row-route";
            var row = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromIndex(1)
                ?? throw new InvalidOperationException("Second fixture row was not realized.");
            // Raise the tunneling device-level event. The button-specific WPF
            // PreviewMouseLeftButtonDown/Up events are Direct events re-raised
            // at each element while the generic preview event traverses its route.
            var down = ButtonEvent(row, Mouse.PreviewMouseDownEvent);
            row.RaiseEvent(down);
            Check(down.Handled && PointerDown(), "The real row-down route begins exactly the application-owned pointer gesture");
            Check(ReferenceEquals(Mouse.Captured, surface) && surface.IsMouseCaptured,
                "HistoryBorder owns capture after a row press");
            Check(!list.IsMouseCaptured && !list.IsMouseCaptureWithin && !NativeTimerRunning(),
                "A row press leaves ListBox uncaptured and its competing native timer stopped");
            var up = ButtonEvent(surface, Mouse.PreviewMouseUpEvent);
            surface.RaiseEvent(up);
            Check(!PointerDown() && !Dragging() && !surface.IsMouseCaptured && !DragFramesRunning(),
                "The real release route ends pointer ownership and stops the application timer");

            // Exercise optional repeat-click cancellation with the same own-app
            // capture protection used above; no simulated global wheel shortcut.
            capturePhase = "repeated-click-wheel-cancel";
            scroll.ScrollToTop(); await Idle();
            store.Settings.DeselectOnRepeatedClick = true;
            window.ApplyRowSelection(0, ModifierKeys.None);
            window.ApplyRowSelection(1, ModifierKeys.None);
            Begin(1);
            Check(surface.CaptureMouse() && Field(window, "pendingDeselectId") is Guid && SelectedExactly(1),
                "A captured repeat press retains its sole selected row while deselection is pending");
            var cancelWheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
                { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = surface };
            surface.RaiseEvent(cancelWheel);
            Check(Field(window, "pendingDeselectId") is null && SelectedExactly(1),
                "The production captured-border wheel route cancels pending repeated-click deselection");
            Check(!window.CompleteRowPointer(new Point(20, 1.5 * 74 - scroll.VerticalOffset)) && SelectedExactly(1)
                && !PointerDown() && !surface.IsMouseCaptured && !NativeTimerRunning(),
                "Releasing on the original row after a wheel event preserves selection and releases capture");

            capturePhase = "repeated-click-capture-cancel";
            Begin(1);
            Check(surface.CaptureMouse() && Field(window, "pendingDeselectId") is Guid,
                "Capture-loss fixture begins with an actual pending repeated click");
            surface.ReleaseMouseCapture();
            Check(!PointerDown() && Field(window, "pendingDeselectId") is null
                && !window.CompleteRowPointer(new Point(20, 1.5 * 74 - scroll.VerticalOffset)) && SelectedExactly(1),
                "Losing the surface's real capture discards pending deselection and ignores a stale release");
            store.Settings.DeselectOnRepeatedClick = false;
            checkingSyntheticCapture = false; capturePhase = "range-logic";

            // Drive the exact frame-step method at different display cadences.
            // This checks time-based motion, not the physical monitor's displayed FPS.
            foreach (int hz in new[] { 60, 144, 240 })
            {
                scroll.ScrollToTop(); await Idle(); Begin(1); SetField(window, "dragging", true);
                double previous = scroll.VerticalOffset; int moved = 0;
                for (int frame = 0; frame < hz; frame++)
                {
                    window.AdvanceDragFrame(new Point(20, list.ActualHeight + 1), 1d / hz); await Idle();
                    if (scroll.VerticalOffset > previous) moved++; previous = scroll.VerticalOffset;
                }
                metrics[$"drag-{hz}Hz-distance"] = scroll.VerticalOffset;
                Check(moved == hz && Math.Abs(scroll.VerticalOffset - 22 / .03) < 4,
                    $"{hz} Hz frame steps all advance, with the same one-second drag distance");
                double top = scroll.VerticalOffset;
                window.AdvanceDragFrame(new Point(20, -1), 1d / hz); await Idle();
                Check(scroll.VerticalOffset < top, $"{hz} Hz direction reversal takes effect in the next step"); End();
            }

            scroll.ScrollToTop(); await Idle();
            Begin(1); DragTo(6);
            Check(SelectedExactly(1, 2, 3, 4, 5, 6), "Forward drag selects every row in the inclusive range");
            int beforeStableEndpoint = selectionEvents;
            for (int repeat = 0; repeat < 50; repeat++) DragTo(6);
            Check(selectionEvents == beforeStableEndpoint, "Fifty updates at the same endpoint emit no duplicate SelectionChanged event");
            DragTo(4);
            Check(SelectedExactly(1, 2, 3, 4) && selectionEvents == beforeStableEndpoint + 1,
                "Shrinking a range updates selection in one transaction without an empty intermediate state");
            End();
            Begin(6); DragTo(1);
            Check(SelectedExactly(1, 2, 3, 4, 5, 6), "Reverse drag produces the same inclusive range");
            End();
            window.ApplyRowSelection(0, ModifierKeys.None);
            window.ApplyRowSelection(20, ModifierKeys.Control);
            Begin(3, ModifierKeys.Control); DragTo(6);
            Check(SelectedExactly(0, 3, 4, 5, 6, 20), "Ctrl-drag preserves both disjoint base rows and adds the dragged range");
            DragTo(4);
            Check(SelectedExactly(0, 3, 4, 20), "Reversing Ctrl-drag removes only the temporary range extension");
            End();

            scroll.ScrollToVerticalOffset(740); await Idle();
            Begin(11);
            checkingSyntheticCapture = true; capturePhase = "border-edge-and-wheel";
            Check(surface.CaptureMouse() && !NativeTimerRunning(), "Dragging on the border retains exclusive capture without a native selection timer");
            SetField(window, "dragging", true);
            double beforeBottom = scroll.VerticalOffset;
            Edge(new Point(20, list.ActualHeight + 20));
            await Idle();
            Check(scroll.VerticalOffset > beforeBottom, "Holding below the viewport moves the real virtualized scroll offset downward");
            Edge(new Point(20, list.ActualHeight + 20));
            await Idle();
            Check(list.SelectedItems.Count > 1 && !NativeTimerRunning(), "Repeated bottom-edge updates retain a multi-row selection with no native timer");
            double beforeTop = scroll.VerticalOffset;
            Edge(new Point(20, -20));
            await Idle();
            Check(scroll.VerticalOffset < beforeTop, "Holding above the viewport moves the real virtualized scroll offset upward");
            Check(list.SelectedItems.Count > 1, "Top-edge selection remains a range rather than a competing native single-row selection");
            int beforeCenter = selectionEvents;
            double centerOffset = scroll.VerticalOffset;
            Edge(new Point(20, list.ActualHeight / 2));
            await Idle();
            Check(Math.Abs(scroll.VerticalOffset - centerOffset) < 0.1, "Dragging within the viewport does not auto-scroll");
            int afterCenter = selectionEvents;
            Edge(new Point(20, list.ActualHeight / 2));
            Check(selectionEvents == afterCenter && afterCenter <= beforeCenter + 1,
                "A stationary interior endpoint emits at most one range change and no repeated updates");

            double beforeWheel = scroll.VerticalOffset;
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = surface };
            surface.RaiseEvent(wheel);
            await Idle();
            Check(wheel.Handled && scroll.VerticalOffset > beforeWheel,
                "Wheel events reaching the captured border are forwarded to immediate pixel scrolling");
            Check(!NativeTimerRunning(), "Wheel scrolling during a drag never starts native ListBox auto-selection");
            End();
            checkingSyntheticCapture = false; capturePhase = "released";
            double afterRelease = scroll.VerticalOffset;
            var selectedAfterRelease = list.SelectedItems.Cast<ClipItem>().ToHashSet();
            Edge(new Point(20, list.ActualHeight + 20));
            await Idle();
            Check(scroll.VerticalOffset == afterRelease && selectedAfterRelease.SetEquals(list.SelectedItems.Cast<ClipItem>()),
                "After release, stale edge ticks cannot scroll or change selection");
            var ordinaryWheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = surface };
            surface.RaiseEvent(ordinaryWheel);
            Check(!ordinaryWheel.Handled, "An idle border leaves ordinary wheel routing to the list's normal handler");

            Begin(12); checkingSyntheticCapture = true; capturePhase = "border-lost-capture";
            Check(surface.CaptureMouse(), "Lost-capture fixture obtains the border capture");
            SetField(window, "dragging", true); Invoke(window, "StartDragFrames");
            var descendantCapture = new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                { RoutedEvent = Mouse.LostMouseCaptureEvent, Source = row };
            Invoke(window, "History_LostCapture", surface, descendantCapture);
            Check(PointerDown() && Dragging() && DragFramesRunning(),
                "A descendant's lost-capture event cannot cancel the border-owned gesture");
            surface.ReleaseMouseCapture();
            checkingSyntheticCapture = false; capturePhase = "capture-lost";
            Check(!PointerDown() && !Dragging() && !DragFramesRunning() && !surface.IsMouseCaptured,
                "Losing the border's own capture stops the gesture and its timer immediately");
            double afterCaptureLoss = scroll.VerticalOffset;
            Edge(new Point(20, -20)); await Idle();
            Check(scroll.VerticalOffset == afterCaptureLoss, "No edge scrolling continues after capture is lost");

            var stableItem = (ClipItem)list.Items[12];
            Begin(12); DragTo(16);
            var stableSelection = list.SelectedItems.Cast<ClipItem>().ToHashSet();
            var arrival = new ClipItem { Kind = ClipKind.Text, Title = "Arrival during pointer hold", Text = "Synthetic deferred arrival" };
            store.Add(arrival); await Idle();
            Check(list.Items.Count == 100 && ReferenceEquals(list.Items[12], stableItem)
                && stableSelection.SetEquals(list.SelectedItems.Cast<ClipItem>()),
                "An arriving record is deferred while the pointer gesture owns row identity and selection");
            End(); await Idle();
            Check(list.Items.Count == 101 && list.Items.Cast<ClipItem>().Any(item => item.Id == arrival.Id),
                "Releasing the gesture applies the deferred arrival");
            Check(stableSelection.SetEquals(list.SelectedItems.Cast<ClipItem>()),
                "Applying the deferred arrival preserves the selected record identities");

            scroll.ScrollToTop(); await Idle();
            var firstRow = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromIndex(0)
                ?? throw new InvalidOperationException("Initial row missing for button routing.");
            var button = Descendant<Button>(firstRow) ?? throw new InvalidOperationException("Row action button missing.");
            var beforeButton = list.SelectedItems.Cast<ClipItem>().ToHashSet();
            var buttonDown = ButtonEvent(button, UIElement.PreviewMouseLeftButtonDownEvent);
            Invoke(window, "History_MouseDown", surface, buttonDown);
            Check(!buttonDown.Handled && !PointerDown() && Mouse.Captured is null
                && beforeButton.SetEquals(list.SelectedItems.Cast<ClipItem>()),
                "Inline button presses are not consumed by drag-selection or allowed to alter the current selection");
            var bar = Descendant<ScrollBar>(scroll) ?? throw new InvalidOperationException("History scroll bar missing.");
            var barDown = ButtonEvent(bar, UIElement.PreviewMouseLeftButtonDownEvent);
            Invoke(window, "History_MouseDown", surface, barDown);
            Check(!barDown.Handled && !PointerDown() && Mouse.Captured is null,
                "Scroll-bar presses remain available to native thumb tracking instead of starting row dragging");
            Check(ReferenceEquals(source, list.ItemsSource) && resets == 0,
                "The entire drag and deferred-refresh sequence preserves the items source without collection resets");
            Check(!NativeTimerRunning() && !DragFramesRunning() && !PointerDown() && !Dragging(),
                "Regression completion leaves both timers and all pointer state stopped");
            metrics["selectionEvents"] = selectionEvents;
            metrics["collectionResets"] = resets;
        }
        catch (Exception exception) { error = exception.ToString(); Environment.ExitCode = 1; }
        finally
        {
            checkingSyntheticCapture = false;
            if (window is not null)
            {
                Invoke(window, "EndDrag", false);
                if (Mouse.Captured is UIElement captured && Window.GetWindow(captured) == window) captured.ReleaseMouseCapture();
            }
            metrics["elapsedMilliseconds"] = watch.Elapsed.TotalMilliseconds;
            metrics["suppressedUnheldSynchronizationMoves"] = suppressedUnheldSynchronizationMoves;
            File.WriteAllText(Path.Combine(directory, "drag-selection-results.json"), JsonSerializer.Serialize(new
            {
                passed = error is null,
                checks,
                metrics,
                captureDiagnostics,
                error,
                scope = "Deterministic own-app off-screen WPF checks with synthetic history only. Uses application handlers, routed events, and own-window WPF capture; no system input injection, clipboard services, external UI control, display frame-rate claim, or user history access. Scoped capture checks suppress WPF-synchronized MouseMove events when the real left button is Released; explicit release/lost-capture handlers remain active, and RunDemo/production are unmodified. Native timer comparison records capture-lifecycle activation, not a physical held-drag or long-hold visual reproduction."
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (window is not null) window.Quit(); else Application.Current.Shutdown();
        }
    }

    /// <summary>Shows only synthetic records; F12 finishes the fixture and saves its in-memory trace.</summary>
    public static void RunDemo(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        string fixtureDirectory = Path.Combine(directory, "fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureDirectory);
        var seed = Enumerable.Range(0, 100).Select(index => new ClipItem
        {
            Kind = ClipKind.Text,
            Title = $"独立合成记录 {index + 1:D3} · 拖选闪烁验证",
            Text = $"第 {index + 1:D3} 条独立合成数据；不读取或写入系统剪贴板。",
            CreatedAt = DateTimeOffset.Now.AddSeconds(-index)
        }).ToArray();
        File.WriteAllText(Path.Combine(fixtureDirectory, "history.json"), JsonSerializer.Serialize(seed));
        var store = new HistoryStore(fixtureDirectory);
        store.Settings.Theme = "Light";
        store.Settings.HistoryEnabled = false;
        store.Settings.WatchScreenshots = false;
        store.Settings.LaunchAtLogin = false;
        var window = new MainWindow(store, demo: true)
        {
            Title = "ClipShelf · 拖选闪烁验证",
            Width = 900,
            Height = 800
        };
        Application.Current.MainWindow = window;
        var list = (HistoryListBox)window.FindName("HistoryList");
        var surface = (Border)window.FindName("HistoryBorder");
        var events = new List<object>();
        var watch = Stopwatch.StartNew();
        var trackedRows = new HashSet<ListBoxItem>();
        const int maximumEvents = 60000;
        int omittedEvents = 0, selectionEvents = 0, collectionResets = 0, loadedRows = 0, unloadedRows = 0;
        bool written = false;
        FieldInfo? pointerField = typeof(MainWindow).GetField("pointerDown", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo? draggingField = typeof(MainWindow).GetField("dragging", BindingFlags.Instance | BindingFlags.NonPublic);
        bool Flag(FieldInfo? field) => field?.GetValue(window) is true;
        int[] Selected() => list.SelectedItems.Cast<ClipItem>().Select(item => list.Items.IndexOf(item)).OrderBy(index => index).ToArray();
        void Record(string kind, object? data = null)
        {
            if (events.Count >= maximumEvents) { omittedEvents++; return; }
            events.Add(new
            {
                sequence = events.Count,
                milliseconds = Math.Round(watch.Elapsed.TotalMilliseconds, 4),
                kind,
                pointerDown = Flag(pointerField),
                dragging = Flag(draggingField),
                selectedCount = list.SelectedItems.Count,
                capture = Mouse.Captured?.GetType().Name,
                focused = Keyboard.FocusedElement?.GetType().Name,
                data
            });
        }
        void WriteReport()
        {
            if (written) return;
            written = true;
            Record("closed");
            File.WriteAllText(Path.Combine(directory, "drag-ui-events.json"), JsonSerializer.Serialize(new
            {
                scope = "Manual own-app fixture with 100 independently generated text records; no clipboard services, user history, native input injection, or per-event disk writes.",
                version = typeof(MainWindow).Assembly.GetName().Version?.ToString(),
                elapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
                nativeIntegrationConnected = window.Integration is not null,
                selectionEvents,
                collectionResets,
                loadedRows,
                unloadedRows,
                omittedEvents,
                finalSelection = Selected(),
                events
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        void ObserveRows()
        {
            for (int index = 0; index < list.Items.Count; index++)
            {
                if (list.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem row || !trackedRows.Add(row)) continue;
                row.Loaded += (_, _) => { loadedRows++; Record("row-loaded", new { index = list.ItemContainerGenerator.IndexFromContainer(row) }); };
                row.Unloaded += (_, _) => { unloadedRows++; Record("row-unloaded", new { index = list.ItemContainerGenerator.IndexFromContainer(row) }); };
            }
        }
        list.ItemContainerGenerator.StatusChanged += (_, _) => ObserveRows();
        list.SelectionChanged += (_, e) =>
        {
            selectionEvents++;
            Record("selection", new
            {
                indices = Selected(),
                added = e.AddedItems.Cast<ClipItem>().Select(item => list.Items.IndexOf(item)).ToArray(),
                removed = e.RemovedItems.Cast<ClipItem>().Select(item => list.Items.IndexOf(item)).ToArray(),
                stack = selectionEvents <= 60 ? new StackTrace(1, false).ToString() : null
            });
        };
        surface.AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler((_, e) =>
        {
            Point point = e.GetPosition(list);
            Record("preview-mouse-move", new { observer = "HistoryBorder", x = point.X, y = point.Y, left = e.LeftButton.ToString(), modifiers = Keyboard.Modifiers.ToString(), handled = e.Handled });
        }), handledEventsToo: true);
        window.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, e) =>
        {
            Point point = e.GetPosition(list);
            Record("preview-mouse-down", new { x = point.X, y = point.Y, button = e.ChangedButton.ToString(), clicks = e.ClickCount, handled = e.Handled });
        }), handledEventsToo: true);
        window.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler((_, e) =>
        {
            Point point = e.GetPosition(list);
            Record("preview-mouse-up", new { x = point.X, y = point.Y, button = e.ChangedButton.ToString(), handled = e.Handled });
        }), handledEventsToo: true);
        surface.AddHandler(Mouse.GotMouseCaptureEvent, new MouseEventHandler((_, e) => Record("got-capture", new { observer = "HistoryBorder", source = e.OriginalSource?.GetType().Name })), handledEventsToo: true);
        surface.AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler((_, e) => Record("lost-capture", new { observer = "HistoryBorder", source = e.OriginalSource?.GetType().Name })), handledEventsToo: true);
        ((INotifyCollectionChanged)list.ItemsSource).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) collectionResets++;
            Record("collection-change", new { action = e.Action.ToString(), e.NewStartingIndex, e.OldStartingIndex });
        };
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.F12) return;
            e.Handled = true;
            Record("finish-key");
            window.Quit();
        };
        window.Closed += (_, _) => WriteReport();
        Application.Current.Exit += (_, _) => WriteReport();
        window.Loaded += (_, _) =>
        {
            ((TextBlock)window.FindName("StatusText")).Text = "独立合成 100 条记录 · 拖选测试 · F12 结束并保存日志";
            var scroll = Descendant<ScrollViewer>(list);
            if (scroll is not null) scroll.ScrollChanged += (_, e) => Record("scroll", new
            {
                e.VerticalOffset,
                e.VerticalChange,
                e.ViewportHeight,
                e.ViewportHeightChange,
                e.ExtentHeight,
                e.ExtentHeightChange
            });
            ObserveRows();
            Record("ready", new { list.ActualWidth, list.ActualHeight, items = list.Items.Count, nativeIntegrationConnected = window.Integration is not null });
        };
        window.Show();
    }

    private static T? Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (Descendant<T>(VisualTreeHelper.GetChild(root, index)) is T child) return child;
        return null;
    }

    private static object? Field(MainWindow window, string name)
        => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);

    private static void SetField(MainWindow window, string name, object value)
        => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);

    private static object? Invoke(MainWindow window, string name, params object[] arguments)
        => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);
}
