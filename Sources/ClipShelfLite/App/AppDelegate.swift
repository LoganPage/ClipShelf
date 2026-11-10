import AppKit
import SwiftUI

extension Notification.Name {
    static let clipShelfOpenSettings = Notification.Name("ClipShelfOpenSettings")
}

enum StatusMenuText {
    static func recordingToggleTitle(isEnabled: Bool) -> String {
        isEnabled ? "暂停记录" : "继续记录"
    }

    /// A record's title in the menu: one short line. A text record's title
    /// is its whole text, which made the menu as wide as the screen.
    static func clipTitle(_ title: String, limit: Int = 48) -> String {
        let line = title.prefix(limit * 4)
            .split(whereSeparator: \.isNewline)
            .first
            .map { $0.trimmingCharacters(in: .whitespaces) } ?? ""
        return line.count > limit ? line.prefix(limit) + "…" : line
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private let store = ClipStore.shared
    private let watcher = ScreenshotFolderWatcher.shared
    private var window: NSWindow?
    private var statusItem: NSStatusItem?
    private var controlServer: RuntimeControlServer?
    private var backdrop: BackdropContainerView?

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.regular)
        CursorSetCoalescer.install()
        normalizeGlobalHotKeyIfNeeded()
        AppIconPreferences.applySavedChoice()
        prepareDockIcon()
        // Selection colour and click-recovery time are no longer in Settings:
        // everyone uses the defaults.
        if SelectionColorPreferences.selectedPreset != .coolGrayBlue {
            SelectionColorPreferences.reset()
        }
        DragSelectionPreferences.resetToDefault()
        showWindow()
        // Not needed for the first frame: done once the window is drawn.
        LaunchDeferral.afterFirstFrame { [weak self] in
            self?.configureStatusItem()
            HotKeyManager.shared.register()
        }
        watcher.start()
        #if CLIPSHELF_BENCH
        ScrollBench.scheduleIfRequested()
        #endif
        HotKeyManager.shared.action = { [weak self] in
            self?.showWindow()
        }
        do {
            controlServer = try RuntimeControlServer.startIfConfigured(store: store)
        } catch {
            NSLog("ClipShelf control server did not start: \(error.localizedDescription)")
        }

