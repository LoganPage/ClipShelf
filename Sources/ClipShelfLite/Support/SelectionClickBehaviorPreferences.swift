import Foundation

enum SelectionClickBehaviorPreferences {
    static let changedNotification = Notification.Name("ClipShelfSelectionClickBehaviorChanged")

    private static let switchToClickedRecordKey = "selectionClickBehavior.switchToClickedRecord"
    private static let legacyMultiSelectionClickBehaviorKey = "multiSelection.clickBehavior"
    private static let multiSelectionClickSelectedBehaviorKey = "multiSelection.clickSelectedBehavior"
    private static let multiSelectionClickUnselectedBehaviorKey = "multiSelection.clickUnselectedBehavior"

    static var switchToClickedRecord: Bool {
        get {
            AppEnvironment.userDefaults.bool(forKey: switchToClickedRecordKey)
        }
        set {
            AppEnvironment.userDefaults.set(newValue, forKey: switchToClickedRecordKey)
            NotificationCenter.default.post(name: changedNotification, object: newValue)
        }
    }

    static var multiSelectionClickSelectedBehavior: MultiSelectionClickSelectedBehavior {
        get {
            if let rawValue = AppEnvironment.userDefaults.string(forKey: multiSelectionClickSelectedBehaviorKey),
               let behavior = MultiSelectionClickSelectedBehavior(rawValue: rawValue) {
                return behavior
            }

            guard let rawValue = AppEnvironment.userDefaults.string(forKey: legacyMultiSelectionClickBehaviorKey),
                  let behavior = MultiSelectionClickSelectedBehavior(rawValue: rawValue) else {
                return .collapseToClicked
            }

            return behavior
        }
        set {
            AppEnvironment.userDefaults.set(newValue.rawValue, forKey: multiSelectionClickSelectedBehaviorKey)
            NotificationCenter.default.post(name: changedNotification, object: nil)
        }
    }

    static var multiSelectionClickUnselectedBehavior: MultiSelectionClickUnselectedBehavior {
        get {
            guard let rawValue = AppEnvironment.userDefaults.string(forKey: multiSelectionClickUnselectedBehaviorKey),
                  let behavior = MultiSelectionClickUnselectedBehavior(rawValue: rawValue) else {
                return .collapseToClicked
            }

            return behavior
        }
        set {
            AppEnvironment.userDefaults.set(newValue.rawValue, forKey: multiSelectionClickUnselectedBehaviorKey)
            NotificationCenter.default.post(name: changedNotification, object: nil)
        }
    }
}

enum MultiSelectionClickSelectedBehavior: String, CaseIterable, Hashable, Identifiable {
    case collapseToClicked
    case clearAll
    case removeClicked

    var id: String { rawValue }

    var title: String {
        switch self {
        case .collapseToClicked:
            return "只保留点击项"
        case .clearAll:
            return "取消全部选择"
        case .removeClicked:
            return "只取消点击项"
        }
    }
}

enum MultiSelectionClickUnselectedBehavior: String, CaseIterable, Hashable, Identifiable {
    case collapseToClicked
    case clearAll

    var id: String { rawValue }

    var title: String {
        switch self {
        case .collapseToClicked:
            return "只保留点击项"
        case .clearAll:
            return "取消全部选择"
        }
    }
}

enum DragSelectionPreferences {
    static let changedNotification = Notification.Name("ClipShelfDragSelectionPreferencesChanged")

    private static let clickRecoveryDurationKey = "dragSelection.clickRecoveryDuration"
    private static let defaultClickRecoveryDuration = 0.6

    static var clickRecoveryDuration: TimeInterval {
        get {
            guard AppEnvironment.userDefaults.object(forKey: clickRecoveryDurationKey) != nil else {
                return defaultClickRecoveryDuration
            }

            return clampedAndRounded(AppEnvironment.userDefaults.double(forKey: clickRecoveryDurationKey))
        }
        set {
            let duration = clampedAndRounded(newValue)
            AppEnvironment.userDefaults.set(duration, forKey: clickRecoveryDurationKey)
            NotificationCenter.default.post(name: changedNotification, object: duration)
        }
    }

    private static func clampedAndRounded(_ value: TimeInterval) -> TimeInterval {
        min(max((value * 10).rounded() / 10, 0), 1)
    }
}
