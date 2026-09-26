using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClipShelf;

internal static class WindowsUpdateTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request, token); }
    internal static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory); Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var checks = new List<string>(); string? error = null; MainWindow? window = null;
        string? packagedAssetName = null, deviatedAssetName = null;
        void Check(bool success, string name) { if (!success) throw new Exception(name); checks.Add(name); }
        string Release(string version = "1.2.0", bool draft = false, string? hash = null, string? url = null, string? tag = null, long size = 100)
        {
            string name = WindowsUpdateService.AssetName(new Version(version));
            return JsonSerializer.Serialize(new { tag_name = tag ?? "windows-v" + version, draft, prerelease = true,
                assets = new[] { new { name, size, digest = hash ?? "sha256:" + new string('a', 64),
                    browser_download_url = url ?? $"https://github.com/LoganPage/ClipShelf/releases/download/windows-v{version}/{name}" } } });
        }
        WindowsRelease? Parse(string json) { using var document = JsonDocument.Parse(json); return WindowsUpdateService.ParseRelease(document.RootElement); }
        async Task<string> PackageAssetName(string script, string version)
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")) {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = true, RedirectStandardOutput = true
            };
            foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Version", version, "-AssetNameOnly" })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to run package naming probe.");
            string output = await process.StandardOutput.ReadToEndAsync(); string stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (process.ExitCode != 0) throw new InvalidOperationException(stderr);
            return output.Trim();
        }
        try
        {
            static string? FindUp(string start, string name)
            {
                for (var folder = new DirectoryInfo(Path.GetFullPath(start)); folder is not null; folder = folder.Parent)
                {
                    string candidate = Path.Combine(folder.FullName, name);
                    if (File.Exists(candidate)) return candidate;
                }
                return null;
            }
            string? packageScript = FindUp(Directory.GetCurrentDirectory(), "package.ps1") ?? FindUp(AppContext.BaseDirectory, "package.ps1");
            Check(File.Exists(packageScript), "Package naming script is available to the update regression test");
            packagedAssetName = await PackageAssetName(packageScript!, "9.8.7");
            Check(packagedAssetName == WindowsUpdateService.AssetName(new Version(9, 8, 7)), "Package output name exactly matches the updater asset name");
            string deviatedScript = Path.Combine(directory, "package-name-deviation.ps1");
            File.WriteAllText(deviatedScript, File.ReadAllText(packageScript!).Replace("-public-x64.zip", "-x64.zip", StringComparison.Ordinal));
            deviatedAssetName = await PackageAssetName(deviatedScript, "9.8.7");
            Check(deviatedAssetName != WindowsUpdateService.AssetName(new Version(9, 8, 7)), "Naming regression probe rejects the former non-public asset name");
            const string real137 = """
                {"tag_name":"windows-v1.3.7","draft":false,"prerelease":false,"assets":[{"name":"ClipShelf-Windows-v1.3.7-public-x64.zip","size":79926775,"digest":"sha256:55247b0ea1de195d3d899507b0313d3abcc98515bc76c9d39d967e1181f0499f","browser_download_url":"https://github.com/LoganPage/ClipShelf/releases/download/windows-v1.3.7/ClipShelf-Windows-v1.3.7-public-x64.zip"}]}
                """;
            const string real136 = """
                {"tag_name":"windows-v1.3.6","draft":false,"prerelease":true,"assets":[{"name":"ClipShelf-Windows-v1.3.6-x64.zip","size":79913540,"digest":"sha256:0a548e0f5a9b1dcef6d6ed18de9b5dfff3f0511fa8c93c0880121572c6cab473","browser_download_url":"https://github.com/LoganPage/ClipShelf/releases/download/windows-v1.3.6/ClipShelf-Windows-v1.3.6-x64.zip"}]}
                """;
            Check(Parse(real137)?.Version == new Version(1, 3, 7), "Real windows-v1.3.7 public asset is accepted");
            Check(Parse(real136) is null, "Real windows-v1.3.6 legacy-named asset remains outside the strict update channel");
            Check(Parse(Release())?.Version == new Version(1, 2, 0), "Windows prerelease with SHA256 is accepted");
            Check(Parse(Release(tag: "v9.0.0")) is null, "Mac release is excluded");
            Check(Parse(Release(draft: true)) is null, "Draft release is excluded");
            Check(Parse(Release(hash: "sha256:bad")) is null, "Malformed digest is excluded");
            Check(Parse(Release(hash: "")) is null, "Missing integrity digest is excluded");
            Check(Parse(Release(size: 256L * 1024 * 1024 + 1)) is null, "Oversized release asset is excluded");
            Check(Parse(Release(url: "https://example.com/update.zip")) is null, "Foreign repository package is excluded");
            Check(Parse(Release("1.2.0.1")) is null && Parse(Release("1.2")) is null, "Release tag must have three version components");
            Check(WindowsUpdateService.TrustedDownloadHost(new Uri("https://release-assets.githubusercontent.com/package")), "GitHub asset redirect is allowed");
            foreach (string bad in new[] { "http://github.com/file", "https://github.com.evil.test/file", "https://user@github.com/file", "https://github.com:444/file" })
                Check(!WindowsUpdateService.TrustedDownloadHost(new Uri(bad)), "Unsafe download endpoint rejected: " + bad);
            using (var service = new WindowsUpdateService(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[" + Release("1.1.1") + "," + Release("1.2.0") + "," + Release("9.0.0", tag: "v9.0.0") + "]") }))))
                Check((await service.CheckAsync(CancellationToken.None))?.Version == new Version(1, 2, 0), "Newest Windows version selected irrespective of release order");
            using (var cancel = new CancellationTokenSource())
            using (var service = new WindowsUpdateService(new Handler(async (_, token) => { await Task.Delay(10000, token); return new HttpResponseMessage(HttpStatusCode.OK); })))
            {
                var request = service.CheckAsync(cancel.Token); cancel.Cancel();
                try { await request; Check(false, "Cancellation must throw"); } catch (OperationCanceledException) { Check(true, "In-flight check cancels promptly"); }
            }
            using (var service = new WindowsUpdateService(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)))))
            { try { await service.CheckAsync(CancellationToken.None); Check(false, "HTTP failure must throw"); } catch (HttpRequestException) { Check(true, "API permission/rate-limit error is propagated safely"); } }
            byte[] payload = new byte[] { 1, 2, 3, 4 };
            var download = Parse(Release())! with { Size = payload.Length, Sha256 = Convert.ToHexString(SHA256.HashData(payload)) };
            using (var service = new WindowsUpdateService(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }))))
            {
                string target = Path.Combine(directory, "download.zip");
                await service.DownloadArchiveAsync(download, target, null, CancellationToken.None);
                Check(File.ReadAllBytes(target).SequenceEqual(payload), "Downloaded package is size and SHA256 verified");
                try { await service.DownloadArchiveAsync(download with { Sha256 = new string('0', 64) }, target, null, CancellationToken.None); Check(false, "Wrong hash must fail"); } catch (InvalidDataException) { Check(true, "Modified package fails integrity check before installation"); }
                try { await service.DownloadArchiveAsync(download with { Size = 8 }, target, null, CancellationToken.None); Check(false, "Wrong size must fail"); } catch (InvalidDataException) { Check(true, "Truncated or wrong-sized download rejected"); }
            }
            int redirects = 0;
            using (var service = new WindowsUpdateService(new Handler((_, _) => { redirects++; var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri("https://evil.test/package"); return Task.FromResult(response); })))
            { try { await service.DownloadArchiveAsync(download, Path.Combine(directory, "redirect.zip"), null, CancellationToken.None); Check(false, "Foreign redirect must fail"); } catch (InvalidDataException) { Check(redirects == 1, "Redirect validation blocks requests to untrusted hosts"); } }
            void Zip(string name, Action<ZipArchive> build, bool valid)
            {
                string file = Path.Combine(directory, name + ".zip"); using (var archive = ZipFile.Open(file, ZipArchiveMode.Create)) build(archive);
                try { WindowsUpdateService.ExtractPackage(file, Path.Combine(directory, name), CancellationToken.None); Check(valid, name); }
                catch (InvalidDataException) { Check(!valid, name); }
                catch (IOException) { Check(!valid, name); }
            }
            Zip("valid", zip => { foreach (string file in new[] { "ClipShelf.exe", "ClipShelf.dll", "ClipShelf.runtimeconfig.json", "ClipShelf.deps.json" }) zip.CreateEntry("ClipShelf/" + file); }, true);
            Zip("traversal", zip => zip.CreateEntry("ClipShelf/../../escape"), false);
            Zip("absolute", zip => zip.CreateEntry("C:/escape"), false);
            Zip("symlink", zip => zip.CreateEntry("ClipShelf/link").ExternalAttributes = 0xA000 << 16, false);
            Zip("missing-runtime", zip => zip.CreateEntry("ClipShelf/ClipShelf.exe"), false);
            Zip("duplicate", zip => { zip.CreateEntry("ClipShelf/a"); zip.CreateEntry("ClipShelf/a"); }, false);
            Zip("entry-limit", zip => { for (int i = 0; i < 4001; i++) zip.CreateEntry("ClipShelf/" + i); }, false);
            using (var cancelled = new CancellationTokenSource())
            { cancelled.Cancel(); try { WindowsUpdateService.ExtractPackage(Path.Combine(directory, "valid.zip"), Path.Combine(directory, "cancelled"), cancelled.Token); Check(false, "Cancelled extraction must throw"); } catch (OperationCanceledException) { Check(true, "Extraction obeys cancellation"); } }
            var helperStart = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(AppContext.BaseDirectory, "InstallUpdate.ps1"), "-Stage", directory, "-Target", directory, "-WaitProcessId", "2147483647" }) helperStart.ArgumentList.Add(arg);
            using (var helper = Process.Start(helperStart)!)
            {
                var stderr = helper.StandardError.ReadToEndAsync(); var stdout = helper.StandardOutput.ReadToEndAsync();
                await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Check(helper.ExitCode != 0 && (await stderr).Contains("Invalid update paths."), "Windows PowerShell 5.1 parses the shipped helper and rejects non-install targets"); await stdout;
            }
            window = new MainWindow(new HistoryStore(Path.Combine(directory, "fixture")), demo: true) { Width = 980, Height = 800, Left = -12000, Top = -12000, ShowActivated = false, ShowInTaskbar = false };
            window.Show(); window.OpenSettings();
            await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var panel = (SettingsPanel)((ContentControl)window.FindName("SettingsContent")).Content;
            var button = Find(panel).OfType<Button>().Single(b => b.Name == "CheckForUpdatesButton");
            Check((string)button.Content == "检查更新" && button.IsEnabled, "Settings exposes a live update button without rebuilding the page");
            Check(window.UpdateStatus.Contains("点击"), "No network check starts automatically");
            var card = (FrameworkElement)window.FindName("SettingsCard");
            var screenshot = new RenderTargetBitmap((int)card.ActualWidth, (int)card.ActualHeight, 96, 96, PixelFormats.Pbgra32); screenshot.Render(card);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(screenshot)); using (var file = File.Create(Path.Combine(directory, "settings-updates.png"))) encoder.Save(file);
        }
        catch (Exception ex) { error = ex.ToString(); }
        File.WriteAllText(Path.Combine(directory, "update-tests.json"), JsonSerializer.Serialize(new { passed = error is null, checks, packagedAssetName, deviatedAssetName, error }, new JsonSerializerOptions { WriteIndented = true }));
        if (window is not null) window.Quit(); else Application.Current.Shutdown(error is null ? 0 : 1);
    }
    private static IEnumerable<DependencyObject> Find(DependencyObject root)
    { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var item in Find(child)) yield return item; } }
}
