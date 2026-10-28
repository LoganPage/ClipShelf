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
///
/// It is really two windows: a glass window underneath and a transparent
/// content window on top. The animation frames are the window server's own
/// picture of the panel as it was last on screen (captured off the main
/// thread, exact, glass included), so ✓ / Esc never wait for the whole SwiftUI
/// page to be re-rendered on the CPU. Only before the very first showing is
/// the page rendered that way, and that happens ahead of time, while idle.
final class SettingsWindowController: NSObject, NSWindowDelegate {
    static let shared = SettingsWindowController()
    static let willShowNotification = Notification.Name("ClipShelfSettingsWillShow")
    static let willCloseNotification = Notification.Name("ClipShelfSettingsWillClose")

    /// The settings button's frame in the main window's SwiftUI global space
    /// (top-left origin). Updated by the toolbar as layout changes.
    var anchorInWindow: CGRect?

    /// The interactive content window (transparent, holds the SwiftUI page).
    private var panel: SettingsPanel?
    /// The glass behind it; ignores the mouse.
    private var glassWindow: NSWindow?
    private weak var parent: NSWindow?
    private var store: ClipStore?
    private var watcher: ScreenshotFolderWatcher?
    private var backdrop: NSView?
    private var hosting: NSView?
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

    /// What a captured image depends on; a different look needs a new one.
    private struct LookKey: Equatable {
        let size: NSSize
        let isDark: Bool
        let glass: GlassTransparency
    }
    /// The whole panel as it last appeared on screen.
    private var liveImage: (image: CGImage, key: LookKey)?
    /// The page alone, rendered on the CPU, for when there is no `liveImage` yet.
    private var fallbackContent: (image: CGImage, key: LookKey)?
    private var pendingFallbackRender = false

    private func lookKey(size: NSSize) -> LookKey {
        let appearance = panel?.effectiveAppearance ?? parent?.effectiveAppearance ?? NSApp.effectiveAppearance
        return LookKey(
            size: size,
            isDark: appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua,
            glass: GlassPreferences.value
        )
    }

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

