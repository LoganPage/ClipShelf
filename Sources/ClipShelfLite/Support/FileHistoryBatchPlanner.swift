import Foundation

enum FileHistoryBatchPlanner {
    static func newItems(
        for paths: [String],
        existingItems: [ClipItem],
        createdAt: Date = Date()
    ) -> [ClipItem] {
        var knownPaths = Set(existingItems
            .filter { $0.kind == .file }
            .flatMap(\.filePaths)
            .map(normalizedPath))
        var newItems = [ClipItem]()

        for path in paths {
            let normalized = normalizedPath(path)
            guard !normalized.isEmpty, knownPaths.insert(normalized).inserted else {
                continue
            }
            let itemDate = createdAt.addingTimeInterval(-Double(newItems.count) * 0.000_001)
            newItems.append(ClipItem(
                kind: .file,
                title: URL(fileURLWithPath: path).lastPathComponent,
                filePaths: [path],
                createdAt: itemDate
            ))
        }
        return newItems
    }

    static func sharesPath(_ lhs: ClipItem, _ rhs: ClipItem) -> Bool {
        let leftPaths = Set(lhs.filePaths.map(normalizedPath))
        return rhs.filePaths.contains { leftPaths.contains(normalizedPath($0)) }
    }

    private static func normalizedPath(_ path: String) -> String {
        guard !path.isEmpty else { return "" }
        return URL(fileURLWithPath: path).standardizedFileURL.path
    }
}
