using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipShelf;

internal static class FileRecordTests
{
    public static async Task RunAsync(string root)
    {
        root = Path.GetFullPath(root); Directory.CreateDirectory(root);
        var checks = new List<string>(); string? error = null; MainWindow? window = null; double elapsed = 0;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        try {
            var store = new HistoryStore(Path.Combine(root, "isolated"), true); store.Settings.MaxItems = 2000;
            store.Settings.HistoryEnabled = store.Settings.WatchScreenshots = store.Settings.LaunchAtLogin = false;
            var paths = Enumerable.Range(0, 1000).Select(i => Path.Combine(root, $"sample-{i:D4}.pdf")).ToList();
            int changes = 0; store.Changed += () => changes++;
            var stopwatch = Stopwatch.StartNew(); store.AddFileBatch(new ClipItem { Kind = ClipKind.File, FilePaths = paths }); stopwatch.Stop(); elapsed = stopwatch.Elapsed.TotalMilliseconds;
            Check(store.Items.Count == 1000 && store.Items.All(i => i.FilePaths.Count == 1), "A thousand copied paths become a thousand separate records");
            Check(changes == 1, "Batch import refreshes the UI once");
            Check(store.Items.Select(i => i.FilePaths[0]).SequenceEqual(paths), "Clipboard order remains deterministic inside the batch");
            var pinned = store.Items[5]; store.TogglePinned(new[] { pinned.Id }); changes = 0;
            store.AddFileBatch(new ClipItem { Kind = ClipKind.File, FilePaths = new() { paths[5].ToUpperInvariant(), paths[5], paths[2] } });
            Check(store.Items.Count == 1000 && changes == 1, "Recopy deduplicates paths case-insensitively with one update");
            Check(store.Items.Any(i => i.Id == pinned.Id && i.IsPinned), "Recopy preserves per-file pin and identity");
            store.Remove(new[] { pinned.Id }); Check(store.Items.Count == 999, "Deleting one imported file affects only that record");
            Check(store.UndoDelete() == 1 && store.Items.Count == 1000, "One-file deletion is independently undoable");
            Check(await store.FlushAsync(), "Batch persistence finishes successfully");
            var reloaded = new HistoryStore(store.DirectoryPath);
            Check(reloaded.Items.Count == 100, "Reload continues to respect the saved/default history limit");
            // Save the explicit setting before testing persistence with a larger cap.
            store.SaveSettings(); await store.FlushAsync(); reloaded = new HistoryStore(store.DirectoryPath);
            Check(reloaded.Items.Count == 1000 && reloaded.Items.All(i => i.FilePaths.Count == 1), "Separate records survive restart");
            var capped = new HistoryStore(Path.Combine(root, "capped")); capped.Settings.MaxItems = 3;
            capped.AddFileBatch(new ClipItem { Kind = ClipKind.File, FilePaths = paths });
            Check(capped.Items.Count == 3 && capped.Items.Select(i => i.FilePaths[0]).SequenceEqual(paths.Take(3)), "Batch import obeys the history limit predictably");
            var gallery = new HistoryStore(Path.Combine(root, "gallery")); gallery.Settings.HistoryEnabled = gallery.Settings.WatchScreenshots = gallery.Settings.LaunchAtLogin = false;
            var names = new[] { "合同.pdf", "预算.xlsx", "报告.docx", "演示.pptx", "资料.zip", "照片.jpg", "说明.txt", "未知.xyz" };
            var categories = new[] { "PDF", "Excel", "Word", "PowerPoint", "Archive", "Image", "Document", "File" };
            gallery.AddFileBatch(new ClipItem { Kind = ClipKind.File, FilePaths = names.Select(n => Path.Combine(root, n)).ToList() });
            for (int i = 0; i < names.Length; i++) Check(RecordTypeIcon.Category(gallery.Items[i]) == categories[i], names[i] + ": correct type and icon family");
            gallery.Add(new ClipItem { Kind = ClipKind.Text, Text = "普通文字记录" });
            gallery.Add(new ClipItem { Kind = ClipKind.File, IsDirectory = true, Title = "资料文件夹", FilePaths = new() { root } });
            window = new MainWindow(gallery, demo: true) { Width = 900, Height = 950, ShowInTaskbar = false }; window.Show(); await window.PendingSearch;
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            foreach (string theme in new[] { "Light", "Dark" }) {
                gallery.Settings.Theme = theme; window.ApplyPreferences(); window.UpdateLayout();
                Check(All<RecordTypeIcon>(window).Count() >= 8, theme + ": real history rows render the vector icons");
                var shot = new RenderTargetBitmap(900, 950, 96, 96, PixelFormats.Pbgra32); shot.Render(window);
                var encoded = new PngBitmapEncoder(); encoded.Frames.Add(BitmapFrame.Create(shot)); using var output = File.Create(Path.Combine(root, "types-" + theme + ".png")); encoded.Save(output);
            }
            Check(gallery.Items.Single(i => i.IsDirectory).KindLabel == "文件夹", "Folder metadata gives a folder icon and accessible type label");
            var original = new ClipItem { Kind = ClipKind.File, FilePaths = new() { paths[0], paths[1] }, Title = "Old grouped record" };
            gallery.Add(original); gallery.Save();
            Check(new HistoryStore(gallery.DirectoryPath).Items.Any(i => i.Id == original.Id && i.FilePaths.Count == 2), "Existing grouped records are preserved without an implicit destructive migration");
        } catch (Exception ex) { error = ex.ToString(); }
        finally { window?.Quit(); File.WriteAllText(Path.Combine(root, "file-record-results.json"), JsonSerializer.Serialize(new { passed = error is null, checks, error, batchMilliseconds = elapsed }, new JsonSerializerOptions { WriteIndented = true })); Application.Current.Shutdown(error is null ? 0 : 1); }
    }
    private static IEnumerable<T> All<T>(DependencyObject root) where T : DependencyObject {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in All<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
