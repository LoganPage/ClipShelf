import AppKit
import SwiftUI

enum AppIconChoice: Int, CaseIterable, Identifiable {
    case clipboardHistory = 1
    case historyList = 2
    case stackedHistory = 3
    case compactClipboard = 4

    var id: Int { rawValue }

    var title: String {
        "方案 \(rawValue)"
    }

    var resourceName: String {
        "AppIcon\(rawValue)"
    }

    var previewImage: NSImage? {
        AppIconPreferences.image(for: self)
    }
}

enum AppIconPreferences {
    static let changedNotification = Notification.Name("AppIconPreferences.changed")
    private static let selectedKey = "appIcon.selected"

    /// Only 方案 2 is offered now, everywhere.
    static var selected: AppIconChoice {
        get {
            .historyList
        }
        set {
            AppEnvironment.userDefaults.set(newValue.rawValue, forKey: selectedKey)
            apply(newValue)
            NotificationCenter.default.post(name: changedNotification, object: newValue)
        }
    }

    static func applySavedChoice() {
        apply(selected)
    }

    static func apply(_ choice: AppIconChoice) {
        // Keep the Dock icon owned by the app bundle's AppIcon.icns.
        // Runtime Dock image overrides make the running icon render at a
        // different visual size from the non-running icon.
    }

    // MARK: Liquid Glass icons (light and dark)

    /// Icons are read once. Views ask for these on every render, so reading
    /// the files from disk each time stalled the main thread.
    private static var previewCache = [Bool: NSImage]()
    private static var fullCache = [Bool: NSImage]()
    private static let previewPixelSize: CGFloat = 160

    /// The icon as drawn inside the app (title row), for light or dark mode.
    static func appImage(dark: Bool) -> NSImage? {
        if let cached = previewCache[dark] { return cached }
        guard let image = fullImage(dark: dark) else { return nil }
        let preview = downsampled(image)
        previewCache[dark] = preview
        return preview
    }

    /// The full-size icon, used for the running app's Dock tile.
    static func fullImage(dark: Bool) -> NSImage? {
        if let cached = fullCache[dark] { return cached }
        let name = dark ? "AppIconDark" : "AppIconLight"
        guard let url = Bundle.main.url(forResource: name, withExtension: "png"),
              let image = NSImage(contentsOf: url) else {
            return Bundle.main.url(forResource: "AppIcon", withExtension: "icns").flatMap(NSImage.init(contentsOf:))
        }
        fullCache[dark] = image
        return image
    }

    /// Kept for older call sites: the in-app icon for the current choice.
    static func image(for choice: AppIconChoice) -> NSImage? {
        appImage(dark: AppearancePreferences.resolvedColorScheme == .dark)
    }

    private static func downsampled(_ image: NSImage) -> NSImage {
        let pixels = Int(previewPixelSize)
        guard let rep = NSBitmapImageRep(
            bitmapDataPlanes: nil,
            pixelsWide: pixels,
            pixelsHigh: pixels,
            bitsPerSample: 8,
            samplesPerPixel: 4,
            hasAlpha: true,
            isPlanar: false,
            colorSpaceName: .deviceRGB,
            bytesPerRow: 0,
            bitsPerPixel: 0
        ) else { return image }
        rep.size = NSSize(width: previewPixelSize / 2, height: previewPixelSize / 2)
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        NSGraphicsContext.current?.imageInterpolation = .high
        image.draw(in: NSRect(origin: .zero, size: rep.size))
        NSGraphicsContext.restoreGraphicsState()
        let preview = NSImage(size: rep.size)
        preview.addRepresentation(rep)
        return preview
    }

    /// The menu bar glyph: a template image, so macOS tints it for light
    /// and dark menu bars by itself.
    static func statusBarImage(for choice: AppIconChoice) -> NSImage? {
        guard let image = Bundle.main.image(forResource: "AppStatusIcon") else {
            return nil
        }
        image.size = NSSize(width: 18, height: 18)
        image.isTemplate = true
        return image
    }
}
