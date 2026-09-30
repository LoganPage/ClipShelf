import Foundation

enum OldHistoryCleanup {
    static let retentionOptions = [7, 30, 90]
    static let defaultRetentionDays = 30
    static let key = "history.oldRecordRetentionDays"

    static var retentionDays: Int {
        get { load(from: AppEnvironment.userDefaults) }
        set { save(newValue, to: AppEnvironment.userDefaults) }
    }

    static func load(from defaults: UserDefaults) -> Int {
        guard defaults.object(forKey: key) != nil else {
            return defaultRetentionDays
        }
        return normalized(defaults.integer(forKey: key))
    }

    @discardableResult
    static func save(_ days: Int, to defaults: UserDefaults) -> Int {
        let normalizedDays = normalized(days)
        defaults.set(normalizedDays, forKey: key)
        return normalizedDays
    }

    static func normalized(_ days: Int) -> Int {
        retentionOptions.contains(days) ? days : defaultRetentionDays
    }

    static func cutoffDate(now: Date, retentionDays: Int) -> Date {
        now.addingTimeInterval(-TimeInterval(normalized(retentionDays)) * 86_400)
    }

    static func eligibleIDs(
        in items: [ClipItem],
        now: Date,
        retentionDays: Int
    ) -> Set<ClipItem.ID> {
        let cutoff = cutoffDate(now: now, retentionDays: retentionDays)
        return Set(items.lazy.filter { !$0.isPinned && $0.createdAt < cutoff }.map(\.id))
    }

    static func summaryText(eligibleCount: Int, retentionDays: Int) -> String {
        guard eligibleCount > 0 else {
            return "没有符合条件的旧记录。"
        }
        return "将清理 \(normalized(retentionDays)) 天前的 \(eligibleCount) 条未置顶记录。"
    }
}
