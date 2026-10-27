import Foundation

/// How history rows behave at the edges of the list.
enum ScrollEffectStyle: String, CaseIterable, Identifiable {
    /// Rows slipping under the toolbar blur gradually; rows reaching the
    /// bottom gather into a stack that unfolds as the list scrolls.
    case stack
    /// Only the gradual blur under the toolbar.
    case blurOnly
    case off

    var id: String { rawValue }

    var title: String {
        switch self {
        case .stack: "叠放"
        case .blurOnly: "仅模糊"
        case .off: "关闭"
        }
    }

    var stacksBottom: Bool { self == .stack }
    var blursTop: Bool { self != .off }
}

enum ScrollEffectPreferences {
    static let changedNotification = Notification.Name("ClipShelfScrollEffectChanged")
    private static let key = "history.scrollEffect"

    static var value: ScrollEffectStyle {
        get {
            AppEnvironment.userDefaults.string(forKey: key).flatMap(ScrollEffectStyle.init(rawValue:)) ?? .stack
        }
        set {
            AppEnvironment.userDefaults.set(newValue.rawValue, forKey: key)
            NotificationCenter.default.post(name: changedNotification, object: newValue)
        }
    }
}

/// How the records of a newly chosen type arrive in the list.
enum ListEntranceStyle: String, CaseIterable, Identifiable {
    /// One after another, springing up from just below.
    case cascade
    /// The whole page slides in from the side the lens moved towards.
    case slide
    /// Blurred and faint, coming into focus.
    case dissolve
    /// Pouring down out of the type switcher's glass lens.
    case pour
    case off

    var id: String { rawValue }

    var title: String {
        switch self {
        case .cascade: "逐条弹入"
        case .slide: "整体滑移"
        case .dissolve: "模糊溶解"
        case .pour: "玻璃泡展开"
        case .off: "关闭"
        }
    }
}

enum ListEntrancePreferences {
    static let changedNotification = Notification.Name("ClipShelfListEntranceChanged")
    private static let key = "history.listEntrance"

    static var value: ListEntranceStyle {
        get {
            AppEnvironment.userDefaults.string(forKey: key).flatMap(ListEntranceStyle.init(rawValue:)) ?? .cascade
        }
        set {
            AppEnvironment.userDefaults.set(newValue.rawValue, forKey: key)
            NotificationCenter.default.post(name: changedNotification, object: newValue)
        }
    }
}
