import AppKit
import CoreImage
import SwiftUI

/// Settings live in their own native window, attached to the main window as a
/// child and backed by system Liquid Glass. It opens by pouring out of the
/// settings button (see `GenieAnimator`) and collapses back into it on close.
///
/// The window is built once and then reused, so opening never rebuilds the
/// ~20 native controls inside it, and because it is a separate window nothing
/// about it costs the main list a single frame while it is hidden.
final class SettingsWindowController: NSObject, NSWindowDelegate {
    static let shared = SettingsWindowController()
    static let willShowNotification = Notification.Name("ClipShelfSettingsWillShow")
    static let willCloseNotification = Notification.Name("ClipShelfSettingsWillClose")

    /// The settings button's frame in the main window's SwiftUI global space
    /// (top-left origin). Updated by the toolbar as layout changes.
    var anchorInWindow: CGRect?

    private var panel: SettingsPanel?
    private weak var parent: NSWindow?
    private var store: ClipStore?
    private var watcher: ScreenshotFolderWatcher?
    private var backdrop: NSView?
    private var hosting: NSView?
    /// The settings content alone (no glass), rendered when it was last shown or prepared.
    private var contentSnapshot: CGImage?
    private var isAnimating = false
    /// Whether settings is on screen. While hidden, the window stays ordered
    /// but parked far off screen, so showing it never pays for ordering a glass
    /// window in front (10–25 ms) at the moment the animation starts.
    private var isShown = false
    private static let parkingOrigin = NSPoint(x: -30_000, y: -30_000)
    /// Cross-dissolve between the live glass panel and the animation image.
    private static let handOverDuration: TimeInterval = 0.18
    /// Opening and closing take the same time, so they feel like one motion.
    private static let genieDuration: TimeInterval = 0.5
    /// Opening frame composed ahead of the click (see `prefetchOpeningSnapshot`).
    private var prefetched: (image: CGImage, frame: NSRect, time: CFTimeInterval)?
    /// Closing frame composed when the pointer reaches ✓ or the settings button.
    private var prefetchedClose: (image: CGImage, frame: NSRect, time: CFTimeInterval)?
    /// The blurred main window behind the panel, kept from the opening so
    /// closing doesn't have to capture and blur it again.
    private var cachedBackdrop: (image: CGImage, frame: NSRect)?

    private func backdrop(for frame: NSRect) -> CGImage? {
        guard let cachedBackdrop, cachedBackdrop.frame == frame else { return nil }
        return cachedBackdrop.image
    }
    private let snapshotQueue = DispatchQueue(label: "ClipShelf.settings-snapshot", qos: .userInteractive)

    var isVisible: Bool { isShown }

