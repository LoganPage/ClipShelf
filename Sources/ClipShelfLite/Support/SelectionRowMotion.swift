import Foundation
import SwiftUI

enum SelectionRowMotion {
    static let duration: TimeInterval = 0.11
    static let animation: Animation = .easeOut(duration: duration)

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