    /// Builds the windows ahead of time and parks them off screen, so the
    /// first opening has a fully drawn page to capture.
    func prepare(parent: NSWindow) {
        self.parent = parent
        guard panel == nil else { return }
        makePanelIfNeeded()
        park(size: targetFrame(in: parent).size)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.15) { [weak self] in
            self?.warmUpBlur()
        }
        scheduleFallbackRender()
    }

    /// Builds the windows (if needed) and parks them, ready to capture.
    func prepareNow(parent: NSWindow) {
        self.parent = parent
        makePanelIfNeeded()
        park(size: targetFrame(in: parent).size)
        panel?.displayIfNeeded()
    }

    /// The main window's image with the settings panel composed over it in
    /// place, so both can pour out of the menu bar icon as one piece.
    func composeOver(mainImage: CGImage, parent: NSWindow) -> CGImage? {
        let frame = targetFrame(in: parent)
        guard let settings = openingImageNow(over: parent, frame: frame),
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
        makePanelIfNeeded()
        NotificationCenter.default.post(name: Self.willShowNotification, object: nil)
        applyAppearance()
        setFrames(targetFrame(in: parent))
        setAlpha(1)
        attach(to: parent)
        isShown = true
        installCloseShortcut()
    }

    /// Called when the pointer reaches the settings button: get the opening
    /// frame ready, so the genie can start the instant the button is clicked.
    func prefetchOpeningSnapshot(for parent: NSWindow?) {
        guard !isShown, !isAnimating, let parent, panel != nil else { return }
        let frame = targetFrame(in: parent)
        let key = lookKey(size: frame.size)
        if let liveImage, liveImage.key == key {
            prefetched = (liveImage.image, frame, CACurrentMediaTime())
            return
        }
        let request = request(over: parent, frame: frame, content: fallback(for: key))
        snapshotQueue.async { [weak self] in
            guard let result = Self.composeSnapshot(request, backdrop: nil) else { return }
            DispatchQueue.main.async {
                self?.prefetched = (result.image, frame, CACurrentMediaTime())
                if let backdrop = result.backdrop { self?.cachedBackdrop = (backdrop, frame) }
            }
        }
    }

    /// Called when the pointer reaches ✓ (or the settings button while open),
    /// so a click can start the closing genie with its frame already made.
    func prefetchClosingSnapshot() {
        guard isShown, !isAnimating, let panel else { return }
        let frame = panel.frame
        captureLive(frame: frame) { [weak self] image in
            self?.prefetchedClose = (image, frame, CACurrentMediaTime())
        }
    }

    /// The opening frame, made right now (for the menu bar pour-out).
    private func openingImageNow(over parent: NSWindow, frame: NSRect) -> CGImage? {
        let key = lookKey(size: frame.size)
        if let liveImage, liveImage.key == key { return liveImage.image }
        return Self.composeSnapshot(request(over: parent, frame: frame, content: fallback(for: key)), backdrop: nil)?.image
    }

    private func request(over parent: NSWindow, frame: NSRect, content: CGImage?) -> SnapshotRequest {
        SnapshotRequest(
            parentWindowNumber: parent.windowNumber,
            parentFrame: parent.frame,
            scale: parent.backingScaleFactor,
            frame: frame,
            isDark: lookKey(size: frame.size).isDark,
            content: content
        )
    }

    /// Captures the panel exactly as it is on screen, off the main thread, and
    /// keeps it for the next opening. `completion` runs on the main thread.
    private func captureLive(frame: NSRect, completion: @escaping (CGImage) -> Void) {
        let key = lookKey(size: frame.size)
        let screenHeight = NSScreen.screens.first?.frame.height ?? 0
        // Window-server rects are top-left based on the main display.
        let rect = CGRect(x: frame.minX, y: screenHeight - frame.maxY, width: frame.width, height: frame.height)
        let radius = GlassMetrics.panelRadius
        // Front to back: content, its glass, the main window under it.
        let windows = [panel?.windowNumber, glassWindow?.windowNumber, parent?.windowNumber].compactMap { $0 }
        snapshotQueue.async { [weak self] in
            guard let image = WindowSnapshot.captureWindows(windows, rect: rect, cornerRadius: radius) else { return }
            DispatchQueue.main.async {
                self?.liveImage = (image, key)
                completion(image)
            }
        }
    }

    /// The CPU-rendered page for `key`, rendering it now if missing (only
    /// ever needed before settings has been on screen in this look).
    private func fallback(for key: LookKey) -> CGImage? {
        if let fallbackContent, fallbackContent.key == key { return fallbackContent.image }
        renderFallback()
        return fallbackContent?.image
    }

    /// Renders the fallback page a moment later, when the app is idle.
    private func scheduleFallbackRender() {
        guard !pendingFallbackRender else { return }
        pendingFallbackRender = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.6) { [weak self] in
            guard let self else { return }
            self.pendingFallbackRender = false
            guard !self.isShown, !self.isAnimating else { return }
            self.renderFallback()
        }
    }

    private func renderFallback() {
        guard let hosting, let parent, hosting.bounds.width > 0 else { return }
        let key = lookKey(size: targetFrame(in: parent).size)
        guard hosting.bounds.size == key.size,
              let rep = hosting.bitmapImageRepForCachingDisplay(in: hosting.bounds) else { return }
        // The per-row edge blur makes a CPU render ~25× slower; leave it out.
        SettingsSnapshotMode.shared.isRendering = true
        hosting.layoutSubtreeIfNeeded()
        hosting.cacheDisplay(in: hosting.bounds, to: rep)
        SettingsSnapshotMode.shared.isRendering = false
        if let image = rep.cgImage {
            fallbackContent = (image, key)
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
        applyAppearance()
        if panel.frame.size != frame.size || !panel.isVisible {
            park(size: frame.size)
        }

        let reveal = { [weak self] in
            guard let self else { return }
            self.setAlpha(0)
            self.setFrames(frame)
            self.attach(to: parent)
            // Dissolve from the animation image into the live glass rather
            // than switching, so the hand-over has no visible colour jump.
            self.setAlpha(1, animatedOver: Self.handOverDuration)
            self.isShown = true
            self.isAnimating = false
            self.installCloseShortcut()
            // Once the panel has settled, keep its exact on-screen picture
            // for the next opening and closing.
            DispatchQueue.main.asyncAfter(deadline: .now() + Self.handOverDuration + 0.15) { [weak self] in
                guard let self, self.isShown, !self.isAnimating, let panel = self.panel else { return }
                self.captureLive(frame: panel.frame) { _ in }
            }
        }

        guard let source = anchorRectOnScreen(in: parent) else {
            reveal()
            return
        }
        isAnimating = true
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
        let key = lookKey(size: frame.size)
        if let liveImage, liveImage.key == key {
            start(liveImage.image)
            return
        }
        // Never shown in this look yet: compose off the main thread and start
        // as soon as it is ready, rather than freezing the UI meanwhile.
        let request = request(over: parent, frame: frame, content: fallback(for: key))
        snapshotQueue.async { [weak self] in
            let result = Self.composeSnapshot(request, backdrop: nil)
            DispatchQueue.main.async {
                if let backdrop = result?.backdrop { self?.cachedBackdrop = (backdrop, frame) }
                start(result?.image)
            }
        }
    }

    func close() {
        guard !isAnimating, isShown, let panel, let parent else { return }
        LiquidMenuPresenter.shared.dismissImmediately()
        NotificationCenter.default.post(name: Self.willCloseNotification, object: nil)
        let frame = panel.frame
        let fresh = prefetchedClose.flatMap { entry in
            entry.frame == frame && CACurrentMediaTime() - entry.time < 3 ? entry.image : nil
        }
        prefetchedClose = nil
        isShown = false
        isAnimating = true
        uninstallCloseShortcut()

        let tidyUp = { [weak self] in
            guard let self else { return }
            self.detach()
            // Same size, so only move away: no resize or re-layout at the
            // very end of the animation, where it used to stutter.
            self.panel?.setFrameOrigin(Self.parkingOrigin)
            self.glassWindow?.setFrameOrigin(Self.parkingOrigin)
            self.setAlpha(1)
            self.isAnimating = false
            // Hand keyboard focus back only now: the key change re-renders
            // both windows (~40 ms), which used to stall the first frames of
            // the closing animation. The next turn keeps it off its last frame.
            DispatchQueue.main.async { [weak self] in
                guard let self, !self.isShown, !self.isAnimating else { return }
                parent.makeKey()
            }
        }

        // The live panel stays put until the animation frame is ready, then
        // dissolves into it, so the hand-over never freezes on screen.
        let start = { [weak self] (snapshot: CGImage?) in
            guard let self else { return }
            self.setAlpha(0, animatedOver: Self.handOverDuration)
            guard let snapshot, let source = self.anchorRectOnScreen(in: parent) else {
                tidyUp()
                return
            }
            GenieAnimator.run(image: snapshot, from: source, to: frame, opening: false, duration: Self.genieDuration, above: parent, completion: tidyUp)
        }

        if let fresh {
            start(fresh)
            return
        }
        // Without a hover beforehand (Esc, ⌘, or a shortcut): capture the
        // panel as it is on screen, off the main thread (a few milliseconds).
        var started = false
        captureLive(frame: frame) { image in
            guard !started else { return }
            started = true
            start(image)
        }
        // Should the capture ever fail, still close.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) {
            guard !started else { return }
            started = true
            start(nil)
        }
    }

    // MARK: Close shortcut

    private var closeShortcutMonitor: Any?

    /// The user's "关闭设置" shortcut, active only while settings is showing.
    private func installCloseShortcut() {
        guard closeShortcutMonitor == nil else { return }
        closeShortcutMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            guard let self, self.isShown, !self.isAnimating,
                  HotKeyRecorderView.activeRecorders == 0 else { return event }
            // The main window keeps focus while settings is open, so Esc is
            // handled here: an open pull-down first, then settings itself.
            if event.keyCode == 53, event.modifierFlags.intersection([.command, .option, .control, .shift]).isEmpty {
                if LiquidMenuPresenter.shared.isOpen {
                    LiquidMenuPresenter.shared.dismiss()
                } else {
                    self.close()
                }
                return nil
            }
            guard CloseSettingsHotKeyDefaults.load().matches(event) else { return event }
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

    /// Keeps the windows ordered but far off screen, sized like their next showing.
    private func park(size: NSSize) {
        setFrames(NSRect(origin: Self.parkingOrigin, size: size))
        if glassWindow?.isVisible == false {
            glassWindow?.orderFrontRegardless()
        }
        if panel?.isVisible == false {
            panel?.orderFrontRegardless()
        }
        panel?.contentView?.layoutSubtreeIfNeeded()
    }

    private func setFrames(_ frame: NSRect) {
        glassWindow?.setFrame(frame, display: true)
        panel?.setFrame(frame, display: true)
    }

    private func setAlpha(_ alpha: CGFloat, animatedOver duration: TimeInterval = 0) {
        guard duration > 0 else {
            glassWindow?.alphaValue = alpha
            panel?.alphaValue = alpha
            return
        }
        NSAnimationContext.runAnimationGroup { context in
            context.duration = duration
            glassWindow?.animator().alphaValue = alpha
            panel?.animator().alphaValue = alpha
        }
    }

    /// Glass above the main window, content above the glass.
    private func attach(to parent: NSWindow) {
        guard let glassWindow, let panel else { return }
        parent.addChildWindow(glassWindow, ordered: .above)
        glassWindow.addChildWindow(panel, ordered: .above)
    }

    private func detach() {
        guard let glassWindow, let panel else { return }
        glassWindow.removeChildWindow(panel)
        glassWindow.parent?.removeChildWindow(glassWindow)
    }

    private func applyAppearance() {
        if let panel { AppearancePreferences.apply(to: panel) }
        if let glassWindow { AppearancePreferences.apply(to: glassWindow) }
    }

    // MARK: Window

    @discardableResult
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
        // The glass window underneath carries the shadow.
        panel.hasShadow = false
        panel.isMovable = false
        panel.isReleasedWhenClosed = false
        panel.animationBehavior = .none
        // Parked off screen while hidden: keep it out of Mission Control and the window cycle.
        panel.collectionBehavior = [.transient, .ignoresCycle]
        panel.delegate = self
        // Esc closes an open pull-down first, then settings.
        panel.onCancel = { [weak self] in
            if LiquidMenuPresenter.shared.isOpen {
                LiquidMenuPresenter.shared.dismiss()
            } else {
                self?.close()
            }
        }
        // The page is transparent between its cards; without this, clicks and
        // scrolling there fell through to the main window underneath.
        panel.ignoresMouseEvents = false
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
        panel.contentView = hosting

        let glass = SettingsPanel(
            contentRect: panel.frame,
            styleMask: [.borderless],
            backing: .buffered,
            defer: false
        )
        glass.isOpaque = false
        glass.backgroundColor = .clear
        glass.hasShadow = true
        glass.ignoresMouseEvents = true
        glass.isReleasedWhenClosed = false
        glass.animationBehavior = .none
        glass.collectionBehavior = [.transient, .ignoresCycle]
        glass.contentView = makeBackdropView()
        glassWindow = glass

        self.panel = panel
        applyAppearance()
        return panel
    }

    /// The glass surface on its own (the content lives in the window above).
    private func makeBackdropView() -> NSView {
        let radius = GlassMetrics.panelRadius
        if #available(macOS 26.0, *) {
            let glass = NSGlassEffectView()
            glass.cornerRadius = radius
            glass.style = .clear
            glass.tintColor = Self.backdropTint
            backdrop = glass
            return glass
        }
        let effect = NSVisualEffectView()
        effect.material = .hudWindow
        effect.blendingMode = .behindWindow
        effect.state = .active
        effect.wantsLayer = true
        effect.layer?.cornerRadius = radius
        effect.layer?.masksToBounds = true
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

    /// Everything needed to compose a genie frame, safe to use off the main thread.
    private struct SnapshotRequest {
        let parentWindowNumber: Int
        let parentFrame: NSRect
        let scale: CGFloat
        let frame: NSRect
        let isDark: Bool
        /// The page rendered without its glass.
        let content: CGImage?
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
        if let content = request.content {
            context.draw(content, in: bounds)
        }
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
            self?.applyAppearance()
            self?.scheduleFallbackRender()
        }
    }

    /// Clear glass frosted continuously by the transparency slider.
    private static var backdropTint: NSColor {
        // Same range as the main window's background: nearly clear glass at
        // the clear end, firmly frosted at the tinted end.
        NSColor.windowBackgroundColor.withAlphaComponent(0.7 * GlassPreferences.value.level)
    }

    @objc private func glassChanged() {
        // Dragging the slider: just re-tint the existing glass.
        if #available(macOS 26.0, *), let glass = backdrop as? NSGlassEffectView {
            glass.tintColor = Self.backdropTint
        } else {
            glassWindow?.contentView = makeBackdropView()
        }
        scheduleFallbackRender()
    }
}

