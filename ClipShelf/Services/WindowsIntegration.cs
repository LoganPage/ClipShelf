using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ClipShelf;

/// <summary>Owns Windows clipboard monitoring, the global shortcut, copying, and screenshot imports.</summary>
public sealed class WindowsIntegration : IDisposable
{
    private readonly Window _window;
    private readonly HistoryStore _store;
    private readonly nint _handle;
    private readonly HwndSource _source;
    private readonly bool _manageStartup;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _imagePreparation = new(1, 1);
    private readonly SemaphoreSlim _copyPreparation = new(1, 1);
    private readonly Dictionary<string, CancellationTokenSource> _pendingScreenshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _knownScreenshotPaths = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _screenshotWatcher;
    private string? _watcherFolder;
    private int _watcherGeneration;
    private uint _lastClipboardSequence;
    private uint _queuedClipboardSequence;
    private bool _readingClipboard;
    private bool _writingClipboard;
    private bool _disposed;
    private bool _registeredHotKey;
    private int _hotKeyId = 0xC1A;
    private uint _hotKeyModifiers, _hotKeyVirtualKey;
    private bool? _appliedLaunchAtLogin;
    private long _captureGeneration;

    public event Action? ShowRequested;
    public event Action<string>? Status;
    internal event Action<string>? Diagnostic;
    public string StatusText { get; private set; } = "准备就绪";
    public string ScreenshotStatus { get; private set; } = "截图监听已暂停";
    public string? LastHotKeyError { get; private set; }