        NotificationCenter.default.addObserver(
            self,
            selector: #selector(hotKeyDidChange(_:)),
            name: HotKeyDefaults.changedNotification,
            object: nil
        )
        NotificationCenter.default.addObserver(
            self,
            selector: #selector(appIconDidChange(_:)),
            name: AppIconPreferences.changedNotification,
            object: nil
        )
        NotificationCenter.default.addObserver(
            self,
            selector: #selector(appearanceDidChange(_:)),
            name: AppearancePreferences.changedNotification,
            object: nil
        )
        NotificationCenter.default.addObserver(
            self,
            selector: #selector(glassTransparencyDidChange(_:)),
            name: GlassPreferences.changedNotification,
            object: nil
        )
        DistributedNotificationCenter.default.addObserver(
            self,
            selector: #selector(systemAppearanceDidChange(_:)),
            name: Notification.Name("AppleInterfaceThemeChangedNotification"),
            object: nil
        )
    }

    func applicationWillTerminate(_ notification: Notification) {
        store.flushHistory()
        NotificationCenter.default.removeObserver(self)
        DistributedNotificationCenter.default.removeObserver(self)
        HotKeyManager.shared.unregister()
        controlServer?.stop()
        controlServer = nil
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        showWindow()
        return true
    }

    @objc func showWindow() {
        makeMainWindowIfNeeded()
        AppearancePreferences.apply(to: window)
        window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    // MARK: Menu bar reveal

    private var isRevealingFromStatusItem = false

    /// 显示 ClipShelf from the menu bar. A hidden or closed window pours out of
    /// the menu bar icon; one that is open but behind other windows is simply
    /// brought to the front.
    @objc func showWindowFromMenu() {
        revealFromStatusItem(withSettings: false)
    }

    /// 设置 from the menu bar. A hidden window pours out together with its
    /// settings, as one piece; an open window comes to the front and then
    /// settings pour out of the settings button as usual.
    @objc func showSettingsFromMenu() {
        revealFromStatusItem(withSettings: true)
    }

    private func revealFromStatusItem(withSettings: Bool) {
        guard !isRevealingFromStatusItem else { return }
        let isHidden = window.map { !$0.isVisible && !$0.isMiniaturized } ?? true
        guard isHidden, !NSApp.isHidden, let source = statusItemRectOnScreen() else {
            withSettings ? showSettings() : showWindow()
            return
        }

        makeMainWindowIfNeeded()
        guard let window = window as? ClipShelfMainWindow else {
            withSettings ? showSettings() : showWindow()
            return
        }
        isRevealingFromStatusItem = true
        AppearancePreferences.apply(to: window)
        NSApp.activate(ignoringOtherApps: true)

        // Render the window off screen so there is something to animate.
        let target = window.frame
        window.allowsOffscreenPlacement = true
        window.setFrameOrigin(ClipShelfMainWindow.parkingOrigin)
        window.orderFrontRegardless()
        window.displayIfNeeded()
        if withSettings {
            SettingsWindowController.shared.prepareNow(parent: window)
        }

        // Give the window server a moment to draw it before capturing.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.06) { [weak self, weak window] in
            guard let self, let window else { return }
            var image = WindowSnapshot.capture(window)
            if withSettings, let base = image {
                image = SettingsWindowController.shared.composeOver(mainImage: base, parent: window) ?? base
            }
            let land = { [weak self, weak window] in
                guard let self, let window else { return }
                window.setFrame(target, display: true)
                window.allowsOffscreenPlacement = false
                window.makeKeyAndOrderFront(nil)
                if withSettings {
                    SettingsWindowController.shared.showImmediately(from: window)
                }
                self.isRevealingFromStatusItem = false
            }
            guard let image else {
                land()
                return
            }
            GenieAnimator.run(image: image, from: source, to: target, opening: true, duration: 0.55, above: nil, completion: land)
        }
    }

    private func statusItemRectOnScreen() -> NSRect? {
        guard let buttonWindow = statusItem?.button?.window else { return nil }
        return buttonWindow.frame
    }

    private func makeMainWindowIfNeeded() {
        if window == nil {
            let window = ClipShelfMainWindow(
                contentRect: NSRect(x: 0, y: 0, width: 720, height: 552),
                styleMask: [.titled, .closable, .resizable, .miniaturizable, .fullSizeContentView],
                backing: .buffered,
                defer: false
            )
            window.title = AppVersionInfo.windowTitle(bundleVersion: AppVersionInfo.bundleVersion)
            // One seamless surface: the SwiftUI glass toolbar draws into the titlebar area.
            window.titlebarAppearsTransparent = true
            window.titleVisibility = .hidden
            // Transparent, so the clear end of the glass slider can let the
            // desktop show through the backdrop.
            window.isOpaque = false
            window.backgroundColor = .clear
            // An empty compact toolbar: a slim titlebar, so the type and
            // action bar sits close under the traffic lights.
            window.toolbar = NSToolbar(identifier: "ClipShelfMainToolbar")
            window.toolbarStyle = .unifiedCompact
            // The system draws the window's border and shadow at its own
            // corner radius; match it to the glass so no second outline
            // shows at the corners.
            window.matchSystemCornerRadius(GlassMetrics.windowRadius)
            // Not moved implicitly: the system treated a three-finger drag
            // starting on a record's glass as dragging the window. It moves
            // only from the title row and blank space (see dragWindow).
            window.isMovable = false
            // The traffic lights are drawn by the app as glass (see MainView).
            for button in [NSWindow.ButtonType.closeButton, .miniaturizeButton, .zoomButton] {
                window.standardWindowButton(button)?.isHidden = true
            }
            window.center()
            let hosting = ClipShelfHostingView(rootView: MainView(store: store, watcher: watcher))
            // The window's minimum size is set directly below. Letting the
            // hosting view derive it made SwiftUI size the whole main view
            // once more before its first layout (~50 ms at launch).
            hosting.sizingOptions = []
            // Full-size content: the content view is the whole window.
            window.minSize = MainView.minimumSize
            let backdrop = Self.makeVibrantContentView(hosting: hosting)
            self.backdrop = backdrop
            window.contentView = backdrop
            window.isReleasedWhenClosed = false
            window.setFrameAutosaveName("ClipShelfMainWindow")
            self.window = window
        }
    }

    /// The desktop shows through, blurred and tinted, behind every card and
    /// glass control, which is what gives Liquid Glass something to refract.
    private static func makeVibrantContentView(hosting: NSView) -> BackdropContainerView {
        let backdrop = BackdropContainerView(content: hosting)
        backdrop.apply(GlassPreferences.value)
        return backdrop
    }

    @objc func chooseFolder() {
        watcher.chooseFolder()
    }

    @objc func toggleClipboardHistory() {
        store.isClipboardHistoryEnabled.toggle()
    }

    @objc func toggleSettingsFromMenuBar() {
        toggleSettingsFromShortcut()
    }

    func toggleSettingsFromShortcut() {
        if SettingsWindowController.shared.isVisible {
            SettingsWindowController.shared.requestClose()
        } else {
            showSettings()
        }
    }

    @objc func showSettings() {
        showWindow()
        DispatchQueue.main.async {
            NotificationCenter.default.post(name: .clipShelfOpenSettings, object: nil)
        }
    }

    @objc func clearHistory() {
        let itemCount = store.items.count
        guard ClearHistoryConfirmation.shouldConfirm(itemCount: itemCount) else { return }

        let alert = NSAlert()
        alert.messageText = ClearHistoryConfirmation.title(itemCount: itemCount)
        alert.informativeText = ClearHistoryConfirmation.message(itemCount: itemCount)
        alert.alertStyle = .warning
        alert.addButton(withTitle: "清空")
        alert.addButton(withTitle: "取消")

        if let window, window.isVisible {
            alert.beginSheetModal(for: window) { [weak self] response in
                guard response == .alertFirstButtonReturn else { return }
                self?.store.clearHistory()
            }
        } else {
            NSApp.activate(ignoringOtherApps: true)
            if alert.runModal() == .alertFirstButtonReturn {
                store.clearHistory()
            }
        }
    }

    @objc func quit() {
        NSApp.terminate(nil)
    }

    @objc func hotKeyDidChange(_ notification: Notification) {
        let configuration = notification.object as? HotKeyConfiguration ?? HotKeyDefaults.load()
        HotKeyManager.shared.register(configuration: configuration)
    }

    @objc func appIconDidChange(_ notification: Notification) {
        updateStatusItemIcon()
    }

    @objc func appearanceDidChange(_ notification: Notification) {
        AppearancePreferences.apply(to: window)
        updateDockIcon(animated: true)
    }

    // MARK: Dock icon

    private var dockIconIsDark: Bool?
    private var dockIconTimer: Timer?

    /// At launch the Dock already shows the bundle icon, which is the light
    /// one, so only a dark appearance needs a new tile. Drawing it takes
    /// ~0.1 s, so that waits until the window is drawn.
    private func prepareDockIcon() {
        guard AppearancePreferences.resolvedColorScheme == .dark else {
            dockIconIsDark = false
            return
        }
        LaunchDeferral.afterFirstFrame { [weak self] in
            self?.updateDockIcon(animated: false)
        }
    }

    /// The running app's Dock tile follows light / dark mode, melting from
    /// one glass icon into the other instead of switching abruptly.
    func updateDockIcon(animated: Bool) {
        let dark = AppearancePreferences.resolvedColorScheme == .dark
        guard dark != dockIconIsDark, let target = AppIconPreferences.fullImage(dark: dark) else { return }
        let previous = dockIconIsDark.flatMap { AppIconPreferences.fullImage(dark: $0) }
        dockIconIsDark = dark
        dockIconTimer?.invalidate()
        guard animated, let previous else {
            NSApp.applicationIconImage = target
            return
        }
        let steps = 10
        var step = 0
        dockIconTimer = Timer.scheduledTimer(withTimeInterval: 0.045, repeats: true) { [weak self] timer in
            step += 1
            let t = Double(step) / Double(steps)
            // Ease in and out.
            let eased = t * t * (3 - 2 * t)
            NSApp.applicationIconImage = Self.blend(previous, into: target, amount: eased)
            if step >= steps {
                timer.invalidate()
                NSApp.applicationIconImage = target
                self?.dockIconTimer = nil
            }
        }
    }

    private static func blend(_ from: NSImage, into to: NSImage, amount: Double) -> NSImage {
        let size = NSSize(width: 512, height: 512)
        return NSImage(size: size, flipped: false) { rect in
            from.draw(in: rect, from: .zero, operation: .sourceOver, fraction: 1 - amount)
            to.draw(in: rect, from: .zero, operation: .sourceOver, fraction: amount)
            return true
        }
    }

    @objc func glassTransparencyDidChange(_ notification: Notification) {
        backdrop?.apply(GlassPreferences.value)
    }

    @objc func systemAppearanceDidChange(_ notification: Notification) {
        guard AppearancePreferences.mode == .system else { return }
        DispatchQueue.main.async {
            AppearancePreferences.apply(to: self.window)
            self.updateDockIcon(animated: true)
            NotificationCenter.default.post(name: AppearancePreferences.systemChangedNotification, object: nil)
        }
    }

    private func normalizeGlobalHotKeyIfNeeded() {
        let configuration = HotKeyDefaults.load()
        if configuration.conflictsWithSystemWindowSwitching {
            HotKeyDefaults.save(.defaultValue)
        }
    }

    func menuWillOpen(_ menu: NSMenu) {
        rebuildMenu(menu)
    }

    private func configureStatusItem() {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        item.button?.imagePosition = .imageOnly
        statusItem = item
        updateStatusItemIcon()

        let menu = NSMenu()
        menu.delegate = self
        item.menu = menu
        rebuildMenu(menu)
    }

    private func updateStatusItemIcon() {
        let image = AppIconPreferences.statusBarImage(for: AppIconPreferences.selected)
        statusItem?.button?.image = image
    }

    private func rebuildMenu(_ menu: NSMenu) {
        menu.removeAllItems()
        menu.addItem(menuItem(title: "显示 ClipShelf", action: #selector(showWindowFromMenu)))
        menu.addItem(menuItem(title: "选择截图文件夹", action: #selector(chooseFolder)))
        menu.addItem(menuItem(
            title: StatusMenuText.recordingToggleTitle(isEnabled: store.isClipboardHistoryEnabled),
            action: #selector(toggleClipboardHistory)
        ))
        menu.addItem(menuItem(title: "设置", action: #selector(showSettingsFromMenu)))
        menu.addItem(.separator())

        if store.items.isEmpty {
            let item = NSMenuItem(title: "还没有历史记录", action: nil, keyEquivalent: "")
            item.isEnabled = false
            menu.addItem(item)
        } else {
            for clip in store.items.prefix(8) {
                let item = NSMenuItem(title: StatusMenuText.clipTitle(clip.title), action: nil, keyEquivalent: "")
                let submenu = NSMenu()
                let copy = menuItem(title: "复制", action: #selector(copyMenuItem(_:)))
                copy.representedObject = clip
                submenu.addItem(copy)
                item.submenu = submenu
                menu.addItem(item)
            }
        }

        menu.addItem(.separator())
        menu.addItem(menuItem(title: "清空历史", action: #selector(clearHistory)))
        menu.addItem(menuItem(title: "退出", action: #selector(quit)))
    }

    @objc private func copyMenuItem(_ sender: NSMenuItem) {
        guard let item = sender.representedObject as? ClipItem else { return }
        store.copy(item)
    }

    private func menuItem(title: String, action: Selector) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: "")
        item.target = self
        return item
    }
}

/// The main window. It can be placed off screen briefly so it can be rendered
/// for the reveal animation; AppKit would otherwise pull it back on screen.
extension NSWindow {
    /// Moves the window with this mouse-down, even though it isn't movable
    /// by itself (see ClipShelfMainWindow).
    func dragWindow(with event: NSEvent) {
        let wasMovable = isMovable
        isMovable = true
        performDrag(with: event)
        isMovable = wasMovable
    }

    /// Sets the radius the system uses for the window's border and shadow.
    func matchSystemCornerRadius(_ radius: CGFloat) {
        let selector = NSSelectorFromString("_setCornerRadius:")
        guard responds(to: selector), let method = class_getInstanceMethod(type(of: self), selector) else { return }
        typealias Setter = @convention(c) (AnyObject, Selector, CGFloat) -> Void
        unsafeBitCast(method_getImplementation(method), to: Setter.self)(self, selector, radius)
    }
}

final class ClipShelfMainWindow: NSWindow {
    static let parkingOrigin = NSPoint(x: -30_000, y: -30_000)
    var allowsOffscreenPlacement = false

    override func constrainFrameRect(_ frameRect: NSRect, to screen: NSScreen?) -> NSRect {
        allowsOffscreenPlacement ? frameRect : super.constrainFrameRect(frameRect, to: screen)
    }

    // Keep the glass in its focused look when the app is in the background;
    // the traffic lights still turn grey on their own.
    @objc(_hasActiveAppearance) func keepsActiveAppearance() -> Bool { true }
    @objc(_hasActiveAppearanceIgnoringKeyFocus) func keepsActiveAppearanceIgnoringKeyFocus() -> Bool { true }
}
