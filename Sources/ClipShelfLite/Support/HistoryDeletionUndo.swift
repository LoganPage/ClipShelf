import Foundation

struct HistoryDeletionEntry: Equatable {
    let item: ClipItem
    let originalIndex: Int
}

struct HistoryDeletionUndoStack {
    static let maximumBatchCount = 10

    private(set) var batches: [[HistoryDeletionEntry]] = []

    var canUndo: Bool {
        !batches.isEmpty
    }

    mutating func record(_ entries: [HistoryDeletionEntry]) {
        guard !entries.isEmpty else { return }
        batches.append(entries.sorted { $0.originalIndex < $1.originalIndex })
        if batches.count > Self.maximumBatchCount {
            batches.removeFirst(batches.count - Self.maximumBatchCount)
        }
    }

    @discardableResult
    mutating func undo(into items: inout [ClipItem], maxItems: Int) -> [ClipItem] {
        guard let batch = batches.popLast() else { return [] }
        let limit = HistoryLimitPreferences.normalized(maxItems)
        let availableSlots = max(0, limit - items.count)
        guard availableSlots > 0 else { return [] }

        let entriesToRestore = batch.prefix(availableSlots)
        for entry in entriesToRestore {
            let insertionIndex = min(max(entry.originalIndex, 0), items.count)
            items.insert(entry.item, at: insertionIndex)
        }
        return entriesToRestore.map(\.item)
    }
}
