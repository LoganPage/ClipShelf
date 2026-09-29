import AppKit
import SwiftUI

enum AppTheme {
    struct FileTypePalette: Equatable {
        let lightBackground: SIMD3<Double>
        let darkBackground: SIMD3<Double>
        let lightForeground: SIMD3<Double>
        let darkForeground: SIMD3<Double>
    }

    static let appBackground = adaptive(
        light: color(red: 0.965, green: 0.968, blue: 0.976),
        dark: color(red: 0.075, green: 0.082, blue: 0.098)
    )
    static let toolbarBackground = adaptive(
        light: color(red: 0.965, green: 0.968, blue: 0.976),
        dark: color(red: 0.075, green: 0.082, blue: 0.098)
    )
    static let rowBackground = adaptive(
        light: .white,
        dark: color(red: 0.125, green: 0.137, blue: 0.16)
    )
    static let searchBackground = adaptive(
        light: .white,
        dark: color(red: 0.105, green: 0.113, blue: 0.131)
    )
    static let settingsBackground = adaptive(
        light: .white,
        dark: color(red: 0.118, green: 0.129, blue: 0.151)
    )
    static let subtleBorder = adaptive(
        light: color(red: 0.84, green: 0.85, blue: 0.88),
        dark: color(red: 0.22, green: 0.24, blue: 0.28)
    )
    static let focusedBackground = adaptive(
        light: color(red: 0.91, green: 0.945, blue: 1.0),
        dark: color(red: 0.14, green: 0.20, blue: 0.28)
    )
    static let selectedBackground = adaptive(
        light: color(red: 0.16, green: 0.44, blue: 0.86),
        dark: color(red: 0.12, green: 0.30, blue: 0.50)
    )
    static let iconBackground = adaptive(
        light: color(red: 0.18, green: 0.20, blue: 0.24),
        dark: color(red: 0.88, green: 0.90, blue: 0.94)
    )
    static let iconForeground = adaptive(
        light: .white,
        dark: color(red: 0.12, green: 0.14, blue: 0.17)
    )
    static let textPreviewBackground = adaptive(
        light: color(red: 0.90, green: 0.95, blue: 0.98),
        dark: color(red: 0.12, green: 0.22, blue: 0.26)
    )
    static let textPreviewForeground = adaptive(
        light: color(red: 0.10, green: 0.32, blue: 0.42),
        dark: color(red: 0.48, green: 0.84, blue: 0.90)
    )
    static let imagePreviewBackground = adaptive(
        light: color(red: 0.98, green: 0.94, blue: 0.88),
        dark: color(red: 0.26, green: 0.20, blue: 0.14)
    )
    static let imagePreviewForeground = adaptive(
        light: color(red: 0.56, green: 0.30, blue: 0.04),
        dark: color(red: 0.96, green: 0.76, blue: 0.42)
    )
    static let imageThumbnailBackground = adaptive(
        light: color(red: 0.93, green: 0.94, blue: 0.96),
        dark: color(red: 0.17, green: 0.18, blue: 0.21)
    )
    static let actionButtonBackground = adaptive(
        light: color(red: 0.91, green: 0.92, blue: 0.94, alpha: 0.78),
        dark: color(red: 0.25, green: 0.27, blue: 0.32, alpha: 0.90)
    )
    static let selectedActionBackground = adaptive(
        light: color(red: 0.0, green: 0.0, blue: 0.0, alpha: 0.07),
        dark: color(red: 1.0, green: 1.0, blue: 1.0, alpha: 0.13)
    )
    static let overlayBackground = adaptive(
        light: color(red: 0.0, green: 0.0, blue: 0.0, alpha: 0.16),
        dark: color(red: 0.0, green: 0.0, blue: 0.0, alpha: 0.46)
    )

    static func fileTypeBackground(_ category: FileTypeIconCategory) -> Color {
        let palette = fileTypePalette(category)
        return adaptive(light: color(palette.lightBackground), dark: color(palette.darkBackground))
    }

    static func fileTypeForeground(_ category: FileTypeIconCategory) -> Color {
        let palette = fileTypePalette(category)
        return adaptive(light: color(palette.lightForeground), dark: color(palette.darkForeground))
    }

    static func fileTypePalette(_ category: FileTypeIconCategory) -> FileTypePalette {
        switch category {
        case .pdf:
            FileTypePalette(
                lightBackground: SIMD3(1.0, 0.91, 0.91), darkBackground: SIMD3(0.30, 0.13, 0.15),
                lightForeground: SIMD3(0.72, 0.13, 0.16), darkForeground: SIMD3(1.0, 0.52, 0.54)
            )
        case .spreadsheet:
            FileTypePalette(
                lightBackground: SIMD3(0.89, 0.97, 0.91), darkBackground: SIMD3(0.12, 0.25, 0.17),
                lightForeground: SIMD3(0.08, 0.43, 0.20), darkForeground: SIMD3(0.43, 0.87, 0.59)
            )
        case .word:
            FileTypePalette(
                lightBackground: SIMD3(0.89, 0.94, 1.0), darkBackground: SIMD3(0.11, 0.21, 0.34),
                lightForeground: SIMD3(0.10, 0.36, 0.70), darkForeground: SIMD3(0.45, 0.72, 1.0)
            )
        case .presentation:
            FileTypePalette(
                lightBackground: SIMD3(1.0, 0.93, 0.86), darkBackground: SIMD3(0.31, 0.19, 0.10),
                lightForeground: SIMD3(0.76, 0.34, 0.06), darkForeground: SIMD3(1.0, 0.67, 0.34)
            )
        case .archive:
            FileTypePalette(
                lightBackground: SIMD3(0.95, 0.90, 0.99), darkBackground: SIMD3(0.24, 0.16, 0.31),
                lightForeground: SIMD3(0.47, 0.20, 0.67), darkForeground: SIMD3(0.78, 0.57, 1.0)
            )
        case .folder:
            FileTypePalette(
                lightBackground: SIMD3(1.0, 0.96, 0.82), darkBackground: SIMD3(0.30, 0.24, 0.11),
                lightForeground: SIMD3(0.67, 0.45, 0.04), darkForeground: SIMD3(0.96, 0.76, 0.31)
            )
        case .generic:
            FileTypePalette(
                lightBackground: SIMD3(0.88, 0.88, 0.88), darkBackground: SIMD3(0.24, 0.24, 0.24),
                lightForeground: SIMD3(0.34, 0.34, 0.34), darkForeground: SIMD3(0.72, 0.72, 0.72)
            )
        }
    }

    private static func color(_ components: SIMD3<Double>) -> NSColor {
        color(red: components.x, green: components.y, blue: components.z)
    }

    private static func color(red: CGFloat, green: CGFloat, blue: CGFloat, alpha: CGFloat = 1) -> NSColor {
        NSColor(calibratedRed: red, green: green, blue: blue, alpha: alpha)
    }

    static func adaptive(light: NSColor, dark: NSColor) -> Color {
        let dynamic = NSColor(name: nil) { appearance in
            appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua ? dark : light
        }
        return Color(nsColor: dynamic)
    }
}
