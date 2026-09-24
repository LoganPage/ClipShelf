using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;

namespace ClipShelf;

/// <summary>Own-app layout/raster regression checks; no desktop input or clipboard access.</summary>
public static class LayoutRegressionTests
{
    private sealed record CheckResult(string Name, bool Passed, string? Detail = null);
    private sealed record CaptureResult(string File, string Theme, double WidthDip, double HeightDip,
        double RenderScale, int WidthPixels, int HeightPixels, double LayoutDpiX, double LayoutDpiY);
    private sealed record ResizeResult(string Theme, int Step, double WidthDip, double HeightDip,
        int CommonContainers, int ReusedContainers, int RealizedContainers, double LayoutMilliseconds);
    private sealed record NativeDwmResult(string Stage, bool CompositionQuerySucceeded, bool CompositionEnabled,
        bool ValidFixtureWindow, int? RegionType, int LastError, string? Detail = null);
    private sealed record NativeAppearanceResult(string Stage, WindowState RequestedWindowState,
        double LayoutDpiX, double LayoutDpiY, WindowAppearanceStatus Status);
    private sealed record ScrollbarGeometryResult(string Stage, double RequestedWidth, double ActualWidth,
        double DesiredWidth, double MinWidth, string ScrollbarBoundsInList, double ThumbWidth,
        string ThumbBoundsInList, string GripBoundsInList, string DeleteButtonBoundsInList,
        double HitClearanceDip, double VisualClearanceDip);
    private sealed class Raster(RenderTargetBitmap bitmap, double scale)
    {
        internal RenderTargetBitmap Bitmap { get; } = bitmap;
        internal double Scale { get; } = scale;
        private readonly byte[] pixels = Pixels(bitmap);
        private static byte[] Pixels(RenderTargetBitmap image)
        {
            var result = new byte[checked(image.PixelWidth * image.PixelHeight * 4)];
            image.CopyPixels(result, image.PixelWidth * 4, 0); return result;
        }
        internal Color At(int x, int y)
        {
            x = Math.Clamp(x, 0, Bitmap.PixelWidth - 1); y = Math.Clamp(y, 0, Bitmap.PixelHeight - 1);
            int index = (y * Bitmap.PixelWidth + x) * 4;
            return Color.FromArgb(pixels[index + 3], pixels[index + 2], pixels[index + 1], pixels[index]);
        }
        internal Color At(Point point) => At((int)Math.Floor(point.X * Scale), (int)Math.Floor(point.Y * Scale));
    }