    func configure(store: ClipStore, watcher: ScreenshotFolderWatcher) {
        self.store = store
        self.watcher = watcher
        NotificationCenter.default.addObserver(self, selector: #selector(appearanceChanged), name: AppearancePreferences.changedNotification, object: nil)
        NotificationCenter.default.addObserver(self, selector: #selector(appearanceChanged), name: AppearancePreferences.systemChangedNotification, object: nil)
        NotificationCenter.default.addObserver(self, selector: #selector(glassChanged), name: GlassPreferences.changedNotification, object: nil)
    }

    /// Builds the window ahead of time, parks it off screen and renders the
    /// content snapshot used by the opening animation.
    func prepare(parent: NSWindow) {
        self.parent = parent
        guard panel == nil || contentSnapshot == nil else { return }
        park(makePanelIfNeeded(), size: targetFrame(in: parent).size)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.15) { [weak self] in
            guard let self, !self.isShown else { return }
            self.contentSnapshot = self.renderContent()
            self.warmUpBlur()
        }
    }

    /// Builds the window (if needed) and renders its content right away, for
    /// callers that need a snapshot immediately.
    func prepareNow(parent: NSWindow) {
        self.parent = parent
        park(makePanelIfNeeded(), size: targetFrame(in: parent).size)
        contentSnapshot = renderContent() ?? contentSnapshot
    }

    /// The main window's image with the settings panel composed over it in
    /// place, so both can pour out of the menu bar icon as one piece.
    func composeOver(mainImage: CGImage, parent: NSWindow) -> CGImage? {
        guard let content = contentSnapshot ?? renderContent(), let panel else { return nil }
        let frame = targetFrame(in: parent)
        let request = SnapshotRequest(
            parentWindowNumber: parent.windowNumber,
            parentFrame: parent.frame,
            scale: parent.backingScaleFactor,
            frame: frame,
            isDark: panel.effectiveAppearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua,
            content: content
        )
        guard let settings = Self.composeSnapshot(request),
              let context = CGContext(
                data: nil,
                width: mainImage.width,
                height: mainImage.height,
                bitsPerComponent: 8,
                bytesPerRow: 0,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
              ) else { return nil }
        let scale = CGFloat(mainImage.width) / max(parent.frame.width, 1)
        context.draw(mainImage, in: CGRect(x: 0, y: 0, width: mainImage.width, height: mainImage.height))
        // Both frames are bottom-left based, like the bitmap.
        context.draw(settings, in: CGRect(
            x: (frame.minX - parent.frame.minX) * scale,
            y: (frame.minY - parent.frame.minY) * scale,
            width: frame.width * scale,
            height: frame.height * scale
        ))
        return context.makeImage()
    }

    /// Puts settings in place without its own animation (used when it has
    /// already poured out together with the main window).
    func showImmediately(from parent: NSWindow) {
        guard !isShown, !isAnimating else { return }
        self.parent = parent
        let panel = makePanelIfNeeded()
        NotificationCenter.default.post(name: Self.willShowNotification, object: nil)
        AppearancePreferences.apply(to: panel)
        panel.setFrame(targetFrame(in: parent), display: true)
        panel.alphaValue = 1
        parent.addChildWindow(panel, ordered: .above)
        panel.makeKey()
        isShown = true
        installCloseShortcut()
    }

    /// Called when the pointer reaches the settings button. Capturing and
    /// blurring the main window takes ~60 ms; doing it on hover, off the main
    /// thread, means the genie can start the instant the button is clicked.
    func prefetchOpeningSnapshot(for parent: NSWindow?) {
        guard !isShown, !isAnimating, let parent, let content = contentSnapshot, let panel else { return }
        let frame = targetFrame(in: parent)
        let request = SnapshotRequest(
            parentWindowNumber: parent.windowNumber,
            parentFrame: parent.frame,
            scale: parent.backingScaleFactor,
            frame: frame,
            isDark: panel.effectiveAppearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua,
            content: content
        )
        snapshotQueue.async { [weak self] in
            guard let result = Self.composeSnapshot(request, backdrop: nil) else { return }
            DispatchQueue.main.async {
                self?.prefetched = (result.image, frame, CACurrentMediaTime())
                if let backdrop = result.backdrop { self?.cachedBackdrop = (backdrop, frame) }
            }
        }
    }

    /// Called when the pointer reaches ✓ (or the settings button while open),
    /// so a click can start the closing genie without rendering first.
    func prefetchClosingSnapshot() {
        guard isShown, !isAnimating, let panel, let parent, let content = renderContent() else { return }
        contentSnapshot = content
        let frame = panel.frame
        let request = SnapshotRequest(
            parentWindowNumber: parent.windowNumber,
            parentFrame: parent.frame,
            scale: parent.backingScaleFactor,
            frame: frame,
            isDark: panel.effectiveAppearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua,
            content: content
        )
        let cached = backdrop(for: frame)
        snapshotQueue.async { [weak self] in
            guard let result = Self.composeSnapshot(request, backdrop: cached) else { return }
            DispatchQueue.main.async {
                self?.prefetchedClose = (result.image, frame, CACurrentMediaTime())
            }
        }
    }

    func toggle(from parent: NSWindow?) {
        isVisible ? close() : show(from: parent)
    }

    func show(from parent: NSWindow?) {
        guard !isAnimating, !isShown, let parent else { return }
        self.parent = parent
        let panel = makePanelIfNeeded()
        let frame = targetFrame(in: parent)
        NotificationCenter.default.post(name: Self.willShowNotification, object: nil)
        AppearancePreferences.apply(to: panel)
        if panel.frame.size != frame.size || !panel.isVisible {
            park(panel, size: frame.size)
        }
        if contentSnapshot == nil {
            contentSnapshot = renderContent()
        }

        let reveal = { [weak self, weak panel] in
            guard let self, let panel else { return }
            panel.alphaValue = 0
            panel.setFrame(frame, display: true)
            parent.addChildWindow(panel, ordered: .above)
            panel.makeKey()
            // Dissolve from the animation image into the live glass rather
            // than switching, so the hand-over has no visible colour jump.
            NSAnimationContext.runAnimationGroup { context in
                context.duration = Self.handOverDuration
                panel.animator().alphaValue = 1
            }
            self.isShown = true
            self.isAnimating = false
            self.installCloseShortcut()
        }

        guard let source = anchorRectOnScreen(in: parent) else {
            reveal()
            return
        }
        isAnimating = true
        // Take focus now, while still parked off screen. Liquid Glass renders
        // differently in an inactive window; doing this only on arrival made
        // the panel land dim and then visibly "light up" a moment later.
        panel.makeKey()
        let start = { (snapshot: CGImage?) in
            guard let snapshot else {
                reveal()
                return
            }
            GenieAnimator.run(image: snapshot, from: source, to: frame, opening: true, duration: Self.genieDuration, above: parent, crossfade: Self.handOverDuration, completion: reveal)
        }

        let fresh = prefetched.flatMap { entry in
            entry.frame == frame && CACurrentMediaTime() - entry.time < 3 ? entry.image : nil
        }
        prefetched = nil
        if let fresh {
            start(fresh)
            return
        }
        // No hover beforehand (e.g. ⌘,): compose off the main thread and start
        // as soon as it is ready, rather than freezing the UI meanwhile.
        guard let content = contentSnapshot else {
            start(nil)
            return
        }
        let request = SnapshotRequest(
            parentWindowNumber: parent.windowNumber,
            parentFrame: parent.frame,
            scale: parent.backingScaleFactor,
            frame: frame,
            isDark: panel.effectiveAppearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua,
            content: content
        )
        snapshotQueue.async { [weak self] in
            let result = Self.composeSnapshot(request, backdrop: nil)
            DispatchQueue.main.async {
                if let backdrop = result?.backdrop { self?.cachedBackdrop = (backdrop, frame) }
                start(result?.image)
            }
        }
    }

    func close() {
        guard !isAnimating, isShown, let panel else { return }
        NotificationCenter.default.post(name: Self.willCloseNotification, object: nil)
        let frame = panel.frame
        let fresh = prefetchedClose.flatMap { entry in
            entry.frame == frame && CACurrentMediaTime() - entry.time < 3 ? entry.image : nil
        }
        prefetchedClose = nil
        isShown = false
        isAnimating = true
        uninstallCloseShortcut()

        let tidyUp = { [weak self, weak panel] in
            guard let self, let panel else { return }
            panel.parent?.removeChildWindow(panel)
            // Same size, so only move it away: no resize or re-layout at the
            // very end of the animation, where it used to stutter.
            panel.setFrameOrigin(Self.parkingOrigin)
            panel.alphaValue = 1
            self.isAnimating = false
        }

        // The live panel stays put until the animation image is ready, then
        // dissolves into it, so the hand-over never freezes on screen.
        let start = { [weak self, weak panel] (snapshot: CGImage?) in
            guard let self, let panel else { return }
            NSAnimationContext.runAnimationGroup { context in
                context.duration = Self.handOverDuration
                panel.animator().alphaValue = 0
            }
            self.parent?.makeKey()
            guard let snapshot, let parent = self.parent, let source = self.anchorRectOnScreen(in: parent) else {
                tidyUp()
                return
            }
            GenieAnimator.run(image: snapshot, from: source, to: frame, opening: false, duration: Self.genieDuration, above: parent, completion: tidyUp)
        }

        if let fresh {
            start(fresh)
            return
        }
        // Without a hover beforehand (Esc, ⌘, or a shortcut): render the
        // content now, but capture and blur the backdrop off the main thread.
        contentSnapshot = renderContent() ?? contentSnapshot
        guard let parent, let content = contentSnapshot else {
            start(nil)
            return
        }
        let request = SnapshotRequest(
            parentWindowNumber: parent.windowNumber,
            parentFrame: parent.frame,
            scale: parent.backingScaleFactor,
            frame: frame,
            isDark: panel.effectiveAppearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua,
            content: content
        )
        // The backdrop kept from opening makes this a quick draw: start at
        // once instead of waiting for a capture and blur.
        if let cached = backdrop(for: frame) {
            start(Self.composeSnapshot(request, backdrop: cached)?.image)
            return
        }
        snapshotQueue.async {
            let image = Self.composeSnapshot(request, backdrop: nil)?.image
            DispatchQueue.main.async { start(image) }
        }
    }

    // MARK: Close shortcut

    private var closeShortcutMonitor: Any?

    /// The user's "关闭设置" shortcut, active only while settings is showing.
    private func installCloseShortcut() {
        guard closeShortcutMonitor == nil else { return }
        closeShortcutMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            guard let self, self.isShown, !self.isAnimating,
                  CloseSettingsHotKeyDefaults.load().matches(event) else { return event }
            self.close()
            return nil
        }
    }

    private func uninstallCloseShortcut() {
        if let closeShortcutMonitor {
            NSEvent.removeMonitor(closeShortcutMonitor)
            self.closeShortcutMonitor = nil
        }
    }

    /// Keeps the window ordered but far off screen, sized like its next showing.
    private func park(_ panel: NSWindow, size: NSSize) {
        panel.setFrame(NSRect(origin: Self.parkingOrigin, size: size), display: true)
        if !panel.isVisible {
            panel.orderFrontRegardless()
        }
        panel.contentView?.layoutSubtreeIfNeeded()
    }

    // MARK: Window

    private func makePanelIfNeeded() -> SettingsPanel {
        if let panel { return panel }
        let panel = SettingsPanel(
            contentRect: NSRect(x: 0, y: 0, width: 680, height: 520),
            styleMask: [.titled, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        panel.title = "设置"
        panel.titleVisibility = .hidden
        panel.titlebarAppearsTransparent = true
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.isMovable = false
        panel.isReleasedWhenClosed = false
        panel.animationBehavior = .none
        // Parked off screen while hidden: keep it out of Mission Control and the window cycle.
        panel.collectionBehavior = [.transient, .ignoresCycle]
        panel.delegate = self
        panel.onCancel = { [weak self] in self?.close() }
        for button in [NSWindow.ButtonType.closeButton, .miniaturizeButton, .zoomButton] {
            panel.standardWindowButton(button)?.isHidden = true
        }

        let root = SettingsWindowRoot(
            store: store ?? .shared,
            watcher: watcher ?? .shared,
            onClose: { [weak self] in self?.close() }
        )
        let hosting = ClipShelfHostingView(rootView: root)
        self.hosting = hosting
        panel.contentView = makeBackdrop(containing: hosting)
        AppearancePreferences.apply(to: panel)
        self.panel = panel
        return panel
    }

    private func makeBackdrop(containing content: NSView) -> NSView {
        let radius = GlassMetrics.panelRadius
        if GlassPreferences.value != .opaque, #available(macOS 26.0, *) {
            let glass = NSGlassEffectView()
            glass.cornerRadius = radius
            switch GlassPreferences.value {
            case .clear:
                glass.style = .clear
            case .tinted:
                glass.style = .regular
                glass.tintColor = NSColor.windowBackgroundColor.withAlphaComponent(0.55)
            case .regular, .opaque:
                glass.style = .regular
            }
            glass.contentView = content
            backdrop = glass
            return glass
        }
        let effect = NSVisualEffectView()
        effect.material = GlassPreferences.value == .opaque ? .windowBackground : .hudWindow
        effect.blendingMode = GlassPreferences.value == .opaque ? .withinWindow : .behindWindow
        effect.state = .active
        effect.wantsLayer = true
        effect.layer?.cornerRadius = radius
        effect.layer?.masksToBounds = true
        content.translatesAutoresizingMaskIntoConstraints = false
        effect.addSubview(content)
        NSLayoutConstraint.activate([
            content.leadingAnchor.constraint(equalTo: effect.leadingAnchor),
            content.trailingAnchor.constraint(equalTo: effect.trailingAnchor),
            content.topAnchor.constraint(equalTo: effect.topAnchor),
            content.bottomAnchor.constraint(equalTo: effect.bottomAnchor)
        ])
        backdrop = effect
        return effect
    }

    /// Covers the main window below its titlebar row, with a slim margin.
    private func targetFrame(in parent: NSWindow) -> NSRect {
        let margin = GlassMetrics.edgeInset
        let titlebar = max(parent.frame.height - parent.contentLayoutRect.height, 44)
        let width = min(parent.frame.width - margin * 2, 860)
        let height = max(parent.frame.height - titlebar - margin, 360)
        return NSRect(
            x: parent.frame.midX - width / 2,
            y: parent.frame.minY + margin,
            width: width,
            height: height
        ).integral
    }

    private func anchorRectOnScreen(in parent: NSWindow) -> NSRect? {
        guard let anchor = anchorInWindow, let content = parent.contentView else { return nil }
        let flipped = NSRect(
            x: anchor.minX,
            y: content.bounds.height - anchor.maxY,
            width: anchor.width,
            height: anchor.height
        )
        return parent.convertToScreen(content.convert(flipped, to: nil))
    }

    /// Renders the settings content without its glass backdrop.
    private func renderContent() -> CGImage? {
        guard let hosting, hosting.bounds.width > 0,
              let rep = hosting.bitmapImageRepForCachingDisplay(in: hosting.bounds) else { return nil }
        hosting.cacheDisplay(in: hosting.bounds, to: rep)
        return rep.cgImage
    }

    /// Everything needed to compose a genie frame, safe to use off the main thread.
    private struct SnapshotRequest {
        let parentWindowNumber: Int
        let parentFrame: NSRect
        let scale: CGFloat
        let frame: NSRect
        let isDark: Bool
        let content: CGImage
    }

    private func composedSnapshot(content: CGImage, frame: NSRect, over parent: NSWindow, appearance: NSAppearance) -> CGImage? {
        Self.composeSnapshot(SnapshotRequest(
            parentWindowNumber: parent.windowNumber,
            parentFrame: parent.frame,
            scale: parent.backingScaleFactor,
            frame: frame,
            isDark: appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua,
            content: content
        ))
    }

    /// What the panel looks like for the genie: the main window behind it,
    /// blurred and tinted like Liquid Glass, with the settings content on top.
    /// (A direct capture of a glass window renders the glass as flat grey.)
    private static func composeSnapshot(_ request: SnapshotRequest) -> CGImage? {
        composeSnapshot(request, backdrop: nil)?.image
    }

    /// Composes the animation image. Pass a `backdrop` to skip capturing and
    /// blurring the main window (the slow part); the one used is returned so
    /// it can be reused.
    private static func composeSnapshot(_ request: SnapshotRequest, backdrop cached: CGImage?) -> (image: CGImage, backdrop: CGImage?)? {
        let scale = request.scale
        let width = Int(request.frame.width * scale)
        let height = Int(request.frame.height * scale)
        guard width > 0, height > 0,
              let context = CGContext(
                data: nil,
                width: width,
                height: height,
                bitsPerComponent: 8,
                bytesPerRow: 0,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
              ) else { return nil }
        let bounds = CGRect(x: 0, y: 0, width: width, height: height)
        let radius = GlassMetrics.panelRadius * scale
        context.addPath(CGPath(roundedRect: bounds, cornerWidth: radius, cornerHeight: radius, transform: nil))
        context.clip()

        let backdrop = cached ?? blurredBackdrop(request)
        if let backdrop {
            context.draw(backdrop, in: bounds)
        }
        let tint = request.isDark ? CGColor(gray: 0.12, alpha: 0.5) : CGColor(gray: 1, alpha: 0.5)
        context.setFillColor(tint)
        context.fill(bounds)
        context.draw(request.content, in: bounds)
        return context.makeImage().map { ($0, backdrop) }
    }

    private static func blurredBackdrop(_ request: SnapshotRequest) -> CGImage? {
        guard let window = WindowSnapshot.capture(windowNumber: request.parentWindowNumber) else { return nil }
        let scale = request.scale
        // CGImage crop rects are top-left based, in pixels.
        let crop = CGRect(
            x: (request.frame.minX - request.parentFrame.minX) * scale,
            y: (request.parentFrame.maxY - request.frame.maxY) * scale,
            width: request.frame.width * scale,
            height: request.frame.height * scale
        ).integral
        guard let region = window.cropping(to: crop) else { return nil }
        // Blur a quarter-size copy: a heavy blur hides the lower resolution and
        // it is much cheaper.
        let factor: CGFloat = 0.25
        let input = CIImage(cgImage: region).transformed(by: CGAffineTransform(scaleX: factor, y: factor))
        guard let blur = CIFilter(name: "CIGaussianBlur") else { return region }
        blur.setValue(input.clampedToExtent(), forKey: kCIInputImageKey)
        blur.setValue(22 * scale * factor, forKey: kCIInputRadiusKey)
        guard let output = blur.outputImage?.cropped(to: input.extent) else { return region }
        return ciContext.createCGImage(output, from: input.extent) ?? region
    }

    /// Creating the Core Image pipeline the first time costs tens of ms; do it
    /// during the off-screen prepare instead of on the user's first click.
    private func warmUpBlur() {
        let pixel = CIImage(color: .white).cropped(to: CGRect(x: 0, y: 0, width: 8, height: 8))
        guard let blur = CIFilter(name: "CIGaussianBlur") else { return }
        blur.setValue(pixel.clampedToExtent(), forKey: kCIInputImageKey)
        blur.setValue(2, forKey: kCIInputRadiusKey)
        if let output = blur.outputImage?.cropped(to: pixel.extent) {
            _ = Self.ciContext.createCGImage(output, from: pixel.extent)
        }
    }

    private static let ciContext = CIContext(options: [.cacheIntermediates: false])

    @objc private func appearanceChanged() {
        DispatchQueue.main.async { [weak self] in
            guard let panel = self?.panel else { return }
            AppearancePreferences.apply(to: panel)
        }
    }

    @objc private func glassChanged() {
        guard let panel, let hosting else { return }
        hosting.removeFromSuperview()
        panel.contentView = makeBackdrop(containing: hosting)
    }
}

/// Settings window: no traffic lights, closes on Esc with the genie animation.
final class SettingsPanel: NSWindow {
    var onCancel: (() -> Void)?

    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }

    /// Positions are always computed from the main window; don't let AppKit
    /// pull the off-screen pre-render back onto the display.
    override func constrainFrameRect(_ frameRect: NSRect, to screen: NSScreen?) -> NSRect {
        frameRect
    }

    override func cancelOperation(_ sender: Any?) {
        onCancel?()
    }
}

/// Keeps the settings content in step with the appearance and glass preferences.
private struct SettingsWindowRoot: View {
    let store: ClipStore
    let watcher: ScreenshotFolderWatcher
    let onClose: () -> Void
    @State private var glassTransparency = GlassPreferences.value