/// Settings window: no traffic lights, closes on Esc with the genie animation.
/// A panel that only takes keyboard focus when a text field needs it, so the
/// main window stays key (and looks focused) while settings is open.
final class SettingsPanel: NSPanel {
    var onCancel: (() -> Void)?

    override init(contentRect: NSRect, styleMask style: NSWindow.StyleMask, backing backingStoreType: NSWindow.BackingStoreType, defer flag: Bool) {
        super.init(contentRect: contentRect, styleMask: style, backing: backingStoreType, defer: flag)
        becomesKeyOnlyIfNeeded = true
        hidesOnDeactivate = false
        isFloatingPanel = false
    }

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
    @ObservedObject private var snapshotMode = SettingsSnapshotMode.shared

    var body: some View {
        SettingsView(store: store, watcher: watcher, onClose: onClose)
            .environment(\.glassTransparency, glassTransparency)
            .environment(\.glassStandIn, snapshotMode.isRendering)
            .scrollIndicators(snapshotMode.isRendering ? .hidden : .automatic)
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

    private typealias CreateImageFromArray = @convention(c) (CGRect, CFArray, UInt32) -> Unmanaged<CGImage>?

    private static let createFromArray: CreateImageFromArray? = {
        guard let handle = dlopen(nil, RTLD_NOW), let symbol = dlsym(handle, "CGWindowListCreateImageFromArray") else { return nil }
        return unsafeBitCast(symbol, to: CreateImageFromArray.self)
    }()

    /// `windowNumbers` (this app's own windows, front to back) composited
    /// within `rect` (top-left based, in points), with the corners outside
    /// `cornerRadius` made transparent. Naming only our own windows needs no
    /// screen recording permission (a plain screen-region capture asked for
    /// it, again after every reinstall). Safe to call off the main thread.
    static func captureWindows(_ windowNumbers: [Int], rect: CGRect, cornerRadius: CGFloat) -> CGImage? {
        var ids: [UnsafeRawPointer?] = windowNumbers.map { UnsafeRawPointer(bitPattern: UInt($0)) }
        guard let array = CFArrayCreate(nil, &ids, ids.count, nil),
              let image = createFromArray?(rect, array, 0)?.takeRetainedValue(),
              let context = CGContext(
                data: nil,
                width: image.width,
                height: image.height,
                bitsPerComponent: 8,
                bytesPerRow: 0,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
              ) else { return nil }
        let bounds = CGRect(x: 0, y: 0, width: image.width, height: image.height)
        let radius = cornerRadius * CGFloat(image.width) / max(rect.width, 1)
        context.addPath(CGPath(roundedRect: bounds, cornerWidth: radius, cornerHeight: radius, transform: nil))
        context.clip()
        context.draw(image, in: bounds)
        return context.makeImage()
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
