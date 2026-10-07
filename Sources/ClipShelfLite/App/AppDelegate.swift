import AppKit
import SwiftUI

extension Notification.Name {
    static let clipShelfOpenSettings = Notification.Name("ClipShelfOpenSettings")
}

enum StatusMenuText {
    static func recordingToggleTitle(isEnabled: Bool) -> String {
        isEnabled ? "暂停记录" : "继续记录"
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private let store = ClipStore.shared
    private let watcher = ScreenshotFolderWatcher.shared
    private var window: NSWindow?
    private var statusItem: NSStatusItem?
    private var controlServer: RuntimeControlServer?

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
        if window == nil {
            let window = NSWindow(
                contentRect: NSRect(x: 0, y: 0, width: 720, height: 552),
                styleMask: [.titled, .closable, .resizable, .miniaturizable],
                backing: .buffered,
                defer: false
            )
            window.title = AppVersionInfo.windowTitle(bundleVersion: AppVersionInfo.bundleVersion)
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

    @objc func toggleClipboardHistory() {
        store.isClipboardHistoryEnabled.toggle()
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
        menu.addItem(menuItem(
            title: StatusMenuText.recordingToggleTitle(isEnabled: store.isClipboardHistoryEnabled),
            action: #selector(toggleClipboardHistory)
        ))
        menu.addItem(menuItem(title: "设置", action: #selector(showSettings)))
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
