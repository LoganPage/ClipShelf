import AppKit
import ObjectiveC
import QuartzCore

/// While content scrolls, AppKit re-sets the same arrow cursor on every display
/// cycle. With a custom pointer size or colour (Accessibility › Display ›
/// Pointer), each `NSCursor.set()` makes macOS regenerate the cursor image on
/// the main thread, which was about a fifth of every scrolled frame.
///
/// This skips a repeat `set()` of the cursor that is already current when the
/// previous set happened moments ago. Any different cursor, or the same one
/// after a short pause, still goes through untouched.
enum CursorSetCoalescer {
    fileprivate static var lastSetCursor: ObjectIdentifier?
    fileprivate static var lastSetTime: CFTimeInterval = 0
    fileprivate static let window: CFTimeInterval = 0.1
    private static var isInstalled = false

    static func install() {
        guard !isInstalled,
              let original = class_getInstanceMethod(NSCursor.self, #selector(NSCursor.set)),
              let replacement = class_getInstanceMethod(NSCursor.self, #selector(NSCursor.clipShelf_coalescedSet)) else {
            return
        }
        method_exchangeImplementations(original, replacement)
        isInstalled = true
    }
}

extension NSCursor {
    @objc fileprivate func clipShelf_coalescedSet() {
        let now = CACurrentMediaTime()
        let identity = ObjectIdentifier(self)
        if identity == CursorSetCoalescer.lastSetCursor,
           NSCursor.current === self,
           now - CursorSetCoalescer.lastSetTime < CursorSetCoalescer.window {
            CursorSetCoalescer.lastSetTime = now
            return
        }
        CursorSetCoalescer.lastSetCursor = identity
        CursorSetCoalescer.lastSetTime = now
        // Implementations are exchanged, so this calls the original -set.
        clipShelf_coalescedSet()
    }
}
