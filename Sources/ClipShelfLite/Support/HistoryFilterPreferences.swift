import Foundation

enum HistoryFilterPreferences {
    static let key = "history.kindFilter"
    static let defaultValue: ClipKindFilter = .all

    static var value: ClipKindFilter {
        get { load(from: AppEnvironment.userDefaults) }
        set { save(newValue, to: AppEnvironment.userDefaults) }
    }

    static func load(from defaults: UserDefaults) -> ClipKindFilter {
        guard let rawValue = defaults.string(forKey: key) else {
            return defaultValue
        }
        return ClipKindFilter(rawValue: rawValue) ?? defaultValue
    }

    @discardableResult
    static func save(_ value: ClipKindFilter, to defaults: UserDefaults) -> ClipKindFilter {
        defaults.set(value.rawValue, forKey: key)
        return value
    }
}
