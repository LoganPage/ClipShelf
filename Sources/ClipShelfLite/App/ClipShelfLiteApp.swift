import SwiftUI

struct ClipShelfApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    var body: some Scene {
        // SwiftUI needs at least one scene, but all of ClipShelf's windows are
        // AppKit-managed. A `Settings` scene here used to open an empty window
        // at launch; a menu bar extra that is never inserted creates nothing.
        // The status item itself is owned by AppDelegate.
        MenuBarExtra("ClipShelf", systemImage: "doc.on.clipboard", isInserted: .constant(false)) {
            EmptyView()
        }
        .commands {
            // "设置…" (⌘,) opens ClipShelf's own settings window, or closes it
            // when it is already open.
            CommandGroup(replacing: .appSettings) {
                Button("设置…") {
                    appDelegate.toggleSettingsFromShortcut()
                }
                .keyboardShortcut(",", modifiers: .command)
            }
        }
    }
}
