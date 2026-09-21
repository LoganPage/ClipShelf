using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClipShelf;

internal sealed record WindowsRelease(Version Version, string Tag, Uri Download, long Size, string Sha256);
internal sealed class WindowsUpdateService : IDisposable
{
    internal static Version CurrentVersion => new(Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0");
    internal static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ClipShelf");
    internal static string UpdateDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipShelf-Updates");
    private const long MaxArchive = 256L * 1024 * 1024;
    private readonly HttpClient client;
    internal WindowsUpdateService(HttpMessageHandler? handler = null) { client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) }; client.DefaultRequestHeaders.UserAgent.ParseAdd("ClipShelf-Windows/" + CurrentVersion); }
    internal static WindowsRelease? ParseRelease(JsonElement release)
    {
        if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (!release.TryGetProperty("tag_name", out var tagValue)) return null;
        string tag = tagValue.GetString() ?? "";
        if (!tag.StartsWith("windows-v", StringComparison.Ordinal) || !Version.TryParse(tag[9..], out var version) || version.Build < 0 || version.Revision >= 0) return null;
        string name = $"ClipShelf-Windows-v{version}-public-x64.zip";
        if (!release.TryGetProperty("assets", out var assets)) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var n) || n.GetString() != name) continue;
            if (!asset.TryGetProperty("size", out var size) || !size.TryGetInt64(out long length) || length <= 0 || length > MaxArchive) continue;
            if (!asset.TryGetProperty("digest", out var digest)) continue;
            string hash = digest.GetString() ?? "";
            if (!hash.StartsWith("sha256:", StringComparison.Ordinal) || hash.Length != 71 || !hash[7..].All(Uri.IsHexDigit)) continue;
            string expected = $"https://github.com/LoganPage/ClipShelf/releases/download/{tag}/{name}";
            if (!asset.TryGetProperty("browser_download_url", out var url) || url.GetString() != expected) continue;
            return new(version, tag, new Uri(expected), length, hash[7..]);
        }
        return null;
    }
    internal async Task<WindowsRelease?> CheckAsync(CancellationToken token)
    {
        WindowsRelease? latest = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        // Windows previews are intentionally included, but Mac tags and arbitrary assets never are.
        for (int page = 1; page <= 5; page++)
        {
            using var response = await client.GetAsync($"https://api.github.com/repos/LoganPage/ClipShelf/releases?per_page=30&page={page}", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token); using var bytes = new MemoryStream();
            await CopyBoundedAsync(stream, bytes, 2 * 1024 * 1024, null, timeout.Token);
            using var json = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            foreach (var element in json.RootElement.EnumerateArray()) { var candidate = ParseRelease(element); if (candidate is not null && (latest is null || candidate.Version > latest.Version)) latest = candidate; }
            if (json.RootElement.GetArrayLength() < 30) break;
        }
        return latest;
    }
    internal static bool TrustedDownloadHost(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
        && uri.Host is "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com";
    private async Task<HttpResponseMessage> DownloadResponseAsync(Uri url, CancellationToken token)
    {
        for (int hop = 0; hop < 6; hop++)
        {
            if (!TrustedDownloadHost(url)) throw new InvalidDataException("更新下载地址不可信。");
            var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var next = response.Headers.Location; response.Dispose();
                if (next is null) throw new InvalidDataException("更新下载跳转无效。");
                url = next.IsAbsoluteUri ? next : new Uri(url, next); continue;
            }
            try { response.EnsureSuccessStatusCode(); return response; } catch { response.Dispose(); throw; }
        }
        throw new InvalidDataException("更新下载跳转过多。");
    }
    internal async Task<string> PrepareAsync(WindowsRelease release, IProgress<double>? progress, CancellationToken token)
    {
        if (!Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar).Equals(InstallDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("便携版请先使用 install.ps1 安装，或到 GitHub 下载新版。");
        Directory.CreateDirectory(UpdateDirectory);
        if ((File.GetAttributes(UpdateDirectory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("更新目录不能是链接。");
        string stage = Path.Combine(UpdateDirectory, "stage-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        try
        {
            string archive = Path.Combine(stage, "package.zip");
            await DownloadArchiveAsync(release, archive, progress, token);
            await Task.Run(() => ExtractPackage(archive, Path.Combine(stage, "payload"), token), token);
            string exe = Path.Combine(stage, "payload", "ClipShelf", "ClipShelf.exe");
            if (!Version.TryParse(FileVersionInfo.GetVersionInfo(exe).ProductVersion, out var actualVersion) || actualVersion != release.Version)
                throw new InvalidDataException("更新包版本不一致。");
            File.Delete(archive); return stage;
        }
        catch { try { Directory.Delete(stage, true); } catch { } throw; }
    }
    internal async Task DownloadArchiveAsync(WindowsRelease release, string archive, IProgress<double>? progress, CancellationToken token)
    {
        using (var response = await DownloadResponseAsync(release.Download, token))
        {
            if (response.Content.Headers.ContentLength is long size && size != release.Size) throw new InvalidDataException("更新包大小不一致。");
            await using var input = await response.Content.ReadAsStreamAsync(token); await using var output = File.Create(archive);
            long sizeWritten = await CopyBoundedAsync(input, output, release.Size, progress, token);
            if (sizeWritten != release.Size) throw new InvalidDataException("更新包下载不完整。");
        }
        await using var verified = File.OpenRead(archive);
        string actual = Convert.ToHexString(await SHA256.HashDataAsync(verified, token));
        if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新包校验失败，已停止安装。");
    }
    internal static void ExtractPackage(string archive, string output, CancellationToken token)
    {
        using var zip = ZipFile.OpenRead(archive); if (zip.Entries.Count > 4000) throw new InvalidDataException("更新包条目过多。");
        long total = 0; string root = Path.GetFullPath(output) + Path.DirectorySeparatorChar;
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested(); string name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(x => x is ".." or ".") || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("更新包路径非法。");
            total = checked(total + entry.Length); if (entry.Length > MaxArchive || total > 1024L * 1024 * 1024) throw new InvalidDataException("更新包超过安全大小限制。");
            if (!name.StartsWith("ClipShelf/", StringComparison.Ordinal)) continue;
            string target = Path.GetFullPath(Path.Combine(output, name)); if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新包越界。");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open(); using var extracted = new FileStream(target, FileMode.CreateNew);
            byte[] buffer = new byte[81920]; long written = 0;
            while (true) { token.ThrowIfCancellationRequested(); int read = input.Read(buffer); if (read == 0) break; written += read; if (written > entry.Length) throw new InvalidDataException("更新包解压大小不一致。"); extracted.Write(buffer, 0, read); }
            if (written != entry.Length) throw new InvalidDataException("更新包不完整。");
        }
        foreach (string file in new[] { "ClipShelf.exe", "ClipShelf.dll", "ClipShelf.runtimeconfig.json", "ClipShelf.deps.json" })
            if (!File.Exists(Path.Combine(output, "ClipShelf", file))) throw new InvalidDataException("更新包缺少程序文件。");
    }
    private static async Task<long> CopyBoundedAsync(Stream source, Stream target, long limit, IProgress<double>? progress, CancellationToken token)
    {
        byte[] buffer = new byte[81920]; long total = 0; var timer = Stopwatch.StartNew();
        while (true) { int read = await source.ReadAsync(buffer, token); if (read == 0) break; total += read; if (total > limit) throw new InvalidDataException("下载内容超过大小限制。"); await target.WriteAsync(buffer.AsMemory(0, read), token); if (timer.ElapsedMilliseconds > 100) { progress?.Report(total * 100d / limit); timer.Restart(); } }
        progress?.Report(100); return total;
    }
    internal static async Task LaunchInstallerAsync(string stage)
    {
        string script = Path.Combine(AppContext.BaseDirectory, "InstallUpdate.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException("缺少安装助手。");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(AppContext.BaseDirectory, "InstallUpdate.ps1"), "-Stage", stage, "-Target", InstallDirectory, "-WaitProcessId", Environment.ProcessId.ToString() }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动安装助手。");
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (process.HasExited) throw new InvalidOperationException("安装助手检查失败。");
            if (File.Exists(Path.Combine(stage, "ready"))) return;
            await Task.Delay(100);
        }
        // The helper only waits for us; terminating this owned helper before exit is safe.
        if (!process.HasExited) process.Kill();
        throw new TimeoutException("安装助手启动超时。");
    }
    internal static void DiscardStage(string stage)
    {
        try
        {
            string path = Path.GetFullPath(stage).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(path), UpdateDirectory, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(path).StartsWith("stage-", StringComparison.Ordinal)
                || !Guid.TryParseExact(Path.GetFileName(path)[6..], "N", out _)) return;
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) Directory.Delete(path, true);
        }
        catch { }
    }
    public void Dispose() => client.Dispose();
}
