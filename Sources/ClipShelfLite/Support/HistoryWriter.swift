import Foundation

final class HistoryWriter: @unchecked Sendable {
    private let destinationURL: URL
    private let coalescingWindow: TimeInterval
    private let queue = DispatchQueue(label: "ClipShelf.history-writer", qos: .utility)
    private var latestSnapshot: [ClipItem]?
    /// Pictures still in use (records plus undoable deletions); the others
    /// are deleted after the history that dropped them is safely written.
    private var latestImageFiles: Set<String>?
    private let imageStore: ImageStore?
    private var generation: UInt64 = 0
    private var storedWriteCount = 0

    init(destinationURL: URL, coalescingWindow: TimeInterval = 0.25, imageStore: ImageStore? = nil) {
        self.destinationURL = destinationURL
        self.imageStore = imageStore
        self.coalescingWindow = min(max(0, coalescingWindow), 0.3)
    }

    var writeCount: Int {
        queue.sync { storedWriteCount }
    }

    func schedule(_ items: [ClipItem], imageFilesInUse: Set<String>? = nil) {
        let snapshot = items
        queue.async { [self] in
            latestSnapshot = snapshot
            latestImageFiles = imageFilesInUse
            generation &+= 1
            let scheduledGeneration = generation
            queue.asyncAfter(deadline: .now() + coalescingWindow) { [self] in
                guard generation == scheduledGeneration else { return }
                writeLatestSnapshotIfNeeded()
            }
        }
    }

    func flush() {
        queue.sync {
            generation &+= 1
            writeLatestSnapshotIfNeeded()
        }
    }

    private func writeLatestSnapshotIfNeeded() {
        guard let snapshot = latestSnapshot else { return }
        latestSnapshot = nil

        do {
            try FileManager.default.createDirectory(
                at: destinationURL.deletingLastPathComponent(),
                withIntermediateDirectories: true
            )
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.sortedKeys]
            let data = try encoder.encode(snapshot)
            try data.write(to: destinationURL, options: .atomic)
            storedWriteCount += 1
            if let imageStore, let imageFiles = latestImageFiles {
                imageStore.removeUnreferenced(keeping: imageFiles)
            }
            latestImageFiles = nil
        } catch {
            NSLog("ClipShelf save failed: \(error.localizedDescription)")
        }
    }
}
