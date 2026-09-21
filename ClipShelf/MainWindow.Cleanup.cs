using System;
using System.Threading;
using System.Linq;
using System.Collections.Generic;

namespace ClipShelf;

public partial class MainWindow
{
    internal ClipItem[] OldHistory(int days) => Store.Items.Where(item => !item.IsPinned && item.CreatedAt < DateTimeOffset.Now.AddDays(-days)).ToArray();
    internal int CleanOldHistory(IReadOnlyList<ClipItem> confirmed, int days)
    {
        var ids = confirmed.Select(item => item.Id).ToHashSet();
        var eligible = OldHistory(days).Where(item => ids.Contains(item.Id)).ToArray();
        DeleteHistoryItems(eligible); return eligible.Length;
    }
    private bool cleanupRunning;
    internal event Action? CleanupChanged;
    internal bool CleanupRunning => cleanupRunning;
    internal string CleanupStatus { get; private set; } = "仅清理可重新生成的缓存和超过一天的更新下载包。";
    internal async void RunCleanup()
    {
        if (cleanupRunning) return;
        if (updateCancellation is not null || UpdateInstalling)
        { CleanupStatus = "正在检查或安装更新，请完成后再清理。"; CleanupChanged?.Invoke(); return; }
        cleanupRunning = true; CleanupStatus = "正在清理缓存…"; CleanupChanged?.Invoke();
        try
        {
            var result = await previewCache.ClearGeneratedAsync(WindowsUpdateService.UpdateDirectory, CancellationToken.None);
            ThumbnailLoader.ClearCache();
            string size = result.Bytes >= 1024 * 1024 ? $"{result.Bytes / 1048576.0:0.##} MB" : $"{result.Bytes / 1024.0:0.##} KB";
            CleanupStatus = result.Files == 0 ? "暂无可清理的磁盘缓存，已释放缓存引用。" : $"已清理 {result.Files} 个缓存文件，释放 {size} 磁盘空间。";
            if (result.Skipped > 0) CleanupStatus += $" {result.Skipped} 项因占用、权限或安全限制已跳过。";
        }
        catch (Exception) { CleanupStatus = "清理未完成，请稍后重试。历史记录与原文件未改动。"; }
        finally { cleanupRunning = false; CleanupChanged?.Invoke(); }
    }
}
