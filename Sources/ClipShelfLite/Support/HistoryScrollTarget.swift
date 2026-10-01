import Foundation

enum HistoryScrollTarget {
    static func newestAnchorID(in items: [ClipItem]) -> ClipItem.ID? {
        newestItem(in: items)?.id
    }

    static func anchorToReveal(
        in items: [ClipItem],
        lastRevealedNewest: Date?
    ) -> ClipItem.ID? {
        guard let newestItem = newestItem(in: items) else {
            return nil
        }
        guard let lastRevealedNewest else {
            return newestItem.id
        }
        return newestItem.createdAt > lastRevealedNewest ? newestItem.id : nil
    }

    static func newestDate(in items: [ClipItem]) -> Date? {
        newestItem(in: items)?.createdAt
    }

    private static func newestItem(in items: [ClipItem]) -> ClipItem? {
        items.max { first, second in
            first.createdAt < second.createdAt
        }
    }
}
