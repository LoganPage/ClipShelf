import UniformTypeIdentifiers
import AppKit
import Foundation
import QuickLookUI

final class PreviewController: NSObject, QLPreviewPanelDataSource, QLPreviewPanelDelegate {
    static let shared = PreviewController()

    private var previewEntries: [PreviewEntry] = []
    private var temporaryURLs: [URL] = []
    private var previewWindow: NSWindow?
    private var keyMonitor: Any?
    private var scrollWheelMonitor: Any?
    private var onNavigate: ((Int) -> Bool)?
    private var imageZoomSession: ImageZoomSession?
    private var imageOCRSession: ImageOCRSession?
    private var textPreviewSession: TextPreviewSession?

    /// Screen rect of the record the glass preview grew out of, so it can
    /// shrink back into it.
    private var sourceRect: NSRect?
    /// Set for the next glass preview that should grow out of a record.
    private var pendingSource: NSRect?
    /// The record on show, to draw the closing animation from.
    private var previewedItem: ClipItem?

    /// - Parameter source: screen rect of the record, to grow the glass
    ///   preview out of (and back into).
    func togglePreview(_ items: [ClipItem], from source: NSRect? = nil, onNavigate: ((Int) -> Bool)? = nil) {
        if closeIfVisible() {
            return
        }

        pendingSource = source
        sourceRect = source
        preview(items, onNavigate: onNavigate)
        pendingSource = nil
    }

    /// - Parameter source: when stepping through records, where the newly
    ///   shown one is, so closing (Space, Esc or ×) shrinks back into it.
    func preview(_ items: [ClipItem], from source: NSRect? = nil, onNavigate: ((Int) -> Bool)? = nil) {
        guard items.count == 1 else { return }
        if let source { sourceRect = source }

        self.onNavigate = onNavigate

        if let panel = QLPreviewPanel.shared(), panel.isVisible {
            panel.orderOut(nil)
        }
        imageZoomSession = nil
        imageOCRSession?.cancel()
        imageOCRSession = nil
        textPreviewSession?.cancel()
        textPreviewSession = nil

        let keepTemporary = temporaryURLs
        temporaryURLs = []
        guard let item = items.first, let url = previewURL(for: item) else { return }
        previewedItem = item

        let panel = quickPanel ?? makeQuickPanel()
        let title = item.kind == .file ? url.lastPathComponent : item.title
        let size = quickLookSize(for: item, url: url)
        let visible = (NSScreen.main ?? panel.screen)?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1200, height: 800)
        let frame = centeredFrame(for: size, in: visible)
        previewWindow = panel
        startKeyMonitor()

        // Stepping with the arrow keys: same window, new content and size.
        if panel.isVisible, !panel.styleMask.contains(.fullScreen) {
            // Window and content change in the same frame (an animated
            // resize lagged behind the new content).
            panel.disableScreenUpdatesUntilFlush()
            panel.setFrame(frame, display: false)
            panel.show(url: url, title: title)
            panel.displayIfNeeded()
            removeTemporaryFiles(keepTemporary)
            return
        }
        panel.show(url: url, title: title)
        removeTemporaryFiles(keepTemporary)
        if !panel.styleMask.contains(.fullScreen) {
            panel.setFrame(frame, display: true)
        }
        AppearancePreferences.apply(to: panel)
        NSApp.activate(ignoringOtherApps: true)

