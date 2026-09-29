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

    func togglePreview(_ items: [ClipItem], onNavigate: ((Int) -> Bool)? = nil) {
        if closeIfVisible() {
            return
        }

        preview(items, onNavigate: onNavigate)
    }

    func preview(_ items: [ClipItem], onNavigate: ((Int) -> Bool)? = nil) {
        guard items.count == 1 else { return }

        self.onNavigate = onNavigate

        if let panel = QLPreviewPanel.shared(), panel.isVisible {
            panel.orderOut(nil)
        }
        previewWindow?.orderOut(nil)
        imageZoomSession = nil
        imageOCRSession?.cancel()
        imageOCRSession = nil
        textPreviewSession?.cancel()
        textPreviewSession = nil

        cleanupTemporaryFiles()

        if showBuiltInPreviewIfPossible(items) {
            return
        }

        previewEntries = items.compactMap(previewEntry(for:))

        guard !previewEntries.isEmpty, let panel = QLPreviewPanel.shared() else {
            return
        }

        panel.currentPreviewItemIndex = 0
        panel.dataSource = self
        panel.delegate = self
        panel.reloadData()
        applyFixedPreviewFrame(to: panel)
        startKeyMonitor()
        NSApp.activate(ignoringOtherApps: true)
        panel.makeKeyAndOrderFront(self)
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

            guard let imageData = item.imageData else { return nil }
            let url = FileManager.default.temporaryDirectory
                .appendingPathComponent("ClipShelfPreview-\(item.id.uuidString).png")
            try? imageData.write(to: url, options: .atomic)
            temporaryURLs.append(url)
            return url
        case .text:
            let url = FileManager.default.temporaryDirectory
                .appendingPathComponent("ClipShelfPreview-\(item.id.uuidString).txt")
            try? (item.text ?? item.title).write(to: url, atomically: true, encoding: .utf8)
            temporaryURLs.append(url)
            return url
        }
    }

    @discardableResult
    func closeIfVisible() -> Bool {
        var didClose = false

        if let window = previewWindow, window.isVisible {
            window.orderOut(nil)
            didClose = true
        }

        if let panel = QLPreviewPanel.shared(), panel.isVisible {
            panel.orderOut(nil)
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
            if let image = NSImage(contentsOf: url) {
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

    private func image(for item: ClipItem) -> NSImage? {
        if let imageData = item.imageData, let image = NSImage(data: imageData) {
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
        ocrToolbar.frame = NSRect(
            x: 0,
            y: rootView.bounds.height - toolbarHeight,
            width: rootView.bounds.width,
            height: toolbarHeight
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

    private func previewPanel(title: String, size: NSSize) -> NSPanel {
        previewWindow?.orderOut(nil)

        let panel = SpaceClosablePanel(
            contentRect: NSRect(origin: .zero, size: size),
            styleMask: [.titled, .closable, .resizable, .utilityWindow],
            backing: .buffered,
            defer: false
        )
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
        startKeyMonitor()
        NSApp.activate(ignoringOtherApps: true)
        panel.makeKeyAndOrderFront(nil)
        if let firstResponder {
            panel.makeFirstResponder(firstResponder)
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

            if let direction = self.previewNavigationDirection(for: event),
               self.onNavigate?(direction) == true {
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

        if event.keyCode == 123 { return -1 }
        if event.keyCode == 124 { return 1 }

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
            try? FileManager.default.removeItem(at: url)
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
        let effectView = NSVisualEffectView(frame: NSRect(x: 0, y: 0, width: 212, height: 40))
        effectView.material = .hudWindow
        effectView.blendingMode = .withinWindow
        effectView.state = .active
        effectView.wantsLayer = true
        effectView.layer?.cornerRadius = 9
        effectView.layer?.masksToBounds = true

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
        let toolbar = NSVisualEffectView()
        toolbar.material = .headerView
        toolbar.blendingMode = .withinWindow
        toolbar.state = .active

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
