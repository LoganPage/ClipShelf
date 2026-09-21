using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ClipShelf;

public sealed class HistoryStore
{
    private readonly List<ClipItem> items = [];
    private readonly ReadOnlyCollection<ClipItem> itemView;
    private readonly List<List<ClipItem>> deleteUndo = [];
    private const int MaxDeleteUndoBatches = 10;
    private readonly Dictionary<string, (long Length, DateTime Modified, string Hash)> imageHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool deferredPersistence;
    private readonly object writeGate = new();
    private HistoryWrite? pendingHistory;
    private AppSettings? pendingSettings;
    private bool writerRunning;
    private Task<bool> writerTask = Task.FromResult(true);
    private string? historyError;
    private string? settingsError;
    private string? recoveryError;

    // Only private, deep-copied snapshots cross into the writer. This flag survives coalescing:
    // Clear followed immediately by Add may share a write, but its backup must not resurrect old history.
    private sealed record HistoryWrite(List<ClipItem> Items, bool ResetBackup);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string DirectoryPath { get; }
    public string HistoryPath { get; }
    public string SettingsPath { get; }
    public AppSettings Settings { get; }
    public IReadOnlyList<ClipItem> Items => itemView;
    public bool CanUndoDelete => deleteUndo.Count > 0;
    /// <summary>Number of deletion transactions available in this session, newest first on undo.</summary>
    public int UndoDeleteCount => deleteUndo.Count;
    public string? LastError => Volatile.Read(ref historyError) ?? Volatile.Read(ref settingsError) ?? Volatile.Read(ref recoveryError);
    public event Action? Changed;
    /// <summary>Raised on the writing thread; UI subscribers must dispatch to their UI thread.</summary>
    public event Action<string>? PersistenceFailed;

