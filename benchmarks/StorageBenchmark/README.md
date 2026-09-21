# ClipShelf storage responsiveness benchmark

This standalone console project links only the data layer. It does not open the clipboard, register shortcuts, touch the normal ClipShelf data folder, or create a visible application window. Every run uses a unique temporary directory.

Run with the repository's local .NET SDK:

```powershell
.tools/dotnet/dotnet.exe run --project windows/benchmarks/StorageBenchmark/StorageBenchmark.csproj --configuration Release -- --deferred --output windows/artifacts/storage-deferred.json
.tools/dotnet/dotnet.exe run --project windows/benchmarks/StorageBenchmark/StorageBenchmark.csproj --configuration Release -- --tests
```

Omit `--deferred` to measure the synchronous mode retained for tests and command-line use. `--tests` runs the synchronous storage checks, deferred-persistence checks, and deletion-undo checks in both persistence modes. Pass `--output` to retain the test result JSON. The workload first seeds 100 records, then performs 120 unique additions and 120 pin toggles in three trials. Each text contains 2,048 padding characters plus its unique label; the full text also appears in the title, matching normal clipboard capture. The history file is approximately 441 KB.

Measured on this Windows host on 2026-09-08:

| Measurement | Original synchronous persistence | Deferred persistence |
| --- | ---: | ---: |
| Mean Add time, range across three trials | 18.38–19.27 ms | 0.058–0.075 ms |
| Add p95, range across three trials | 34.46–37.87 ms | 0.066–0.087 ms |
| Mean TogglePinned time | 14.30–19.21 ms | 0.043–0.067 ms |
| Calling-thread time for 240 operations | 4,005–4,510 ms | 12.7–15.8 ms |
| Final exit flush | Already included in each operation | 60.5–75.7 ms |

The calling-thread total decreased by approximately 99.66% in this burst workload. This is a data-layer microbenchmark, not a claim about overall UI frame rate. The deferred result includes a final durable flush and verifies that a newly reopened store has exactly the same final record IDs, order, and pin state. Both modes finish with 100 records.

Raw measurements are retained in `windows/artifacts/storage-baseline.json` and `windows/artifacts/storage-deferred.json`. The baseline was recorded before changing `HistoryStore`.

## Persistence contract

`new HistoryStore(directory, deferredPersistence: true)` takes independent item/file-list/settings snapshots on the calling thread. One background writer merges updates within a bounded 50 ms window and serializes and atomically replaces the files. A clear-history barrier survives coalescing and failed-write retries, so the backup cannot restore records preceding a clear.

`FlushAsync()` returns `Task<bool>` and waits for pending snapshots. A failed snapshot remains queued; a later explicit flush or change retries it. Failures do not cause a background retry loop. `LastError` retains an unresolved history or settings error, even if the other file saves successfully. `PersistenceFailed` is raised on the writer thread; UI subscribers should use asynchronous dispatch.

Stop clipboard/screenshot collection before normal shutdown, save final settings, await `FlushAsync()`, and then call synchronous `Flush()` on the owning thread to safely reclaim unused cached images. `Flush()` also waits for outstanding persistence and is suitable as an exit fallback. Background persistence never deletes image files, so it cannot race a newly allocated image or pending snapshot. Cache reclamation occurs at startup or synchronous flush and never removes original source files. Both flush methods retain failed snapshots and report `false`; callers can offer retry or cancel shutdown.

The tests cover deep snapshot isolation, immediate change notification, burst ordering, pin state, actual Windows file-lock failures, retry recovery, clear-backup barriers, and pending/new-image safety. The synchronous mode remains the constructor default and preserves its immediate-save behavior.

## Session deletion undo (1.0.3)

Each successful `Remove(IEnumerable<Guid>)` records one independent deletion transaction, retaining the latest ten batches. `CanUndoDelete` reports availability, `UndoDeleteCount` reports batch count, and `UndoDelete()` consumes the latest batch and returns the number actually restored. Empty or unknown-ID removals do not create transactions. Automatic history-cap evictions are not deletion transactions.

Undo preserves original IDs, timestamps, pin state, text formatting, file lists, and cached images. Current records always win ID or content conflicts. Undo only fills available capacity; it never evicts a record received after deletion. A partially or entirely conflicted/full-capacity batch is consumed, and `Changed` still updates the UI's undo availability.

Cache cleanup protects images referenced by every retained undo transaction. Dropping an old batch, consuming an obsolete conflicting undo, or clearing history releases that protection; external original files are never deleted. `Clear()` immediately invalidates every undo batch and retains its existing primary/backup clear barrier. Undo is not written to disk and is unavailable in the next session.

The 1.0.3 synthetic suite passed 99 checks, including mixed text/file/image batches, deep-copy isolation, ten-batch LIFO ordering, duplicate and ID-reuse conflicts, partial/full capacity, image-cache lifetimes, session boundaries, and real file-lock failures followed by undo/clear retries. Its result is saved in `windows/artifacts/storage-undo-tests-1.0.3.json`; tests do not touch the system clipboard or launch the main WPF app.
