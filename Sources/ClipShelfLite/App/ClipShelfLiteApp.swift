import AppKit

/// ClipShelf runs on the AppKit app lifecycle. All of its windows are
/// AppKit-managed, and the SwiftUI `App` shell cost about a fifth of a second
/// at launch: an invisible menu bar extra it needed as its one scene, and a
/// generated main menu that loaded extra frameworks.
enum ClipShelfApp {
    private static let delegate = AppDelegate()

    static func main() {
        let app = NSApplication.shared
        app.delegate = delegate
        app.mainMenu = MainMenu.make(delegate: delegate)
        app.run()
    }
}

enum MainMenu {
    static func make(delegate: AppDelegate) -> NSMenu {
        let main = NSMenu()

        let appMenu = submenu(in: main, title: "ClipShelf")
        appMenu.addItem(item("关于 ClipShelf", #selector(NSApplication.orderFrontStandardAboutPanel(_:))))
        appMenu.addItem(.separator())
        // "设置…" (⌘,) opens ClipShelf's own settings, or closes them when
        // they are already open.
        let settings = item("设置…", #selector(AppDelegate.toggleSettingsFromMenuBar), ",")
        settings.target = delegate
        appMenu.addItem(settings)
        appMenu.addItem(.separator())
        let services = NSMenuItem(title: "服务", action: nil, keyEquivalent: "")
        let servicesMenu = NSMenu(title: "服务")
        services.submenu = servicesMenu
        NSApp.servicesMenu = servicesMenu
        appMenu.addItem(services)
        appMenu.addItem(.separator())
        appMenu.addItem(item("隐藏 ClipShelf", #selector(NSApplication.hide(_:)), "h"))
        appMenu.addItem(item("隐藏其他", #selector(NSApplication.hideOtherApplications(_:)), "h", [.command, .option]))
        appMenu.addItem(item("全部显示", #selector(NSApplication.unhideAllApplications(_:))))
        appMenu.addItem(.separator())
        appMenu.addItem(item("退出 ClipShelf", #selector(NSApplication.terminate(_:)), "q"))

        let edit = submenu(in: main, title: "编辑")
        edit.addItem(item("撤销", Selector(("undo:")), "z"))
        edit.addItem(item("重做", Selector(("redo:")), "z", [.command, .shift]))
        edit.addItem(.separator())
        edit.addItem(item("剪切", #selector(NSText.cut(_:)), "x"))
        edit.addItem(item("拷贝", #selector(NSText.copy(_:)), "c"))
        edit.addItem(item("粘贴", #selector(NSText.paste(_:)), "v"))
        edit.addItem(item("删除", #selector(NSText.delete(_:))))
        edit.addItem(item("全选", #selector(NSText.selectAll(_:)), "a"))

        let window = submenu(in: main, title: "窗口")
        window.addItem(item("关闭", #selector(NSWindow.performClose(_:)), "w"))
        window.addItem(item("最小化", #selector(NSWindow.performMiniaturize(_:)), "m"))
        window.addItem(item("缩放", #selector(NSWindow.performZoom(_:))))
        window.addItem(.separator())
        window.addItem(item("前置全部窗口", #selector(NSApplication.arrangeInFront(_:))))
        NSApp.windowsMenu = window

        return main
    }

    private static func submenu(in main: NSMenu, title: String) -> NSMenu {
        let holder = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        let menu = NSMenu(title: title)
        holder.submenu = menu
        main.addItem(holder)
        return menu
    }

    private static func item(
        _ title: String,
        _ action: Selector,
        _ key: String = "",
        _ modifiers: NSEvent.ModifierFlags = .command
    ) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: key)
        item.keyEquivalentModifierMask = modifiers
        return item
    }
}
