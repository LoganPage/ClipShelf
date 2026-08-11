import AppKit
import SwiftUI

enum AppearanceMode: String, CaseIterable, Identifiable {
    case system
    case light
    case dark

    var id: String { rawValue }

    var title: String {
        switch self {
        case .system: "跟随系统"
        case .light: "关闭"
        case .dark: "开启"
        }
    }

    var colorScheme: ColorScheme? {
        switch self {
        case .system: nil
        case .light: .light
        case .dark: .dark
        }
    }
}

enum AppearancePreferences {
    static let changedNotification = Notification.Name("ClipShelfAppearanceChanged")
    static let systemChangedNotification = Notification.Name("ClipShelfSystemAppearanceChanged")
    private static let modeKey = "appearance.mode"

    static var mode: AppearanceMode {
        get {
            AppearanceMode(rawValue: UserDefaults.standard.string(forKey: modeKey) ?? "system") ?? .system
        }
        set {
            UserDefaults.standard.set(newValue.rawValue, forKey: modeKey)
            NotificationCenter.default.post(name: changedNotification, object: newValue)
        }
    }

    static var systemColorScheme: ColorScheme {
        NSApp.effectiveAppearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua ? .dark : .light
    }

    static var resolvedColorScheme: ColorScheme {
        switch mode {
        case .system:
            systemColorScheme
        case .light:
            .light
        case .dark:
            .dark
        }
    }

    static func apply(to window: NSWindow?) {
        guard let window else { return }
        let appearanceName: NSAppearance.Name = resolvedColorScheme == .dark ? .darkAqua : .aqua
        window.appearance = NSAppearance(named: appearanceName)
    }
}