    public HistoryStore(string? directory = null, bool deferredPersistence = false)
    {
        this.deferredPersistence = deferredPersistence;
        itemView = items.AsReadOnly();
        DirectoryPath = Path.GetFullPath(directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipShelf"));
        HistoryPath = Path.Combine(DirectoryPath, "history.json");
        SettingsPath = Path.Combine(DirectoryPath, "settings.json");
        Directory.CreateDirectory(DirectoryPath);
        Directory.CreateDirectory(Path.Combine(DirectoryPath, "images"));
        Settings = ReadWithRecovery<AppSettings>(SettingsPath) ?? new AppSettings();
        Settings.MaxItems = Math.Clamp(Settings.MaxItems, 1, 10000);
        Settings.ClickRecoveryMilliseconds = Math.Clamp(Settings.ClickRecoveryMilliseconds, 0, 1000);
        Settings.WindowWidth = double.IsFinite(Settings.WindowWidth) ? Math.Clamp(Settings.WindowWidth, 480, 4000) : 720;
        Settings.WindowHeight = double.IsFinite(Settings.WindowHeight) ? Math.Clamp(Settings.WindowHeight, 320, 3000) : 540;
        foreach (var item in ReadWithRecovery<List<ClipItem>>(HistoryPath) ?? [])
        {
            if (item is null || !Enum.IsDefined(item.Kind)) continue;
            item.Title ??= "";
            item.FilePaths ??= [];
            item.FilePaths.RemoveAll(string.IsNullOrWhiteSpace);
            if (item.Id == Guid.Empty || items.Any(x => x.Id == item.Id)) item.Id = Guid.NewGuid();
            if (item.Kind == ClipKind.Text && string.IsNullOrWhiteSpace(item.Text ?? item.Title)) continue;
            items.Add(item);
        }
        SortAndTrim();
        // At startup there are no live clipboard imports or queued snapshots to race with cleanup.
        if (deferredPersistence) CleanupImages(items);
    }

    public string ImagePathFor(Guid id) => Path.Combine(DirectoryPath, "images", id.ToString("N") + ".png");

    // One clipboard transaction, one sort/save/notification, not one full refresh per file.
    public void AddFileBatch(ClipItem capture)
    {
        var paths = capture.FilePaths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).Take(10000).ToArray();
        if (paths.Length == 0) return;
        var existing = items.Where(i => i.Kind == ClipKind.File && i.FilePaths.Count == 1)
            .GroupBy(i => i.FilePaths[0], StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        var remove = new HashSet<Guid>(); var incoming = new List<ClipItem>(paths.Length);
        for (int i = 0; i < paths.Length; i++)
        {
            existing.TryGetValue(paths[i], out var prior);
            if (prior is not null) foreach (var duplicate in prior) remove.Add(duplicate.Id);
            incoming.Add(new ClipItem {
                Id = prior?.FirstOrDefault()?.Id ?? Guid.NewGuid(), Kind = ClipKind.File,
                FilePaths = new() { paths[i] }, Title = Path.GetFileName(paths[i].TrimEnd(Path.DirectorySeparatorChar)),
                IsDirectory = Directory.Exists(paths[i]), IsPinned = capture.IsPinned || prior?.Any(p => p.IsPinned) == true,
                CreatedAt = capture.CreatedAt.AddTicks(-Math.Min(i, (capture.CreatedAt - DateTimeOffset.MinValue).Ticks))
            });
        }
        items.RemoveAll(i => remove.Contains(i.Id)); items.AddRange(incoming);
        SortAndTrim(); Save(); Changed?.Invoke();
    }

    public void Add(ClipItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Kind == ClipKind.Text && string.IsNullOrWhiteSpace(item.Text ?? item.Title)) return;
        item.Title ??= "";
        item.FilePaths ??= [];
        if (item.Id == Guid.Empty) item.Id = Guid.NewGuid();
        var duplicates = items.Where(existing => IsDuplicate(existing, item)).ToList();
        if (duplicates.Count > 0)
        {
            item.Id = duplicates[0].Id;
            item.IsPinned |= duplicates.Any(existing => existing.IsPinned);
            item.SourcePath ??= duplicates[0].SourcePath;
            items.RemoveAll(existing => duplicates.Contains(existing));
        }
        else if (items.Any(existing => existing.Id == item.Id)) item.Id = Guid.NewGuid();
        items.Add(item);
        SortAndTrim();
        Save();
        Changed?.Invoke();
    }

    public void Remove(IEnumerable<Guid> ids)
    {
        var selected = ids.ToHashSet();
        var deleted = CopyItems(items.Where(item => selected.Contains(item.Id)));
        if (deleted.Count == 0) return;
        // Independent copies retain the deleted value even if another caller still holds its old object.
        deleteUndo.Add(deleted);
        if (deleteUndo.Count > MaxDeleteUndoBatches) deleteUndo.RemoveAt(0);
        items.RemoveAll(item => selected.Contains(item.Id));
        Save();
        Changed?.Invoke();
    }

    /// <summary>
    /// Consumes the most recent deletion transaction. Existing records always win ID/content conflicts,
    /// and undo never evicts a current record to make room. Returns the number actually restored.
    /// </summary>
    public int UndoDelete()
    {
        if (deleteUndo.Count == 0) return 0;
        var deleted = deleteUndo[^1];
        deleteUndo.RemoveAt(deleteUndo.Count - 1);
        int remaining = Math.Max(0, Settings.MaxItems - items.Count);
        int restored = 0;
        foreach (var candidate in deleted.OrderByDescending(item => item.IsPinned).ThenByDescending(item => item.CreatedAt))
        {
            if (remaining == 0) break;
            if (items.Any(current => current.Id == candidate.Id || IsDuplicate(current, candidate))) continue;
            items.Add(candidate);
            remaining--;
            restored++;
        }
        if (restored > 0)
        {
            SortAndTrim();
            Save();
        }
        // Even an entirely conflicted/capacity-limited batch changes the availability of undo.
        Changed?.Invoke();
        return restored;
    }

