import AppKit
import QuartzCore

/// A Dock-style "genie" transition between a small source rect (the settings
/// button) and a window frame.
///
/// macOS doesn't expose its genie effect, so this rebuilds it with public Core
/// Animation: a snapshot of the window is cut into thin horizontal strips that
/// share edges, and each edge travels between the button and its place in the
/// window on its own schedule. The far end leads when opening (the window pours
/// out of the button like cloth) and trails when closing (it is sucked back in).
/// Strips are composited by the GPU; the main thread only moves ~120 layer frames
/// per tick.
final class GenieAnimator {
    private static var running: [ObjectIdentifier: GenieAnimator] = [:]

    private let window: NSWindow
    private let strips: [CALayer]
    private let source: NSRect
    private let target: NSRect
    private let opening: Bool
    private let duration: TimeInterval
    private let completion: () -> Void
    private var timer: Timer?
    /// When > 0, the snapshot fades out over this long after the motion ends,
    /// dissolving into the real window underneath instead of switching.
    private var crossfade: TimeInterval = 0
    /// Reduce Motion: the snapshot stays in place and only fades.
    private var fadesOnly = false
    private var startTime: CFTimeInterval = 0
    private let stripCount = 120
    private let lead: CGFloat = 0.6

    /// - Parameters:
    ///   - image: snapshot of the window, covering `target` exactly.
    ///   - source: screen rect of the button the window comes out of.
    ///   - target: screen frame of the window.
    ///   - opening: true pours out of `source`, false collapses into it.
    @discardableResult
    static func run(
        image: CGImage,
        from source: NSRect,
        to target: NSRect,
        opening: Bool,
        duration: TimeInterval,
        above parent: NSWindow?,
        crossfade: TimeInterval = 0,
        level: NSWindow.Level? = nil,
        completion: @escaping () -> Void
    ) -> GenieAnimator {
        // Reduce Motion: no pouring out of the button; the window fades in
        // (or out) where it stands.
        let fadesOnly = AccessibilityLook.current.reduceMotion
        let animator = GenieAnimator(
            image: image,
            source: fadesOnly ? target : source,
            target: target,
            opening: opening,
            duration: fadesOnly ? min(duration, 0.2) : duration,
            parent: parent,
            completion: completion
        )
        animator.fadesOnly = fadesOnly
        animator.crossfade = crossfade
        if let level { animator.window.level = level }
        running[ObjectIdentifier(animator)] = animator
        animator.start()
        return animator
    }

    private init(
        image: CGImage,
        source: NSRect,
        target: NSRect,
        opening: Bool,
        duration: TimeInterval,
        parent: NSWindow?,
        completion: @escaping () -> Void
    ) {
        self.source = source
        self.target = target
        self.opening = opening
        self.duration = duration
        self.completion = completion

        let frame = source.union(target).insetBy(dx: -24, dy: -24)
        window = NSWindow(contentRect: frame, styleMask: [.borderless], backing: .buffered, defer: false)
        window.isOpaque = false
        window.backgroundColor = .clear
        window.hasShadow = false
        window.ignoresMouseEvents = true
        window.isReleasedWhenClosed = false
        window.level = parent?.level ?? .normal
        window.collectionBehavior = [.transient, .ignoresCycle]

        let host = NSView(frame: NSRect(origin: .zero, size: frame.size))
        host.wantsLayer = true
        window.contentView = host
        let scale = parent?.backingScaleFactor ?? NSScreen.main?.backingScaleFactor ?? 2

        var layers: [CALayer] = []
        for index in 0..<stripCount {
            let strip = CALayer()
            strip.contents = image
            strip.contentsGravity = .resize
            strip.contentsScale = scale
            strip.magnificationFilter = .linear
            strip.minificationFilter = .linear
            strip.actions = ["position": NSNull(), "bounds": NSNull(), "opacity": NSNull()]
            // Layer contents have their origin at the bottom; strip 0 is the top band.
            let height = 1 / CGFloat(stripCount)
            strip.contentsRect = CGRect(x: 0, y: 1 - height * CGFloat(index + 1), width: 1, height: height)
            host.layer?.addSublayer(strip)
            layers.append(strip)
        }
        strips = layers

        if let parent {
            parent.addChildWindow(window, ordered: .above)
        }
    }

    private func start() {
        if fadesOnly {
            layout(progress: 1)
            window.alphaValue = opening ? 0 : 1
        } else {
            layout(progress: opening ? 0 : 1)
        }
        window.orderFront(nil)
        startTime = CACurrentMediaTime()
        let timer = Timer(timeInterval: 1.0 / 120.0, repeats: true) { [weak self] _ in
            self?.tick()
        }
        self.timer = timer
        RunLoop.main.add(timer, forMode: .common)
    }

    private func tick() {
        let elapsed = CACurrentMediaTime() - startTime
        let linear = min(max(elapsed / duration, 0), 1)
        if fadesOnly {
            window.alphaValue = CGFloat(opening ? linear : 1 - linear)
        } else {
            layout(progress: CGFloat(opening ? linear : 1 - linear))
        }
        if linear >= 1 {
            finish()
        }
    }

    private func finish() {
        timer?.invalidate()
        timer = nil
        completion()
        let remove = { [self] in
            window.parent?.removeChildWindow(window)
            window.orderOut(nil)
            Self.running[ObjectIdentifier(self)] = nil
        }
        guard crossfade > 0 else {
            // Let the real window draw first so there is no blank frame in between.
            DispatchQueue.main.async(execute: remove)
            return
        }
        NSAnimationContext.runAnimationGroup({ context in
            context.duration = crossfade
            // Stays nearly opaque at first while the real window fades in.
            context.timingFunction = CAMediaTimingFunction(name: .easeIn)
            window.animator().alphaValue = 0
        }, completionHandler: remove)
    }

    /// `progress` 0 = collapsed in the button, 1 = the full window.
    private func layout(progress: CGFloat) {
        let origin = window.frame.origin
        let sourceIsAbove = source.midY > target.midY
        var edgeY: [CGFloat] = []
        var edgeWidth: [CGFloat] = []
        var edgeMidX: [CGFloat] = []
        var edgeProgress: [CGFloat] = []

        for edge in 0...stripCount {
            let fraction = CGFloat(edge) / CGFloat(stripCount)
            // 0 = the end nearest the button, 1 = the far end.
            let distance = sourceIsAbove ? fraction : 1 - fraction
            let local = min(max(progress * (1 + lead) - lead * (1 - distance), 0), 1)
            let eased = local * local * (3 - 2 * local)
            let targetY = target.maxY - fraction * target.height
            edgeY.append(source.midY + (targetY - source.midY) * eased)
            edgeWidth.append(source.width + (target.width - source.width) * pow(eased, 1.5))
            edgeMidX.append(source.midX + (target.midX - source.midX) * pow(eased, 0.85))
            edgeProgress.append(local)
        }

        CATransaction.begin()
        CATransaction.setDisableActions(true)
        for (index, strip) in strips.enumerated() {
            let top = edgeY[index]
            let bottom = edgeY[index + 1]
            let width = (edgeWidth[index] + edgeWidth[index + 1]) / 2
            let midX = (edgeMidX[index] + edgeMidX[index + 1]) / 2
            let height = max(abs(top - bottom), 0.5)
            strip.frame = CGRect(
                x: midX - width / 2 - origin.x,
                y: min(top, bottom) - origin.y,
                width: width,
                height: height
            )
            // Bands squeezed into the button fade out instead of piling up.
            strip.opacity = Float(min(1, (edgeProgress[index] + edgeProgress[index + 1]) * 3))
        }
        CATransaction.commit()
    }
}
