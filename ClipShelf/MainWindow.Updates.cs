using System;
using System.IO;
using System.Net.Http;
using System.Threading;

namespace ClipShelf;

public partial class MainWindow
{
    private readonly WindowsUpdateService updates = new();
    private CancellationTokenSource? updateCancellation;
    private WindowsRelease? availableUpdate;
    private string? pendingUpdateStage;
    internal event Action? UpdateChanged;
    internal string UpdateStatus { get; private set; } = File.Exists(Path.Combine(WindowsUpdateService.UpdateDirectory, "last-error.txt"))
        ? "上次安装未完成，已保留或恢复旧版。可重试更新；备份位于 ClipShelf-backups。" : "点击检查更新，查看是否有新版。";
    internal string UpdateActionLabel => UpdateInstalling ? "正在重启安装…" : updateCancellation is not null ? "取消" : availableUpdate is not null ? $"下载并安装 {availableUpdate.Version}" : "检查更新";
    internal bool UpdateInstalling { get; private set; }
    internal async void RunUpdateAction()
    {
        if (UpdateInstalling) return;
        if (cleanupRunning) { UpdateStatus = "正在清理缓存，请完成后再更新。"; UpdateChanged?.Invoke(); return; }
        if (updateCancellation is not null) { updateCancellation.Cancel(); return; }
        using var cancellation = new CancellationTokenSource(); updateCancellation = cancellation;
        try
        {
            if (availableUpdate is null)
            {
                UpdateStatus = "正在检查 Windows 更新…"; UpdateChanged?.Invoke();
                var release = await updates.CheckAsync(cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                availableUpdate = release is not null && release.Version > WindowsUpdateService.CurrentVersion ? release : null;
                UpdateStatus = availableUpdate is not null ? $"发现 Windows {availableUpdate.Version}，点击下方按钮下载并重启安装。" : "当前没有可安装的 Windows 新版。";
            }
            else
            {
                UpdateStatus = "正在下载更新…"; UpdateChanged?.Invoke();
                var progress = new Progress<double>(percent => { if (updateCancellation != cancellation || cancellation.IsCancellationRequested || UpdateInstalling) return; UpdateStatus = percent < 100 ? $"正在下载更新：{percent:0}%" : "下载完成，正在校验和准备安装…"; UpdateChanged?.Invoke(); });
                string stage = await updates.PrepareAsync(availableUpdate, progress, cancellation.Token);
                if (cancellation.IsCancellationRequested) { WindowsUpdateService.DiscardStage(stage); cancellation.Token.ThrowIfCancellationRequested(); }
                pendingUpdateStage = stage;
                UpdateInstalling = true; UpdateStatus = "正在保存历史；随后备份并重启安装。"; UpdateChanged?.Invoke();
                Quit();
            }
        }
        catch (OperationCanceledException) { UpdateStatus = cancellation.IsCancellationRequested ? "已取消更新，当前版本不受影响。" : "检查或下载超时，请稍后重试。"; }
        catch (HttpRequestException) { UpdateStatus = "暂时无法连接 GitHub，请检查网络后重试。当前版本不受影响。"; }
        catch (Exception ex) { UpdateStatus = ex is InvalidOperationException or InvalidDataException ? ex.Message : "更新准备失败，请稍后重试或从 GitHub 下载。当前版本不受影响。"; }
        finally { updateCancellation = null; UpdateChanged?.Invoke(); }
    }
    private void UpdateExitFailed(string message)
    {
        if (pendingUpdateStage is { } stage) WindowsUpdateService.DiscardStage(stage);
        pendingUpdateStage = null; UpdateInstalling = false; UpdateStatus = message; UpdateChanged?.Invoke();
    }
}