        // Pours out of the record (the genie), then the window itself fades
        // in. Quick Look draws its content in another process, which can't be
        // captured, so the animation shows a stand-in made from the record.
        guard let source = pendingSource, let image = standInImage(for: item, size: frame.size) else {
            panel.alphaValue = 1
            panel.makeKeyAndOrderFront(nil)
            return
        }
        panel.alphaValue = 0
        panel.makeKeyAndOrderFront(nil)
        GenieAnimator.run(image: image, from: source, to: frame, opening: true, duration: 0.45, above: nil, crossfade: 0.25, level: .floating) { [weak panel] in
            guard let panel else { return }
            NSAnimationContext.runAnimationGroup { context in
                context.duration = 0.16
                panel.animator().alphaValue = 1
            }
        }
    }

    private var quickPanel: QuickPreviewPanel?

    private func makeQuickPanel() -> QuickPreviewPanel {
        let panel = QuickPreviewPanel()
        panel.onClose = { [weak self] in self?.closeIfVisible() }
        panel.onSpace = { [weak self] in self?.closeIfVisible() }
        quickPanel = panel
        return panel
    }

    /// Like Quick Look: an image gets a window of its own proportions, other
    /// kinds the standard size.
    private func quickLookSize(for item: ClipItem, url: URL) -> NSSize {
        let visible = (NSScreen.main?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1200, height: 800))
        let header = QuickPreviewPanel.headerHeight
        let image = image(for: item) ?? (Self.isImageFile(url) ? NSImage(contentsOf: url) : nil)
        if let image, image.size.width > 0, image.size.height > 0 {
            let maxSize = NSSize(width: visible.width * 0.6, height: visible.height * 0.75 - header)
            let fit = min(maxSize.width / image.size.width, maxSize.height / image.size.height, 1)
            return NSSize(
                width: max(image.size.width * fit, 360),
                height: max(image.size.height * fit, 200) + header
            )
        }
        return NSSize(width: min(816, visible.width * 0.8), height: min(728, visible.height * 0.85))
    }

    private func removeTemporaryFiles(_ urls: [URL]) {
        for url in urls {
            let folder = url.deletingLastPathComponent()
            try? FileManager.default.removeItem(at: folder.lastPathComponent.hasPrefix("ClipShelfPreview-") ? folder : url)
        }
    }

    /// The record drawn the way the panel shows it: its image, the start
    /// of its text, or its file icon, on the panel's background with a
    /// title bar.
    private func standInImage(for item: ClipItem, size: NSSize) -> CGImage? {
        let scale = NSScreen.main?.backingScaleFactor ?? 2
        guard size.width > 0, size.height > 0,
              let rep = NSBitmapImageRep(
                bitmapDataPlanes: nil,
                pixelsWide: Int(size.width * scale),
                pixelsHigh: Int(size.height * scale),
                bitsPerSample: 8,
                samplesPerPixel: 4,
                hasAlpha: true,
                isPlanar: false,
                colorSpaceName: .deviceRGB,
                bytesPerRow: 0,
                bitsPerPixel: 0
              ) else { return nil }
        rep.size = size
        let isDark = AppearancePreferences.resolvedColorScheme == .dark
        NSGraphicsContext.saveGraphicsState()
        defer { NSGraphicsContext.restoreGraphicsState() }
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        let bounds = NSRect(origin: .zero, size: size)
        NSBezierPath(roundedRect: bounds, xRadius: 16, yRadius: 16).addClip()
        (isDark ? NSColor(white: 0.16, alpha: 1) : NSColor(white: 0.97, alpha: 1)).setFill()
        bounds.fill()
        let barHeight: CGFloat = 52
        (isDark ? NSColor(white: 0.22, alpha: 1) : NSColor(white: 0.92, alpha: 1)).setFill()
        NSRect(x: 0, y: size.height - barHeight, width: size.width, height: barHeight).fill()
        let titleAttributes: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 13, weight: .semibold),
            .foregroundColor: isDark ? NSColor(white: 0.9, alpha: 1) : NSColor(white: 0.2, alpha: 1)
        ]
        (item.title as NSString).draw(
            in: NSRect(x: 64, y: size.height - barHeight + 17, width: size.width - 128, height: 18),
            withAttributes: titleAttributes
        )
        let content = NSRect(x: 0, y: 0, width: size.width, height: size.height - barHeight)
        if let image = image(for: item) {
            // Aspect-fit, like the panel.
            let fit = min(content.width / max(image.size.width, 1), content.height / max(image.size.height, 1), 1)
            let drawn = NSSize(width: image.size.width * fit, height: image.size.height * fit)
            image.draw(in: NSRect(x: content.midX - drawn.width / 2, y: content.midY - drawn.height / 2, width: drawn.width, height: drawn.height))
        } else if item.kind == .text {
            let text = String((item.text ?? item.title).prefix(4000))
            (text as NSString).draw(
                in: content.insetBy(dx: 14, dy: 10),
                withAttributes: [
                    .font: NSFont.monospacedSystemFont(ofSize: 12, weight: .regular),
                    .foregroundColor: isDark ? NSColor.white : NSColor.black
                ]
            )
        } else if let path = item.filePaths.first {
            let icon = NSWorkspace.shared.icon(forFile: path)
            let side = min(content.width, content.height) * 0.4
            icon.draw(in: NSRect(x: content.midX - side / 2, y: content.midY - side / 2, width: side, height: side))
        }
        return rep.cgImage
    }

    func numberOfPreviewItems(in panel: QLPreviewPanel!) -> Int {
        previewEntries.count
    }

    func previewPanel(_ panel: QLPreviewPanel!, previewItemAt index: Int) -> QLPreviewItem! {
        previewEntries[index].url as NSURL
    }



    private func previewEntry(for item: ClipItem) -> PreviewEntry? {
        guard let url = previewURL(for: item) else { return nil }
        return PreviewEntry(
            url: url,
            recommendedSize: recommendedSize(for: item, url: url)
        )
    }

    private func previewURL(for item: ClipItem) -> URL? {
        switch item.kind {
        case .file:
            return item.filePaths.first.map { URL(fileURLWithPath: $0) }
        case .image:
            if let sourcePath = item.sourcePath, FileManager.default.fileExists(atPath: sourcePath) {
                return URL(fileURLWithPath: sourcePath)
            }

            if let storedURL = item.imageURL {
                // A copy under a readable name: the stored file is named by id.
                let url = temporaryURL(for: item, fallbackName: "剪贴板图片", extension: storedURL.pathExtension)
                try? FileManager.default.removeItem(at: url)
                guard (try? FileManager.default.copyItem(at: storedURL, to: url)) != nil else { return nil }
                temporaryURLs.append(url)
                return url
            }
            guard let imageData = item.legacyImageData else { return nil }
            let url = temporaryURL(for: item, fallbackName: "剪贴板图片", extension: "png")
            try? imageData.write(to: url, options: .atomic)
            temporaryURLs.append(url)
            return url
        case .text:
            let url = temporaryURL(for: item, fallbackName: "剪贴板文字", extension: "txt")
            try? (item.text ?? item.title).write(to: url, atomically: true, encoding: .utf8)
            temporaryURLs.append(url)
            return url
        }
    }

    /// A temporary file named after the record (Quick Look shows the name
    /// as its title), in a folder of its own so names never clash.
    private func temporaryURL(for item: ClipItem, fallbackName: String, extension ext: String) -> URL {
        let folder = FileManager.default.temporaryDirectory
            .appendingPathComponent("ClipShelfPreview-\(item.id.uuidString)", isDirectory: true)
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        var name = item.title
            .replacingOccurrences(of: "/", with: "／")
            .replacingOccurrences(of: ":", with: "：")
            .trimmingCharacters(in: .whitespacesAndNewlines)
        if let firstLine = name.split(whereSeparator: \.isNewline).first { name = String(firstLine) }
        if name.count > 60 { name = String(name.prefix(60)) + "…" }
        if name.isEmpty { name = fallbackName }
        if (name as NSString).pathExtension.lowercased() != ext { name += ".\(ext)" }
        return folder.appendingPathComponent(name)
    }

    @discardableResult
    func closeIfVisible() -> Bool {
        var didClose = false

        if let window = previewWindow, window.isVisible {
            // Shrinks back into the record it came from.
            if let source = sourceRect, let item = previewedItem,
               !window.styleMask.contains(.fullScreen),
               let image = standInImage(for: item, size: window.frame.size) {
                let frame = window.frame
                window.orderOut(nil)
                GenieAnimator.run(image: image, from: source, to: frame, opening: false, duration: 0.42, above: nil, level: .floating) {}
            } else if window.styleMask.contains(.fullScreen) {
                window.toggleFullScreen(nil)
                window.orderOut(nil)
            } else {
                window.orderOut(nil)
            }
            didClose = true
        }

        if let panel = QLPreviewPanel.shared(), panel.isVisible {
            // Shrinks back into its record the same way it came out.
            if let source = sourceRect, let item = previewedItem,
               let image = standInImage(for: item, size: panel.frame.size) {
                let frame = panel.frame
                panel.orderOut(nil)
                panel.alphaValue = 1
                GenieAnimator.run(image: image, from: source, to: frame, opening: false, duration: 0.42, above: nil, level: .floating) {}
            } else {
                panel.orderOut(nil)
            }
            didClose = true
        }

        if didClose {
            stopKeyMonitor()
            imageZoomSession = nil
            imageOCRSession?.cancel()
            imageOCRSession = nil
            textPreviewSession?.cancel()
            textPreviewSession = nil
            onNavigate = nil
        }

        return didClose
    }

    private func showBuiltInPreviewIfPossible(_ items: [ClipItem]) -> Bool {
        guard items.count == 1, let item = items.first else {
            return false
        }

        switch item.kind {
        case .image:
            if let image = image(for: item) {
                showImagePreview(image, title: item.title)
                return true
            }
            return false
        case .text:
            showTextPreview(item.text ?? item.title, title: item.title)
            return true
        case .file:
            guard item.filePaths.count == 1 else { return false }
            let url = URL(fileURLWithPath: item.filePaths[0])
            // Only image files are loaded as images; reading an arbitrary large
            // file just to find out it isn't one used to stall the main thread.
            if Self.isImageFile(url), let image = NSImage(contentsOf: url) {
                showImagePreview(image, title: url.lastPathComponent)
                return true
            }
            if TextEncodingDetector.isTextFile(url) {
                showTextFilePreview(url)
                return true
            }
            return false
        }
    }

    private static func isImageFile(_ url: URL) -> Bool {
        guard let type = UTType(filenameExtension: url.pathExtension.lowercased()) else { return false }
        return type.conforms(to: .image)
    }

    private func image(for item: ClipItem) -> NSImage? {
        if let url = item.imageURL, let image = NSImage(contentsOf: url) {
            return image
        }
        if let imageData = item.legacyImageData, let image = NSImage(data: imageData) {
            return image
        }

        if let sourcePath = item.sourcePath {
            return NSImage(contentsOf: URL(fileURLWithPath: sourcePath))
        }

        return nil
    }

    private func showImagePreview(_ image: NSImage, title: String) {
        let panel = previewPanel(title: title, size: NSSize(width: 760, height: 560))
        let rootView = NSView(frame: panel.contentView?.bounds ?? .zero)
        rootView.autoresizingMask = [.width, .height]
        let toolbarHeight: CGFloat = 46

        let scrollView = NSScrollView(frame: NSRect(
            x: 0,
            y: 0,
            width: rootView.bounds.width,
            height: max(1, rootView.bounds.height - toolbarHeight)
        ))
        scrollView.autoresizingMask = [.width, .height]
        scrollView.hasVerticalScroller = true
        scrollView.hasHorizontalScroller = true
        scrollView.drawsBackground = false

        let imageView = NSImageView()
        imageView.image = image
        imageView.imageScaling = .scaleProportionallyUpOrDown
        imageView.imageAlignment = .alignCenter
        imageView.frame = scrollView.contentView.bounds

        scrollView.documentView = imageView
        rootView.addSubview(scrollView)

        let ocrSession = ImageOCRSession(image: image, imageView: imageView)
        let ocrToolbar = ocrSession.makeToolbar()
        // Floats as a glass capsule, inset from the edges.
        ocrToolbar.frame = NSRect(
            x: 12,
            y: rootView.bounds.height - toolbarHeight + 2,
            width: rootView.bounds.width - 24,
            height: toolbarHeight - 6
        )
        ocrToolbar.autoresizingMask = [.width, .minYMargin]
        rootView.addSubview(ocrToolbar)

        let zoomSession = ImageZoomSession(scrollView: scrollView, imageView: imageView)
        let controls = zoomSession.makeControls()
        controls.frame.origin = NSPoint(
            x: rootView.bounds.width - controls.frame.width - 14,
            y: 14
        )
        controls.autoresizingMask = [.minXMargin, .maxYMargin]
        rootView.addSubview(controls)

        imageZoomSession = zoomSession
        imageOCRSession = ocrSession
        panel.contentView = rootView
        show(panel, firstResponder: scrollView)
    }

    private func showTextPreview(_ text: String, title: String) {
        let panel = previewPanel(title: title, size: NSSize(width: 640, height: 460))
        let session = TextPreviewSession(
            frame: panel.contentView?.bounds ?? .zero,
            text: text,
            encodingName: "剪贴板文字 · UTF-8"
        )
        textPreviewSession = session
        panel.contentView = session.rootView
        show(panel, firstResponder: session.firstResponder)
    }

    private func showTextFilePreview(_ url: URL) {
        let panel = previewPanel(title: url.lastPathComponent, size: NSSize(width: 640, height: 460))
        let session = TextPreviewSession(
            frame: panel.contentView?.bounds ?? .zero,
            text: "正在读取文本文件…",
            encodingName: "正在检测编码…"
        )
        textPreviewSession = session
        panel.contentView = session.rootView
        show(panel, firstResponder: session.firstResponder)
        session.loadFile(url)
    }

    private func recommendedSize(for item: ClipItem, url: URL) -> NSSize {
        switch item.kind {
        case .image:
            let image = image(for: item) ?? NSImage(contentsOf: url)
            return recommendedImageWindowSize(for: image?.size)
        case .text:
            return NSSize(width: 760, height: 560)
        case .file:
            return NSSize(width: 900, height: 700)
        }
    }

    private func recommendedImageWindowSize(for imageSize: NSSize?) -> NSSize {
        let minimum = NSSize(width: 520, height: 380)
        guard let imageSize,
              imageSize.width > 0,
              imageSize.height > 0 else {
            return NSSize(width: 760, height: 560)
        }

        let maxSize = maximumPreviewWindowSize()
        let paddedSize = NSSize(
            width: imageSize.width + 96,
            height: imageSize.height + 120
        )
        let scale = min(
            maxSize.width / paddedSize.width,
            maxSize.height / paddedSize.height,
            1
        )

        return NSSize(
            width: min(max(paddedSize.width * scale, minimum.width), maxSize.width),
            height: min(max(paddedSize.height * scale, minimum.height), maxSize.height)
        )
    }

    private func maximumPreviewWindowSize() -> NSSize {
        let visibleFrame = NSScreen.main?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1200, height: 800)
        return NSSize(
            width: max(520, visibleFrame.width * 0.9),
            height: max(380, visibleFrame.height * 0.9)
        )
    }

    private func constrainedPreviewSize(_ size: NSSize, for panel: NSWindow) -> NSSize {
        let visibleFrame = (panel.screen ?? NSScreen.main)?.visibleFrame
            ?? NSRect(x: 0, y: 0, width: 1200, height: 800)
        let minimum = NSSize(width: 520, height: 380)
        let maximum = NSSize(width: visibleFrame.width * 0.92, height: visibleFrame.height * 0.92)

        return NSSize(
            width: min(max(size.width, minimum.width), maximum.width),
            height: min(max(size.height, minimum.height), maximum.height)
        )
    }

    private func applyFixedPreviewFrame(to panel: NSWindow) {
        let size = fixedPreviewWindowSize(for: panel)
        let visibleFrame = (panel.screen ?? NSScreen.main)?.visibleFrame
            ?? NSRect(x: 0, y: 0, width: 1200, height: 800)
        let frame = centeredFrame(for: size, in: visibleFrame)
        panel.setFrame(frame, display: true, animate: false)
    }

    private func fixedPreviewWindowSize(for panel: NSWindow) -> NSSize {
        let fallback = NSSize(width: 760, height: 560)
        let largestSize = previewEntries.reduce(fallback) { partial, entry in
            NSSize(
                width: max(partial.width, entry.recommendedSize.width),
                height: max(partial.height, entry.recommendedSize.height)
            )
        }

        return constrainedPreviewSize(largestSize, for: panel)
    }

    private func centeredFrame(for size: NSSize, in visibleFrame: NSRect) -> NSRect {
        let anchor = centerPoint(of: visibleFrame)
        var frame = NSRect(
            x: anchor.x - size.width / 2,
            y: anchor.y - size.height / 2,
            width: size.width,
            height: size.height
        )

        if frame.minX < visibleFrame.minX {
            frame.origin.x = visibleFrame.minX
        }
        if frame.maxX > visibleFrame.maxX {
            frame.origin.x = visibleFrame.maxX - frame.width
        }
        if frame.minY < visibleFrame.minY {
            frame.origin.y = visibleFrame.minY
        }
        if frame.maxY > visibleFrame.maxY {
            frame.origin.y = visibleFrame.maxY - frame.height
        }

        return frame
    }

    private func centerPoint(of rect: NSRect) -> CGPoint {
        CGPoint(x: rect.midX, y: rect.midY)
    }

    static let glassRadius: CGFloat = 26

    private func previewPanel(title: String, size: NSSize) -> NSPanel {
        previewWindow?.orderOut(nil)

        // Liquid Glass like the main window: transparent titlebar over a
        // system glass base, its border following the glass's corners.
        let panel = SpaceClosablePanel(
            contentRect: NSRect(origin: .zero, size: size),
            styleMask: [.titled, .closable, .resizable, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        panel.titlebarAppearsTransparent = true
        AppearancePreferences.apply(to: panel)
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.matchSystemCornerRadius(Self.glassRadius)
        for button in [NSWindow.ButtonType.miniaturizeButton, .zoomButton] {
            panel.standardWindowButton(button)?.isHidden = true
        }
        panel.title = title
        panel.onSpace = { [weak self] in
            self?.closeIfVisible()
        }
        panel.onClose = { [weak self] in
            self?.previewWindowDidClose()
        }
        panel.isReleasedWhenClosed = false
        panel.level = .floating
        panel.center()
        previewWindow = panel
        return panel
    }

    private func previewWindowDidClose() {
        stopKeyMonitor()
        imageZoomSession = nil
        imageOCRSession?.cancel()
        imageOCRSession = nil
        textPreviewSession?.cancel()
        textPreviewSession = nil
        onNavigate = nil
    }

    private func show(_ panel: NSWindow, firstResponder: NSResponder? = nil) {
        // The content goes on a system glass base.
        if let content = panel.contentView, !(content is PreviewGlassContainer) {
            panel.contentView = PreviewGlassContainer(content: content, cornerRadius: Self.glassRadius)
        }
        // Ends at the centre of the screen, where ClipShelf itself opens.
        let visible = (NSScreen.main ?? panel.screen)?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1200, height: 800)
        panel.setFrame(centeredFrame(for: panel.frame.size, in: visible), display: true)
        startKeyMonitor()
        NSApp.activate(ignoringOtherApps: true)

        let reveal = { [weak panel] in
            guard let panel else { return }
            panel.makeKeyAndOrderFront(nil)
            NSAnimationContext.runAnimationGroup { context in
                context.duration = 0.18
                panel.animator().alphaValue = 1
            }
            if let firstResponder {
                panel.makeFirstResponder(firstResponder)
            }
        }
        // Grows out of the record that was previewed (not when stepping
        // through records with the arrow keys).
        guard let source = pendingSource else {
            reveal()
            return
        }
        panel.contentView?.layoutSubtreeIfNeeded()
        guard let image = WindowSnapshot.capture(panel, cornerRadius: Self.glassRadius) else {
            reveal()
            return
        }
        panel.alphaValue = 0
        panel.orderFront(nil)
        GenieAnimator.run(image: image, from: source, to: panel.frame, opening: true, duration: 0.45, above: nil, crossfade: 0.25, level: .floating) {
            reveal()
        }
    }

    private func startKeyMonitor() {
        stopKeyMonitor()
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: [.keyDown]) { [weak self] event in
            guard let self, self.isPreviewVisible else {
                return event
            }

            if self.textPreviewSession?.handleKeyDown(event) == true {
                return nil
            }

            if event.keyCode == 49, self.textPreviewSession?.isFindFieldEditing != true {
                self.closeIfVisible()
                return nil
            }

            if self.handleImageZoomKey(event) {
                return nil
            }

            if self.textPreviewSession?.isFindFieldEditing == true {
                return event
            }

            // ↑ ↓ always belong to stepping, never to scrolling the content
            // (that's for the mouse and trackpad).
            if let direction = self.previewNavigationDirection(for: event) {
                _ = self.onNavigate?(direction)
                return nil
            }

            return event
        }
        scrollWheelMonitor = NSEvent.addLocalMonitorForEvents(matching: [.scrollWheel]) { [weak self] event in
            guard let self,
                  self.isPreviewVisible,
                  event.modifierFlags.contains(.command),
                  let imageZoomSession = self.imageZoomSession else {
                return event
            }

            imageZoomSession.applyWheelDelta(event.scrollingDeltaY)
            return nil
        }
    }

    private func handleImageZoomKey(_ event: NSEvent) -> Bool {
        guard let imageZoomSession,
              event.modifierFlags.contains(.command),
              event.modifierFlags.intersection([.option, .control]).isEmpty else {
            return false
        }

        switch event.keyCode {
        case 24:
            imageZoomSession.zoomIn()
        case 27:
            imageZoomSession.zoomOut()
        case 29:
            imageZoomSession.reset()
        default:
            return false
        }
        return true
    }

    private var isPreviewVisible: Bool {
        previewWindow?.isVisible == true || QLPreviewPanel.shared()?.isVisible == true
    }

    private func previewNavigationDirection(for event: NSEvent) -> Int? {
        guard event.modifierFlags.intersection([.command, .option, .control]).isEmpty else {
            return nil
        }

        // ↑ ↓ step through records (← → no longer do).
        if event.keyCode == 126 { return -1 }
        if event.keyCode == 125 { return 1 }

        return nil
    }

    private func stopKeyMonitor() {
        if let keyMonitor {
            NSEvent.removeMonitor(keyMonitor)
            self.keyMonitor = nil
        }
        if let scrollWheelMonitor {
            NSEvent.removeMonitor(scrollWheelMonitor)
            self.scrollWheelMonitor = nil
        }
    }

    private func cleanupTemporaryFiles() {
        imageOCRSession?.cancel()
        imageOCRSession = nil
        textPreviewSession?.cancel()
        textPreviewSession = nil
        for url in temporaryURLs {
            // The file's own temporary folder goes with it.
            let folder = url.deletingLastPathComponent()
            try? FileManager.default.removeItem(at: folder.lastPathComponent.hasPrefix("ClipShelfPreview-") ? folder : url)
        }
        temporaryURLs.removeAll()
    }
}