    var body: some View {
        SettingsView(store: store, watcher: watcher, onClose: onClose)
            .environment(\.glassTransparency, glassTransparency)
            // The hidden titlebar shouldn't push the content down.
            .ignoresSafeArea()
            .onReceive(NotificationCenter.default.publisher(for: GlassPreferences.changedNotification)) { notification in
                glassTransparency = notification.object as? GlassTransparency ?? GlassPreferences.value
            }
    }
}

/// Captures one of ClipShelf's own windows, as composited on screen.
enum WindowSnapshot {
    private typealias CreateImage = @convention(c) (CGRect, UInt32, UInt32, UInt32) -> Unmanaged<CGImage>?

    /// CGWindowListCreateImage is no longer in the SDK headers but still
    /// captures the app's own windows without screen-recording permission.
    /// Thread-safe: it only talks to the window server.
    private static let create: CreateImage? = {
        guard let handle = dlopen(nil, RTLD_NOW), let symbol = dlsym(handle, "CGWindowListCreateImage") else { return nil }
        return unsafeBitCast(symbol, to: CreateImage.self)
    }()

    static func capture(windowNumber: Int) -> CGImage? {
        // .optionIncludingWindow, .boundsIgnoreFraming
        create?(.null, 1 << 3, UInt32(windowNumber), 1 << 0)?.takeRetainedValue()
    }

    static func capture(_ window: NSWindow) -> CGImage? {
        if let image = capture(windowNumber: window.windowNumber) {
            return image
        }
        guard let view = window.contentView,
              let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { return nil }
        view.cacheDisplay(in: view.bounds, to: rep)
        return rep.cgImage
    }
}
