import AppKit
import SwiftUI

enum SelectionColorPreset: String, CaseIterable, Identifiable {
    case coolGrayBlue
    case appleBlue
    case neutralGray
    case lavenderGray
    case tealGray

    var id: String { rawValue }

    var title: String {
        switch self {
        case .coolGrayBlue: "冷灰蓝"
        case .appleBlue: "Apple 蓝"
        case .neutralGray: "中性灰"
        case .lavenderGray: "淡紫灰"
        case .tealGray: "青灰"
        }
    }

    var feeling: String {
        switch self {
        case .coolGrayBlue: "稳重耐看"
        case .appleBlue: "交互明显"
        case .neutralGray: "极简克制"
        case .lavenderGray: "柔和有感"
        case .tealGray: "清爽工具感"
        }
    }

    var color: Color {
        AppTheme.adaptive(light: lightColor, dark: darkColor)
    }

    private var lightColor: NSColor {
        switch self {
        case .coolGrayBlue: Self.rgb(0xE8, 0xEF, 0xF7)
        case .appleBlue: Self.rgb(0xE5, 0xF1, 0xFF)
        case .neutralGray: Self.rgb(0xEC, 0xED, 0xEF)
        case .lavenderGray: Self.rgb(0xEE, 0xEA, 0xF7)
        case .tealGray: Self.rgb(0xE6, 0xF1, 0xF1)
        }
    }

    private var darkColor: NSColor {
        switch self {
        case .coolGrayBlue: Self.rgb(0x28, 0x35, 0x44)
        case .appleBlue: Self.rgb(0x17, 0x3A, 0x5E)
        case .neutralGray: Self.rgb(0x34, 0x35, 0x38)
        case .lavenderGray: Self.rgb(0x37, 0x31, 0x46)
        case .tealGray: Self.rgb(0x24, 0x3B, 0x3D)
        }
    }

    private static func rgb(_ red: Int, _ green: Int, _ blue: Int) -> NSColor {
        NSColor(
            calibratedRed: CGFloat(red) / 255,
            green: CGFloat(green) / 255,
            blue: CGFloat(blue) / 255,
            alpha: 1
        )
    }
}

enum SelectionColorPreferences {
    static let changedNotification = Notification.Name("ClipShelfSelectionColorChanged")

    private static let presetKey = "selectionColor.preset"
    private static let redKey = "selectionColor.red"
    private static let greenKey = "selectionColor.green"
    private static let blueKey = "selectionColor.blue"

    static var selectedPreset: SelectionColorPreset? {
        if let rawValue = AppEnvironment.userDefaults.string(forKey: presetKey) {
            return SelectionColorPreset(rawValue: rawValue)
        }

        guard AppEnvironment.userDefaults.object(forKey: redKey) == nil else { return nil }
        return .coolGrayBlue
    }

    static var color: Color {
        get {
            if let selectedPreset {
                return selectedPreset.color
            }

            guard AppEnvironment.userDefaults.object(forKey: redKey) != nil else {
                return SelectionColorPreset.coolGrayBlue.color
            }

            return Color(
                red: AppEnvironment.userDefaults.double(forKey: redKey),
                green: AppEnvironment.userDefaults.double(forKey: greenKey),
                blue: AppEnvironment.userDefaults.double(forKey: blueKey)
            )
        }
        set {
            AppEnvironment.userDefaults.removeObject(forKey: presetKey)
            let nsColor = NSColor(newValue)
                .usingColorSpace(.deviceRGB)
                ?? NSColor.labelColor

            AppEnvironment.userDefaults.set(nsColor.redComponent, forKey: redKey)
            AppEnvironment.userDefaults.set(nsColor.greenComponent, forKey: greenKey)
            AppEnvironment.userDefaults.set(nsColor.blueComponent, forKey: blueKey)
            NotificationCenter.default.post(name: changedNotification, object: nil)
        }
    }

    static func select(_ preset: SelectionColorPreset) {
        AppEnvironment.userDefaults.set(preset.rawValue, forKey: presetKey)
        AppEnvironment.userDefaults.removeObject(forKey: redKey)
        AppEnvironment.userDefaults.removeObject(forKey: greenKey)
        AppEnvironment.userDefaults.removeObject(forKey: blueKey)
        NotificationCenter.default.post(name: changedNotification, object: preset)
    }

    static func reset() {
        select(.coolGrayBlue)
    }
}