enum ImagePreviewZoom {
    static let minimum = 0.5
    static let maximum = 4.0
    static let resetValue = 1.0
    static let step = 0.25

    static func clamped(_ value: Double) -> Double {
        min(max(value, minimum), maximum)
    }

    static func stepped(from value: Double, direction: Int) -> Double {
        clamped(value + Double(direction) * step)
    }

    static func adjustedForWheel(from value: Double, deltaY: CGFloat) -> Double {
        let limitedDelta = min(max(Double(deltaY), -8), 8)
        return clamped(value * (1 + limitedDelta * 0.025))
    }
}

private struct PreviewEntry {
    let url: URL
    let recommendedSize: NSSize
}

private final class ImageZoomSession: NSObject {
    private weak var scrollView: NSScrollView?
    private weak var imageView: NSImageView?
    private weak var percentageLabel: NSTextField?
    private let canvas = NSView()
    private var resizeObserver: NSObjectProtocol?
    private var isLayingOut = false
    private(set) var zoom = ImagePreviewZoom.resetValue

    init(scrollView: NSScrollView, imageView: NSImageView) {
        self.scrollView = scrollView
        self.imageView = imageView
        super.init()

        imageView.removeFromSuperview()
        canvas.addSubview(imageView)
        scrollView.documentView = canvas
        applyZoom(ImagePreviewZoom.resetValue, force: true)

        let clipView = scrollView.contentView
        clipView.postsFrameChangedNotifications = true
        resizeObserver = NotificationCenter.default.addObserver(
            forName: NSView.frameDidChangeNotification,
            object: clipView,
            queue: .main
        ) { [weak self] _ in
            guard let self else { return }
            self.applyZoom(self.zoom, force: true)
        }
    }

