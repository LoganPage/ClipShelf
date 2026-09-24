using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;

namespace ClipShelf;

public partial class App : Application
{
    private Mutex? instance;
    private HistoryStore? activeStore;
    private string? expectedTestReport;
    private string? testUnhandledError;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        expectedTestReport = ExpectedTestReport(e.Args);
        if (expectedTestReport is not null)
            DispatcherUnhandledException += (_, args) => { testUnhandledError = args.Exception.ToString(); args.Handled = true; Shutdown(1); };
        if (e.Args.Length == 3 && e.Args[0] == "--test-report-guard-probe")
        {
            if (e.Args[2] == "false") File.WriteAllText(e.Args[1], "{\"passed\":false,\"error\":\"Deliberate guard probe\"}");
            if (e.Args[2] == "malformed") File.WriteAllText(e.Args[1], "not a JSON report");
            Shutdown(0);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--cleanup-test") { await CacheCleanupTests.RunAsync(e.Args[1]); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--tooltip-test") { await ToolTipPresentationTests.RunAsync(e.Args[1]); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--update-test") { await WindowsUpdateTests.RunAsync(e.Args[1]); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--native-preview-test") { await NativePreviewTests.RunAsync(e.Args[1]); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--text-preview-test") { await TextFilePreviewTests.RunAsync(e.Args[1]); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--native-preview-benchmark") { await NativePreviewBenchmark.RunAsync(e.Args[1]); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--theme-transition-test") { await ThemeTransitionTests.Run(e.Args[1]); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--preview-interaction-test") { await PreviewInteractionTests.Run(e.Args[1]); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--common-preview-test") { await CommonPreviewTests.RunAsync(e.Args[1]); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--file-record-test") { await FileRecordTests.RunAsync(e.Args[1]); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--file-preview-test") { await FilePreviewTests.RunAsync(e.Args[1]); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--file-preview-demo") { FilePreviewTests.RunDemo(e.Args[1]); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--settings-demo") { SettingsExperienceTests.RunDemo(e.Args[1]); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--copy-only-test") { await CopyOnlyTests.RunAsync(e.Args[1]); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--copy-only-demo") { CopyOnlyTests.RunDemo(e.Args[1]); return; }
        if (e.Args.Contains("--quit")) { ClipShelf.MainWindow.QuitExistingInstance(); Shutdown(); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--render-qa") { await QaRenderer.RunAsync(e.Args[1]); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--ui-performance-test") { await UiPerformanceTests.RunAsync(e.Args[1]); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--layout-test") { await LayoutRegressionTests.RunAsync(e.Args[1]); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--interaction-test") { await WindowsInteractionTests.RunAsync(e.Args[1]); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--presentation-test") { await FluentPresentationTests.RunAsync(e.Args[1]); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--settings-test") { await SettingsExperienceTests.RunAsync(e.Args[1]); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--tray-test") { await TrayInteractionTests.RunAsync(e.Args[1]); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--tray-popup-test") { await TrayInteractionTests.RunPopupAsync(e.Args[1]); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--drag-demo") { DragSelectionTests.RunDemo(e.Args[1]); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--drag-test") { await DragSelectionTests.RunAsync(e.Args[1]); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--focus-test") { await FocusCueTests.RunAsync(e.Args[1]); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--focus-demo") { FocusCueTests.RunDemo(e.Args[1]); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--tray-demo") { TrayInteractionTests.RunDemo(e.Args[1]); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--scroll-render-test") { await ScrollRenderingProbe.RunAsync(e.Args[1]); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--multi-scroll-test") { await ScrollRenderingProbe.RunAsync(e.Args[1], multiSelection: true); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--fine-scroll-test") { await ScrollRenderingProbe.RunAsync(e.Args[1], fineWheel: true); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--preview-scroll-test") { await ScrollRenderingProbe.RunAsync(e.Args[1], previewStress: true); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--smoothness-test") { await SmoothnessProbe.RunAsync(e.Args[1]); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--smoothness-multi-test") { await SmoothnessProbe.RunAsync(e.Args[1], multiSelection: true); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--smoothness-fine-test") { await SmoothnessProbe.RunAsync(e.Args[1], fineWheel: true, variant: e.Args.Length >= 3 ? e.Args[2] : null); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--smoothness-regression-test") { await SmoothnessRegressionTests.RunAsync(e.Args[1]); return; }
        DispatcherUnhandledException += (_, args) =>
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipShelf");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), DateTimeOffset.Now + " " + args.Exception.GetType().Name + "\n" + args.Exception.StackTrace + "\n");
            MessageBox.Show("操作没有完成。请重试；详细错误已保存在本地 error.log。", "ClipShelf", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        if (e.Args.Contains("--self-test"))
        {
            await AppSelfTest.RunAsync();
            return;
        }
        string? dataDir = null;
        int di = Array.IndexOf(e.Args, "--data-dir");
        if (di >= 0 && di + 1 < e.Args.Length) dataDir = Path.GetFullPath(e.Args[di + 1]);
        bool demo = e.Args.Contains("--demo");
        if (demo) dataDir = Path.Combine(Path.GetTempPath(), "ClipShelf-demo-" + Guid.NewGuid().ToString("N"));
        string mutexName = "Local\\ClipShelf.Windows." + (dataDir is null ? "Default" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dataDir))).Substring(0, 16));
        instance = new Mutex(true, mutexName, out bool first);
        if (!first)
        {
            ClipShelf.MainWindow.SignalExistingInstance();
            Shutdown();
            return;
        }
        var store = new HistoryStore(dataDir, deferredPersistence: true);
        activeStore = store;
        if (demo) DemoContent.Add(store);
        var window = new MainWindow(store, demo);
        store.PersistenceFailed += error => window.ShowStatus(error);
        SessionEnding += (_, _) => store.Flush();
        MainWindow = window;
        window.Show();
        if (e.Args.Contains("--background")) window.Hide();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (expectedTestReport is { } report)
        {
            bool passed = false, validReport = false;
            try
            {
                if (File.Exists(report))
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(report));
                    if (json.RootElement.TryGetProperty("passed", out var value) &&
                        value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    { validReport = true; passed = value.ValueKind == JsonValueKind.True; }
                }
            }
            catch (Exception error) { testUnhandledError ??= error.ToString(); }
            if (!passed || testUnhandledError is not null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                    if (!validReport || passed)
                        File.WriteAllText(report, JsonSerializer.Serialize(new { passed = false,
                            error = testUnhandledError ?? "Test report was not produced or was invalid." }));
                }
                catch (Exception) { /* The requested path itself may be inaccessible; the exit code still fails. */ }
                e.ApplicationExitCode = 1;
                Environment.ExitCode = 1;
            }
        }
        activeStore?.Flush();
        try { instance?.ReleaseMutex(); } catch (ApplicationException) { }
        instance?.Dispose();
        base.OnExit(e);
    }

    private static string? ExpectedTestReport(string[] args)
    {
        if (args.Contains("--self-test"))
        {
            int index = Array.IndexOf(args, "--test-report");
            return Path.GetFullPath(index >= 0 && index + 1 < args.Length
                ? args[index + 1] : Path.Combine(AppContext.BaseDirectory, "self-test-results.json"));
        }
        if (args.Length < 2) return null;
        if (args[0] == "--test-report-guard-probe") return Path.GetFullPath(args[1]);
        string? file = args[0] switch
        {
            "--cleanup-test" => "cleanup-tests.json", "--tooltip-test" => "results.json",
            "--update-test" => "update-tests.json", "--native-preview-test" or "--preview-interaction-test" => "native-preview-results.json",
            "--text-preview-test" => "text-preview-results.json", "--native-preview-benchmark" => "benchmark.json",
            "--common-preview-test" or "--file-preview-test" => "file-preview-results.json",
            "--file-record-test" => "file-record-results.json", "--copy-only-test" => "copy-only-results.json",
            "--layout-test" => "layout-regression-results.json", "--presentation-test" => "presentation-results.json",
            "--settings-test" => "settings-results.json", "--tray-test" => "tray-results.json",
            "--tray-popup-test" => "tray-popup-results.json", "--drag-test" => "drag-selection-results.json",
            "--focus-test" => "focus-results.json", _ => null
        };
        if (file is not null) return Path.GetFullPath(Path.Combine(args[1], file));
        return args[0] is "--theme-transition-test" or "--ui-performance-test" or "--interaction-test" or
            "--scroll-render-test" or "--multi-scroll-test" or "--fine-scroll-test" or "--preview-scroll-test" or
            "--smoothness-test" or "--smoothness-multi-test" or "--smoothness-fine-test" or "--smoothness-regression-test"
            ? Path.GetFullPath(args[1]) : null;
    }
}