    public void TogglePinned(IEnumerable<Guid> ids)
    {
        var selected = ids.ToHashSet();
        var targets = items.Where(item => selected.Contains(item.Id)).ToList();
        if (targets.Count == 0) return;
        bool pinned = targets.Any(item => !item.IsPinned);
        foreach (var item in targets) item.IsPinned = pinned;
        SortAndTrim();
        Save();
        Changed?.Invoke();
    }

    public void Clear()
    {
        items.Clear();
        deleteUndo.Clear();
        SaveHistory(resetBackup: true);
        Changed?.Invoke();
    }

    public void SaveSettings()
    {
        Settings.MaxItems = Math.Clamp(Settings.MaxItems, 1, 10000);
        if (!deferredPersistence)
        {
            Persist(SettingsPath, Settings);
            return;
        }
        var snapshot = CopySettings(Settings);
        lock (writeGate)
        {
            pendingSettings = snapshot;
            StartWriterLocked();
        }
    }

    public void Save()
    {
        SaveHistory(resetBackup: false);
    }

    private void SaveHistory(bool resetBackup)
    {
        if (!deferredPersistence)
        {
            if (PersistHistory(new HistoryWrite(items, resetBackup))) CleanupImages(items);
            return;
        }
        var snapshot = CopyItems(items);
        lock (writeGate)
        {
            pendingHistory = new HistoryWrite(snapshot, resetBackup || pendingHistory?.ResetBackup == true);
            StartWriterLocked();
        }
    }

    /// <summary>
    /// Waits for queued writes and cleans unused image cache files. Call on the owning thread after
    /// stopping clipboard/screenshot collection during shutdown; no UI-thread callbacks are awaited.
    /// Failed snapshots remain queued so a subsequent Flush can retry them.
    /// </summary>
    public bool Flush()
    {
        bool saved = FlushAsync().GetAwaiter().GetResult();
        if (saved) CleanupImages(items);
        return saved;
    }

    /// <summary>
    /// Asynchronously drains the ordered writer. Image deletion is deliberately left to synchronous
    /// Flush/startup, where it cannot race a newly allocated image or a future pending snapshot.
    /// </summary>
    public async Task<bool> FlushAsync()
    {
        if (!deferredPersistence) return !HasPersistenceError;
        while (true)
        {
            Task<bool> current;
            lock (writeGate)
            {
                StartWriterLocked();
                if (!writerRunning) return !HasPersistenceError;
                current = writerTask;
            }
            if (!await current.ConfigureAwait(false)) return false;
            lock (writeGate)
                if (!writerRunning && pendingHistory is null && pendingSettings is null) return !HasPersistenceError;
        }
    }

    private bool HasPersistenceError => Volatile.Read(ref historyError) is not null || Volatile.Read(ref settingsError) is not null;

    private void StartWriterLocked()
    {
        if (writerRunning || (pendingHistory is null && pendingSettings is null)) return;
        writerRunning = true;
        writerTask = Task.Run(DrainWritesAsync);
    }

    private async Task<bool> DrainWritesAsync()
    {
        // A short bounded window merges a burst of selection/pin/clipboard changes into one snapshot.
        await Task.Delay(50).ConfigureAwait(false);
        while (true)
        {
            HistoryWrite? history;
            AppSettings? settings;
            lock (writeGate)
            {
                history = pendingHistory;
                settings = pendingSettings;
                pendingHistory = null;
                pendingSettings = null;
                if (history is null && settings is null)
                {
                    writerRunning = false;
                    return true;
                }
            }

            bool historySaved = history is null;
            bool settingsSaved = settings is null;
            try
            {
                if (history is not null) historySaved = PersistHistory(history);
                if (settings is not null) settingsSaved = Persist(SettingsPath, settings);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A malformed setting or unexpected filesystem error must not strand the queue.
                if (!historySaved) RecordPersistenceResult(HistoryPath, exception.Message);
                if (!settingsSaved) RecordPersistenceResult(SettingsPath, exception.Message);
            }
            if (historySaved && settingsSaved) continue;

            lock (writeGate)
            {
                if (!historySaved && history is not null)
                {
                    // A newer queued snapshot supersedes the failed data, but not a pending clear barrier.
                    pendingHistory = pendingHistory is null ? history
                        : pendingHistory with { ResetBackup = pendingHistory.ResetBackup || history.ResetBackup };
                }
                if (!settingsSaved && pendingSettings is null) pendingSettings = settings;
                writerRunning = false;
            }
            // Do not spin or repeatedly hit a full/locked disk. A new change or explicit Flush retries.
            return false;
        }
    }

