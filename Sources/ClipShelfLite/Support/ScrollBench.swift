#if CLIPSHELF_BENCH
import AppKit
import QuartzCore

/// Measures how much main-thread work scrolling costs. Only in builds made
/// with `-Xswiftc -DCLIPSHELF_BENCH`, and only when CLIPSHELF_BENCH_REPORT
/// names a file to write the result to:
///
///     swift build -c release -Xswiftc -DCLIPSHELF_BENCH --scratch-path <dir>
///     CLIPSHELF_DATA_DIR=… CLIPSHELF_DEFAULTS_SUITE=… CLIPSHELF_PASTEBOARD_NAME=… \
///     CLIPSHELF_BENCH_REPORT=<file> <dir>/release/ClipShelf
///
/// It scrolls the history list up and down at a steady speed
/// (CLIPSHELF_BENCH_SPEED points a second, CLIPSHELF_BENCH_SECONDS long),
/// then opens settings and scrolls that (unless CLIPSHELF_BENCH_ONLY=list),
/// and quits. For each it reports the time the
/// main thread was busy per displayed frame and how many frames were late.
enum ScrollBench {
    private static var report = ""
    private static var driver: Driver?

    static func scheduleIfRequested() {
        if let folder = ProcessInfo.processInfo.environment["CLIPSHELF_BENCH_PICTURES"], !folder.isEmpty {
            savePictures(to: URL(fileURLWithPath: folder))
            return
        }
        guard let path = ProcessInfo.processInfo.environment["CLIPSHELF_BENCH_REPORT"], !path.isEmpty else { return }
        let seconds = Double(ProcessInfo.processInfo.environment["CLIPSHELF_BENCH_SECONDS"] ?? "") ?? 6
        DispatchQueue.main.asyncAfter(deadline: .now() + 3) {
            run(name: "history list", seconds: seconds, scrollView: largestScrollView(inSettings: false)) {
                if ProcessInfo.processInfo.environment["CLIPSHELF_BENCH_ONLY"] == "list" {
                    try? report.write(toFile: path, atomically: true, encoding: .utf8)
                    exit(0)
                }
                NotificationCenter.default.post(name: .clipShelfOpenSettings, object: nil)
                DispatchQueue.main.asyncAfter(deadline: .now() + 2.5) {
                    run(name: "settings", seconds: seconds, scrollView: largestScrollView(inSettings: true)) {
                        try? report.write(toFile: path, atomically: true, encoding: .utf8)
                        exit(0)
                    }
                }
            }
        }
    }

    /// Which layers were redrawn on the CPU, by class and owner.
    private static var displayCounts: [String: Int] = [:]
    private static var countsDisplays = false