    public WindowsIntegration(Window window, HistoryStore store, bool manageStartup = true)
    {
        _window = window;
        _store = store;
        _manageStartup = manageStartup;
        _handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_handle) ?? throw new InvalidOperationException("无法创建剪贴板监听窗口。");
        _source.AddHook(WindowMessage);
        _lastClipboardSequence = NativeMethods.GetClipboardSequenceNumber();
        _queuedClipboardSequence = _lastClipboardSequence;
        if (!NativeMethods.AddClipboardFormatListener(_handle))
            SetStatus("剪贴板监听未能启动，请重新打开 ClipShelf。");
        ApplySettings();
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (_disposed) return 0;
        if (message == NativeMethods.WmHotKey && wParam.ToInt32() == _hotKeyId)
        {
            ShowRequested?.Invoke();
            handled = true;
        }
        else if (message == NativeMethods.WmClipboardUpdate)
        {
            var sequence = NativeMethods.GetClipboardSequenceNumber();
            if (_writingClipboard || sequence == _lastClipboardSequence) return 0;
            if (NativeMethods.GetClipboardOwner() == _handle)
            {
                _lastClipboardSequence = sequence;
                _queuedClipboardSequence = sequence;
                return 0;
            }
            _queuedClipboardSequence = sequence;
            if (!_store.Settings.HistoryEnabled)
            {
                _lastClipboardSequence = sequence;
                return 0;
            }
            if (!_readingClipboard) _ = ReadClipboardChangesAsync();
        }
        return 0;
    }

    private async Task ReadClipboardChangesAsync()
    {
        _readingClipboard = true;
        try
        {
            while (!_disposed && _queuedClipboardSequence != _lastClipboardSequence)
            {
                var wantedSequence = _queuedClipboardSequence;
                var finished = false;
                for (var attempt = 0; attempt < 6 && !_disposed; attempt++)
                {
                    await Task.Delay(30 + attempt * 35, _lifetime.Token);
                    if (!_store.Settings.HistoryEnabled || _writingClipboard)
                    {
                        _lastClipboardSequence = NativeMethods.GetClipboardSequenceNumber();
                        finished = true;
                        break;
                    }
                    if (NativeMethods.GetClipboardSequenceNumber() != wantedSequence)
                    {
                        _queuedClipboardSequence = NativeMethods.GetClipboardSequenceNumber();
                        finished = true;
                        break;
                    }
                    try
                    {
                        // Capture clipboard-owned data on this STA thread before doing any background work.
                        var capture = ReadClipboardItem();
                        if (NativeMethods.GetClipboardSequenceNumber() != wantedSequence)
                        {
                            _queuedClipboardSequence = NativeMethods.GetClipboardSequenceNumber();
                            finished = true;
                            break;
                        }
                        _lastClipboardSequence = wantedSequence;
                        if (capture is not null)
                        {
                            if (capture.Item.Kind == ClipKind.Image) _ = StoreCapturedImageAsync(capture);
                            else if (capture.Item.Kind == ClipKind.File) _store.AddFileBatch(capture.Item);
                            else _store.Add(capture.Item);
                        }
                        finished = true;
                        break;
                    }
                    catch (Exception exception) when (IsClipboardException(exception))
                    {
                        // Another process may briefly own the clipboard. Never log its contents.
                        RecordDiagnostic("ReadClipboardItem", exception);
                    }
                }
                if (!finished)
                {
                    _lastClipboardSequence = wantedSequence;
                    SetStatus("剪贴板正在被其他应用使用，本次内容未能读取。");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (IsClipboardException(exception))
        {
            RecordDiagnostic("ReadClipboardChanges", exception);
            SetStatus("这条剪贴板内容未能保存。");
        }
        finally { _readingClipboard = false; }
    }

    private sealed record ClipboardCapture(ClipItem Item, byte[]? Png = null, BitmapSource? Bitmap = null);

    private ClipboardCapture? ReadClipboardItem()
    {
        _window.Dispatcher.VerifyAccess();
        if (NativeClipboard.IsExcluded(_handle)) return null;
        if (Clipboard.ContainsFileDropList())
        {
            var paths = Clipboard.GetFileDropList().Cast<string>()
                .Where(path => File.Exists(path) || Directory.Exists(path)).ToList();
            if (paths.Count > 0)
                return new ClipboardCapture(new ClipItem
                {
                    Id = Guid.NewGuid(), Kind = ClipKind.File, FilePaths = paths,
                    Title = paths.Count == 1 ? Path.GetFileName(paths[0].TrimEnd(Path.DirectorySeparatorChar)) : $"{paths.Count} 个文件",
                    CreatedAt = DateTimeOffset.Now
                });
        }
        if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
        {
            var content = Clipboard.GetText(TextDataFormat.UnicodeText);
            if (!string.IsNullOrWhiteSpace(content))
                return new ClipboardCapture(new ClipItem
                {
                    Id = Guid.NewGuid(), Kind = ClipKind.Text, Text = content,
                    Title = content.Trim(), CreatedAt = DateTimeOffset.Now
                });
        }
        var png = NativeClipboard.ReadBytes(_handle, NativeClipboard.PngFormat);
        if (png is not null)
        {
            return new ClipboardCapture(NewImageItem("剪贴板图片"), Png: png);
        }
        try
        {
            var dibImage = NativeClipboard.ReadDibImage(_handle);
            if (dibImage is not null) return new ClipboardCapture(NewImageItem("剪贴板图片"), Bitmap: dibImage);
        }
        catch (Exception exception) when (exception is NotSupportedException or ArgumentException or IOException)
        {
            // Unusual legacy DIB variants can still be handled by WPF's clipboard conversion.
            RecordDiagnostic("ReadDibImageFallback", exception);
        }
        if (Clipboard.ContainsImage())
        {
            var image = Clipboard.GetImage();
            if (image is not null)
            {
                image.Freeze();
                return new ClipboardCapture(NewImageItem("剪贴板图片"), Bitmap: image);
            }
        }
        return null;
    }

    private static ClipItem NewImageItem(string title, string? sourcePath = null) => new()
    {
        Id = Guid.NewGuid(), Kind = ClipKind.Image, Title = title,
        SourcePath = sourcePath, CreatedAt = DateTimeOffset.Now
    };

    private async Task StoreCapturedImageAsync(ClipboardCapture capture)
    {
        var token = _lifetime.Token;
        var generation = _captureGeneration;
        var entered = false;
        try
        {
            // Bound image encoding concurrency while allowing new text/file clipboard events through.
            await _imagePreparation.WaitAsync(token);
            entered = true;
            if (generation != _captureGeneration || !_store.Settings.HistoryEnabled) return;
            var png = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                if (capture.Png is { } original)
                {
                    NativeClipboard.DecodeImage(original);
                    return original; // Preserve the original PNG bytes and transparency.
                }
                return NativeClipboard.EncodePng(capture.Bitmap!);
            }, token);
            if (generation != _captureGeneration || !_store.Settings.HistoryEnabled) return;
            await PersistImageAsync(capture.Item, png, token);
            if (_disposed || !_store.Settings.HistoryEnabled || generation != _captureGeneration)
            {
                DeleteUncommittedImage(capture.Item);
                return;
            }
            _store.Add(capture.Item);
        }
        catch (OperationCanceledException) { DeleteUncommittedImage(capture.Item); }
        catch (Exception exception) when (IsClipboardException(exception))
        {
            RecordDiagnostic("StoreCapturedImage", exception);
            DeleteUncommittedImage(capture.Item);
            if (!_disposed) SetStatus("这条剪贴板图片未能保存。");
        }
        finally { if (entered) _imagePreparation.Release(); }
    }

    private async Task PersistImageAsync(ClipItem item, byte[] png, CancellationToken token)
    {
        item.ImagePath = _store.ImagePathFor(item.Id);
        var ownedPath = item.ImagePath;
        await Task.Run(async () =>
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(ownedPath)!);
            await File.WriteAllBytesAsync(ownedPath, png, token).ConfigureAwait(false);
        }, token);
    }

    /// <summary>Call before clearing history so already-captured background images cannot reappear.</summary>
    public void CancelPendingCaptures() => _captureGeneration++;

    private void DeleteUncommittedImage(ClipItem? item)
    {
        if (item?.ImagePath is not { } imagePath) return;
        // Only ever remove the exact app-owned image allocated for this uncommitted item.
        if (!string.Equals(imagePath, _store.ImagePathFor(item.Id), StringComparison.OrdinalIgnoreCase)) return;
        try { File.Delete(imagePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public async Task<bool> CopyAsync(IReadOnlyList<ClipItem> items)
    {
        if (!_window.Dispatcher.CheckAccess())
            return await _window.Dispatcher.InvokeAsync(() => CopyAsync(items)).Task.Unwrap();
        if (_disposed || items.Count == 0) return false;
        // Snapshot mutable history models on the UI thread, then prepare large payloads off-thread.
        var snapshot = items.Select(item => new ClipItem
        {
            Kind = item.Kind, Title = item.Title, Text = item.Text,
            FilePaths = new List<string>(item.FilePaths), IsDirectory = item.IsDirectory, ImagePath = item.ImagePath, SourcePath = item.SourcePath
        }).ToArray();
        return await CopyPreparedAsync(token => Task.Run(() => BuildClipboardPayload(snapshot), token), snapshot.Length);
    }

    private async Task<bool> CopyPreparedAsync(Func<CancellationToken, Task<Dictionary<uint, byte[]>>> prepare, int count, CancellationToken requestToken = default)
    {
        if (_disposed) return false;
        using var linkedCancellation = requestToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, requestToken) : null;
        var token = linkedCancellation?.Token ?? _lifetime.Token;
        var entered = false;
        try
        {
            // Keep rapid copy requests ordered even when one image takes longer to decode than another.
            await _copyPreparation.WaitAsync(token);
            entered = true;
            var payload = await prepare(token);
            token.ThrowIfCancellationRequested();
            return await WriteClipboardPayloadAsync(payload, count, token);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception exception) when (IsClipboardException(exception))
        {
            if (!_disposed) SetStatus("内容已不可用，可能是原文件被移动或删除。");
            return false;
        }
        finally { if (entered) _copyPreparation.Release(); }
    }

    private async Task<bool> WriteClipboardPayloadAsync(Dictionary<uint, byte[]> payload, int count, CancellationToken token)
    {
        _window.Dispatcher.VerifyAccess();
        for (var attempt = 0; attempt < 6 && !_disposed; attempt++)
        {
            try
            {
                _writingClipboard = true;
                NativeClipboard.Write(_handle, payload);
                _lastClipboardSequence = NativeMethods.GetClipboardSequenceNumber();
                _queuedClipboardSequence = _lastClipboardSequence;
                SetStatus(count == 1 ? "已复制" : $"已复制 {count} 项");
                return true;
            }
            catch (Exception exception) when (IsClipboardException(exception)) { }
            finally { _writingClipboard = false; }
            try { await Task.Delay(40 + attempt * 40, token); }
            catch (OperationCanceledException) { return false; }
        }
        SetStatus("剪贴板正在被其他应用使用，请稍后再试。");
        return false;
    }

    private static Dictionary<uint, byte[]> BuildClipboardPayload(IReadOnlyList<ClipItem> items)
    {
        if (items.Count == 1)
        {
            var item = items[0];
            if (item.Kind == ClipKind.Image)
            {
                if (string.IsNullOrEmpty(item.ImagePath)) throw new FileNotFoundException();
                var png = File.ReadAllBytes(item.ImagePath);
                return NativeClipboard.Image(png, NativeClipboard.DecodeImage(png));
            }
            if (item.Kind == ClipKind.File)
            {
                var paths = ExistingFilePaths(item).ToArray();
                if (paths.Length == 0) throw new FileNotFoundException();
                return NativeClipboard.Files(paths);
            }
            return NativeClipboard.Text(item.Text ?? item.Title);
        }
        if (items.All(item => item.Kind != ClipKind.Text))
        {
            var paths = items.SelectMany(ExistingFilePaths).ToArray();
            if (items.Any(item => !ExistingFilePaths(item).Any())) throw new FileNotFoundException();
            return NativeClipboard.Files(paths);
        }
        return NativeClipboard.Text(string.Join(Environment.NewLine, items.Select(item => item.Kind switch
        {
            ClipKind.Text => item.Text ?? item.Title,
            ClipKind.File => string.Join(Environment.NewLine, item.FilePaths),
            _ => item.SourcePath ?? item.ImagePath ?? item.Title
        })));
    }

    private static IEnumerable<string> ExistingFilePaths(ClipItem item)
    {
        if (item.Kind == ClipKind.File)
            return item.FilePaths.Where(path => File.Exists(path) || Directory.Exists(path));
        if (item.Kind == ClipKind.Image)
        {
            var path = File.Exists(item.SourcePath) ? item.SourcePath : item.ImagePath;
            if (path is not null && File.Exists(path)) return [path];
        }
        return [];
    }

    public bool RegisterHotKey(string shortcut)
    {
        if (_disposed) return false;
        if (!TryParseHotKey(shortcut, out var modifiers, out var key))
        {
            LastHotKeyError = "快捷键需要 Ctrl、Alt 或 Win 加一个普通按键。";
            SetStatus(LastHotKeyError);
            return false;
        }
        if (_registeredHotKey && modifiers == _hotKeyModifiers && key == _hotKeyVirtualKey)
        {
            LastHotKeyError = null;
            return true;
        }
        // Register a candidate under a second ID, retaining the working shortcut if this one conflicts.
        var candidateId = _registeredHotKey ? (_hotKeyId == 0xC1A ? 0xC1B : 0xC1A) : _hotKeyId;
        if (!NativeMethods.RegisterHotKey(_handle, candidateId, modifiers | 0x4000, key))
        {
            LastHotKeyError = $"快捷键 {shortcut} 已被其他应用占用，请换一个组合。";
            SetStatus(LastHotKeyError);
            return false;
        }
        if (_registeredHotKey) NativeMethods.UnregisterHotKey(_handle, _hotKeyId);
        _hotKeyId = candidateId;
        _hotKeyModifiers = modifiers;
        _hotKeyVirtualKey = key;
        _registeredHotKey = true;
        LastHotKeyError = null;
        return true;
    }

    public void SuspendHotKey()
    {
        if (_registeredHotKey) NativeMethods.UnregisterHotKey(_handle, _hotKeyId);
        _registeredHotKey = false;
    }

    private static bool TryParseHotKey(string? shortcut, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0; virtualKey = 0;
        if (string.IsNullOrWhiteSpace(shortcut)) return false;
        var parts = shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL": case "CONTROL": modifiers |= 2; continue;
                case "ALT": modifiers |= 1; continue;
                case "SHIFT": modifiers |= 4; continue;
                case "WIN": case "WINDOWS": modifiers |= 8; continue;
            }
            if (virtualKey != 0) return false;
            try
            {
                var name = part.Equals("Esc", StringComparison.OrdinalIgnoreCase) ? "Escape" : part;
                var key = (Key)new KeyConverter().ConvertFromInvariantString(name)!;
                virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
            }
            catch (Exception exception) when (exception is NotSupportedException or FormatException or ArgumentException) { return false; }
        }
        return virtualKey != 0 && (modifiers & (1 | 2 | 8)) != 0;
    }

    public void ApplySettings()
    {
        if (_disposed) return;
        RegisterHotKey(_store.Settings.GlobalHotKey);
        ApplyScreenshotWatcher();
        if (_manageStartup) SetLaunchAtLogin(_store.Settings.LaunchAtLogin);
    }

    public void SetLaunchAtLogin(bool enabled)
    {
        if (_appliedLaunchAtLogin == enabled) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)
                ?? (enabled ? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run") : null);
            if (enabled)
            {
                var executable = Environment.ProcessPath;
                if (string.IsNullOrEmpty(executable) || !string.Equals(Path.GetFileNameWithoutExtension(executable), "ClipShelf", StringComparison.OrdinalIgnoreCase))
                {
                    SetStatus("请从 ClipShelf.exe 启动后再设置开机启动。");
                    return;
                }
                key?.SetValue("ClipShelf", $"\"{executable}\" --background", RegistryValueKind.String);
            }
            else key?.DeleteValue("ClipShelf", false);
            _appliedLaunchAtLogin = enabled;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            SetStatus("无法更改开机启动设置，请检查 Windows 权限。");
        }
    }

    public static string DefaultScreenshotFolder
    {
        get
        {
            var folderId = new Guid("B7BEDE81-DF94-4682-A7D8-57A52620B86F");
            if (NativeMethods.SHGetKnownFolderPath(ref folderId, 0, 0, out var pointer) == 0)
            {
                try { return Marshal.PtrToStringUni(pointer) ?? FallbackScreenshotFolder; }
                finally { Marshal.FreeCoTaskMem(pointer); }
            }
            return FallbackScreenshotFolder;
        }
    }

    private static string FallbackScreenshotFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");

    private void ApplyScreenshotWatcher()
    {
        if (!_store.Settings.WatchScreenshots && _screenshotWatcher is null)
        {
            ScreenshotStatus = "截图监听已暂停";
            return;
        }
        var configured = _store.Settings.ScreenshotFolder;
        var folder = string.IsNullOrWhiteSpace(configured) ? DefaultScreenshotFolder : configured;
        try { folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            StopScreenshotWatcher();
            ScreenshotStatus = "截图文件夹路径无效，请重新选择";
            SetStatus(ScreenshotStatus);
            return;
        }
        if (_store.Settings.WatchScreenshots && _screenshotWatcher is not null && string.Equals(folder, _watcherFolder, StringComparison.OrdinalIgnoreCase)) return;
        StopScreenshotWatcher();
        if (!_store.Settings.WatchScreenshots)
        {
            ScreenshotStatus = "截图监听已暂停";
            return;
        }
        if (!Directory.Exists(folder))
        {
            ScreenshotStatus = "截图文件夹不存在，请在设置中选择文件夹";
            SetStatus(ScreenshotStatus);
            return;
        }
        try
        {
            _watcherFolder = Path.GetFullPath(folder);
            foreach (var path in Directory.EnumerateFiles(_watcherFolder)) _knownScreenshotPaths.Add(path);
            var generation = _watcherGeneration;
            var watcher = new FileSystemWatcher(_watcherFolder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = 32768
            };
            watcher.Created += (_, args) => QueueScreenshotAction(generation, () => ScheduleScreenshot(args.FullPath));
            watcher.Changed += (_, args) => QueueScreenshotAction(generation, () =>
            {
                if (_pendingScreenshots.ContainsKey(args.FullPath)) ScheduleScreenshot(args.FullPath);
            });
            watcher.Renamed += (_, args) => QueueScreenshotAction(generation, () =>
            {
                if (_knownScreenshotPaths.Remove(args.OldFullPath)) _knownScreenshotPaths.Add(args.FullPath);
                else ScheduleScreenshot(args.FullPath);
            });
            watcher.Error += (_, _) => QueueScreenshotAction(generation, () =>
            {
                ScreenshotStatus = "截图监听出现错误，请重新选择文件夹";
                SetStatus(ScreenshotStatus);
                StopScreenshotWatcher();
            });
            _screenshotWatcher = watcher;
            watcher.EnableRaisingEvents = true;
            ScreenshotStatus = $"正在监听：{Path.GetFileName(_watcherFolder.TrimEnd(Path.DirectorySeparatorChar))}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StopScreenshotWatcher();
            ScreenshotStatus = "无法监听截图文件夹，请重新选择";
            SetStatus(ScreenshotStatus);
        }
    }

    private void QueueScreenshotAction(int generation, Action action)
    {
        if (_disposed || _window.Dispatcher.HasShutdownStarted) return;
        _window.Dispatcher.BeginInvoke(() =>
        {
            if (!_disposed && generation == _watcherGeneration) action();
        });
    }

    private void ScheduleScreenshot(string path)
    {
        if (_knownScreenshotPaths.Contains(path) || !IsImagePath(path)) return;
        if (_pendingScreenshots.Remove(path, out var oldToken)) { oldToken.Cancel(); oldToken.Dispose(); }
        var token = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _pendingScreenshots[path] = token;
        _ = ImportScreenshotAsync(path, token);
    }

    private static bool IsImagePath(string path) => Path.GetExtension(path).ToLowerInvariant()
        is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp" or ".tif" or ".tiff";

    private async Task ImportScreenshotAsync(string path, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        var generation = _captureGeneration;
        ClipItem? uncommitted = null;
        try
        {
            long previousLength = -1;
            DateTime previousWrite = DateTime.MinValue;
            for (var attempt = 0; attempt < 16; attempt++)
            {
                await Task.Delay(attempt == 0 ? 300 : 250, token);
                try
                {
                    var fileState = await Task.Run(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        var info = new FileInfo(path);
                        return info.Exists ? (Length: info.Length, Write: info.LastWriteTimeUtc) : (Length: 0L, Write: DateTime.MinValue);
                    }, token);
                    if (fileState.Length == 0) continue;
                    if (fileState.Length != previousLength || fileState.Write != previousWrite)
                    {
                        previousLength = fileState.Length; previousWrite = fileState.Write;
                        continue;
                    }
                    // A successful decoder load also confirms the file is complete enough to import.
                    var bytes = await File.ReadAllBytesAsync(path, token);
                    (byte[] Png, Dictionary<uint, byte[]> Payload) prepared;
                    await _imagePreparation.WaitAsync(token);
                    try
                    {
                        prepared = await Task.Run(() =>
                        {
                            token.ThrowIfCancellationRequested();
                            var image = NativeClipboard.DecodeImage(bytes);
                            var png = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? bytes : NativeClipboard.EncodePng(image);
                            token.ThrowIfCancellationRequested();
                            return (png, NativeClipboard.Image(png, image));
                        }, token);
                    }
                    finally { _imagePreparation.Release(); }
                    token.ThrowIfCancellationRequested();
                    if (generation != _captureGeneration) return;
                    var item = NewImageItem(Path.GetFileName(path), path);
                    uncommitted = item;
                    await PersistImageAsync(item, prepared.Png, token);
                    token.ThrowIfCancellationRequested();
                    if (_disposed || generation != _captureGeneration) return;
                    try { _store.Add(item); }
                    catch { DeleteUncommittedImage(item); throw; }
                    uncommitted = null;
                    _knownScreenshotPaths.Add(path);
                    // Reuse this decoded image's formats instead of rereading and decoding the new cache file.
                    if (await CopyPreparedAsync(_ =>
                    {
                        if (generation != _captureGeneration) throw new OperationCanceledException();
                        return Task.FromResult(prepared.Payload);
                    }, 1, token)) SetStatus("新截图已收录并复制");
                    return;
                }
                catch (Exception exception) when (IsClipboardException(exception))
                {
                    DeleteUncommittedImage(uncommitted);
                    uncommitted = null;
                }
            }
            SetStatus("一张新截图暂时无法读取，请确认图片已保存完成。");
        }
        catch (OperationCanceledException) { }
        finally
        {
            DeleteUncommittedImage(uncommitted);
            if (_pendingScreenshots.TryGetValue(path, out var current) && current == cancellation)
                _pendingScreenshots.Remove(path);
            cancellation.Dispose();
        }
    }

    private void StopScreenshotWatcher()
    {
        _watcherGeneration++;
        _screenshotWatcher?.Dispose();
        _screenshotWatcher = null;
        _watcherFolder = null;
        foreach (var cancellation in _pendingScreenshots.Values) cancellation.Cancel();
        _pendingScreenshots.Clear();
        _knownScreenshotPaths.Clear();
    }

    private static bool IsClipboardException(Exception exception) => exception is
        ExternalException or IOException or UnauthorizedAccessException or InvalidOperationException or
        NotSupportedException or ArgumentException or Win32Exception;

    private void SetStatus(string text)
    {
        StatusText = text;
        Status?.Invoke(text);
    }

    private void RecordDiagnostic(string operation, Exception exception)
    {
        // Tests may opt in to failure diagnostics. Never include exception messages or clipboard payloads.
        Diagnostic?.Invoke(operation + ": " + exception.GetType().FullName + " (0x" + exception.HResult.ToString("X8") + ")\n" + exception.StackTrace);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        StopScreenshotWatcher();
        if (_registeredHotKey) NativeMethods.UnregisterHotKey(_handle, _hotKeyId);
        NativeMethods.RemoveClipboardFormatListener(_handle);
        _source.RemoveHook(WindowMessage);
        _lifetime.Dispose();
    }
}