    private bool PersistHistory(HistoryWrite history)
    {
        if (!Persist(HistoryPath, history.Items)) return false;
        return !history.ResetBackup || Persist(HistoryPath, history.Items);
    }

    private static List<ClipItem> CopyItems(IEnumerable<ClipItem> source) => source.Select(item => new ClipItem
    {
        Id = item.Id,
        Kind = item.Kind,
        Title = item.Title,
        Text = item.Text,
        FilePaths = new List<string>(item.FilePaths),
        IsDirectory = item.IsDirectory,
        ImagePath = item.ImagePath,
        SourcePath = item.SourcePath,
        CreatedAt = item.CreatedAt,
        IsPinned = item.IsPinned
    }).ToList();

    private static AppSettings CopySettings(AppSettings source) => new()
    {
        HistoryEnabled = source.HistoryEnabled,
        WatchScreenshots = source.WatchScreenshots,
        ScreenshotFolder = source.ScreenshotFolder,
        GlobalHotKey = source.GlobalHotKey,
        ClearSelectionHotKey = source.ClearSelectionHotKey,
        PinHotKey = source.PinHotKey,
        Theme = source.Theme,
        SelectionPreset = source.SelectionPreset,
        SelectionColor = source.SelectionColor,
        AppIcon = source.AppIcon,
        ClickBehavior = source.ClickBehavior,
        DeselectOnRepeatedClick = source.DeselectOnRepeatedClick,
        SwitchToClickedRecord = source.SwitchToClickedRecord,
        MultiSelectedClick = source.MultiSelectedClick,
        MultiUnselectedClick = source.MultiUnselectedClick,
        ClickRecoveryMilliseconds = source.ClickRecoveryMilliseconds,
        MaxItems = source.MaxItems,
        LaunchAtLogin = source.LaunchAtLogin,
        CloseToTray = source.CloseToTray,
        WindowWidth = source.WindowWidth,
        WindowHeight = source.WindowHeight
    };

    private void SortAndTrim()
    {
        items.Sort((left, right) => left.IsPinned != right.IsPinned
            ? right.IsPinned.CompareTo(left.IsPinned) : right.CreatedAt.CompareTo(left.CreatedAt));
        while (items.Count > Settings.MaxItems)
        {
            int unpinned = items.FindLastIndex(item => !item.IsPinned);
            items.RemoveAt(unpinned >= 0 ? unpinned : items.Count - 1);
        }
    }

    private bool IsDuplicate(ClipItem left, ClipItem right)
    {
        if (left.Kind != right.Kind) return false;
        return right.Kind switch
        {
            ClipKind.Text => string.Equals(left.Text ?? left.Title, right.Text ?? right.Title, StringComparison.Ordinal),
            ClipKind.File => left.FilePaths.SequenceEqual(right.FilePaths, StringComparer.OrdinalIgnoreCase),
            ClipKind.Image => !string.IsNullOrWhiteSpace(left.SourcePath) && !string.IsNullOrWhiteSpace(right.SourcePath)
                ? string.Equals(left.SourcePath, right.SourcePath, StringComparison.OrdinalIgnoreCase)
                : ImagesEqual(left.ImagePath, right.ImagePath),
            _ => false
        };
    }