    private static func installDisplayCounter() {
        guard !countsDisplays, let method = class_getInstanceMethod(CALayer.self, #selector(CALayer.display)) else { return }
        countsDisplays = true
        typealias Display = @convention(c) (CALayer, Selector) -> Void
        let original = unsafeBitCast(method_getImplementation(method), to: Display.self)
        let block: @convention(block) (CALayer) -> Void = { layer in
            var owner = layer.delegate.map { String(describing: type(of: $0)) } ?? "-"
            if let view = layer.delegate as? NSView {
                var chain: [String] = []
                var current: NSView? = view.superview
                while let node = current, chain.count < 3 {
                    chain.append(String(describing: type(of: node)).prefix(40).description)
                    current = node.superview
                }
                owner += " < " + chain.joined(separator: " < ") + " \(Int(view.bounds.width))x\(Int(view.bounds.height))"
            }
            if layer.delegate == nil {
                var chain: [String] = []
                var current = layer.superlayer
                while let node = current, chain.count < 7 {
                    let who = node.delegate.map { String(describing: type(of: $0)).prefix(36).description } ?? "-"
                    chain.append("\(type(of: node))(\(who))")
                    current = node.superlayer
                }
                owner = chain.joined(separator: " < ") + " \(Int(layer.bounds.width))x\(Int(layer.bounds.height))"
            }
            displayCounts["\(type(of: layer)) / \(owner)", default: 0] += 1
            original(layer, #selector(CALayer.display))
        }
        method_setImplementation(method, imp_implementationWithBlock(block))
    }

    /// With CLIPSHELF_BENCH_PICTURES naming a folder: saves the pictures
    /// the window animations are made from (the main window as it pours
    /// out of the menu bar, the settings page as it pours out of its
    /// button), then quits. They are drawn by the app itself, so this
    /// shows what those animations actually display.
    private static func savePictures(to folder: URL) {
        func save(_ image: CGImage?, _ name: String) {
            guard let image else { return }
            try? NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:])?
                .write(to: folder.appendingPathComponent(name + ".png"))
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 4) {
            WindowPictureMode.main.isRendering = true
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 4.03) {
            let main = NSApp.windows.first { $0 is ClipShelfMainWindow }
            let started = CACurrentMediaTime()
            let picture = main.flatMap { WindowSnapshot.capture($0) }
            let took = (CACurrentMediaTime() - started) * 1_000
            WindowPictureMode.main.isRendering = false
            save(picture, "main-window")
            try? String(format: "main window picture drawn in %.1f ms\n", took)
                .write(to: folder.appendingPathComponent("timing.txt"), atomically: true, encoding: .utf8)
            save(SettingsWindowController.shared.benchPageImage(), "settings-page")
            // Then the real thing: hide the window and pour it out of the
            // menu bar, keeping the picture that animation is given.
            revealPicture = { save($0, "menu-bar-reveal") }
            main?.orderOut(nil)
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
                (NSApp.delegate as? AppDelegate)?.showWindowFromMenu()
            }
            DispatchQueue.main.asyncAfter(deadline: .now() + 2) { exit(0) }
        }
    }

    /// Given the picture the menu-bar reveal animates (see AppDelegate).
    static var revealPicture: ((CGImage?) -> Void)?

    private static func run(name: String, seconds: Double, scrollView: NSScrollView?, then next: @escaping () -> Void) {
        installDisplayCounter()
        displayCounts = [:]
        guard let scrollView, let document = scrollView.documentView,
              document.bounds.height > scrollView.contentView.bounds.height + 40 else {
            report += "\(name): nothing to scroll\n"
            next()
            return
        }
        let driver = Driver(scrollView: scrollView, seconds: seconds) { summary in
            report += "\(name): \(summary)\n"
            for (key, count) in displayCounts.sorted(by: { $0.value > $1.value }).prefix(8) {
                report += "    redrawn \(count)x  \(key)\n"
            }
            Self.driver = nil
            next()
        }
        Self.driver = driver
        driver.start()
    }

    private static func largestScrollView(inSettings: Bool) -> NSScrollView? {
        var best: NSScrollView?
        for window in NSApp.windows where window.isVisible && window.frame.minX > -10_000 {
            guard (window is ClipShelfMainWindow) != inSettings, let content = window.contentView else { continue }
            for scrollView in scrollViews(in: content) {
                let area = scrollView.bounds.width * scrollView.bounds.height
                if area > (best.map { $0.bounds.width * $0.bounds.height } ?? 10_000) { best = scrollView }
            }
        }
        return best
    }

    private static func scrollViews(in view: NSView) -> [NSScrollView] {
        ((view as? NSScrollView).map { [$0] } ?? []) + view.subviews.flatMap(scrollViews(in:))
    }

    private final class Driver {
        private let scrollView: NSScrollView
        private let seconds: Double
        private let completion: (String) -> Void
        private var timer: Timer?
        private var observer: CFRunLoopObserver?
        private var startTime: CFTimeInterval = 0
        private var lastTick: CFTimeInterval = 0
        private var wokeAt: CFTimeInterval = 0
        private var busy: CFTimeInterval = 0
        private var busyThisFrame: CFTimeInterval = 0
        private var frameBusy: [Double] = []
        private var intervals: [Double] = []
        private var direction: CGFloat = 1
        /// Points per second, like a brisk two-finger scroll.
        private let speed = CGFloat(Double(ProcessInfo.processInfo.environment["CLIPSHELF_BENCH_SPEED"] ?? "") ?? 1_800)

        init(scrollView: NSScrollView, seconds: Double, completion: @escaping (String) -> Void) {
            self.scrollView = scrollView
            self.seconds = seconds
            self.completion = completion
        }

        func start() {
            startTime = CACurrentMediaTime()
            lastTick = startTime
            wokeAt = startTime
            // Time between waking and going back to sleep is work.
            let activities = CFRunLoopActivity.afterWaiting.rawValue | CFRunLoopActivity.beforeWaiting.rawValue
            let observer = CFRunLoopObserverCreateWithHandler(nil, activities, true, .max) { [weak self] _, activity in
                guard let self else { return }
                let now = CACurrentMediaTime()
                if activity == .afterWaiting {
                    self.wokeAt = now
                } else {
                    self.busy += now - self.wokeAt
                    self.busyThisFrame += now - self.wokeAt
                    self.wokeAt = now
                }
            }
            self.observer = observer
            CFRunLoopAddObserver(CFRunLoopGetMain(), observer, .commonModes)
            let timer = Timer(timeInterval: 1.0 / 60.0, repeats: true) { [weak self] _ in self?.tick() }
            self.timer = timer
            RunLoop.main.add(timer, forMode: .common)
        }

        private func tick() {
            let now = CACurrentMediaTime()
            let elapsed = now - lastTick
            intervals.append(elapsed * 1_000)
            frameBusy.append(busyThisFrame * 1_000)
            busyThisFrame = 0
            lastTick = now
            guard now - startTime < seconds, let document = scrollView.documentView else {
                finish()
                return
            }
            let clip = scrollView.contentView
            let maxY = max(0, document.bounds.height - clip.bounds.height)
            var y = clip.bounds.origin.y + direction * speed * CGFloat(min(elapsed, 0.05))
            if y >= maxY { y = maxY; direction = -1 }
            if y <= 0 { y = 0; direction = 1 }
            clip.scroll(to: NSPoint(x: clip.bounds.origin.x, y: y))
            scrollView.reflectScrolledClipView(clip)
        }

        private func finish() {
            timer?.invalidate()
            if let observer { CFRunLoopRemoveObserver(CFRunLoopGetMain(), observer, .commonModes) }
            let total = CACurrentMediaTime() - startTime
            let sortedBusy = frameBusy.sorted()
            func percentile(_ p: Double) -> Double {
                sortedBusy.isEmpty ? 0 : sortedBusy[min(sortedBusy.count - 1, Int(Double(sortedBusy.count) * p))]
            }
            let late = intervals.filter { $0 > 25 }.count
            completion(String(
                format: "frames=%d late(>25ms)=%d main-busy=%.1f%% per-frame avg=%.2fms p50=%.2fms p95=%.2fms max=%.2fms",
                intervals.count, late, busy / total * 100,
                sortedBusy.reduce(0, +) / Double(max(sortedBusy.count, 1)),
                percentile(0.5), percentile(0.95), sortedBusy.last ?? 0
            ))
        }
    }
}
#endif
