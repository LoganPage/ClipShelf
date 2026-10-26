import SwiftUI

enum IconButtonAppearance {
    /// Row icons rest without a plate so the card stays light; hover and press
    /// reveal a soft surface.
    static func background(
        isSelected: Bool,
        isPressed: Bool,
        isHovered: Bool
    ) -> Color {
        if isPressed {
            return Color.primary.opacity(isSelected ? 0.2 : 0.14)
        }
        if isHovered {
            return Color.primary.opacity(isSelected ? 0.12 : 0.07)
        }
        return .clear
    }
}