    private bool ImagesEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return true;
        var leftHash = ImageHash(left);
        return leftHash is not null && leftHash == ImageHash(right);
    }

    private string? ImageHash(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (imageHashes.TryGetValue(path, out var cached) && cached.Length == info.Length && cached.Modified == info.LastWriteTimeUtc)
                return cached.Hash;
            using var stream = info.OpenRead();
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            imageHashes[path] = (info.Length, info.LastWriteTimeUtc, hash);
            return hash;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private T? ReadWithRecovery<T>(string path) where T : class
    {
        if (TryRead<T>(path, out var current)) return current;
        bool hasBackup = TryRead<T>(path + ".bak", out var backup);
        if (File.Exists(path))
        {
            try
            {
                // Retain damaged input for diagnosis; do not replace a valid backup with it.
                File.Move(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
                recoveryError = hasBackup ? "记录文件损坏，已从备份恢复。" : "记录文件损坏，已保留原文件。";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { recoveryError = ex.Message; }
        }
        if (hasBackup)
        {
            try { if (!File.Exists(path)) File.Copy(path + ".bak", path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { recoveryError = ex.Message; }
        }
        return backup;
    }

    private static bool TryRead<T>(string path, out T? value) where T : class
    {
        value = null;
        try
        {
            if (!File.Exists(path)) return false;
            using var stream = File.OpenRead(path);
            value = JsonSerializer.Deserialize<T>(stream, JsonOptions);
            return value is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        { return false; }
    }

    private bool Persist<T>(string path, T value)
    {
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: true);
            else
            {
                File.Move(temporary, path);
                if (!File.Exists(path + ".bak")) File.Copy(path, path + ".bak");
            }
            RecordPersistenceResult(path, null);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            RecordPersistenceResult(path, ex.Message);
            return false;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Trace.WriteLine(ex.Message); }
        }
    }

    private void RecordPersistenceResult(string path, string? error)
    {
        string? message = error is null ? null : $"无法保存 ClipShelf 数据：{error}";
        if (path == HistoryPath) Volatile.Write(ref historyError, message);
        else Volatile.Write(ref settingsError, message);
        if (message is null) Volatile.Write(ref recoveryError, null);
        else
        {
            Trace.WriteLine(message);
            try { PersistenceFailed?.Invoke(message); }
            catch (Exception exception) { Trace.WriteLine(exception.Message); }
        }
    }

    private void CleanupImages(IEnumerable<ClipItem> liveItems)
    {
        // Originals are never deleted. Only app-named cached PNGs directly inside images are eligible.
        try
        {
            string imagesDirectory = Path.GetFullPath(Path.Combine(DirectoryPath, "images"));
            if (!Directory.Exists(imagesDirectory) || (File.GetAttributes(imagesDirectory) & FileAttributes.ReparsePoint) != 0) return;
            if (File.Exists(HistoryPath + ".bak") && !TryRead<List<ClipItem>>(HistoryPath + ".bak", out _)) return;
            TryRead<List<ClipItem>>(HistoryPath + ".bak", out var backupItems);
            var referenced = liveItems.Concat(backupItems ?? []).Concat(deleteUndo.SelectMany(batch => batch))
                .Where(item => item is not null)
                .SelectMany(item => new[] { item.ImagePath, item.SourcePath })
                .Where(path => !string.IsNullOrWhiteSpace(path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.EnumerateFiles(imagesDirectory, "*.png", SearchOption.TopDirectoryOnly))
            {
                string exactPath = Path.GetFullPath(path);
                if (!string.Equals(Path.GetDirectoryName(exactPath), imagesDirectory, StringComparison.OrdinalIgnoreCase)
                    || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(exactPath), "N", out _)
                    || referenced.Contains(exactPath) || (File.GetAttributes(exactPath) & FileAttributes.ReparsePoint) != 0) continue;
                File.Delete(exactPath);
                imageHashes.Remove(exactPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Trace.WriteLine(ex.Message); }
    }
}
