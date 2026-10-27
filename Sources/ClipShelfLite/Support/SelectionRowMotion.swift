import Foundation
import SwiftUI

enum SelectionRowMotion {
    /// Spring response. Long enough for the selection glass to visibly flow
    /// from one record to the next, damped so it settles without wobbling.
    static let duration: TimeInterval = 0.38
    static let dampingFraction: Double = 0.78
    static let animation: Animation = .spring(response: duration, dampingFraction: dampingFraction)

    static func surfaceOpacity(
        isSelected: Bool,
        isDark: Bool,
        isPreset: Bool
    ) -> Double {
        guard isSelected else {
            return 0
        }
        return isDark && !isPreset ? 0.68 : 1
    }
}
