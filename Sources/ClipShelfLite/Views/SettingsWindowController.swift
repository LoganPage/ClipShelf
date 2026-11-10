import AppKit
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
    /// Asks ✓ to confirm, as if clicked (see `requestClose`).
    static let confirmCloseNotification = Notification.Name("ClipShelfSettingsConfirmClose")
    /// Settings has folded away and is parked again.
    static let didCloseNotification = Notification.Name("ClipShelfSettingsDidClose")

    /// ✓'s frame in the settings page (SwiftUI global space, top-left origin).
    var doneButtonFrame: CGRect?
    /// ✓ is lit for this closing, so the closing image shows it lit too.
    private var closesWithLitDone = false
    private var doneConfirmationStarted = false

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
    private var backdrop: WindowBackdropView?
    private var hosting: NSView?
    private var isAnimating = false
    /// Whether settings is on screen. While hidden, the window stays ordered
    /// but parked far off screen, so showing it never pays for ordering a glass
    /// window in front (10–25 ms) at the moment the animation starts.
    private var isShown = false
    private static let parkingOrigin = NSPoint(x: -30_000, y: -30_000)
    /// Cross-dissolve between the live glass panel and the animation image.
    private static let handOverDuration: TimeInterval = 0.3
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
    /// Set once the page may differ from `liveImage` (something was clicked,
    /// typed or scrolled in it). Rendering the page takes ~0.1 s on the main
    /// thread, so it is only redone when needed, and then while idle.
    private var liveImageIsStale = true
    private var interactionMonitor: Any?
    private var idleCapture: DispatchWorkItem?
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
                space: parent.screen?.colorSpace?.cgColorSpace ?? CGColorSpace(name: CGColorSpace.sRGB)!,
                // The layout Core Animation draws from directly.
                bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue
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
        installInteractionMonitor()
        markLiveImageStale()
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
        if let image = cleanLiveImage(size: frame.size) {
            prefetchedClose = (image, frame, CACurrentMediaTime())
            return
        }
        captureLive(frame: frame) { [weak self] image in
            self?.prefetchedClose = (image, frame, CACurrentMediaTime())
        }
    }

    /// `liveImage`, when it still shows the page as it is.
    private func cleanLiveImage(size: NSSize) -> CGImage? {
        guard !liveImageIsStale, let liveImage, liveImage.key == lookKey(size: size) else { return nil }
        return liveImage.image
    }

    /// The page changed (or may have): render it again once things are
    /// quiet, so closing can use a ready image instead of freezing to make one.
    private func markLiveImageStale() {
        liveImageIsStale = true
        idleCapture?.cancel()
        let pointerAtSchedule = NSEvent.mouseLocation
        let work = DispatchWorkItem { [weak self] in
            guard let self, self.isShown, !self.isAnimating, let panel = self.panel else { return }
            // Not mid-gesture (e.g. dragging the glass slider), and not
            // while the pointer is on its way somewhere: the render holds
            // the main thread for ~0.1 s, which showed as a hitch in
            // whatever was hovered or scrolled next.
            guard NSEvent.pressedMouseButtons == 0, NSEvent.mouseLocation == pointerAtSchedule else {
                self.markLiveImageStale()
                return
            }
            self.captureLive(frame: panel.frame) { _ in }
        }
        idleCapture = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.0, execute: work)
    }

    /// Anything done in settings may change what it shows.
    private func installInteractionMonitor() {
        guard interactionMonitor == nil else { return }
        interactionMonitor = NSEvent.addLocalMonitorForEvents(matching: [.leftMouseUp, .rightMouseUp, .keyDown, .scrollWheel]) { [weak self] event in
            guard let self, self.isShown, !self.closesSettings(event) else { return event }
            // Keys go to the main window (it keeps focus), but may still edit
            // a field in settings; clicks and scrolls only count inside it.
            if event.type == .keyDown || event.window === self.panel || event.window === self.glassWindow {
                self.markLiveImageStale()
            }
            return event
        }
    }

    private func removeInteractionMonitor() {
        idleCapture?.cancel()
        idleCapture = nil
        if let interactionMonitor {
            NSEvent.removeMonitor(interactionMonitor)
            self.interactionMonitor = nil
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
            content: content,
            colorSpace: parent.screen?.colorSpace?.cgColorSpace ?? CGColorSpace(name: CGColorSpace.sRGB)!
        )
    }

    /// Renders the panel as it is now (no screen capture: that asks for
    /// screen-recording permission), composes it off the main thread and
    /// keeps it for the next opening. `completion` runs on the main thread.
    private func captureLive(frame: NSRect, completion: @escaping (CGImage) -> Void) {
        guard let parent else { return }
        let key = lookKey(size: frame.size)
        renderFallback()
        // What was just rendered is the page as it is now.
        liveImageIsStale = false
        idleCapture?.cancel()
        let request = request(over: parent, frame: frame, content: fallbackContent?.image)
        snapshotQueue.async { [weak self] in
            guard let image = Self.composeSnapshot(request, backdrop: nil)?.image else { return }
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
        // Glass and the edge bands draw as flat stand-ins while rendering
        // (see `glassStandIn`); real glass can't be drawn on the CPU.
        SettingsSnapshotMode.shared.isRendering = true
        hosting.layoutSubtreeIfNeeded()
        let restore = Self.dropShadowsForCPURender(hosting.layer)
        hosting.cacheDisplay(in: hosting.bounds, to: rep)
        restore()
        SettingsSnapshotMode.shared.isRendering = false
        if let image = rep.cgImage {
            fallbackContent = (image, key)
        }
    }

    /// The closing image with ✓ drawn lit and swollen, as it is on screen
    /// when closing starts from it: the image itself is usually made before
    /// the press (on hover, or while idle), and ✓ fell back to unlit in it.
    private func withLitDone(_ image: CGImage, panelSize: NSSize) -> CGImage? {
        guard let button = doneButtonFrame, button.width > 0, panelSize.width > 0,
              let space = image.colorSpace,
              let context = CGContext(
                data: nil,
                width: image.width,
                height: image.height,
                bitsPerComponent: 8,
                bytesPerRow: 0,
                space: space,
                bitmapInfo: image.bitmapInfo.rawValue
              ) else { return nil }
        let scale = CGFloat(image.width) / panelSize.width
        context.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
        let swell = DoneButtonLook.pressedScale
        let center = CGPoint(x: button.midX * scale, y: (panelSize.height - button.midY) * scale)
        let side = button.width * swell * scale
        let circle = CGRect(x: center.x - side / 2, y: center.y - side / 2, width: side, height: side)
        context.setFillColor(NSColor.controlAccentColor.withAlphaComponent(DoneButtonLook.litOpacity).cgColor)
        context.fillEllipse(in: circle)

        let config = NSImage.SymbolConfiguration(pointSize: DoneButtonLook.symbolSize, weight: .bold)
            .applying(.init(paletteColors: [.white]))
        if let check = NSImage(systemSymbolName: "checkmark", accessibilityDescription: nil)?
            .withSymbolConfiguration(config) {
            let size = NSSize(width: check.size.width * swell * scale, height: check.size.height * swell * scale)
            let rect = NSRect(x: center.x - size.width / 2, y: center.y - size.height / 2, width: size.width, height: size.height)
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: false)
            check.draw(in: rect)
            NSGraphicsContext.restoreGraphicsState()
        }
        return context.makeImage()
    }

    /// Takes the layer shadows (the toggles' knobs) off the page while it
    /// is drawn on the CPU for the closing animation's image, and returns
    /// what puts them back. On the CPU each shadow blurred a page-sized
    /// layer (~40 ms apiece); a two-point shadow isn't missed in an image
    /// that only shows while it shrinks away. Layer-level only, so SwiftUI
    /// lays nothing out again.
    private static func dropShadowsForCPURender(_ root: CALayer?) -> () -> Void {
        guard let root else { return {} }
        var shadowed: [(layer: CALayer, opacity: Float)] = []
        func walk(_ layer: CALayer) {
            if layer.shadowOpacity > 0 {
                shadowed.append((layer, layer.shadowOpacity))
            }
            layer.sublayers?.forEach(walk)
        }
        walk(root)
        guard !shadowed.isEmpty else { return {} }
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        shadowed.forEach { $0.layer.shadowOpacity = 0 }
        CATransaction.commit()
        return {
            CATransaction.begin()
            CATransaction.setDisableActions(true)
            shadowed.forEach { $0.layer.shadowOpacity = $0.opacity }
            CATransaction.commit()
        }
    }

    func toggle(from parent: NSWindow?) {
        isVisible ? requestClose() : show(from: parent)
    }

    /// Closes the way clicking ✓ does: ✓ lights up and swells, then settings
    /// folds away. For Esc, Return and the shortcuts that close settings.
    func requestClose() {
        guard isShown, !isAnimating else { return }
        doneConfirmationStarted = false
        NotificationCenter.default.post(name: Self.confirmCloseNotification, object: nil)
        if !doneConfirmationStarted { close() }
    }

    /// ✓ was pressed (or confirmed on its behalf) and is about to close settings.
    func doneWillConfirm() {
        guard isShown, !isAnimating else { return }
        doneConfirmationStarted = true
        closesWithLitDone = true
    }

    func show(from parent: NSWindow?) {
        guard !isAnimating, !isShown, let parent else { return }
        self.parent = parent
        closesWithLitDone = false
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
            // The live view comes in quickly (ease-out) while the animation
            // image underneath leaves slowly (ease-in, see GenieAnimator), so
            // there is no see-through dip in the middle of the hand-over.
            self.setAlpha(1, animatedOver: Self.handOverDuration, timing: .easeOut)
            self.isShown = true
            self.isAnimating = false
            self.installCloseShortcut()
            self.installInteractionMonitor()
            // Without a picture of the page as it is, make one once things
            // are quiet (not right after opening, which used to stutter).
            if self.cleanLiveImage(size: frame.size) == nil {
                self.markLiveImageStale()
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
            GenieAnimator.run(image: snapshot, from: source, to: frame, opening: true, duration: Self.genieDuration, above: parent, crossfade: Self.handOverDuration * 1.6, completion: reveal)
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
        let litDone = closesWithLitDone
        closesWithLitDone = false
        isShown = false
        isAnimating = true
        uninstallCloseShortcut()
        removeInteractionMonitor()

        let tidyUp = { [weak self] in
            guard let self else { return }
            self.detach()
            // Same size, so only move away: no resize or re-layout at the
            // very end of the animation, where it used to stutter.
            self.panel?.setFrameOrigin(Self.parkingOrigin)
            self.glassWindow?.setFrameOrigin(Self.parkingOrigin)
            self.restoreAfterClosing()
            self.setAlpha(1)
            self.isAnimating = false
            NotificationCenter.default.post(name: Self.didCloseNotification, object: nil)
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
            guard var snapshot, let source = self.anchorRectOnScreen(in: parent) else {
                self.setAlpha(0, animatedOver: 0.18)
                tidyUp()
                return
            }
            if litDone {
                snapshot = self.withLitDone(snapshot, panelSize: frame.size) ?? snapshot
            }
            GenieAnimator.run(image: snapshot, from: source, to: frame, opening: false, duration: Self.genieDuration, above: parent, completion: tidyUp)
            self.fadeOutForClosing()
        }

        if let ready = fresh ?? cleanLiveImage(size: frame.size) {
            start(ready)
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
                    self.requestClose()
                }
                return nil
            }
            // Return confirms like ✓, wherever focus is; a field being edited
            // in settings or an open pull-down keeps it.
            if self.isConfirmKey(event), !self.isEditingText, !LiquidMenuPresenter.shared.isOpen {
                self.requestClose()
                return nil
            }
            guard CloseSettingsHotKeyDefaults.load().matches(event) else { return event }
            self.requestClose()
            return nil
        }
    }

    private func isConfirmKey(_ event: NSEvent) -> Bool {
        (event.keyCode == 36 || event.keyCode == 76)
            && event.modifierFlags.intersection([.command, .option, .control, .shift]).isEmpty
    }

    private var isEditingText: Bool {
        guard let panel, panel.isKeyWindow else { return false }
        return panel.firstResponder is NSText
    }

    /// Keys that close settings change nothing on the page.
    private func closesSettings(_ event: NSEvent) -> Bool {
        guard event.type == .keyDown else { return false }
        let plain = event.modifierFlags.intersection([.command, .option, .control, .shift]).isEmpty
        return (event.keyCode == 53 && plain)
            || (isConfirmKey(event) && !isEditingText)
            || CloseSettingsHotKeyDefaults.load().matches(event)
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

    /// The live panel dissolves into the closing animation drawn above it.
    /// Its contents fade in Core Animation, off the main thread: fading the
    /// windows themselves updated the window server every frame, which
    /// stalled the animation's first frames. The glass window's shadow goes
    /// at once; the animation image has none.
    private func fadeOutForClosing() {
        glassWindow?.hasShadow = false
        for view in [glassWindow?.contentView, panel?.contentView].compactMap({ $0 }) {
            view.wantsLayer = true
            guard let layer = view.layer else { continue }
            let fade = CABasicAnimation(keyPath: "opacity")
            fade.fromValue = 1
            fade.toValue = 0
            fade.duration = 0.18
            fade.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
            layer.opacity = 0
            layer.add(fade, forKey: "closingFade")
        }
    }

    private func restoreAfterClosing() {
        for view in [glassWindow?.contentView, panel?.contentView].compactMap({ $0 }) {
            view.layer?.removeAnimation(forKey: "closingFade")
            view.layer?.opacity = 1
        }
        glassWindow?.hasShadow = true
    }

    private func setAlpha(_ alpha: CGFloat, animatedOver duration: TimeInterval = 0, timing: CAMediaTimingFunctionName = .easeInEaseOut) {
        guard duration > 0 else {
            glassWindow?.alphaValue = alpha
            panel?.alphaValue = alpha
            return
        }
        NSAnimationContext.runAnimationGroup { context in
            context.duration = duration
            context.timingFunction = CAMediaTimingFunction(name: timing)
            glassWindow?.animator().alphaValue = alpha
            panel?.animator().alphaValue = alpha
        }
    }

    /// Glass above the main window, content above the glass.
    private func attach(to parent: NSWindow) {
        guard let glassWindow, let panel else { return }
        // Bring both right above the main window first. They were parked off
        // screen since launch, so other apps' windows sit above them; adding
        // them as children from there pulled the whole app down behind those.
        glassWindow.order(.above, relativeTo: parent.windowNumber)
        panel.order(.above, relativeTo: glassWindow.windowNumber)
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
                self?.requestClose()
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
        // While parked, keep both windows parked: entering full screen moved
        // the parked glass onto the screen, where it stayed as a stray pane.
        for window in [panel, glass] as [NSWindow] {
            NotificationCenter.default.addObserver(self, selector: #selector(parkedWindowMoved(_:)), name: NSWindow.didMoveNotification, object: window)
            NotificationCenter.default.addObserver(self, selector: #selector(parkedWindowMoved(_:)), name: NSWindow.didChangeScreenNotification, object: window)
        }
        // The system's border and shadow follow the glass's corners.
        panel.matchSystemCornerRadius(GlassMetrics.panelRadius)
        glass.matchSystemCornerRadius(GlassMetrics.panelRadius)

        self.panel = panel
        applyAppearance()
        return panel
    }

    /// The glass surface on its own (the content lives in the window above).
    private func makeBackdropView() -> NSView {
        let view = WindowBackdropView(cornerRadius: GlassMetrics.panelRadius, lightDepth: 1.8)
        view.apply(GlassPreferences.value, animated: false)
        backdrop = view
        return view
    }

    /// Covers the main window below its titlebar row, with a slim margin.
    /// Everything below the title row: the toolbar and the records are fully
    /// covered, edge to edge, with the window's own bottom corners.
    private func targetFrame(in parent: NSWindow) -> NSRect {
        let titlebar = parent.frame.height - parent.contentLayoutRect.height
        return NSRect(
            x: parent.frame.minX,
            y: parent.frame.minY,
            width: parent.frame.width,
            height: max(parent.frame.height - titlebar, 360)
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
        /// The screen's colour space: an image already in it goes to the
        /// screen as it is, instead of being converted when the animation's
        /// first frame is drawn (~30 ms on the main thread).
        let colorSpace: CGColorSpace
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
                space: request.colorSpace,
                // The layout Core Animation draws from directly.
                bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue
              ) else { return nil }
        let bounds = CGRect(x: 0, y: 0, width: width, height: height)
        let radius = GlassMetrics.panelRadius * scale
        context.addPath(CGPath(roundedRect: bounds, cornerWidth: radius, cornerHeight: radius, transform: nil))
        context.clip()

        // Nothing is captured from the screen (that needs screen-recording
        // permission); the glass colour alone stands in for what's behind.
        let backdrop = cached
        if let backdrop {
            context.draw(backdrop, in: bounds)
        }
        // The real glass's resulting colour (measured on screen), mostly
        // covering the captured main window, so the opening animation hands
        // over to the live glass without a change of colour.
        let level = GlassPreferences.value.strength
        let alpha: CGFloat = backdrop == nil ? 1 : 0.82
        let tint = request.isDark
            ? CGColor(gray: 0.32 - 0.14 * level, alpha: alpha)
            : CGColor(gray: 0.7, alpha: alpha)
        context.setFillColor(tint)
        context.fill(bounds)
        if let content = request.content {
            context.draw(content, in: bounds)
        }
        return context.makeImage().map { ($0, backdrop) }
    }


    @objc private func appearanceChanged() {
        DispatchQueue.main.async { [weak self] in
            self?.applyAppearance()
            self?.scheduleFallbackRender()
        }
    }

    @objc private func parkedWindowMoved(_ notification: Notification) {
        guard !isShown, !isAnimating, let window = notification.object as? NSWindow,
              window.frame.origin != Self.parkingOrigin else { return }
        DispatchQueue.main.async { [weak self] in
            guard let self, !self.isShown, !self.isAnimating else { return }
            self.panel?.setFrameOrigin(Self.parkingOrigin)
            self.glassWindow?.setFrameOrigin(Self.parkingOrigin)
        }
    }

    @objc private func glassChanged() {
        // Dragging the slider: just re-tint the existing glass.
        backdrop?.apply(GlassPreferences.value)
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

    // Keep the glass in its focused look when the app is in the background.
    @objc(_hasActiveAppearance) func keepsActiveAppearance() -> Bool { true }
    @objc(_hasActiveAppearanceIgnoringKeyFocus) func keepsActiveAppearanceIgnoringKeyFocus() -> Bool { true }

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
            .clipShelfAccessibilityLook()
            .scrollIndicators(snapshotMode.isRendering ? .hidden : .automatic)
            // The hidden titlebar shouldn't push the content down.
            .ignoresSafeArea()
            .onReceive(NotificationCenter.default.publisher(for: GlassPreferences.changedNotification)) { notification in
                glassTransparency = notification.object as? GlassTransparency ?? GlassPreferences.value
            }
    }
}

/// Pictures of ClipShelf's own windows for animations, rendered by the app
/// itself: any window-server capture (even of our own windows) makes macOS
/// ask for screen-recording permission, again after every reinstall.
enum WindowSnapshot {
    /// The window's content drawn over its base glass colour, with rounded
    /// corners (live glass can't be drawn off screen, so a matching colour
    /// stands in for it).
    static func capture(_ window: NSWindow, cornerRadius: CGFloat = GlassMetrics.windowRadius) -> CGImage? {
        guard let view = window.contentView,
              let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { return nil }
        view.cacheDisplay(in: view.bounds, to: rep)
        guard let content = rep.cgImage,
              let context = CGContext(
                data: nil,
                width: content.width,
                height: content.height,
                bitsPerComponent: 8,
                bytesPerRow: 0,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
              ) else { return nil }
        let bounds = CGRect(x: 0, y: 0, width: content.width, height: content.height)
        let radius = cornerRadius * CGFloat(content.width) / max(view.bounds.width, 1)
        context.addPath(CGPath(roundedRect: bounds, cornerWidth: radius, cornerHeight: radius, transform: nil))
        context.clip()
        let isDark = window.effectiveAppearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
        let level = GlassPreferences.value.strength
        context.setFillColor(isDark ? CGColor(gray: 0.3 - 0.12 * level, alpha: 1) : CGColor(gray: 0.77, alpha: 1))
        context.fill(bounds)
        context.draw(content, in: bounds)
        return context.makeImage()
    }
}
