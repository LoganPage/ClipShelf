import Foundation

struct ClipSearchIndex: Equatable {
    struct Field: Equatable {
        let normalized: String
        let pinyin: String
        let initials: String
    }

    let fields: [Field]

    init(item: ClipItem) {
        fields = SearchMatcher.searchableTexts(for: item).map { value in
            let transliterated = SearchMatcher.pinyinText(value)
            return Field(
                normalized: SearchMatcher.normalize(value),
                pinyin: SearchMatcher.normalize(transliterated),
                initials: SearchMatcher.pinyinInitials(transliterated)
            )
        }
    }
}

final class ClipSearchIndexCache: @unchecked Sendable {
    private struct SearchableSnapshot: Equatable {
        let title: String
        let text: String?
        let filePaths: [String]
        let sourcePath: String?

        init(item: ClipItem) {
            title = item.title
            text = item.text
            filePaths = item.filePaths
            sourcePath = item.sourcePath
        }
    }

    private struct Entry {
        let snapshot: SearchableSnapshot
        let index: ClipSearchIndex
        var lastAccess: UInt64
        var matchResults: [String: Bool]
    }

    private let lock = NSLock()
    private var entries = [ClipItem.ID: Entry]()
    private var accessCounter: UInt64 = 0
    private var maximumEntryCount: Int
    private var storedBuildCount = 0

    init(maximumEntryCount: Int = 8) {
        self.maximumEntryCount = max(0, maximumEntryCount)
    }

    var count: Int {
        lock.withLock { entries.count }
    }

    var buildCount: Int {
        lock.withLock { storedBuildCount }
    }

    func setMaximumEntryCount(_ count: Int) {
        lock.withLock {
            maximumEntryCount = max(0, count)
            evictIfNeeded()
        }
    }

    func index(for item: ClipItem) -> ClipSearchIndex {
        let snapshot = SearchableSnapshot(item: item)

        if let cached = lock.withLock({ cachedIndex(for: item.id, snapshot: snapshot) }) {
            return cached
        }

        let builtIndex = ClipSearchIndex(item: item)
        return lock.withLock {
            if let cached = cachedIndex(for: item.id, snapshot: snapshot) {
                return cached
            }

            accessCounter &+= 1
            storedBuildCount += 1
            entries[item.id] = Entry(
                snapshot: snapshot,
                index: builtIndex,
                lastAccess: accessCounter,
                matchResults: [:]
            )
            evictIfNeeded()
            return builtIndex
        }
    }

    func matches(_ item: ClipItem, query rawQuery: String) -> Bool {
        let query = SearchMatcher.normalize(rawQuery)
        guard !query.isEmpty else { return true }
        let snapshot = SearchableSnapshot(item: item)

        if let cachedResult = lock.withLock({ () -> Bool? in
            guard var entry = entries[item.id], entry.snapshot == snapshot,
                  let result = entry.matchResults[query] else { return nil }
            accessCounter &+= 1
            entry.lastAccess = accessCounter
            entries[item.id] = entry
            return result
        }) {
            return cachedResult
        }

        let searchIndex = index(for: item)
        let result = SearchMatcher.matchesNormalized(searchIndex, query: query)
        lock.withLock {
            guard var entry = entries[item.id], entry.snapshot == snapshot else { return }
            if entry.matchResults.count >= 16 {
                entry.matchResults.removeValue(forKey: entry.matchResults.keys.first!)
            }
            entry.matchResults[query] = result
            entries[item.id] = entry
        }
        return result
    }

    func prewarm(_ items: [ClipItem], headroom: Int = 8) {
        setMaximumEntryCount(items.count + max(0, headroom))
        for item in items {
            _ = index(for: item)
        }
    }

    private func cachedIndex(for id: ClipItem.ID, snapshot: SearchableSnapshot) -> ClipSearchIndex? {
        guard var entry = entries[id], entry.snapshot == snapshot else {
            return nil
        }
        accessCounter &+= 1
        entry.lastAccess = accessCounter
        entries[id] = entry
        return entry.index
    }

    private func evictIfNeeded() {
        while entries.count > maximumEntryCount,
              let leastRecentlyUsedID = entries.min(by: { $0.value.lastAccess < $1.value.lastAccess })?.key {
            entries.removeValue(forKey: leastRecentlyUsedID)
        }
    }
}

private extension NSLock {
    func withLock<T>(_ operation: () throws -> T) rethrows -> T {
        lock()
        defer { unlock() }
        return try operation()
    }
}
