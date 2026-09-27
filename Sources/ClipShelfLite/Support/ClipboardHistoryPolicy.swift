import AppKit

enum ClipboardHistoryPolicy {
    static let concealedType = NSPasteboard.PasteboardType("org.nspasteboard.ConcealedType")
    static let transientType = NSPasteboard.PasteboardType("org.nspasteboard.TransientType")

    private static let excludedTypeNames: Set<String> = [
        concealedType.rawValue,
        transientType.rawValue
    ]

    static func shouldExclude(_ pasteboard: NSPasteboard) -> Bool {
        shouldExclude(typeNames: pasteboard.types?.map(\.rawValue))
    }

    static func shouldCapture(
        historyEnabled: Bool,
        observedChangeCount: Int,
        previousChangeCount: inout Int,
        typeNames: [String]?
    ) -> Bool {
        guard historyEnabled else { return false }
        guard observedChangeCount != previousChangeCount else { return false }

        previousChangeCount = observedChangeCount
        return !shouldExclude(typeNames: typeNames)
    }

    static func shouldExclude(typeNames: [String]?) -> Bool {
        guard let typeNames else {
            return false
        }
        return !excludedTypeNames.isDisjoint(with: typeNames)
    }
}
