import AppKit
import SwiftUI

enum AppTheme {
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
    static let filePreviewBackground = adaptive(
        light: color(red: 0.91, green: 0.96, blue: 0.92),
        dark: color(red: 0.13, green: 0.24, blue: 0.18)
    )
    static let filePreviewForeground = adaptive(
        light: color(red: 0.12, green: 0.38, blue: 0.20),
        dark: color(red: 0.45, green: 0.86, blue: 0.67)
    )
    static let pdfPreviewBackground = adaptive(
        light: color(red: 1.0, green: 0.91, blue: 0.91),
        dark: color(red: 0.30, green: 0.13, blue: 0.15)
    )
    static let pdfPreviewForeground = adaptive(
        light: color(red: 0.72, green: 0.13, blue: 0.16),
        dark: color(red: 1.0, green: 0.52, blue: 0.54)
    )
    static let spreadsheetPreviewBackground = adaptive(
        light: color(red: 0.89, green: 0.97, blue: 0.91),
        dark: color(red: 0.12, green: 0.25, blue: 0.17)
    )
    static let spreadsheetPreviewForeground = adaptive(
        light: color(red: 0.08, green: 0.43, blue: 0.20),
        dark: color(red: 0.43, green: 0.87, blue: 0.59)
    )
    static let wordPreviewBackground = adaptive(
        light: color(red: 0.89, green: 0.94, blue: 1.0),
        dark: color(red: 0.11, green: 0.21, blue: 0.34)
    )
    static let wordPreviewForeground = adaptive(
        light: color(red: 0.10, green: 0.36, blue: 0.70),
        dark: color(red: 0.45, green: 0.72, blue: 1.0)
    )
    static let presentationPreviewBackground = adaptive(
        light: color(red: 1.0, green: 0.93, blue: 0.86),
        dark: color(red: 0.31, green: 0.19, blue: 0.10)
    )
    static let presentationPreviewForeground = adaptive(
        light: color(red: 0.76, green: 0.34, blue: 0.06),
        dark: color(red: 1.0, green: 0.67, blue: 0.34)
    )
    static let archivePreviewBackground = adaptive(
        light: color(red: 0.95, green: 0.90, blue: 0.99),
        dark: color(red: 0.24, green: 0.16, blue: 0.31)
    )
    static let archivePreviewForeground = adaptive(
        light: color(red: 0.47, green: 0.20, blue: 0.67),
        dark: color(red: 0.78, green: 0.57, blue: 1.0)
    )
    static let folderPreviewBackground = adaptive(
        light: color(red: 1.0, green: 0.96, blue: 0.82),
        dark: color(red: 0.30, green: 0.24, blue: 0.11)
    )
    static let folderPreviewForeground = adaptive(
        light: color(red: 0.67, green: 0.45, blue: 0.04),
        dark: color(red: 0.96, green: 0.76, blue: 0.31)
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
        switch category {
        case .pdf: pdfPreviewBackground
        case .spreadsheet: spreadsheetPreviewBackground
        case .word: wordPreviewBackground
        case .presentation: presentationPreviewBackground
        case .archive: archivePreviewBackground
        case .folder: folderPreviewBackground
        case .generic: filePreviewBackground
        }
    }

    static func fileTypeForeground(_ category: FileTypeIconCategory) -> Color {
        switch category {
        case .pdf: pdfPreviewForeground
        case .spreadsheet: spreadsheetPreviewForeground
        case .word: wordPreviewForeground
        case .presentation: presentationPreviewForeground
        case .archive: archivePreviewForeground
        case .folder: folderPreviewForeground
        case .generic: filePreviewForeground
        }
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
