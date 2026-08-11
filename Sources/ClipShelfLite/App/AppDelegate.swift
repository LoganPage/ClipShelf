import AppKit
import SwiftUI

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private let store = ClipStore.shared
    private let watcher = ScreenshotFolderWatcher.shared
    private var window: NSWindow?
    private var statusItem: NSStatusItem?

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.regular)
        normalizeGlobalHotKeyIfNeeded()
        AppIconPreferences.applySavedChoice()
        configureStatusItem()
        showWindow()
        watcher.start()
        HotKeyManager.shared.action = { [weak self] in
            self?.showWindow()
        }
        HotKeyManager.shared.register()

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
        DistributedNotificationCenter.default.addObserver(
            self,
            selector: #selector(systemAppearanceDidChange(_:)),
            name: Notification.Name("AppleInterfaceThemeChangedNotification"),
            object: nil
        )
    }

    func applicationWillTerminate(_ notification: Notification) {
        NotificationCenter.default.removeObserver(self)
        DistributedNotificationCenter.default.removeObserver(self)
        HotKeyManager.shared.unregister()
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        showWindow()
        return true
    }

    @objc func showWindow() {
        if window == nil {
            let window = NSWindow(
                contentRect: NSRect(x: 0, y: 0, width: 720, height: 540),
                styleMask: [.titled, .closable, .resizable, .miniaturizable],
                backing: .buffered,
                defer: false
            )
            window.title = "ClipShelf"
            window.center()
            window.contentView = NSHostingView(rootView: MainView(store: store, watcher: watcher))
            window.isReleasedWhenClosed = false
            window.setFrameAutosaveName("ClipShelfMainWindow")
            self.window = window
        }

        AppearancePreferences.apply(to: window)
        window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    @objc func chooseFolder() {
        watcher.chooseFolder()
    }

    @objc func clearHistory() {
        store.clearHistory()
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
    }

    @objc func systemAppearanceDidChange(_ notification: Notification) {
        guard AppearancePreferences.mode == .system else { return }
        DispatchQueue.main.async {
            AppearancePreferences.apply(to: self.window)
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
        menu.addItem(menuItem(title: "显示 ClipShelf", action: #selector(showWindow)))
        menu.addItem(menuItem(title: "选择截图文件夹", action: #selector(chooseFolder)))
        menu.addItem(.separator())

        if store.items.isEmpty {
            let item = NSMenuItem(title: "还没有历史记录", action: nil, keyEquivalent: "")
            item.isEnabled = false
            menu.addItem(item)
        } else {
            for clip in store.items.prefix(8) {
                let item = NSMenuItem(title: clip.title, action: nil, keyEquivalent: "")
                let submenu = NSMenu()
                let copy = menuItem(title: "复制", action: #selector(copyMenuItem(_:)))
                copy.representedObject = clip
                submenu.addItem(copy)

                let paste = menuItem(title: "粘贴", action: #selector(pasteMenuItem(_:)))
                paste.representedObject = clip
                submenu.addItem(paste)
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

    @objc private func pasteMenuItem(_ sender: NSMenuItem) {
        guard let item = sender.representedObject as? ClipItem else { return }
        store.paste(item)
    }

    private func menuItem(title: String, action: Selector) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: "")
        item.target = self
        return item
    }
}
