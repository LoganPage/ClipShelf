import Foundation

enum HistoryLimitPreferences {
    static let key = "history.maxItems"
    static let defaultValue = 100
    static let allowedRange = 1...10_000

    static var value: Int {
        get { load(from: AppEnvironment.userDefaults) }
        set { save(newValue, to: AppEnvironment.userDefaults) }
    }

    static func load(from defaults: UserDefaults) -> Int {
        guard defaults.object(forKey: key) != nil else {
            return defaultValue
        }
        return normalized(defaults.integer(forKey: key))
    }

    @discardableResult
    static func save(_ value: Int, to defaults: UserDefaults) -> Int {
        let normalizedValue = normalized(value)
        defaults.set(normalizedValue, forKey: key)
        return normalizedValue
    }

    static func normalized(_ value: Int) -> Int {
        min(max(value, allowedRange.lowerBound), allowedRange.upperBound)
    }

    static func normalized(text: String, fallback: Int) -> Int {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, let value = Int(trimmed) else {
            return normalized(fallback)
        }
        return normalized(value)
    }
}

enum HistoryTrimmer {
    @discardableResult
    static func trim(_ items: inout [ClipItem], maxItems: Int) -> Bool {
        let limit = HistoryLimitPreferences.normalized(maxItems)
        let originalCount = items.count

        while items.count > limit {
            if let lastUnpinnedIndex = items.lastIndex(where: { !$0.isPinned }) {
                items.remove(at: lastUnpinnedIndex)
            } else {
                items.removeLast()
            }
        }

        return items.count != originalCount
    }
}
