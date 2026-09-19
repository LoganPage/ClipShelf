using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ClipShelf;

internal static class CacheCleanupTests
{
    internal static async Task RunAsync(string output)
    {
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Directory.CreateDirectory(output);
        string root = Path.Combine(Path.GetTempPath(), "ClipShelf-cleanup-test-" + Guid.NewGuid().ToString("N"));
        string cache = Path.Combine(root, "cache"), updates = Path.Combine(root, "updates");
        Directory.CreateDirectory(cache); Directory.CreateDirectory(updates);
        var checks = new List<string>(); string? error = null;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks.Add(name); }
        try
        {
            string hash = new('A', 64);
            Check(CacheCleanupService.GeneratedPage(hash + ".png"), "recognized page");
            Check(!CacheCleanupService.GeneratedPage("history.json"), "history excluded");
            Check(!CacheCleanupService.GeneratedPage("settings.json"), "settings excluded");
            Check(!CacheCleanupService.GeneratedPage("image.png"), "unknown image excluded");
            File.WriteAllBytes(Path.Combine(cache, hash + ".png"), new byte[123]);
            File.WriteAllText(Path.Combine(cache, "history.json"), "retain");
            Directory.CreateDirectory(Path.Combine(cache, "nested"));
            File.WriteAllText(Path.Combine(cache, "nested", hash + ".png"), "retain");
            string stage = Path.Combine(updates, "stage-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
            string package = Path.Combine(stage, "package.zip"); File.WriteAllBytes(package, new byte[77]); File.SetLastWriteTimeUtc(package, DateTime.UtcNow.AddDays(-2));
            string fresh = Path.Combine(updates, "stage-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fresh); File.WriteAllText(Path.Combine(fresh, "package.zip"), "keep");
            string locked = Path.Combine(cache, new string('B', 64) + ".json");
            using (var held = new FileStream(locked, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                var result = await Task.Run(() => CacheCleanupService.Clean(cache, updates, CancellationToken.None));
                Check(result.Files == 2 && result.Bytes == 200, "accurate file and byte counts");
                Check(result.Skipped == 1 && File.Exists(locked), "occupied file skipped");
            }
            Check(File.ReadAllText(Path.Combine(cache, "history.json")) == "retain", "history retained");
            Check(File.Exists(Path.Combine(cache, "nested", hash + ".png")), "no recursive deletion");
            Check(File.Exists(Path.Combine(fresh, "package.zip")), "fresh download retained");
            using var cts = new CancellationTokenSource(); cts.Cancel();
            try { CacheCleanupService.Clean(cache, updates, cts.Token); throw new Exception("cancellation ignored"); } catch (OperationCanceledException) { checks.Add("cancellation honored"); }
            var store = new HistoryStore(Path.Combine(root, "history"));
            foreach (string text in new[] { "old", "pinned", "new" }) store.Add(new ClipItem { Kind = ClipKind.Text, Text = text, Title = text });
            var old = store.Items.Single(i => i.Text == "old"); old.CreatedAt = DateTimeOffset.Now.AddDays(-60);
            var pinned = store.Items.Single(i => i.Text == "pinned"); pinned.CreatedAt = old.CreatedAt; pinned.IsPinned = true;
            var window = new MainWindow(store, demo: true);
            Check(window.OldHistory(30).Length == 1, "history age and pin filter");
            var candidates = window.OldHistory(30); old.IsPinned = true;
            Check(window.CleanOldHistory(candidates, 30) == 0, "newly pinned record protected at confirmation");
            old.IsPinned = false; Check(window.CleanOldHistory(candidates, 30) == 1, "confirmed old record removed");
            Check(store.UndoDelete() == 1 && store.Items.Count == 3, "history cleanup undo");
        }
        catch (Exception ex) { error = ex.ToString(); }
        File.WriteAllText(Path.Combine(output, "cleanup-tests.json"), JsonSerializer.Serialize(new { passed = error is null, checks, error }, new JsonSerializerOptions { WriteIndented = true }));
        Application.Current.Shutdown(error is null ? 0 : 1);
    }
}