    deinit {
        if let resizeObserver {
            NotificationCenter.default.removeObserver(resizeObserver)
        }
    }

    func makeControls() -> NSView {
        // A floating capsule of clear system glass.
        let effectView = PreviewGlassBar(frame: NSRect(x: 0, y: 0, width: 212, height: 40), cornerRadius: 20)

        let zoomOutButton = NSButton(image: NSImage(systemSymbolName: "minus", accessibilityDescription: "缩小") ?? NSImage(), target: self, action: #selector(zoomOut))
        let resetButton = NSButton(title: "复位", target: self, action: #selector(reset))
        let label = NSTextField(labelWithString: "100%")
        let zoomInButton = NSButton(image: NSImage(systemSymbolName: "plus", accessibilityDescription: "放大") ?? NSImage(), target: self, action: #selector(zoomIn))

        [zoomOutButton, resetButton, zoomInButton].forEach {
            $0.isBordered = false
            $0.bezelStyle = .inline
        }
        label.alignment = .center
        label.font = .monospacedDigitSystemFont(ofSize: 12, weight: .medium)
        label.textColor = .labelColor
        label.setContentHuggingPriority(.required, for: .horizontal)
        percentageLabel = label

        let stack = NSStackView(views: [zoomOutButton, resetButton, label, zoomInButton])
        stack.orientation = .horizontal
        stack.alignment = .centerY
        stack.distribution = .fill
        stack.spacing = 9
        stack.edgeInsets = NSEdgeInsets(top: 6, left: 10, bottom: 6, right: 10)
        stack.frame = effectView.bounds
        stack.autoresizingMask = [.width, .height]
        effectView.addSubview(stack)
        return effectView
    }

    @objc func zoomIn() {
        setZoom(ImagePreviewZoom.stepped(from: zoom, direction: 1))
    }

    @objc func zoomOut() {
        setZoom(ImagePreviewZoom.stepped(from: zoom, direction: -1))
    }

    @objc func reset() {
        setZoom(ImagePreviewZoom.resetValue)
    }

    func applyWheelDelta(_ deltaY: CGFloat) {
        guard deltaY != 0 else { return }
        setZoom(ImagePreviewZoom.adjustedForWheel(from: zoom, deltaY: deltaY))
    }

    private func setZoom(_ proposedZoom: Double) {
        applyZoom(ImagePreviewZoom.clamped(proposedZoom), force: false)
    }

    private func applyZoom(_ nextZoom: Double, force: Bool) {
        guard let scrollView, let imageView else { return }
        guard force || nextZoom != zoom, !isLayingOut else { return }
        isLayingOut = true
        defer { isLayingOut = false }

        let clipView = scrollView.contentView
        let viewportSize = clipView.bounds.size
        guard viewportSize.width > 0, viewportSize.height > 0 else { return }

        let oldFrame = imageView.frame
        let oldCenter = NSPoint(x: clipView.bounds.midX, y: clipView.bounds.midY)
        let normalizedCenter: NSPoint
        if force && zoom == ImagePreviewZoom.resetValue {
            normalizedCenter = NSPoint(x: 0.5, y: 0.5)
        } else {
            normalizedCenter = NSPoint(
                x: oldFrame.width > 0 ? (oldCenter.x - oldFrame.minX) / oldFrame.width : 0.5,
                y: oldFrame.height > 0 ? (oldCenter.y - oldFrame.minY) / oldFrame.height : 0.5
            )
        }

        zoom = nextZoom
        let scaledSize = NSSize(
            width: viewportSize.width * nextZoom,
            height: viewportSize.height * nextZoom
        )
        let documentSize = NSSize(
            width: max(viewportSize.width, scaledSize.width),
            height: max(viewportSize.height, scaledSize.height)
        )
        imageView.frame = NSRect(
            x: (documentSize.width - scaledSize.width) / 2,
            y: (documentSize.height - scaledSize.height) / 2,
            width: scaledSize.width,
            height: scaledSize.height
        )

        canvas.frame = NSRect(origin: .zero, size: documentSize)

        let newImagePoint = NSPoint(
            x: imageView.frame.minX + imageView.frame.width * min(max(normalizedCenter.x, 0), 1),
            y: imageView.frame.minY + imageView.frame.height * min(max(normalizedCenter.y, 0), 1)
        )
        let maximumOrigin = NSPoint(
            x: max(0, documentSize.width - viewportSize.width),
            y: max(0, documentSize.height - viewportSize.height)
        )
        let origin = NSPoint(
            x: min(max(newImagePoint.x - viewportSize.width / 2, 0), maximumOrigin.x),
            y: min(max(newImagePoint.y - viewportSize.height / 2, 0), maximumOrigin.y)
        )
        clipView.scroll(to: origin)
        scrollView.reflectScrolledClipView(clipView)
        percentageLabel?.stringValue = "\(Int((nextZoom * 100).rounded()))%"
    }
}

private final class ImageOCRSession: NSObject {
    private let image: NSImage
    private weak var imageView: NSImageView?
    private weak var recognizeButton: NSButton?
    private weak var copySelectedButton: NSButton?
    private weak var copyAllButton: NSButton?
    private weak var statusLabel: NSTextField?
    private weak var progressIndicator: NSProgressIndicator?
    private var overlayView: OCRSelectionOverlayView?
    private var recognitionTask: Task<Void, Never>?
    private var regions: [ImageRecognizedTextRegion] = []
    private var selectedIndices = IndexSet()

    init(image: NSImage, imageView: NSImageView) {
        self.image = image
        self.imageView = imageView
    }

    func makeToolbar() -> NSView {
        let toolbar = PreviewGlassBar(frame: .zero, cornerRadius: 20)

        let recognizeButton = NSButton(title: "识别文字", target: self, action: #selector(startRecognition))
        recognizeButton.bezelStyle = .rounded
        recognizeButton.image = NSImage(systemSymbolName: "text.viewfinder", accessibilityDescription: nil)
        recognizeButton.imagePosition = .imageLeading
        self.recognizeButton = recognizeButton

        let progress = NSProgressIndicator()
        progress.style = .spinning
        progress.controlSize = .small
        progress.isDisplayedWhenStopped = false
        progress.setContentHuggingPriority(.required, for: .horizontal)
        progressIndicator = progress

        let status = NSTextField(labelWithString: "按需识别，不会在打开图片时自动运行")
        status.textColor = .secondaryLabelColor
        status.lineBreakMode = .byTruncatingTail
        status.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        statusLabel = status

        let copySelected = NSButton(title: "复制所选", target: self, action: #selector(copySelectedText))
        copySelected.bezelStyle = .rounded
        copySelected.isEnabled = false
        copySelectedButton = copySelected

        let copyAll = NSButton(title: "复制全部", target: self, action: #selector(copyAllText))
        copyAll.bezelStyle = .rounded
        copyAll.isEnabled = false
        copyAllButton = copyAll

        let stack = NSStackView(views: [recognizeButton, progress, status, copySelected, copyAll])
        stack.orientation = .horizontal
        stack.alignment = .centerY
        stack.spacing = 10
        stack.edgeInsets = NSEdgeInsets(top: 7, left: 12, bottom: 7, right: 12)
        stack.frame = toolbar.bounds
        stack.autoresizingMask = [.width, .height]
        toolbar.addSubview(stack)
        return toolbar
    }

    @objc private func startRecognition() {
        guard recognitionTask == nil, let cgImage = image.cgImage(forProposedRect: nil, context: nil, hints: nil) else {
            statusLabel?.stringValue = "无法读取这张图片。"
            return
        }

        recognizeButton?.isEnabled = false
        copySelectedButton?.isEnabled = false
        copyAllButton?.isEnabled = false
        statusLabel?.stringValue = "正在识别文字，大图会自动优化…"
        progressIndicator?.startAnimation(nil)

        recognitionTask = Task { @MainActor [weak self] in
            do {
                let result = try await ImageTextRecognizer.recognize(cgImage: cgImage)
                guard !Task.isCancelled else { return }
                self?.finish(with: result)
            } catch is CancellationError {
                return
            } catch {
                self?.finish(with: error)
            }
        }
    }

    func cancel() {
        recognitionTask?.cancel()
        recognitionTask = nil
        progressIndicator?.stopAnimation(nil)
        overlayView?.removeFromSuperview()
        overlayView = nil
        regions.removeAll()
        selectedIndices.removeAll()
    }

    private func finish(with result: ImageTextRecognitionResult) {
        recognitionTask = nil
        progressIndicator?.stopAnimation(nil)
        recognizeButton?.isEnabled = true
        regions = result.regions
        selectedIndices.removeAll()

        guard !regions.isEmpty else {
            statusLabel?.stringValue = "未识别到文字，可以继续查看或重试。"
            copySelectedButton?.isEnabled = false
            copyAllButton?.isEnabled = false
            overlayView?.removeFromSuperview()
            overlayView = nil
            return
        }

        let optimization = result.didDownsample ? " · 已优化大图" : ""
        statusLabel?.stringValue = String(
            format: "识别完成：%d 段 · %.1f 秒%@",
            regions.count,
            result.duration,
            optimization
        )
        copyAllButton?.isEnabled = true
        installOverlay()
    }

    private func finish(with error: Error) {
        recognitionTask = nil
        progressIndicator?.stopAnimation(nil)
        recognizeButton?.isEnabled = true
        copySelectedButton?.isEnabled = false
        copyAllButton?.isEnabled = false
        statusLabel?.stringValue = "识别失败：\(error.localizedDescription)"
    }

    private func installOverlay() {
        guard let imageView else { return }
        overlayView?.removeFromSuperview()
        let overlay = OCRSelectionOverlayView(frame: imageView.bounds)
        overlay.autoresizingMask = [.width, .height]
        overlay.imageSize = image.size
        overlay.regions = regions
        overlay.onSelectionChanged = { [weak self] indices in
            self?.selectedIndices = indices
            self?.copySelectedButton?.isEnabled = !indices.isEmpty
        }
        imageView.addSubview(overlay)
        overlayView = overlay
    }

    @objc private func copySelectedText() {
        let text = selectedIndices.compactMap { index in
            regions.indices.contains(index) ? regions[index].text : nil
        }.joined(separator: "\n")
        copy(text)
    }

    @objc private func copyAllText() {
        copy(regions.map(\.text).joined(separator: "\n"))
    }

    private func copy(_ text: String) {
        guard !text.isEmpty else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
        statusLabel?.stringValue = "文字已复制"
    }
}

private final class OCRSelectionOverlayView: NSView {
    var imageSize = NSSize.zero { didSet { needsDisplay = true } }
    var regions = [ImageRecognizedTextRegion]() { didSet { needsDisplay = true } }
    var onSelectionChanged: ((IndexSet) -> Void)?

    private var dragStart: NSPoint?
    private var selectionRect: NSRect?
    private var selectedIndices = IndexSet()

    override var acceptsFirstResponder: Bool { true }

    override func mouseDown(with event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        dragStart = point
        selectionRect = NSRect(origin: point, size: .zero)
        selectedIndices.removeAll()
        onSelectionChanged?(selectedIndices)
        needsDisplay = true
    }

    override func mouseDragged(with event: NSEvent) {
        guard let dragStart else { return }
        let point = convert(event.locationInWindow, from: nil)
        let rect = NSRect(
            x: min(dragStart.x, point.x),
            y: min(dragStart.y, point.y),
            width: abs(point.x - dragStart.x),
            height: abs(point.y - dragStart.y)
        )
        selectionRect = rect
        selectedIndices = IndexSet(regions.indices.filter { rect.intersects(displayRect(for: regions[$0])) })
        onSelectionChanged?(selectedIndices)
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        mouseDragged(with: event)
        dragStart = nil
    }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)

        for index in regions.indices {
            let rect = displayRect(for: regions[index])
            let selected = selectedIndices.contains(index)
            (selected ? NSColor.controlAccentColor.withAlphaComponent(0.22) : NSColor.systemTeal.withAlphaComponent(0.10)).setFill()
            NSBezierPath(roundedRect: rect, xRadius: 3, yRadius: 3).fill()
            (selected ? NSColor.controlAccentColor : NSColor.systemTeal.withAlphaComponent(0.75)).setStroke()
            let path = NSBezierPath(roundedRect: rect, xRadius: 3, yRadius: 3)
            path.lineWidth = selected ? 2 : 1
            path.stroke()
        }

        if let selectionRect, selectionRect.width > 1 || selectionRect.height > 1 {
            NSColor.controlAccentColor.withAlphaComponent(0.12).setFill()
            selectionRect.fill()
            NSColor.controlAccentColor.setStroke()
            let path = NSBezierPath(rect: selectionRect)
            path.lineWidth = 1
            path.setLineDash([4, 3], count: 2, phase: 0)
            path.stroke()
        }
    }

    private func displayRect(for region: ImageRecognizedTextRegion) -> NSRect {
        let imageRect = displayedImageRect()
        let box = region.boundingBox
        return NSRect(
            x: imageRect.minX + box.minX * imageRect.width,
            y: imageRect.minY + box.minY * imageRect.height,
            width: box.width * imageRect.width,
            height: box.height * imageRect.height
        )
    }

    private func displayedImageRect() -> NSRect {
        guard imageSize.width > 0, imageSize.height > 0,
              bounds.width > 0, bounds.height > 0 else {
            return bounds
        }
        let scale = min(bounds.width / imageSize.width, bounds.height / imageSize.height)
        let size = NSSize(width: imageSize.width * scale, height: imageSize.height * scale)
        return NSRect(
            x: (bounds.width - size.width) / 2,
            y: (bounds.height - size.height) / 2,
            width: size.width,
            height: size.height
        )
    }
}

/// A toolbar of clear system glass (frosted material on older systems);
/// its controls are added on top like any subviews.
final class PreviewGlassBar: NSView {
    init(frame: NSRect, cornerRadius: CGFloat) {
        super.init(frame: frame)
        wantsLayer = true
        let background: NSView
        if #available(macOS 26.0, *) {
            let glass = NSGlassEffectView()
            glass.style = .clear
            glass.cornerRadius = cornerRadius
            background = glass
        } else {
            let effect = NSVisualEffectView()
            effect.material = .hudWindow
            effect.blendingMode = .withinWindow
            effect.state = .active
            effect.wantsLayer = true
            effect.layer?.cornerRadius = cornerRadius
            effect.layer?.masksToBounds = true
            background = effect
        }
        background.frame = bounds
        background.autoresizingMask = [.width, .height]
        addSubview(background)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
}

/// The preview's content over a system glass base with rounded corners.
private final class PreviewGlassContainer: NSView {
    init(content: NSView, cornerRadius: CGFloat) {
        super.init(frame: content.frame)
        wantsLayer = true
        let backdrop = WindowBackdropView(cornerRadius: cornerRadius)
        backdrop.apply(GlassPreferences.value, animated: false)
        content.wantsLayer = true
        content.layer?.cornerRadius = cornerRadius
        content.layer?.cornerCurve = .continuous
        content.layer?.masksToBounds = true
        for view in [backdrop, content] {
            view.translatesAutoresizingMaskIntoConstraints = false
            addSubview(view)
            NSLayoutConstraint.activate([
                view.leadingAnchor.constraint(equalTo: leadingAnchor),
                view.trailingAnchor.constraint(equalTo: trailingAnchor),
                // The content starts below the transparent titlebar.
                view.topAnchor.constraint(equalTo: topAnchor, constant: view === content ? 28 : 0),
                view.bottomAnchor.constraint(equalTo: bottomAnchor)
            ])
        }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
}

private final class SpaceClosablePanel: NSPanel {
    var onSpace: (() -> Void)?
    var onClose: (() -> Void)?

    override func close() {
        super.close()
        onClose?()
    }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 49 {
            onSpace?()
        } else {
            super.keyDown(with: event)
        }
    }
}
