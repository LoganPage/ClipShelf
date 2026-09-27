import Carbon.HIToolbox
import AppKit
import Foundation

struct HotKeyConfiguration: Codable, Equatable {
    var keyCode: UInt32
    var modifiers: UInt32

    var isValid: Bool {
        modifiers > 0
    }

    var conflictsWithSystemWindowSwitching: Bool {
        guard modifiers == UInt32(controlKey) else { return false }

        switch Int(keyCode) {
        case kVK_ANSI_0, kVK_ANSI_1, kVK_ANSI_2, kVK_ANSI_3, kVK_ANSI_4,
             kVK_ANSI_5, kVK_ANSI_6, kVK_ANSI_7, kVK_ANSI_8, kVK_ANSI_9,
             kVK_LeftArrow, kVK_RightArrow, kVK_UpArrow, kVK_DownArrow:
            return true
        default:
            return false
        }
    }

    var displayText: String {
        var parts: [String] = []
        if modifiers & UInt32(cmdKey) != 0 { parts.append("⌘") }
        if modifiers & UInt32(optionKey) != 0 { parts.append("⌥") }
        if modifiers & UInt32(controlKey) != 0 { parts.append("⌃") }
        if modifiers & UInt32(shiftKey) != 0 { parts.append("⇧") }
        parts.append(Self.keyName(for: keyCode))
        return parts.joined(separator: " ")
    }

    func matches(_ event: NSEvent) -> Bool {
        keyCode == UInt32(event.keyCode)
            && modifiers == Self.carbonModifiers(from: event.modifierFlags)
    }

    static let defaultValue = HotKeyConfiguration(
        keyCode: UInt32(kVK_Space),
        modifiers: UInt32(optionKey)
    )

    static let defaultClearSelectionValue = HotKeyConfiguration(
        keyCode: UInt32(kVK_Escape),
        modifiers: UInt32(cmdKey)
    )

    static let defaultPinValue = HotKeyConfiguration(
        keyCode: UInt32(kVK_ANSI_P),
        modifiers: UInt32(cmdKey | shiftKey)
    )

    static func carbonModifiers(from flags: NSEvent.ModifierFlags) -> UInt32 {
        var result: UInt32 = 0
        if flags.contains(.command) { result |= UInt32(cmdKey) }
        if flags.contains(.option) { result |= UInt32(optionKey) }
        if flags.contains(.control) { result |= UInt32(controlKey) }
        if flags.contains(.shift) { result |= UInt32(shiftKey) }
        return result
    }

    static func keyName(for keyCode: UInt32) -> String {
        keyNames[Int(keyCode)] ?? "Key \(keyCode)"
    }

    private static let keyNames: [Int: String] = [
        kVK_Space: "Space",
        kVK_Return: "Return",
        kVK_Tab: "Tab",
        kVK_Escape: "Esc",
        kVK_Delete: "Delete",
        kVK_ANSI_A: "A", kVK_ANSI_B: "B", kVK_ANSI_C: "C", kVK_ANSI_D: "D",
        kVK_ANSI_E: "E", kVK_ANSI_F: "F", kVK_ANSI_G: "G", kVK_ANSI_H: "H",
        kVK_ANSI_I: "I", kVK_ANSI_J: "J", kVK_ANSI_K: "K", kVK_ANSI_L: "L",
        kVK_ANSI_M: "M", kVK_ANSI_N: "N", kVK_ANSI_O: "O", kVK_ANSI_P: "P",
        kVK_ANSI_Q: "Q", kVK_ANSI_R: "R", kVK_ANSI_S: "S", kVK_ANSI_T: "T",
        kVK_ANSI_U: "U", kVK_ANSI_V: "V", kVK_ANSI_W: "W", kVK_ANSI_X: "X",
        kVK_ANSI_Y: "Y", kVK_ANSI_Z: "Z", kVK_ANSI_0: "0", kVK_ANSI_1: "1",
        kVK_ANSI_2: "2", kVK_ANSI_3: "3", kVK_ANSI_4: "4", kVK_ANSI_5: "5",
        kVK_ANSI_6: "6", kVK_ANSI_7: "7", kVK_ANSI_8: "8", kVK_ANSI_9: "9"
    ]
}

enum ClearSelectionHotKeyDefaults {
    static let changedNotification = Notification.Name("ClipShelfClearSelectionHotKeyChanged")
    private static let key = "clearSelectionHotKey.configuration"

    static func load() -> HotKeyConfiguration {
        guard let data = AppEnvironment.userDefaults.data(forKey: key),
              let configuration = try? JSONDecoder().decode(HotKeyConfiguration.self, from: data) else {
            return .defaultClearSelectionValue
        }
        return configuration
    }

    static func save(_ configuration: HotKeyConfiguration) {
        if let data = try? JSONEncoder().encode(configuration) {
            AppEnvironment.userDefaults.set(data, forKey: key)
            NotificationCenter.default.post(name: changedNotification, object: configuration)
        }
    }
}

enum PinHotKeyDefaults {
    static let changedNotification = Notification.Name("ClipShelfPinHotKeyChanged")
    private static let key = "pinHotKey.configuration"

    static func load() -> HotKeyConfiguration {
        guard let data = AppEnvironment.userDefaults.data(forKey: key),
              let configuration = try? JSONDecoder().decode(HotKeyConfiguration.self, from: data) else {
            return .defaultPinValue
        }
        return configuration
    }

    static func save(_ configuration: HotKeyConfiguration) {
        if let data = try? JSONEncoder().encode(configuration) {
            AppEnvironment.userDefaults.set(data, forKey: key)
            NotificationCenter.default.post(name: changedNotification, object: configuration)
        }
    }
}

enum HotKeyDefaults {
    static let changedNotification = Notification.Name("ClipShelfHotKeyChanged")
    private static let key = "globalHotKey.configuration"

    static func load() -> HotKeyConfiguration {
        guard let data = AppEnvironment.userDefaults.data(forKey: key),
              let configuration = try? JSONDecoder().decode(HotKeyConfiguration.self, from: data) else {
            return .defaultValue
        }
        return configuration
    }

    static func save(_ configuration: HotKeyConfiguration) {
        if let data = try? JSONEncoder().encode(configuration) {
            AppEnvironment.userDefaults.set(data, forKey: key)
            NotificationCenter.default.post(name: changedNotification, object: configuration)
        }
    }
}
