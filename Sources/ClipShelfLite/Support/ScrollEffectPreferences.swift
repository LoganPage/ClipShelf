import Foundation

/// How history rows behave as they reach the edges of the list.
enum ScrollEffectStyle: String, CaseIterable, Identifiable {
    /// A slight tilt and fade only at the very edges; nearly every row stays readable.
    case light
    /// The top stays plain (rows slide under the glass toolbar); only the bottom rolls away.
    case bottomOnly
    /// A clear picker-wheel roll at both edges, in a narrow band.
    case wheel
    case off

    var id: String { rawValue }

    var title: String {
        switch self {
        case .light: "轻度"
        case .bottomOnly: "只底部"
        case .wheel: "滚轮"
        case .off: "关闭"
        }
    }

    /// Near the edges the list bends away from the viewer, as if seen in
    /// perspective: rows recede, shrink towards the middle and darken. The bend
    /// grows with a power of the distance from the middle, so most of the list
    /// stays flat and fully readable and only the last row or two at each edge
    /// visibly recede. Rows still inside the visible area are never blurred.
    struct Parameters {
        /// Higher keeps more of the middle flat (bend = distance ^ power).
        let power: Double
        /// How much a row at the very edge shrinks, 0...1.
        let shrink: Double
        /// How far a row at the very edge tilts away, in degrees.
        let tilt: Double
        /// How far edge rows are pulled towards the middle, as a fraction of
        /// half the visible height.
        let pull: Double
        /// How much receding rows darken, 0...1.
        let shade: Double
        let curvesTop: Bool
        let curvesBottom: Bool
        /// Blur for rows that have slid past the visible edge.
        let outsideBlur: Double
    }

    var parameters: Parameters? {
        switch self {
        case .light:
            Parameters(power: 3, shrink: 0.08, tilt: 12, pull: 0.03, shade: 0.06, curvesTop: true, curvesBottom: true, outsideBlur: 2)
        case .bottomOnly:
            Parameters(power: 2.4, shrink: 0.14, tilt: 22, pull: 0.06, shade: 0.12, curvesTop: false, curvesBottom: true, outsideBlur: 3)
        case .wheel:
            Parameters(power: 2.2, shrink: 0.16, tilt: 24, pull: 0.07, shade: 0.14, curvesTop: true, curvesBottom: true, outsideBlur: 3)
        case .off:
            nil
        }
    }
}

enum ScrollEffectPreferences {
    static let changedNotification = Notification.Name("ClipShelfScrollEffectChanged")
    private static let key = "history.scrollEffect"

    static var value: ScrollEffectStyle {
        get {
            AppEnvironment.userDefaults.string(forKey: key).flatMap(ScrollEffectStyle.init(rawValue:)) ?? .light
        }
        set {
            AppEnvironment.userDefaults.set(newValue.rawValue, forKey: key)
            NotificationCenter.default.post(name: changedNotification, object: newValue)
        }
    }
}
