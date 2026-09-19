using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace ClipShelf;

public partial class App : Application
{
    private Mutex? instance;
    private HistoryStore? activeStore;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] == "--tooltip-test") { await ToolTipPresentationTests.RunAsync(e.Args[1]); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--update-test") { await WindowsUpdateTests.RunAsync(e.Args[1]); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--native-preview-test") { await NativePreviewTests.RunAsync(e.Args[1]); return; }
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
        activeStore?.Flush();
        try { instance?.ReleaseMutex(); } catch (ApplicationException) { }
        instance?.Dispose();
        base.OnExit(e);
    }
}
