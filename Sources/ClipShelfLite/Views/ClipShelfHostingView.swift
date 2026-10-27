import AppKit
import QuartzCore
import SwiftUI

/// Hosting view for the main window.
///
/// While content scrolls, AppKit asks the hosting view to update the cursor on
/// every display cycle and SwiftUI re-sets the arrow each time. When the user
/// has a custom pointer size or colour (Accessibility › Display › Pointer),
/// every `NSCursor.set()` makes macOS regenerate the cursor image on the main
/// thread, which was a large share of each scrolling frame. ClipShelf uses no
/// custom SwiftUI pointer styles, so only touch the cursor when it changes.
final class ClipShelfHostingView<Content: View>: NSHostingView<Content> {
    private var cachedAcceptsFirstResponder: (value: Bool, time: CFTimeInterval)?

    override func cursorUpdate(with event: NSEvent) {
        if NSCursor.current != NSCursor.arrow {
            NSCursor.arrow.set()
        }
    }

    /// With a full-size content view, AppKit recomputes the titlebar drag region
    /// on every scrolled frame and asks this, which makes SwiftUI walk the whole
    /// focus graph. The answer doesn't change frame to frame, so reuse it briefly.
    override var acceptsFirstResponder: Bool {
        let now = CACurrentMediaTime()
        if let cached = cachedAcceptsFirstResponder, now - cached.time < 0.5 {
            return cached.value
        }
        let value = super.acceptsFirstResponder
        cachedAcceptsFirstResponder = (value, now)
        return value
    }
}