    public static async Task RunAsync(string reportDirectory)
    {
        reportDirectory = Path.GetFullPath(reportDirectory);
        Directory.CreateDirectory(reportDirectory);
        var checks = new List<CheckResult>();
        var captures = new List<CaptureResult>();
        var resizeMetrics = new List<ResizeResult>();
        var nativeDwm = new List<NativeDwmResult>();
        var nativeAppearance = new List<NativeAppearanceResult>();
        var scrollbarGeometry = new List<ScrollbarGeometryResult>();
        MainWindow? window = null;
        string? fatalError = null;
        int resets = 0;
        NotifyCollectionChangedEventHandler? collectionHandler = null;
        INotifyCollectionChanged? collection = null;
        void Check(bool passed, string name, string? detail = null) => checks.Add(new CheckResult(name, passed, detail));
        async Task Idle()
        {
            await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            window?.UpdateLayout();
        }
        try
        {
            string fixtureDirectory = Path.Combine(Path.GetTempPath(), "ClipShelf-layout-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixtureDirectory);
            var seed = Enumerable.Range(0, 80).Select(index => new ClipItem
            {
                Title = $"布局测试 {index + 1:D2} · 连续选中记录",
                Text = $"这是一条独立合成记录 {index + 1}，用于验证边框和选中底色。",
                CreatedAt = new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero).AddSeconds(-index)
            }).ToArray();
            File.WriteAllText(Path.Combine(fixtureDirectory, "history.json"), JsonSerializer.Serialize(seed));
            var store = new HistoryStore(fixtureDirectory);
            store.Settings.Theme = "Light"; store.Settings.SelectionPreset = "appleBlue";
            window = new MainWindow(store, demo: true)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -12000, Top = -12000, ShowActivated = false, Width = 720, Height = 572
            };
            window.Show(); await Idle();
            CheckNativeRegion(window, "Initial fixture window", nativeDwm, Check);
            CheckNativeAppearance(window, "Initial fixture window", nativeAppearance, Check);
            var frame = Require<Border>(window, "WindowFrame");
            var header = Require<FrameworkElement>(window, "HeaderPanel");
            var search = Require<Border>(window, "SearchBorder");
            var history = Require<Border>(window, "HistoryBorder");
            var list = Require<HistoryListBox>(window, "HistoryList");
            var scroll = Find<ScrollViewer>(list) ?? throw new InvalidOperationException("History ScrollViewer was not generated.");
            var source = list.ItemsSource;
            collection = source as INotifyCollectionChanged;
            if (collection is not null)
            {
                collectionHandler = (_, args) => { if (args.Action == NotifyCollectionChangedAction.Reset) resets++; };
                collection.CollectionChanged += collectionHandler;
            }
            Check(window.Integration is null, "Fixture never starts clipboard or native services");
            Check(list.Items.Count == 80, "Only 80 synthetic text records are loaded");
            Check(VirtualizingPanel.GetIsVirtualizing(list) && VirtualizingPanel.GetScrollUnit(list) == ScrollUnit.Pixel,
                "Pixel-scrolling virtualization remains enabled");
            Check(IsZero(list.Padding) && IsZero(list.BorderThickness), "History list has no implicit padding or border");
            Check(list.FocusVisualStyle is null, "History list has no focus adornment");
            var chrome = WindowChrome.GetWindowChrome(window);
            Check(chrome is not null && !IsZero(chrome.GlassFrameThickness) && IsRadius(chrome.CornerRadius, 0),
                "WindowChrome retains DWM composition with no legacy rounded-region radius");
            Check(!window.AllowsTransparency && window.Background is SolidColorBrush { Color.A: 255 },
                "Top-level client remains opaque rather than a per-pixel layered window");
            CheckRectangularClient(frame, "Initial fixture window", Check);
            foreach (var (element, label, radius) in new[] { (search, "search", 4.0), (history, "history", 4.0), (Require<Border>(window, "SettingsCard"), "settings", 8.0) })
                Check(IsRadius(element.CornerRadius, radius), $"Internal panel uses its Windows corner tier ({radius:0} DIP): " + label, element.CornerRadius.ToString());

            foreach (string theme in new[] { "Light", "Dark" })
            {
                store.Settings.Theme = theme; window.ApplyPreferences(); await Idle();
                CheckNativeAppearance(window, theme + " theme", nativeAppearance, Check);
                foreach (var size in new[] { new Size(720, 572), new Size(1200, 780) })
                {
                    window.Width = size.Width; window.Height = size.Height;
                    scroll.ScrollToTop(); await Idle();
                    list.ReplaceSelection(new[] { list.Items[0], list.Items[1] }); await Idle();
                    foreach (double scale in new[] { 1.25, 1.5, 2.0 })
                    {
                        string label = $"{theme} {size.Width:0}x{size.Height:0} @{scale * 100:0}%";
                        double tolerance = 1.0 / scale + 0.05;
                        CheckAligned(frame, header, search, history, list, scroll, tolerance, label, scrollbarGeometry, Check);
                        var raster = Render(frame, scale);
                        string name = $"layout-{theme.ToLowerInvariant()}-{size.Width:0}x{size.Height:0}-{scale * 100:0}pct.png";
                        Save(raster.Bitmap, Path.Combine(reportDirectory, name));
                        var dpi = VisualTreeHelper.GetDpi(frame);
                        captures.Add(new CaptureResult(name, theme, frame.ActualWidth, frame.ActualHeight, scale,
                            raster.Bitmap.PixelWidth, raster.Bitmap.PixelHeight, dpi.PixelsPerInchX, dpi.PixelsPerInchY));
                        Check(raster.Bitmap.PixelWidth == (int)Math.Ceiling(frame.ActualWidth * scale)
                            && raster.Bitmap.PixelHeight == (int)Math.Ceiling(frame.ActualHeight * scale), "Raster dimensions: " + label);
                        var background = ((SolidColorBrush)Application.Current.Resources["BackgroundBrush"]).Color;
                        Check(Near(raster.At(0, 0), background) && Near(raster.At(raster.Bitmap.PixelWidth - 2, 0), background) &&
                            Near(raster.At(0, raster.Bitmap.PixelHeight - 2), background) &&
                            Near(raster.At(raster.Bitmap.PixelWidth - 2, raster.Bitmap.PixelHeight - 2), background),
                            "Client corner pixels remain solid with no competing rounded stroke: " + label);
                        CheckSelectedRaster(frame, history, list, scroll, raster, label, Check);
                    }
                }

                window.Width = 720; window.Height = 572; scroll.ScrollToTop(); await Idle();
                list.ReplaceSelection(new[] { list.Items[0], list.Items[1] }); await Idle();
                var previousContainers = Realized(list);
                var originalClips = new[] { frame, search, history }.Select(element => element.Clip).ToArray();
                int blankFrames = 0;
                for (int step = 0; step < 24; step++)
                {
                    double amount = step < 12 ? (step + 1) / 12.0 : (23 - step) / 12.0;
                    var watch = Stopwatch.StartNew();
                    window.Width = 720 + 480 * amount; window.Height = 572 + 208 * amount;
                    await Idle(); watch.Stop();
                    var currentContainers = Realized(list);
                    var common = previousContainers.Keys.Intersect(currentContainers.Keys).ToArray();
                    int reused = common.Count(id => ReferenceEquals(previousContainers[id], currentContainers[id]));
                    string label = $"{theme} resize {step + 1:D2}";
                    Check(ReferenceEquals(source, list.ItemsSource) && resets == 0, "No ItemsSource/reset churn: " + label);
                    Check(common.Length > 0 && reused > 0, "Retains existing visual containers: " + label,
                        $"{reused}/{common.Length} common containers reused; {currentContainers.Count} realized");
                    Check(list.SelectedItems.Count == 2 && list.SelectedItems.Contains(list.Items[0]) && list.SelectedItems.Contains(list.Items[1]),
                        "Continuous selection survives resize: " + label);
                    var elements = new[] { frame, search, history };
                    for (int i = 0; i < elements.Length; i++)
                        if (originalClips[i] is not null)
                            Check(ReferenceEquals(originalClips[i], elements[i].Clip), "Clip geometry identity remains stable: " + label + " / " + elements[i].Name);
                    var raster = Render(frame, 1.0);
                    var historyBounds = Bounds(history, frame);
                    if (raster.At(new Point(historyBounds.Left + 3, historyBounds.Top + 37)).A < 250
                        || raster.At(new Point(historyBounds.Right - 3, historyBounds.Top + 111)).A < 250) blankFrames++;
                    CheckSelectedRaster(frame, history, list, scroll, raster, label, Check);
                    resizeMetrics.Add(new ResizeResult(theme, step + 1, frame.ActualWidth, frame.ActualHeight,
                        common.Length, reused, currentContainers.Count, watch.Elapsed.TotalMilliseconds));
                    CheckNativeRegion(window, label, nativeDwm, Check);
                    CheckRectangularClient(frame, label, Check);
                    Check(ReferenceEquals(chrome, WindowChrome.GetWindowChrome(window)) &&
                        chrome is not null && !IsZero(chrome.GlassFrameThickness) && IsRadius(chrome.CornerRadius, 0),
                        "Resize does not rebuild or disable the DWM WindowChrome: " + label);
                    previousContainers = currentContainers;
                }
                Check(blankFrames == 0, "No transparent history edges in sampled resize frames: " + theme, $"{blankFrames} blank sampled frames");

                // A maximized visible off-screen window would be moved onto a real monitor.
                // Keep this fixture hidden while exercising maximized/restored native requests.
                window.Hide(); window.WindowState = WindowState.Maximized; await Idle();
                Check(!window.IsVisible && window.WindowState == WindowState.Maximized,
                    "Maximized-state fixture remains hidden: " + theme);
                CheckRectangularClient(frame, theme + " maximized", Check);
                CheckNativeAppearance(window, theme + " maximized", nativeAppearance, Check);
                CheckNativeRegion(window, theme + " maximized", nativeDwm, Check);
                window.WindowState = WindowState.Normal;
                window.Left = -12000; window.Top = -12000; window.Width = 720; window.Height = 572;
                await Idle();
                CheckNativeAppearance(window, theme + " restored", nativeAppearance, Check);
                CheckRectangularClient(frame, theme + " restored", Check);
                CheckNativeRegion(window, theme + " restored", nativeDwm, Check);
                window.Show(); await Idle();
            }
            Check(resets == 0, "No collection Reset during all layout and resize checks", resets.ToString());
        }
        catch (Exception exception)
        {
            fatalError = exception.ToString();
            checks.Add(new CheckResult("Layout test completed", false, exception.Message));
        }
        finally
        {
            if (collection is not null && collectionHandler is not null) collection.CollectionChanged -= collectionHandler;
            bool passed = fatalError is null && checks.All(result => result.Passed);
            if (!passed) Environment.ExitCode = 1;
            File.WriteAllText(Path.Combine(reportDirectory, "layout-regression-results.json"), JsonSerializer.Serialize(new
            {
                passed, checkCount = checks.Count, failedCount = checks.Count(result => !result.Passed), checks, captures, resizeMetrics, nativeDwm, nativeAppearance, scrollbarGeometry,
                collectionResets = resets, fatalError,
                scope = "Only isolated synthetic WPF fixtures and their own visual tree were rendered. No clipboard access, desktop screenshot, input injection, or user-history access.",
                dpiScope = "125/150/200 percent RenderTargetBitmap output scales; recorded layout DPI is the actual off-screen host DPI. This is not a per-monitor DPI-transition test.",
                resizeScope = "Checks static intermediate layout frames, clipping and container reuse; it does not measure desktop compositor frame timing or prove absence of all visible flicker.",
                outerFrameScope = "RenderTargetBitmap tests only the opaque rectangular WPF client. DWM HRESULTs record accepted native appearance requests, not composited corner pixels. Maximized/restored state requests are exercised while the fixture is hidden; OS snapping and visible maximization are not desktop-tested."
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (window is not null) window.Quit(); else Application.Current.Shutdown();
        }
    }

    private static void CheckAligned(Border frame, FrameworkElement header, Border search, Border history,
        HistoryListBox list, ScrollViewer scroll, double tolerance, string label,
        List<ScrollbarGeometryResult> scrollbarGeometry, Action<bool, string, string?> check)
    {
        double left = frame.BorderThickness.Left + frame.Padding.Left + 18,
            right = frame.ActualWidth - frame.BorderThickness.Right - frame.Padding.Right - 18;
        var elements = new[] { (header, "header"), ((FrameworkElement)search, "search"), (history, "history") };
        foreach (var (element, name) in elements)
        {
            var bounds = Bounds(element, frame);
            check(Math.Abs(bounds.Left - left) <= tolerance && Math.Abs(bounds.Right - right) <= tolerance,
                "Aligned side edges: " + label + " / " + name, $"left={bounds.Left:F3}, right={bounds.Right:F3}, expected={left:F3}/{right:F3}");
        }
        var historyBounds = Bounds(history, frame);
        var listBounds = Bounds(list, frame);
        double insideLeft = historyBounds.Left + history.BorderThickness.Left;
        double insideRight = historyBounds.Right - history.BorderThickness.Right;
        check(Math.Abs(listBounds.Left - insideLeft) <= tolerance && Math.Abs(listBounds.Right - insideRight) <= tolerance,
            "List fills history interior: " + label, listBounds.ToString());
        var presenter = Find<ScrollContentPresenter>(scroll);
        check(presenter is not null && Math.Abs(Bounds(presenter, frame).Left - insideLeft) <= tolerance
            && Math.Abs(Bounds(presenter, frame).Right - insideRight) <= tolerance,
            "Overlay scrollbar reserves no content gutter: " + label, presenter is null ? "No content presenter" : Bounds(presenter, frame).ToString());
        var first = list.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
        var second = list.ItemContainerGenerator.ContainerFromIndex(1) as FrameworkElement;
        check(first is not null && second is not null && Math.Abs(first.ActualHeight - 74) <= tolerance
            && Math.Abs(Bounds(first, frame).Bottom - Bounds(second, frame).Top) <= tolerance,
            "Rows are contiguous and 74 DIP high: " + label, first is null || second is null ? "Missing first rows" : Bounds(first, frame) + " / " + Bounds(second, frame));
        if (first is not null)
        {
            var bounds = Bounds(first, frame);
            check(Math.Abs(bounds.Left - insideLeft) <= tolerance && Math.Abs(bounds.Right - insideRight) <= tolerance,
                "Row container fills both side edges: " + label, bounds.ToString());
            check(first is Control control && ReferenceEquals(control.FocusVisualStyle, Application.Current.FindResource("RowKeyboardFocusVisual")), "Row uses its square keyboard-only focus visual: " + label, null);
            if (first is ListBoxItem row)
            {
                var rowBackground = row.Template.FindName("RowBg", row) as Border;
                check(rowBackground is not null && IsRadius(rowBackground.CornerRadius, 0),
                    "Row background remains square for seamless continuous selection: " + label,
                    rowBackground?.CornerRadius.ToString());
            }
            var deleteButton = FindAll<Button>(first).LastOrDefault();
            var scrollbar = FindAll<ScrollBar>(scroll).FirstOrDefault(bar => bar.IsVisible && bar.Orientation == Orientation.Vertical);
            var thumb = scrollbar is null ? null : FindAll<Thumb>(scrollbar).FirstOrDefault(item => item.IsVisible && item.ActualWidth > 0);
            var grip = thumb?.Template.FindName("Grip", thumb) as Border;
            check(deleteButton is not null && scrollbar is not null && thumb is not null && grip is not null,
                "Scrollbar/action geometry is available: " + label, null);
            if (deleteButton is not null && scrollbar is not null && thumb is not null && grip is not null)
            {
                var buttonBounds = Bounds(deleteButton, list);
                var thumbBounds = Bounds(thumb, list);
                var gripBounds = Bounds(grip, list);
                double hitClearance = thumbBounds.Left - buttonBounds.Right;
                double visualClearance = gripBounds.Left - buttonBounds.Right;
                scrollbarGeometry.Add(new ScrollbarGeometryResult(label, scrollbar.Width, scrollbar.ActualWidth,
                    scrollbar.DesiredSize.Width, scrollbar.MinWidth, Bounds(scrollbar, list).ToString(),
                    thumb.ActualWidth, thumbBounds.ToString(), gripBounds.ToString(), buttonBounds.ToString(), hitClearance, visualClearance));
                // The widened transparent hit target may touch the button, but the visible 5 DIP grip must retain whitespace.
                check(hitClearance >= -0.001 && visualClearance >= 2 - 0.001 &&
                    Math.Abs(grip.ActualWidth - 5) < 0.1 && thumb.ActualWidth >= grip.ActualWidth + 4,
                    "Visible scrollbar grip clears row action by at least 2 DIP without hit-area overlap: " + label,
                    $"hit={hitClearance:F3}, visible={visualClearance:F3}; thumb={thumbBounds}; grip={gripBounds}; delete={buttonBounds}");
            }
        }
    }

    private static void CheckSelectedRaster(Border frame, Border history, HistoryListBox list, ScrollViewer scroll,
        Raster raster, string label, Action<bool, string, string?> check)
    {
        var first = list.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
        var second = list.ItemContainerGenerator.ContainerFromIndex(1) as FrameworkElement;
        if (first is null || second is null) { check(false, "Selected rows render: " + label, "No first/second row container"); return; }
        var expected = ((SolidColorBrush)Application.Current.Resources["SelectedBrush"]).Color;
        var historyBounds = Bounds(history, frame);
        double left = historyBounds.Left + history.BorderThickness.Left;
        double right = historyBounds.Right - history.BorderThickness.Right;
        var thumbs = FindAll<Thumb>(scroll).Where(thumb => thumb.IsVisible && thumb.ActualWidth > 0 && thumb.ActualHeight > 0)
            .Select(thumb => Bounds(thumb, frame)).ToArray();
        bool CoveredByThumb(Point point) => thumbs.Any(bounds => bounds.Contains(point));
        int edgeSamples = 0, edgeFailures = 0;
        var mismatches = new List<string>();
        foreach (var row in new[] { first, second })
        {
            var rowBounds = Bounds(row, frame);
            foreach (double fraction in new[] { 0.25, 0.5, 0.75 })
                foreach (double x in new[] { left + 2 / raster.Scale, right - 2 / raster.Scale })
                {
                    var point = new Point(x, rowBounds.Top + rowBounds.Height * fraction);
                    if (CoveredByThumb(point)) continue;
                    edgeSamples++;
                    var color = raster.At(point);
                    if (!Near(color, expected)) { edgeFailures++; if (mismatches.Count < 3) mismatches.Add($"{point}: {color}"); }
                }
        }
        check(edgeSamples >= 6 && edgeFailures == 0, "Selection reaches left/right row edges: " + label,
            $"{edgeFailures}/{edgeSamples} mismatches; expected {expected}; " + string.Join("; ", mismatches));
        double seam = Bounds(first, frame).Bottom;
        int seamSamples = 0, seamFailures = 0;
        int xStart = (int)Math.Ceiling((left + 3) * raster.Scale), xEnd = (int)Math.Floor((right - 3) * raster.Scale);
        int yStart = (int)Math.Floor(seam * raster.Scale) - 1, yEnd = (int)Math.Floor(seam * raster.Scale) + 1;
        for (int y = yStart; y <= yEnd; y++)
            for (int x = xStart; x <= xEnd; x++)
            {
                var point = new Point((x + 0.5) / raster.Scale, (y + 0.5) / raster.Scale);
                if (CoveredByThumb(point)) continue;
                seamSamples++;
                if (!Near(raster.At(x, y), expected)) seamFailures++;
            }
        check(seamSamples > 0 && seamFailures == 0, "No pale seam between adjacent selected rows: " + label,
            $"{seamFailures}/{seamSamples} mismatched seam pixels; expected {expected}");
    }

    private static Raster Render(FrameworkElement root, double scale)
    {
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root); bitmap.Freeze(); return new Raster(bitmap, scale);
    }
    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static bool Near(Color actual, Color expected) => actual.A >= 250
        && Math.Abs(actual.R - expected.R) <= 3 && Math.Abs(actual.G - expected.G) <= 3 && Math.Abs(actual.B - expected.B) <= 3;
    private static bool IsZero(Thickness value) => value.Left == 0 && value.Top == 0 && value.Right == 0 && value.Bottom == 0;
    private static bool IsRadius(CornerRadius value, double radius) => value.TopLeft == radius && value.TopRight == radius && value.BottomLeft == radius && value.BottomRight == radius;
    private static Rect Bounds(FrameworkElement element, Visual root) => element.TransformToAncestor(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    private static T Require<T>(MainWindow window, string name) where T : class => window.FindName(name) as T ?? throw new InvalidOperationException("Missing layout contract element: " + name);
    private static Dictionary<Guid, DependencyObject> Realized(HistoryListBox list) => Enumerable.Range(0, list.Items.Count)
        .Where(index => list.ItemContainerGenerator.ContainerFromIndex(index) is not null)
        .ToDictionary(index => ((ClipItem)list.Items[index]).Id, index => list.ItemContainerGenerator.ContainerFromIndex(index)!);
    private static void CheckRectangularClient(Border frame, string stage, Action<bool, string, string?> check)
    {
        check(frame.GetType() == typeof(Border) && IsRadius(frame.CornerRadius, 0) && IsZero(frame.BorderThickness) &&
            frame.Clip is null && frame.Child?.Clip is null,
            "Only DWM owns the outer contour; no WPF rounded clip or border stroke: " + stage, null);
        check(frame.Background is SolidColorBrush { Color.A: 255 }, "Client frame background remains opaque: " + stage, null);
    }

    private static void CheckNativeAppearance(MainWindow window, string stage, List<NativeAppearanceResult> results,
        Action<bool, string, string?> check)
    {
        var status = window.NativeAppearanceStatus;
        var dpi = VisualTreeHelper.GetDpi(window);
        results.Add(new NativeAppearanceResult(stage, window.WindowState, dpi.PixelsPerInchX, dpi.PixelsPerInchY, status));
        if (!status.Supported || !status.CompositionEnabled)
        {
            check(status.CornerResult is null && status.BorderColorResult is null,
                "Unsupported native rounding leaves the system fallback unchanged: " + stage, null);
            return;
        }
        check(status.CompositionResult == 0 && status.ApplyCount > 0 && status.CornerResult == 0 &&
            status.DarkModeResult == 0 && status.BorderColorResult == 0 && status.CaptionColorResult == 0 && status.TextColorResult == 0,
            "DWM accepts the sole outer-frame appearance requests: " + stage, JsonSerializer.Serialize(status));
        check(status.CornerPreference == (window.WindowState == WindowState.Maximized ? 1u : 2u),
            "Native corner preference follows maximized/restored state: " + stage, status.CornerPreference?.ToString());
        check(status.DarkMode == (!SystemParameters.HighContrast && ThemeManager.IsDark),
            "Native frame follows the application's light/dark appearance: " + stage, null);
        var border = ((SolidColorBrush)Application.Current.Resources["BorderBrush"]).Color;
        uint expectedBorder = SystemParameters.HighContrast ? 0xffffffffu : (uint)(border.R | (border.G << 8) | (border.B << 16));
        check(status.BorderColor == expectedBorder, "DWM draws the single theme-colored outer edge: " + stage, null);
        check(status.VisibleBorderThicknessResult == 0 && status.VisibleBorderThicknessPixels.HasValue,
            "DWM reports physical-pixel frame thickness at the fixture DPI: " + stage, status.VisibleBorderThicknessPixels?.ToString());
    }

    private static void CheckNativeRegion(MainWindow window, string stage, List<NativeDwmResult> results,
        Action<bool, string, string?> check)
    {
        // Read only the fixture HWND. The temporary HRGN belongs exclusively to this test.
        nint handle = new WindowInteropHelper(window).Handle;
        bool valid = IsWindow(handle);
        int compositionResult = DwmIsCompositionEnabled(out bool composition);
        if (compositionResult != 0 || !composition)
        {
            results.Add(new NativeDwmResult(stage, compositionResult == 0, composition, valid, null, compositionResult,
                "Legacy-region assertion skipped because DWM composition is unavailable or disabled."));
            return;
        }
        if (!valid)
        {
            results.Add(new NativeDwmResult(stage, true, true, false, null, 0, "Fixture HWND is invalid."));
            check(false, "DWM fixture window handle is valid: " + stage, null); return;
        }
        nint region = CreateRectRgn(0, 0, 0, 0);
        if (region == 0) throw new InvalidOperationException("Unable to allocate a temporary test region.");
        try
        {
            int kind = GetWindowRgn(handle, region);
            int error = Marshal.GetLastWin32Error();
            results.Add(new NativeDwmResult(stage, true, true, true, kind, error,
                kind == 0 ? "No legacy window region; DWM owns the frame." : "A legacy window region is present."));
            check(kind == 0, "DWM resize does not recreate a legacy window region: " + stage, $"GetWindowRgn={kind}, lastError={error}");
        }
        finally { DeleteObject(region); }
    }
    [DllImport("dwmapi.dll")] private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowRgn(nint window, nint region);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
    private static T? Find<T>(DependencyObject root) where T : DependencyObject => FindAll<T>(root).FirstOrDefault();
    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T found) yield return found;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in FindAll<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
