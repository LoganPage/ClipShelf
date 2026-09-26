using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ClipShelf;

public static class StorageTests
{
    public static IReadOnlyList<string> Run(string directory)
    {
        // A unique isolated child prevents tests from altering existing user history.
        string testDirectory = Path.Combine(Path.GetFullPath(directory), "storage-tests-" + Guid.NewGuid().ToString("N"));
        var results = new List<string>();
        var store = new HistoryStore(testDirectory);
        var original = new ClipItem { Text = "  café\n第二行  ", Title = "café", CreatedAt = DateTimeOffset.Now.AddMinutes(-2) };
        store.Add(original);
        Assert(store.Items.Single().Text == "  café\n第二行  ", "Original whitespace is retained", results);
        store.Add(new ClipItem { Text = " \t\r\n" });
        Assert(store.Items.Count == 1, "Empty text is ignored", results);
        store.TogglePinned([original.Id]);
        store.Add(new ClipItem { Text = original.Text, Title = original.Title });
        Assert(store.Items.Count == 1 && store.Items[0].Id == original.Id && store.Items[0].IsPinned,
            "Recopy preserves identity and pin", results);
        store.Settings.MaxItems = 3;
        for (int i = 0; i < 5; i++) store.Add(new ClipItem { Text = "value-" + i, Title = "value-" + i, CreatedAt = DateTimeOffset.Now.AddSeconds(i) });
        Assert(store.Items.Count == 3 && store.Items[0].Id == original.Id, "History cap prefers unpinned eviction", results);
        store.Settings.Theme = "Dark";
        store.SaveSettings();
        var reopened = new HistoryStore(testDirectory);
        Assert(reopened.Items.Count == 3 && reopened.Settings.Theme == "Dark" && reopened.Settings.MaxItems == 3,
            "History and settings survive restart", results);
        var adjustable = new HistoryStore(Path.Combine(testDirectory, "adjustable-limit"));
        var protectedItem = new ClipItem { Text = "Pinned limit record", Title = "Pinned limit record", IsPinned = true, CreatedAt = DateTimeOffset.Now.AddDays(-5) };
        adjustable.Add(protectedItem);
        for (int index = 0; index < 5; index++) adjustable.Add(new ClipItem { Text = "Limit record " + index, Title = "Limit record " + index, CreatedAt = DateTimeOffset.Now.AddMinutes(index) });
        int trimmed = adjustable.SetMaxItems(3);
        Assert(trimmed == 3 && adjustable.Items.Count == 3 && adjustable.Items.Any(item => item.Id == protectedItem.Id),
            "Changing the history limit immediately trims old unpinned records and preserves pinned records", results);
        Assert(new HistoryStore(adjustable.DirectoryPath).Settings.MaxItems == 3 && new HistoryStore(adjustable.DirectoryPath).Items.Count == 3,
            "Changed history limit and trimmed history persist together", results);
        var legacySettings = new HistoryStore(Path.Combine(testDirectory, "legacy-settings"));
        File.WriteAllText(legacySettings.SettingsPath, "{\"Theme\":\"Light\"}");
        Assert(new HistoryStore(legacySettings.DirectoryPath).Settings.MaxItems == 100,
            "Settings without MaxItems retain the historical default of 100", results);
        adjustable.Settings.WindowLeft = 1840;
        adjustable.Settings.WindowTop = 160;
        adjustable.SaveSettings();
        var positioned = new HistoryStore(adjustable.DirectoryPath);
        Assert(positioned.Settings.WindowLeft == 1840 && positioned.Settings.WindowTop == 160,
            "Window position survives a settings round trip", results);
        var screens = new[] { new System.Windows.Rect(0, 0, 1920, 1040), new System.Windows.Rect(1920, 0, 2560, 1400) };
        Assert(WindowPositionPolicy.IsReachable(2100, 80, 680, screens), "A reachable secondary-monitor position is restored", results);
        Assert(!WindowPositionPolicy.IsReachable(9000, 9000, 680, screens)
            && !WindowPositionPolicy.IsReachable(double.NaN, 0, 680, screens),
            "Off-screen and non-finite window positions fall back to centering", results);
        Assert(new ClipItem { Title = "Résumé ＣＡＦÉ", Text = "Project notes" }.Search("resume cafe"),
            "Search folds case, diacritics, width and tokens", results);
        Assert(new ClipItem { Title = "clipboard history" }.Search("clipbord"), "Search tolerates a typo", results);
        if (OperatingSystem.IsWindows() && PinyinTransliterator.ToLatin("剪贴板") != "剪贴板")
        {
            Assert(new ClipItem { Title = "剪贴板历史" }.Search("jiantieban"), "Chinese pinyin search", results);
            Assert(new ClipItem { Title = "剪贴板历史" }.Search("jtbls"), "Chinese pinyin initials search", results);
        }
        reopened.Settings.MaxItems = 100;
        string originalFile = Path.Combine(testDirectory, "original-user-image.png");
        File.WriteAllBytes(originalFile, [1, 2, 3, 4]);
        var image = new ClipItem { Kind = ClipKind.Image, Title = "image", SourcePath = originalFile };
        image.ImagePath = reopened.ImagePathFor(image.Id);
        File.Copy(originalFile, image.ImagePath);
        reopened.Add(image);
        var sameImage = new ClipItem { Kind = ClipKind.Image, Title = "same pixels" };
        sameImage.ImagePath = reopened.ImagePathFor(sameImage.Id);
        File.Copy(originalFile, sameImage.ImagePath);
        reopened.Add(sameImage);
        Assert(reopened.Items.Count(item => item.Kind == ClipKind.Image) == 1, "Image content deduplication", results);
        reopened.Remove([image.Id]);
        Assert(File.Exists(originalFile), "Deleting history preserves original files", results);
        reopened.Save();
        Assert(File.Exists(sameImage.ImagePath) && reopened.CanUndoDelete, "Undo retains cached images after backup rotation", results);
        reopened.Clear();
        Assert(!File.Exists(sameImage.ImagePath) && !reopened.CanUndoDelete && File.Exists(originalFile),
            "Clear releases undo image caches without deleting original files", results);
        reopened.Add(new ClipItem { Text = "recoverable", Title = "recoverable" });
        reopened.Save();
        File.WriteAllText(reopened.HistoryPath, "{ interrupted-invalid-json");
        var recovered = new HistoryStore(testDirectory);
        Assert(recovered.Items.Single().Text == "recoverable", "Corrupt history recovers from atomic backup", results);
        Assert(Directory.EnumerateFiles(testDirectory, "history.json.corrupt-*").Any(), "Corrupt input retained for recovery", results);
        recovered.Clear();
        Assert(new HistoryStore(testDirectory).Items.Count == 0, "Clear persists across restart", results);
        File.WriteAllText(recovered.HistoryPath, "invalid-after-clear");
        Assert(new HistoryStore(testDirectory).Items.Count == 0, "Clear also erases recoverable backup records", results);
        return results;
    }

