import Foundation

final class HistoryWriter: @unchecked Sendable {
    private let destinationURL: URL
    private let coalescingWindow: TimeInterval
    private let queue = DispatchQueue(label: "ClipShelf.history-writer", qos: .utility)
    private var latestSnapshot: [ClipItem]?
    private var generation: UInt64 = 0
    private var storedWriteCount = 0

    init(destinationURL: URL, coalescingWindow: TimeInterval = 0.25) {
        self.destinationURL = destinationURL
        self.coalescingWindow = min(max(0, coalescingWindow), 0.3)
    }

    var writeCount: Int {
        queue.sync { storedWriteCount }
    }

    func schedule(_ items: [ClipItem]) {
        let snapshot = items
        queue.async { [self] in
            latestSnapshot = snapshot
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
        } catch {
            NSLog("ClipShelf save failed: \(error.localizedDescription)")
        }
    }
}
