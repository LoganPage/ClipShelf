import AppKit
import Combine
import SwiftUI

/// The system's display accessibility settings that change how ClipShelf
/// draws itself (System Settings → Accessibility → Display):
/// - Reduce Transparency: solid surfaces instead of see-through glass;
/// - Increase Contrast: stronger edges and more solid fills;
/// - Reduce Motion: no stretching, bouncing or flying windows.
///
/// For testing, each can be forced on with an environment variable
/// (CLIPSHELF_REDUCE_TRANSPARENCY=1, CLIPSHELF_INCREASE_CONTRAST=1,
/// CLIPSHELF_REDUCE_MOTION=1) without changing the system setting.
struct AccessibilityLook: Equatable {
    var reduceTransparency = false
    var increaseContrast = false
    var reduceMotion = false

    static var current: AccessibilityLook {
        AccessibilityLookMonitor.shared.look
    }

    static func read(
        workspace: NSWorkspace = .shared,
        environment: [String: String] = ProcessInfo.processInfo.environment
    ) -> AccessibilityLook {
        func forced(_ name: String) -> Bool { environment[name] == "1" }
        return AccessibilityLook(
            reduceTransparency: workspace.accessibilityDisplayShouldReduceTransparency
                || forced("CLIPSHELF_REDUCE_TRANSPARENCY"),
            increaseContrast: workspace.accessibilityDisplayShouldIncreaseContrast
                || forced("CLIPSHELF_INCREASE_CONTRAST"),
            reduceMotion: workspace.accessibilityDisplayShouldReduceMotion
                || forced("CLIPSHELF_REDUCE_MOTION")
        )
    }
}

/// Keeps `AccessibilityLook.current` in step with System Settings and tells
/// views and AppKit surfaces when it changes.
final class AccessibilityLookMonitor: ObservableObject {
    static let shared = AccessibilityLookMonitor()
    static let changedNotification = Notification.Name("ClipShelfAccessibilityLookChanged")

    @Published private(set) var look = AccessibilityLook.read()
    private var observer: NSObjectProtocol?

    private init() {
        observer = NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.accessibilityDisplayOptionsDidChangeNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            self?.refresh()
        }
    }

    private func refresh() {
        let next = AccessibilityLook.read()
        guard next != look else { return }
        look = next
        NotificationCenter.default.post(name: Self.changedNotification, object: next)
    }
}

private struct AccessibilityLookKey: EnvironmentKey {
    static let defaultValue = AccessibilityLook()
}

extension EnvironmentValues {
    /// Set at each window's root (see `clipShelfAccessibilityLook()`).
    var accessibilityLook: AccessibilityLook {
        get { self[AccessibilityLookKey.self] }
        set { self[AccessibilityLookKey.self] = newValue }
    }
}

private struct AccessibilityLookRoot: ViewModifier {
    @ObservedObject private var monitor = AccessibilityLookMonitor.shared

    func body(content: Content) -> some View {
        content.environment(\.accessibilityLook, monitor.look)
    }
}

extension View {
    /// Hands the current accessibility settings to everything in this window.
    func clipShelfAccessibilityLook() -> some View {
        modifier(AccessibilityLookRoot())
    }
}

// MARK: - Solid surfaces (Reduce Transparency)

/// Opaque colours drawn in place of glass when Reduce Transparency is on.
/// Three steps of grey keep the window, the cards on it and the controls on
/// the cards apart without any see-through.
enum SolidSurface {
    static func window(isDark: Bool) -> Color {
        isDark ? Color(white: 0.11) : Color(white: 0.9)
    }

    static func card(isDark: Bool) -> Color {
        isDark ? Color(white: 0.18) : Color(white: 1)
    }

    static func control(isDark: Bool) -> Color {
        isDark ? Color(white: 0.26) : Color(white: 0.95)
    }

    static func windowColor(isDark: Bool) -> NSColor {
        isDark ? NSColor(white: 0.11, alpha: 1) : NSColor(white: 0.9, alpha: 1)
    }

    static func controlColor(isDark: Bool) -> NSColor {
        isDark ? NSColor(white: 0.26, alpha: 1) : NSColor(white: 0.95, alpha: 1)
    }
}

/// The outline every surface gets: the usual hairline, or a clearly
/// visible line when Increase Contrast is on.
enum SurfaceEdge {
    static func color(isDark: Bool, look: AccessibilityLook) -> Color {
        if look.increaseContrast {
            return isDark ? Color.white.opacity(0.55) : Color.black.opacity(0.5)
        }
        return isDark ? Color.white.opacity(0.16) : Color.black.opacity(0.1)
    }

    static func width(look: AccessibilityLook) -> CGFloat {
        look.increaseContrast ? 1 : 0.75
    }
}

// MARK: - Motion (Reduce Motion)

extension Animation {
    /// `animation`, or a short plain ease when Reduce Motion is on: no
    /// overshoot or bounce.
    static func clipShelf(_ animation: Animation) -> Animation {
        AccessibilityLook.current.reduceMotion ? .easeInOut(duration: 0.18) : animation
    }
}
