import Foundation

/// Launch work that the first frame doesn't need (the menu bar icon, the
/// Dock tile, the global hot key) runs right after the main window has been
/// drawn, so the window appears sooner.
enum LaunchDeferral {
    private static var pending: [() -> Void] = []
    private static var observer: CFRunLoopObserver?

    static func afterFirstFrame(_ work: @escaping () -> Void) {
        pending.append(work)
        guard observer == nil else { return }
        // Before the run loop first sleeps, after Core Animation's own
        // observer (order 2,000,000) has committed the frame.
        let observer = CFRunLoopObserverCreateWithHandler(
            nil, CFRunLoopActivity.beforeWaiting.rawValue, false, 2_100_000
        ) { observer, _ in
            CFRunLoopRemoveObserver(CFRunLoopGetMain(), observer, .commonModes)
            Self.observer = nil
            let work = pending
            pending = []
            DispatchQueue.main.async {
                work.forEach { $0() }
            }
        }
        self.observer = observer
        CFRunLoopAddObserver(CFRunLoopGetMain(), observer, .commonModes)
    }
}