    public static async Task<IReadOnlyList<string>> RunDeferredAsync(string directory)
    {
        string root = Path.Combine(Path.GetFullPath(directory), "deferred-storage-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var results = new List<string>();

        var snapshots = new HistoryStore(Path.Combine(root, "snapshots"), deferredPersistence: true);
        int changeEvents = 0;
        snapshots.Changed += () => changeEvents++;
        var file = new ClipItem { Kind = ClipKind.File, Title = "Saved title", FilePaths = ["first-file.txt"] };
        snapshots.Add(file);
        Assert(changeEvents == 1 && snapshots.Items.Single().Id == file.Id, "Deferred changes notify immediately", results);
        file.Title = "Unsaved title";
        file.FilePaths.Add("unsaved-second-file.txt");
        snapshots.Settings.Theme = "Dark";
        snapshots.Settings.WindowWidth = 888;
        snapshots.Settings.WindowLeft = 240;
        snapshots.Settings.WindowTop = 160;
        snapshots.Settings.PrewarmAdjacentPreview = true;
        snapshots.SaveSettings();
        snapshots.Settings.Theme = "Light";
        snapshots.Settings.WindowWidth = 999;
        snapshots.Settings.WindowLeft = 480;
        snapshots.Settings.WindowTop = 320;
        snapshots.Settings.PrewarmAdjacentPreview = false;
        Assert(await snapshots.FlushAsync(), "Deferred snapshots flush successfully", results);
        var snapshotReload = new HistoryStore(snapshots.DirectoryPath);
        Assert(snapshotReload.Items.Single().Title == "Saved title" && snapshotReload.Items.Single().FilePaths.SequenceEqual(["first-file.txt"]),
            "Writer uses an independent item and file-list snapshot", results);
        Assert(snapshotReload.Settings.Theme == "Dark" && snapshotReload.Settings.WindowWidth == 888
            && snapshotReload.Settings.WindowLeft == 240 && snapshotReload.Settings.WindowTop == 160,
            "Writer uses an independent settings snapshot", results);
        Assert(snapshotReload.Settings.PrewarmAdjacentPreview,
            "Adjacent preview prewarm survives deferred save and restart", results);
        snapshots.Save();
        snapshots.SaveSettings();
        Assert(snapshots.Flush(), "Synchronous exit flush waits without a dispatcher", results);
        var updatedSnapshot = new HistoryStore(snapshots.DirectoryPath);
        Assert(updatedSnapshot.Items.Single().Title == "Unsaved title" && updatedSnapshot.Items.Single().FilePaths.Count == 2
            && updatedSnapshot.Settings.Theme == "Light" && updatedSnapshot.Settings.WindowWidth == 999,
            "Later saved snapshots supersede earlier snapshots", results);
        foreach (var property in typeof(AppSettings).GetProperties())
        {
            object? value = property.GetValue(snapshots.Settings);
            property.SetValue(snapshots.Settings, value switch
            {
                bool boolean => !boolean,
                int integer => integer + 7,
                double number => number + 7,
                string text => text + "-snapshot-check",
                _ => value
            });
        }
        snapshots.SaveSettings();
        await snapshots.FlushAsync();
        var allSettings = new HistoryStore(snapshots.DirectoryPath).Settings;
        Assert(typeof(AppSettings).GetProperties().All(property => Equals(property.GetValue(snapshots.Settings), property.GetValue(allSettings))),
            "Every existing preference survives deferred settings persistence", results);

        var burst = new HistoryStore(Path.Combine(root, "burst"), deferredPersistence: true);
        for (int i = 0; i < 250; i++)
            burst.Add(new ClipItem { Title = "Burst " + i, Text = "Burst " + i, CreatedAt = DateTimeOffset.UtcNow.AddTicks(i) });
        var pinnedIds = burst.Items.Take(8).Select(item => item.Id).ToArray();
        burst.TogglePinned(pinnedIds);
        var expected = burst.Items.Select(item => (item.Id, item.Text, item.IsPinned)).ToArray();
        Assert(await burst.FlushAsync(), "Burst of deferred writes drains successfully", results);
        Assert(expected.SequenceEqual(new HistoryStore(burst.DirectoryPath).Items.Select(item => (item.Id, item.Text, item.IsPinned))),
            "Burst persists the final capped order, records, and pin state", results);
        var draining = burst.FlushAsync();
        burst.Add(new ClipItem { Title = "Latest", Text = "Latest" });
        await draining;
        Assert(await burst.FlushAsync() && new HistoryStore(burst.DirectoryPath).Items.Any(item => item.Text == "Latest"),
            "A write queued around an idle drain is not stranded", results);
        burst.Add(new ClipItem { Text = "Start drain", Title = "Start drain" });
        var activeDrain = burst.FlushAsync();
        burst.Add(new ClipItem { Text = "Added during drain", Title = "Added during drain" });
        Assert(await activeDrain && new HistoryStore(burst.DirectoryPath).Items.Any(item => item.Text == "Added during drain"),
            "An active flush also drains snapshots queued while it waits", results);

        string failuresDirectory = Path.Combine(root, "write-failures");
        var seed = new HistoryStore(failuresDirectory);
        seed.Add(new ClipItem { Text = "Existing durable record", Title = "Existing durable record" });
        var failures = new HistoryStore(failuresDirectory, deferredPersistence: true);
        int failuresNotified = 0;
        failures.PersistenceFailed += _ => System.Threading.Interlocked.Increment(ref failuresNotified);
        using (var lockedHistory = new FileStream(failures.HistoryPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            failures.Add(new ClipItem { Text = "Latest unsaved record", Title = "Latest unsaved record" });
            failures.Settings.Theme = "Dark";
            failures.SaveSettings();
            Assert(!await failures.FlushAsync() && failures.LastError is not null && failuresNotified > 0,
                "Disk failure is reported while keeping the latest in-memory state", results);
        }
        Assert(await failures.FlushAsync() && failures.LastError is null,
            "A later flush retries retained failed snapshots", results);
        var recoveredFailure = new HistoryStore(failuresDirectory);
        Assert(recoveredFailure.Items.Any(item => item.Text == "Latest unsaved record") && recoveredFailure.Settings.Theme == "Dark",
            "Retry preserves history and independently saved settings", results);
        using (var lockedSettings = new FileStream(failures.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            failures.Settings.Theme = "Light";
            failures.SaveSettings();
            failures.Add(new ClipItem { Text = "History still saves", Title = "History still saves" });
            Assert(!await failures.FlushAsync() && failures.LastError is not null,
                "A history success does not hide an unresolved settings failure", results);
        }
        Assert(await failures.FlushAsync() && new HistoryStore(failuresDirectory).Settings.Theme == "Light",
            "A failed settings snapshot is retried without losing its latest value", results);

        using (var lockedHistory = new FileStream(failures.HistoryPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            failures.Clear();
            failures.Add(new ClipItem { Text = "After clear", Title = "After clear" });
            Assert(!await failures.FlushAsync(), "A blocked clear reports its persistence failure", results);
        }
        failures.Add(new ClipItem { Text = "After failed write", Title = "After failed write" });
        Assert(await failures.FlushAsync(), "Clear barrier survives a failed write and later coalescing", results);
        File.WriteAllText(failures.HistoryPath, "invalid-after-coalesced-clear");
        var clearRecovery = new HistoryStore(failuresDirectory);
        Assert(clearRecovery.Items.Count == 2 && clearRecovery.Items.All(item => item.Text is "After clear" or "After failed write"),
            "Backup recovery cannot resurrect records preceding a coalesced clear", results);
        failures.Clear();
        Assert(await failures.FlushAsync(), "Deferred empty clear flushes both primary and backup", results);
        File.WriteAllText(failures.HistoryPath, "invalid-after-empty-clear");
        Assert(new HistoryStore(failuresDirectory).Items.Count == 0, "Deferred clear leaves an empty recovery backup", results);

        var images = new HistoryStore(Path.Combine(root, "image-safety"), deferredPersistence: true);
        string sourcePath = Path.Combine(root, "original-screenshot.png");
        File.WriteAllBytes(sourcePath, [2, 4, 6, 8]);
        var imageItem = new ClipItem { Kind = ClipKind.Image, Title = "Screenshot", SourcePath = sourcePath };
        imageItem.ImagePath = images.ImagePathFor(imageItem.Id);
        File.Copy(sourcePath, imageItem.ImagePath);
        images.Add(new ClipItem { Text = "Concurrent text", Title = "Concurrent text" });
        Assert(await images.FlushAsync() && File.Exists(imageItem.ImagePath),
            "Background writes cannot delete an image allocated before its Add", results);
        images.Add(imageItem);
        Assert(await images.FlushAsync() && File.Exists(imageItem.ImagePath), "Pending image snapshot remains available after persistence", results);
        images.Remove([imageItem.Id]);
        await images.FlushAsync();
        images.Save();
        Assert(await images.FlushAsync() && File.Exists(imageItem.ImagePath),
            "Asynchronous drains leave cache reclamation to a safe lifecycle point", results);
        Assert(images.Flush() && File.Exists(imageItem.ImagePath) && images.CanUndoDelete,
            "Exit flush protects image caches referenced by deletion undo", results);
        images.Clear();
        Assert(images.Flush() && !File.Exists(imageItem.ImagePath) && File.Exists(sourcePath),
            "Clear and flush reclaim undo image caches while preserving their original files", results);
        string orphanPath = images.ImagePathFor(Guid.NewGuid());
        File.WriteAllBytes(orphanPath, [1, 3, 5, 7]);
        _ = new HistoryStore(images.DirectoryPath, deferredPersistence: true);
        Assert(!File.Exists(orphanPath), "Next startup reclaims abandoned cache files", results);
        return results;
    }

    public static async Task<IReadOnlyList<string>> RunUndoAsync(string directory)
    {
        string root = Path.Combine(Path.GetFullPath(directory), "undo-storage-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var results = new List<string>();
        foreach (bool deferred in new[] { false, true })
        {
            string mode = deferred ? "Deferred" : "Synchronous";
            string modeDirectory = Path.Combine(root, mode);
            var time = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
            var basic = new HistoryStore(Path.Combine(modeDirectory, "mixed-batch"), deferred);
            Assert(!basic.CanUndoDelete && basic.UndoDeleteCount == 0 && basic.UndoDelete() == 0,
                mode + " undo starts empty", results);
            string originalFile = Path.Combine(modeDirectory, "original-file.txt");
            File.WriteAllText(originalFile, "untouched original file");
            string originalScreenshot = Path.Combine(modeDirectory, "original-screenshot.png");
            byte[] imageBytes = [1, 7, 31, 127, 255];
            File.WriteAllBytes(originalScreenshot, imageBytes);
            var text = new ClipItem { Title = "Original text title", Text = "  original\n中文  ", CreatedAt = time };
            var file = new ClipItem { Kind = ClipKind.File, Title = "Original file title", FilePaths = [originalFile], CreatedAt = time.AddSeconds(1) };
            var image = new ClipItem { Kind = ClipKind.Image, Title = "Original image title", SourcePath = originalScreenshot, CreatedAt = time.AddSeconds(2), IsPinned = true };
            image.ImagePath = basic.ImagePathFor(image.Id);
            basic.Add(text);
            basic.Add(file);
            File.Copy(originalScreenshot, image.ImagePath);
            basic.Add(image);
            var orderBeforeDelete = basic.Items.Select(item => item.Id).ToArray();
            int changed = 0;
            basic.Changed += () => changed++;
            basic.Remove([text.Id, Guid.NewGuid(), file.Id, image.Id, text.Id]);
            Assert(basic.Items.Count == 0 && basic.UndoDeleteCount == 1 && changed == 1,
                mode + " multi-delete records one transaction and one immediate change", results);
            basic.Remove([Guid.NewGuid()]);
            basic.Remove([]);
            Assert(basic.UndoDeleteCount == 1 && changed == 1, mode + " no-op removal does not consume an undo slot", results);
            text.Text = "mutated external object";
            text.Title = "mutated title";
            file.FilePaths.Clear();
            image.IsPinned = false;
            image.CreatedAt = time.AddDays(5);
            basic.Save();
            Assert(await basic.FlushAsync() && basic.Flush() && File.ReadAllBytes(image.ImagePath).SequenceEqual(imageBytes),
                mode + " undo image survives persistence, backup rotation, and cache cleanup", results);
            Assert(basic.UndoDelete() == 3 && !basic.CanUndoDelete && basic.UndoDeleteCount == 0 && changed == 2,
                mode + " one undo restores the whole mixed deletion transaction", results);
            var restoredText = basic.Items.Single(item => item.Id == text.Id);
            var restoredFile = basic.Items.Single(item => item.Id == file.Id);
            var restoredImage = basic.Items.Single(item => item.Id == image.Id);
            Assert(restoredText.Text == "  original\n中文  " && restoredText.Title == "Original text title"
                && restoredText.CreatedAt == time && restoredFile.FilePaths.SequenceEqual([originalFile])
                && restoredImage.IsPinned && restoredImage.CreatedAt == time.AddSeconds(2)
                && basic.Items.Select(item => item.Id).SequenceEqual(orderBeforeDelete),
                mode + " undo restores independent original values, IDs, timestamps, pin state, and order", results);
            Assert(File.ReadAllBytes(restoredImage.ImagePath!).SequenceEqual(imageBytes)
                && File.ReadAllText(originalFile) == "untouched original file" && File.ReadAllBytes(originalScreenshot).SequenceEqual(imageBytes),
                mode + " restored image bytes and external original files stay intact", results);
            await basic.FlushAsync();
            var reloaded = new HistoryStore(basic.DirectoryPath);
            Assert(reloaded.Items.Select(item => item.Id).SequenceEqual(orderBeforeDelete) && !reloaded.CanUndoDelete,
                mode + " restored history persists without persisting the undo stack", results);

            var lifo = new HistoryStore(Path.Combine(modeDirectory, "bounded-lifo"), deferred);
            var removed = Enumerable.Range(0, 12).Select(index => new ClipItem
            {
                Title = "Delete batch " + index, Text = "Delete batch " + index, CreatedAt = time.AddSeconds(index)
            }).ToArray();
            foreach (var item in removed) lifo.Add(item);
            foreach (var item in removed) lifo.Remove([item.Id]);
            Assert(lifo.UndoDeleteCount == 10, mode + " undo retains at most ten deletion batches", results);
            var newest = new ClipItem { Title = "New clipboard arrival", Text = "New clipboard arrival", CreatedAt = time.AddDays(1) };
            lifo.Add(newest);
            bool reverseOrder = true;
            for (int index = 11; index >= 2; index--)
                reverseOrder &= lifo.UndoDelete() == 1 && lifo.Items.Any(item => item.Id == removed[index].Id);
            Assert(reverseOrder && lifo.UndoDelete() == 0 && lifo.Items.All(item => item.Id != removed[0].Id && item.Id != removed[1].Id),
                mode + " batches undo newest-first and evicted transactions cannot reappear", results);
            Assert(lifo.Items.First().Id == newest.Id && lifo.Items.Select(item => item.Id).Distinct().Count() == lifo.Items.Count,
                mode + " incoming clipboard items survive older undo transactions without duplicate IDs", results);
            await lifo.FlushAsync();

            var conflicts = new HistoryStore(Path.Combine(modeDirectory, "conflicts"), deferred);
            var oldText = new ClipItem { Text = "Repeated text", Title = "Old title", IsPinned = true, CreatedAt = time };
            conflicts.Add(oldText);
            conflicts.Remove([oldText.Id]);
            var newText = new ClipItem { Text = "Repeated text", Title = "New title", CreatedAt = time.AddDays(1) };
            conflicts.Add(newText);
            int conflictEvents = 0;
            conflicts.Changed += () => conflictEvents++;
            Assert(conflicts.UndoDelete() == 0 && ReferenceEquals(conflicts.Items.Single(), newText)
                && !newText.IsPinned && newText.Title == "New title" && newText.CreatedAt == time.AddDays(1)
                && !conflicts.CanUndoDelete && conflictEvents == 1,
                mode + " a newer duplicate wins unchanged and updates undo availability", results);
            var reusedId = new ClipItem { Text = "Before ID reuse", Title = "Before ID reuse", CreatedAt = time };
            conflicts.Add(reusedId);
            conflicts.Remove([reusedId.Id]);
            var replacement = new ClipItem { Id = reusedId.Id, Text = "After ID reuse", Title = "After ID reuse", CreatedAt = time.AddDays(1) };
            conflicts.Add(replacement);
            Assert(conflicts.UndoDelete() == 0 && conflicts.Items.Single(item => item.Id == reusedId.Id).Text == "After ID reuse",
                mode + " undo never overwrites a current record reusing the deleted ID", results);
            var oldFile = new ClipItem { Kind = ClipKind.File, Title = "Old files", FilePaths = [originalFile.ToUpperInvariant()], CreatedAt = time };
            conflicts.Add(oldFile);
            conflicts.Remove([oldFile.Id]);
            var newFile = new ClipItem { Kind = ClipKind.File, Title = "New files", FilePaths = [originalFile.ToLowerInvariant()], CreatedAt = time.AddDays(1) };
            conflicts.Add(newFile);
            Assert(conflicts.UndoDelete() == 0 && conflicts.Items.Count(item => item.Kind == ClipKind.File) == 1
                && conflicts.Items.Single(item => item.Kind == ClipKind.File).Id == newFile.Id,
                mode + " undo respects Windows case-insensitive file duplicates", results);
            var oldImage = new ClipItem { Kind = ClipKind.Image, Title = "Old image", SourcePath = originalScreenshot, CreatedAt = time };
            oldImage.ImagePath = conflicts.ImagePathFor(oldImage.Id);
            File.Copy(originalScreenshot, oldImage.ImagePath);
            conflicts.Add(oldImage);
            conflicts.Remove([oldImage.Id]);
            var newImage = new ClipItem { Kind = ClipKind.Image, Title = "New image", CreatedAt = time.AddDays(1) };
            newImage.ImagePath = conflicts.ImagePathFor(newImage.Id);
            File.Copy(originalScreenshot, newImage.ImagePath);
            conflicts.Add(newImage);
            Assert(conflicts.UndoDelete() == 0 && conflicts.Items.Single(item => item.Kind == ClipKind.Image).Id == newImage.Id,
                mode + " identical image bytes do not create a duplicate during undo", results);
            await conflicts.FlushAsync();
            conflicts.Save();
            Assert(conflicts.Flush() && File.Exists(newImage.ImagePath) && !File.Exists(oldImage.ImagePath) && File.Exists(originalScreenshot),
                mode + " a consumed conflicting image undo releases only its obsolete cache", results);

            var limited = new HistoryStore(Path.Combine(modeDirectory, "capacity"), deferred);
            limited.Settings.MaxItems = 3;
            var olderPinned = new ClipItem { Text = "Deleted pinned", Title = "Deleted pinned", IsPinned = true, CreatedAt = time };
            var deletedNewer = new ClipItem { Text = "Deleted newer", Title = "Deleted newer", CreatedAt = time.AddSeconds(1) };
            var deletedOldest = new ClipItem { Text = "Deleted oldest", Title = "Deleted oldest", CreatedAt = time.AddSeconds(-1) };
            foreach (var item in new[] { olderPinned, deletedNewer, deletedOldest }) limited.Add(item);
            limited.Remove(limited.Items.Select(item => item.Id).ToArray());
            var arriving = Enumerable.Range(0, 2).Select(index => new ClipItem
            {
                Text = "Current arrival " + index, Title = "Current arrival " + index, CreatedAt = time.AddDays(1).AddSeconds(index)
            }).ToArray();
            foreach (var item in arriving) limited.Add(item);
            Assert(limited.UndoDelete() == 1 && limited.Items.Count == 3 && limited.Items.Any(item => item.Id == olderPinned.Id)
                && arriving.All(arrival => limited.Items.Any(item => item.Id == arrival.Id)),
                mode + " partial undo respects capacity and preserves every newer arrival", results);
            limited.Remove([olderPinned.Id]);
            var fullArrival = new ClipItem { Text = "Fill remaining capacity", Title = "Fill remaining capacity", CreatedAt = time.AddDays(2) };
            limited.Add(fullArrival);
            var fullIds = limited.Items.Select(item => item.Id).ToArray();
            Assert(limited.UndoDelete() == 0 && limited.Items.Select(item => item.Id).SequenceEqual(fullIds) && !limited.CanUndoDelete,
                mode + " full-capacity undo never evicts current records", results);
            limited.Add(new ClipItem { Text = "Automatic cap eviction", Title = "Automatic cap eviction", CreatedAt = time.AddDays(3) });
            Assert(!limited.CanUndoDelete, mode + " automatic history trimming is not a user-deletion transaction", results);
            await limited.FlushAsync();

            var release = new HistoryStore(Path.Combine(modeDirectory, "undo-cache-limit"), deferred);
            var cachedPaths = new List<string>();
            for (int index = 0; index < 11; index++)
            {
                var cached = new ClipItem { Kind = ClipKind.Image, Title = "Cached image " + index };
                cached.ImagePath = release.ImagePathFor(cached.Id);
                File.WriteAllBytes(cached.ImagePath, [(byte)index, 50, 100]);
                cachedPaths.Add(cached.ImagePath);
                release.Add(cached);
                release.Remove([cached.Id]);
            }
            await release.FlushAsync();
            release.Save();
            Assert(release.Flush() && release.UndoDeleteCount == 10 && !File.Exists(cachedPaths[0]) && cachedPaths.Skip(1).All(File.Exists),
                mode + " cache protection expires with the oldest of eleven undo batches", results);
            release.Clear();
            Assert(!release.CanUndoDelete && release.UndoDeleteCount == 0 && release.UndoDelete() == 0,
                mode + " clear invalidates every undo transaction immediately", results);
            Assert(await release.FlushAsync() && release.Flush() && cachedPaths.All(path => !File.Exists(path)),
                mode + " clear releases all undo-only image cache references", results);
            File.WriteAllText(release.HistoryPath, "corrupt-after-clearing-undo");
            var clearedRecovery = new HistoryStore(release.DirectoryPath);
            Assert(clearedRecovery.Items.Count == 0 && !clearedRecovery.CanUndoDelete && clearedRecovery.UndoDelete() == 0,
                mode + " corrupt-file recovery cannot resurrect cleared undo history", results);
            var session = new HistoryStore(Path.Combine(modeDirectory, "session-lifetime"), deferred);
            var removedBeforeExit = new ClipItem { Text = "Session-only undo", Title = "Session-only undo" };
            session.Add(removedBeforeExit);
            session.Remove([removedBeforeExit.Id]);
            await session.FlushAsync();
            var nextSession = new HistoryStore(session.DirectoryPath);
            Assert(session.CanUndoDelete && !nextSession.CanUndoDelete && nextSession.UndoDelete() == 0 && nextSession.Items.Count == 0,
                mode + " deletion undo does not cross process/session startup", results);
        }

        var failing = new HistoryStore(Path.Combine(root, "deferred-undo-write-failure"), deferredPersistence: true);
        var durable = new ClipItem { Text = "Restore despite disk lock", Title = "Restore despite disk lock" };
        failing.Add(durable);
        await failing.FlushAsync();
        using (var lockedHistory = new FileStream(failing.HistoryPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            failing.Remove([durable.Id]);
            Assert(!await failing.FlushAsync() && failing.CanUndoDelete,
                "A failed deferred deletion retains its undo transaction", results);
            Assert(failing.UndoDelete() == 1 && failing.Items.Single().Id == durable.Id && !failing.CanUndoDelete,
                "Undo restores in memory even while persistence is blocked", results);
            Assert(!await failing.FlushAsync(), "Failed undo persistence remains queued for retry", results);
        }
        Assert(await failing.FlushAsync() && new HistoryStore(failing.DirectoryPath).Items.Single().Id == durable.Id,
            "Undo supersedes a failed deletion and persists on the next successful flush", results);
        using (var lockedHistory = new FileStream(failing.HistoryPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            failing.Remove([durable.Id]);
            failing.Clear();
            Assert(!failing.CanUndoDelete && failing.UndoDelete() == 0 && !await failing.FlushAsync(),
                "Clear invalidates undo even when its disk write must be retried", results);
        }
        Assert(await failing.FlushAsync(), "A clear after failed deletion remains retryable", results);
        File.WriteAllText(failing.HistoryPath, "corrupt-after-failed-delete-and-clear");
        Assert(new HistoryStore(failing.DirectoryPath).Items.Count == 0,
            "Clear after a failed deletion leaves no recoverable undo records in the backup", results);
        return results;
    }

    private static void Assert(bool condition, string name, ICollection<string> results)
    {
        if (!condition) throw new InvalidOperationException("Storage self-test failed: " + name);
        results.Add("PASS " + name);
    }
}
