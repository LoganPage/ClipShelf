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

    /// Icons are read once and shrunk to the largest size the UI draws
    /// (76pt tiles at 2x). Views ask for these on every render, so reading the
    /// multi-megabyte .icns from disk each time stalled the main thread.
    private static var imageCache = [AppIconChoice: NSImage]()
    private static let previewPixelSize: CGFloat = 160

    static func image(for choice: AppIconChoice) -> NSImage? {
        if let cached = imageCache[choice] {
            return cached
        }
        guard let image = loadImage(for: choice) else { return nil }
        let preview = downsampled(image)
        imageCache[choice] = preview
        return preview
    }

    private static func loadImage(for choice: AppIconChoice) -> NSImage? {
        if let url = Bundle.main.url(forResource: choice.resourceName, withExtension: "icns") {
            return NSImage(contentsOf: url)
        }

        if let url = Bundle.main.url(forResource: "AppIcon", withExtension: "icns") {
            return NSImage(contentsOf: url)
        }

        return nil
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

    static func statusBarImage(for choice: AppIconChoice) -> NSImage? {
        guard let url = Bundle.main.url(forResource: "AppStatusIcon", withExtension: "png"),
              let image = NSImage(contentsOf: url) else {
            return nil
        }

        image.size = NSSize(width: 18, height: 18)
        image.isTemplate = true
        return image
    }
}
