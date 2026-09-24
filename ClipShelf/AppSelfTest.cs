using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipShelf;

public static class AppSelfTest
{
    public static async Task RunAsync()
    {
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var checks = new List<string>();
        var skippedTests = new List<string>();
        var diagnostics = new ConcurrentQueue<string>();
        var timer = Stopwatch.StartNew();
        string testDirectory = Path.Combine(Path.GetTempPath(), "ClipShelf-self-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        string reportPath = ReportPath();
        string? error = null;
        bool clipboardChanged = false;
        bool clipboardCaptured = false;
        DataObject? clipboardBackup = null;
        Window? window = null;
        Window? otherWindow = null;
        WindowsIntegration? integration = null;
        WindowsIntegration? otherIntegration = null;
        try
        {
            checks.AddRange(StorageTests.Run(testDirectory));
            checks.AddRange(await StorageTests.RunDeferredAsync(testDirectory));
            await CheckFrozenDecodedFrameEncodingAsync(checks);
            clipboardBackup = CaptureClipboard();
            clipboardCaptured = true;
            var store = new HistoryStore(Path.Combine(testDirectory, "integration"));
            store.Settings.WatchScreenshots = false;
            store.Settings.HistoryEnabled = true;
            store.Settings.GlobalHotKey = "Ctrl+Alt+Shift+F24";
            window = HiddenWindow();
            integration = new WindowsIntegration(window, store, manageStartup: false);
            integration.Diagnostic += diagnostics.Enqueue;
            Check(integration.LastHotKeyError is null, "Global shortcut registers with Windows", checks);

            if (Environment.GetCommandLineArgs().Contains("--image-capture-smoke", StringComparer.Ordinal))
            {
                clipboardChanged = true;
                await ClipboardAction(() => Clipboard.SetImage(TestBitmap(3, 2, 42)));
                await WaitFor(() => store.Items.Any(item => item.Kind == ClipKind.Image), "Focused Windows bitmap capture", 5000);
                var focusedImage = store.Items.Single(item => item.Kind == ClipKind.Image);
                Check(File.Exists(focusedImage.ImagePath), "Focused Windows bitmap capture persists an image", checks);
                Check(await integration.CopyAsync([focusedImage]) && Clipboard.ContainsImage(), "Focused image copies after background preparation", checks);
                return;
            }

            const string firstText = "  ClipShelf test\r\n中文 café  ";
            clipboardChanged = true;
            await ClipboardAction(() => Clipboard.SetText(firstText, TextDataFormat.UnicodeText));
            await WaitFor(() => store.Items.Any(item => item.Text == firstText), "Windows clipboard text capture");
            var first = store.Items.Single(item => item.Text == firstText);
            int copyActivations = 0;
            window.Activated += (_, _) => copyActivations++;
            bool visibleBeforeCopy = window.IsVisible;
            var stateBeforeCopy = window.WindowState;
            Check(first.Text == firstText, "Clipboard capture retains whitespace and Unicode", checks);
            Check(await integration.CopyAsync([first]), "History item copies to Windows clipboard", checks);
            await Task.Delay(250);
            Check(Clipboard.GetText(TextDataFormat.UnicodeText) == firstText && ReferenceEquals(store.Items.Single(), first),
                "Own copy does not recapture or replace the history record", checks);

            int count = store.Items.Count;
            var joined = new[] { new ClipItem { Text = "one" }, new ClipItem { Text = "two" } };
            Check(await integration.CopyAsync(joined), "Multiple text records copy together", checks);
            await Task.Delay(180);
            Check(Clipboard.GetText() == "one" + Environment.NewLine + "two" && store.Items.Count == count,
                "Multi-copy preserves selection order and suppresses collection", checks);

            store.Settings.HistoryEnabled = false;
            await ClipboardAction(() => Clipboard.SetText("ClipShelf paused test"));
            await Task.Delay(250);
            Check(store.Items.Count == count, "Paused history ignores clipboard changes", checks);
            store.Settings.HistoryEnabled = true;
            integration.ApplySettings();
            await Task.Delay(180);
            Check(store.Items.Count == count, "Resuming does not import paused clipboard content", checks);
            await ClipboardAction(() => Clipboard.SetText("ClipShelf resumed test"));
            await WaitFor(() => store.Items.Any(item => item.Text == "ClipShelf resumed test"), "Resumed capture");
            checks.Add("PASS Resumed history captures new clipboard changes");

            BitmapSource bitmap = TestBitmap(3, 2, 42);
            await ClipboardAction(() => Clipboard.SetImage(bitmap));
            await WaitFor(() => store.Items.Any(item => item.Kind == ClipKind.Image), "Windows bitmap capture");
            var image = store.Items.First(item => item.Kind == ClipKind.Image);
            var decoded = BitmapDecoder.Create(new Uri(image.ImagePath!), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            Check(decoded.PixelWidth == 3 && decoded.PixelHeight == 2 && File.Exists(image.ImagePath),
                "Standard Windows bitmap is persisted as a readable PNG", checks);
            Check(await integration.CopyAsync([image]), "Stored image returns to the Windows clipboard", checks);
            Check(Clipboard.ContainsImage() && Clipboard.GetImage()?.PixelWidth == 3,
                "Copied image is available to standard Windows applications", checks);

            otherWindow = HiddenWindow();
            var transparentBitmap = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 24, 72, 160, 64, 45, 99, 210, 180 }, 8);
            var alphaPng = new PngBitmapEncoder();
            alphaPng.Frames.Add(BitmapFrame.Create(transparentBitmap));
            using var alphaBuffer = new MemoryStream();
            alphaPng.Save(alphaBuffer);
            var dibPayload = NativeClipboard.Image(alphaBuffer.ToArray(), transparentBitmap);
            dibPayload.Remove(NativeClipboard.PngFormat);
            int imageCount = store.Items.Count(item => item.Kind == ClipKind.Image);
            await ClipboardAction(() => NativeClipboard.Write(new WindowInteropHelper(otherWindow).EnsureHandle(), dibPayload));
            await WaitFor(() => store.Items.Count(item => item.Kind == ClipKind.Image) > imageCount, "Windows DIBV5 alpha capture");
            var alphaItem = store.Items.First(item => item.Kind == ClipKind.Image);
            var alphaFrame = BitmapDecoder.Create(new Uri(alphaItem.ImagePath!), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            var alphaPixels = new byte[8];
            new FormatConvertedBitmap(alphaFrame, PixelFormats.Bgra32, null, 0).CopyPixels(alphaPixels, 8, 0);
            Check(alphaPixels[3] == 64 && alphaPixels[7] == 180, "Windows DIBV5 transparency survives history storage", checks);

            string sourceFile = Path.Combine(testDirectory, "original-file.txt");
            File.WriteAllText(sourceFile, "ClipShelf self-test source file");
            var files = new StringCollection { sourceFile };
            await ClipboardAction(() => Clipboard.SetFileDropList(files));
            await WaitFor(() => store.Items.Any(item => item.Kind == ClipKind.File && item.FilePaths.Contains(sourceFile)), "Windows file-drop capture");
            var fileItem = store.Items.First(item => item.Kind == ClipKind.File);
            Check(await integration.CopyAsync([fileItem]) && Clipboard.GetFileDropList().Cast<string>().SequenceEqual([sourceFile]),
                "Files round-trip through the Windows file clipboard", checks);
            Check(copyActivations == 0 && window.IsVisible == visibleBeforeCopy && window.WindowState == stateBeforeCopy,
                "Native text, multi-record, image, and file copying never activates or changes the clipboard window state", checks);
            store.Remove([fileItem.Id]);
            Check(File.Exists(sourceFile), "Removing a copied file leaves the original untouched", checks);

            string screenshotFolder = Path.Combine(testDirectory, "Screenshots");
            Directory.CreateDirectory(screenshotFolder);
            string existingScreenshot = Path.Combine(screenshotFolder, "already-exists.png");
            SavePng(TestBitmap(4, 3, 78), existingScreenshot);
            store.Settings.ScreenshotFolder = screenshotFolder;
            store.Settings.WatchScreenshots = true;
            integration.ApplySettings();
            await Task.Delay(750);
            Check(store.Items.All(item => item.SourcePath != existingScreenshot), "Screenshot watcher ignores pre-existing images", checks);
            string screenshot = Path.Combine(screenshotFolder, "new-screenshot.png");
            SavePng(TestBitmap(5, 4, 91), screenshot);
            await WaitFor(() => store.Items.Any(item => item.SourcePath == screenshot), "New screenshot import", 6000);
            await WaitFor(() => Clipboard.ContainsImage() && Clipboard.GetImage()?.PixelWidth == 5, "Screenshot auto-copy", 2500);
            var screenshotItem = store.Items.Single(item => item.SourcePath == screenshot);
            Check(screenshotItem.Kind == ClipKind.Image && screenshotItem.KindLabel == "截图" && screenshotItem.ImagePath != screenshot,
                "New screenshot is imported into its own cache and copied", checks);
            store.Remove([screenshotItem.Id]);
            Check(File.Exists(screenshot), "Deleting screenshot history preserves the screenshot file", checks);
            store.Settings.WatchScreenshots = false;
            integration.ApplySettings();
            string ignoredScreenshot = Path.Combine(screenshotFolder, "watcher-disabled.png");
            SavePng(TestBitmap(6, 5, 123), ignoredScreenshot);
            await Task.Delay(900);
            Check(store.Items.All(item => item.SourcePath != ignoredScreenshot), "Paused screenshot watcher stops importing files", checks);

            var otherStore = new HistoryStore(Path.Combine(testDirectory, "shortcut-conflict"));
            otherStore.Settings.HistoryEnabled = false;
            otherStore.Settings.WatchScreenshots = false;
            otherStore.Settings.GlobalHotKey = store.Settings.GlobalHotKey;
            otherIntegration = new WindowsIntegration(otherWindow, otherStore, manageStartup: false);
            Check(otherIntegration.LastHotKeyError is not null, "Shortcut conflicts produce a recoverable error", checks);
            Check(!integration.RegisterHotKey("not-a-valid-shortcut") && !otherIntegration.RegisterHotKey(store.Settings.GlobalHotKey),
                "Invalid shortcut changes retain the working registration", checks);
            integration.SuspendHotKey();
            Check(otherIntegration.RegisterHotKey(store.Settings.GlobalHotKey), "Shortcut recorder can suspend the current global binding", checks);
            otherIntegration.SuspendHotKey();
            Check(integration.RegisterHotKey(store.Settings.GlobalHotKey), "Shortcut recorder restores the global binding", checks);
            integration.Dispose();
            integration = null;
            Check(otherIntegration.RegisterHotKey(store.Settings.GlobalHotKey), "Closing an integration releases its global shortcut", checks);
            Check(store.LastError is null, "Integration changes persist without storage errors", checks);
            otherIntegration.Dispose();
            otherIntegration = null;
            await RunBackgroundRegressionsAsync(testDirectory, checks, diagnostics);
        }
        catch (Exception exception)
        {
            error = exception.GetType().Name + ": " + exception.Message;
        }
        finally
        {
            integration?.Dispose();
            otherIntegration?.Dispose();
            window?.Close();
            otherWindow?.Close();
            if (clipboardCaptured && clipboardChanged)
            {
                try
                {
                    await ClipboardAction(() =>
                    {
                        if (clipboardBackup is null) Clipboard.Clear();
                        else Clipboard.SetDataObject(clipboardBackup, copy: true);
                    });
                    checks.Add("PASS Original clipboard restored after testing");
                }
                catch { error ??= "The original clipboard could not be restored because Windows kept it busy."; }
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                File.WriteAllText(reportPath, JsonSerializer.Serialize(new
                {
                    passed = error is null,
                    checks,
                    skippedCount = skippedTests.Count,
                    skippedTests,
                    diagnostics = diagnostics.ToArray(),
                    error,
                    durationSeconds = Math.Round(timer.Elapsed.TotalSeconds, 2),
                    testDirectory,
                    completedAt = DateTimeOffset.Now
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { error ??= "The test report could not be written."; }
            Application.Current.Shutdown(error is null ? 0 : 1);
        }
    }

    private static Window HiddenWindow() => new()
    {
        Width = 1, Height = 1, ShowInTaskbar = false, ShowActivated = false,
        WindowStyle = WindowStyle.None, Title = "ClipShelf isolated self-test"
    };

    private static async Task CheckFrozenDecodedFrameEncodingAsync(ICollection<string> checks)
    {
        var pixels = new byte[] { 24, 72, 160, 64, 45, 99, 210, 180 };
        var original = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        original.Freeze();
        // Unlike a plain BitmapSource, this frozen frame still has a UI-thread BitmapDecoder behind it.
        var decodedOnUi = NativeClipboard.DecodeImage(NativeClipboard.EncodePng(original));
        var encodedOnWorker = await Task.Run(() => NativeClipboard.EncodePng(decodedOnUi));
        var roundTrip = NativeClipboard.DecodeImage(encodedOnWorker);
        var actual = new byte[8];
        new FormatConvertedBitmap(roundTrip, PixelFormats.Bgra32, null, 0).CopyPixels(actual, 8, 0);
        Check(actual.SequenceEqual(pixels), "Frozen decoder-backed frames encode on a worker without metadata thread errors or alpha loss", checks);
    }

    private static async Task RunBackgroundRegressionsAsync(string testDirectory, ICollection<string> checks, ConcurrentQueue<string> diagnostics)
    {
        // All history, cached images, and watched files remain under this run's temporary fixture.
        string fixtureDirectory = Path.Combine(testDirectory, "background-regressions");
        var store = new HistoryStore(fixtureDirectory, deferredPersistence: true);
        store.Settings.HistoryEnabled = true;
        store.Settings.WatchScreenshots = false;
        store.Settings.GlobalHotKey = "Ctrl+Alt+Shift+F24";
        var targetWindow = HiddenWindow();
        var sourceWindow = HiddenWindow();
        WindowsIntegration? integration = null;
        try
        {
            integration = new WindowsIntegration(targetWindow, store, manageStartup: false);
            integration.Diagnostic += diagnostics.Enqueue;
            nint sourceHandle = new WindowInteropHelper(sourceWindow).EnsureHandle();
            Check(integration.LastHotKeyError is null, "Background regression fixture uses an isolated shortcut and store", checks);

            // Hold the production preparation gate, ensuring the snapshot exists but cannot yet commit.
            // This makes the race deterministic without timing assumptions or memory-heavy test images.
            using (var blockedImage = await HoldPreparationAsync(integration, "_imagePreparation"))
            {
                store.Add(new ClipItem { Text = "Background clear fixture", Title = "Background clear fixture" });
                await PublishImageSnapshotAsync(integration, sourceHandle, TestBitmap(17, 13, 51), "Clear-race image snapshot");
                integration.CancelPendingCaptures();
                store.Clear();
                blockedImage.Dispose();
                await DrainPreparationAsync(integration, "_imagePreparation");
                Check(store.Items.Count == 0, "Clearing history cancels an already-captured background image without resurrection", checks);
                Check(await store.FlushAsync() && new HistoryStore(fixtureDirectory).Items.Count == 0,
                    "Cleared background-image fixture remains empty after persistence and reload", checks);
            }

            const string newestText = "ClipShelf background image followed by newer text";
            using (var blockedImage = await HoldPreparationAsync(integration, "_imagePreparation"))
            {
                await PublishImageSnapshotAsync(integration, sourceHandle, TestBitmap(19, 11, 66), "Image-before-text snapshot");
                await ClipboardAction(() => Clipboard.SetText(newestText, TextDataFormat.UnicodeText));
                await WaitFor(() => store.Items.Any(item => item.Text == newestText), "Text capture while image preparation is blocked");
                Check(store.Items.Count == 1 && store.Items[0].Text == newestText,
                    "A pending image does not block subsequent Windows text capture", checks);
                blockedImage.Dispose();
                await DrainPreparationAsync(integration, "_imagePreparation");
                await WaitFor(() => store.Items.Any(item => item.Kind == ClipKind.Image), "Earlier image finishes background preparation");
                Check(store.Items.Count == 2 && store.Items[0].Text == newestText && Clipboard.GetText() == newestText,
                    "Image-to-text capture preserves capture-time ordering and leaves the newest clipboard text intact", checks);
            }

            var capturedImage = store.Items.Single(item => item.Kind == ClipKind.Image);
            const string finalCopiedText = "ClipShelf final ordered copy";
            using (var blockedCopy = await HoldPreparationAsync(integration, "_copyPreparation"))
            {
                var imageCopy = integration.CopyAsync([capturedImage]);
                var textCopy = integration.CopyAsync([new ClipItem { Text = finalCopiedText, Title = finalCopiedText }]);
                Check(!imageCopy.IsCompleted && !textCopy.IsCompleted, "Rapid-copy fixture queues both copy requests before releasing preparation", checks);
                blockedCopy.Dispose();
                var copied = await Task.WhenAll(imageCopy, textCopy).WaitAsync(TimeSpan.FromSeconds(8));
                await Task.Delay(180);
                Check(copied.All(result => result) && Clipboard.GetText() == finalCopiedText && store.Items.Count == 2,
                    "Rapid image-then-text copying finishes in request order without recapturing owned writes", checks);
            }

            string screenshotFolder = Path.Combine(fixtureDirectory, "watched-screenshots");
            Directory.CreateDirectory(screenshotFolder);
            store.Settings.ScreenshotFolder = screenshotFolder + Path.DirectorySeparatorChar;
            store.Settings.WatchScreenshots = true;
            integration.ApplySettings();
            var watcher = PrivateField<FileSystemWatcher>(integration, "_screenshotWatcher");
            int watcherGeneration = PrivateField<int>(integration, "_watcherGeneration");
            int hotKeyId = PrivateField<int>(integration, "_hotKeyId");
            string screenshot = Path.Combine(screenshotFolder, "pending-during-preferences.png");
            using (var blockedImage = await HoldPreparationAsync(integration, "_imagePreparation"))
            {
                SavePng(TestBitmap(23, 9, 81), screenshot);
                await WaitFor(() => PrivateField<Dictionary<string, CancellationTokenSource>>(integration, "_pendingScreenshots").ContainsKey(screenshot),
                    "Screenshot is pending during preference changes");
                for (int index = 0; index < 8; index++)
                {
                    store.Settings.ScreenshotFolder = index % 2 == 0
                        ? Path.Combine(screenshotFolder, ".") + Path.DirectorySeparatorChar : screenshotFolder;
                    integration.ApplySettings();
                }
                Check(ReferenceEquals(watcher, PrivateField<FileSystemWatcher>(integration, "_screenshotWatcher")) &&
                    watcherGeneration == PrivateField<int>(integration, "_watcherGeneration") &&
                    hotKeyId == PrivateField<int>(integration, "_hotKeyId"),
                    "Repeated equivalent ApplySettings retains the watcher, pending import generation, and global shortcut", checks);
                blockedImage.Dispose();
                await WaitFor(() => store.Items.Any(item => item.SourcePath == screenshot), "Pending screenshot survives equivalent preference applications", 6000);
                await WaitFor(() => Clipboard.ContainsImage() && Clipboard.GetImage()?.PixelWidth == 23, "Pending screenshot auto-copy finishes", 3000);
                checks.Add("PASS Existing pending screenshot still imports and copies after repeated preferences");
            }
            store.Settings.WatchScreenshots = false;
            integration.ApplySettings();

            using (var blockedImage = await HoldPreparationAsync(integration, "_imagePreparation"))
            using (var blockedCopy = await HoldPreparationAsync(integration, "_copyPreparation"))
            {
                await PublishImageSnapshotAsync(integration, sourceHandle, TestBitmap(29, 7, 97), "Dispose-race image snapshot");
                int beforeDispose = store.Items.Count;
                var queuedCopy = integration.CopyAsync([new ClipItem { Text = "This queued copy must be cancelled" }]);
                const string afterDisposeText = "ClipShelf dispose preserves this clipboard sentinel";
                await ClipboardAction(() => Clipboard.SetText(afterDisposeText));
                uint clipboardSequence = NativeMethods.GetClipboardSequenceNumber();
                integration.Dispose();
                blockedImage.Dispose();
                blockedCopy.Dispose();
                Check(!await queuedCopy.WaitAsync(TimeSpan.FromSeconds(5)), "Disposing the integration cancels a queued clipboard copy", checks);
                await DrainPreparationAsync(integration, "_imagePreparation");
                await Task.Delay(180);
                Check(store.Items.Count == beforeDispose && Clipboard.GetText() == afterDisposeText &&
                    NativeMethods.GetClipboardSequenceNumber() == clipboardSequence,
                    "Disposed background work adds no image and performs no later clipboard write", checks);
                Check(!await integration.CopyAsync([capturedImage]) && NativeMethods.GetClipboardSequenceNumber() == clipboardSequence,
                    "New copy requests after disposal leave the clipboard untouched", checks);
            }
            Check(await store.FlushAsync() && store.LastError is null, "Background regression fixture flushes without storage errors", checks);
        }
        finally
        {
            integration?.Dispose();
            await store.FlushAsync();
            targetWindow.Close();
            sourceWindow.Close();
        }
    }

    private static async Task PublishImageSnapshotAsync(WindowsIntegration integration, nint owner, BitmapSource image, string name)
    {
        var payload = new Dictionary<uint, byte[]> { [NativeClipboard.PngFormat] = NativeClipboard.EncodePng(image) };
        await ClipboardAction(() => NativeClipboard.Write(owner, payload));
        uint sequence = NativeMethods.GetClipboardSequenceNumber();
        await WaitFor(() => PrivateField<uint>(integration, "_lastClipboardSequence") == sequence, name);
    }

    private static T PrivateField<T>(WindowsIntegration integration, string name)
    {
        var field = typeof(WindowsIntegration).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Background fixture field is missing: " + name);
        return field.GetValue(integration) is T value
            ? value : throw new InvalidOperationException("Background fixture field has an unexpected value: " + name);
    }

    private static async Task<PreparationLease> HoldPreparationAsync(WindowsIntegration integration, string name)
    {
        var semaphore = PrivateField<SemaphoreSlim>(integration, name);
        if (!await semaphore.WaitAsync(TimeSpan.FromSeconds(6)))
            throw new InvalidOperationException("Timed out holding preparation gate: " + name);
        return new PreparationLease(semaphore);
    }

    private static async Task DrainPreparationAsync(WindowsIntegration integration, string name)
    {
        using (await HoldPreparationAsync(integration, name)) { }
        // Let cancellation and dispatcher continuations finish after the preceding gate owner releases it.
        await Task.Delay(80);
    }

    private sealed class PreparationLease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? current = semaphore;
        public void Dispose() => Interlocked.Exchange(ref current, null)?.Release();
    }

    private static string ReportPath()
    {
        var arguments = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(arguments, "--test-report");
        return Path.GetFullPath(index >= 0 && index + 1 < arguments.Length
            ? arguments[index + 1] : Path.Combine(AppContext.BaseDirectory, "self-test-results.json"));
    }

    internal static DataObject? CaptureClipboard()
    {
        var source = Clipboard.GetDataObject();
        if (source is null) return null;
        var backup = new DataObject();
        foreach (var format in source.GetFormats(autoConvert: false))
        {
            try
            {
                object? value = source.GetData(format, autoConvert: false);
                if (value is MemoryStream memory) value = new MemoryStream(memory.ToArray(), writable: false);
                else if (value is BitmapSource image) { var copy = image.CloneCurrentValue(); copy.Freeze(); value = copy; }
                else if (value is string[] strings) value = strings.ToArray();
                if (value is not null) backup.SetData(format, value, autoConvert: false);
            }
            catch { /* Some application-specific delayed formats cannot be materialized. */ }
        }
        return backup.GetFormats(autoConvert: false).Length > 0 ? backup : null;
    }

    private static BitmapSource TestBitmap(int width, int height, byte shade)
    {
        var pixels = new byte[width * height * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = shade;
            pixels[offset + 1] = 110;
            pixels[offset + 2] = 220;
            pixels[offset + 3] = 255;
        }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    private static void SavePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static async Task ClipboardAction(Action action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { action(); return; }
            catch when (attempt < 5) { await Task.Delay(50 + attempt * 35); }
        }
    }

    private static async Task WaitFor(Func<bool> condition, string name, int milliseconds = 2500)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds)
        {
            try { if (condition()) return; }
            catch (System.Runtime.InteropServices.ExternalException) { }
            await Task.Delay(40);
        }
        throw new InvalidOperationException("Timed out: " + name);
    }

    private static void Check(bool condition, string name, ICollection<string> checks)
    {
        if (!condition) throw new InvalidOperationException("Self-test failed: " + name);
        checks.Add("PASS " + name);
    }
}
