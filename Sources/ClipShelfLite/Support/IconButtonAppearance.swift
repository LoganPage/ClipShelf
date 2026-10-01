import SwiftUI

enum IconButtonAppearance {
    static func background(
        isSelected: Bool,
        isPressed: Bool,
        isHovered: Bool
    ) -> Color {
        if isPressed {
            return isSelected
                ? AppTheme.selectedActionBackground.opacity(1.5)
                : AppTheme.actionButtonBackground.opacity(1.2)
        }
        if isHovered {
            return AppTheme.actionButtonHoverBackground
        }
        return isSelected ? AppTheme.selectedActionBackground : AppTheme.actionButtonBackground
    }
}
