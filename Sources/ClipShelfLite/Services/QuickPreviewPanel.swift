import AppKit
import Quartz

/// ClipShelf's Space preview: laid out and behaving like the system Quick
/// Look window (close and full-screen on the left, the name, then share and
/// "open with" on the right; content that adapts to every file type, from
/// Quick Look itself), on Liquid Glass.
final class QuickPreviewPanel: NSPanel {
    static let headerHeight: CGFloat = 52
    static let cornerRadius: CGFloat = 26

    var onClose: (() -> Void)?
    var onSpace: (() -> Void)?

    private let previewView: QLPreviewView
    private let titleLabel = NSTextField(labelWithString: "")
    private let openButton = PressScaleButton(title: "", target: nil, action: nil)
    private var shareButton: NSButton?
    private(set) var url: URL?

    init() {
        previewView = QLPreviewView(frame: .zero, style: .normal)!
        super.init(
            contentRect: NSRect(x: 0, y: 0, width: 816, height: 728),
            styleMask: [.titled, .closable, .resizable, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        titleVisibility = .hidden
        titlebarAppearsTransparent = true
        isOpaque = false
        backgroundColor = .clear
        isReleasedWhenClosed = false
        level = .floating
        collectionBehavior = [.fullScreenPrimary]
        // Moved only explicitly (header, three-finger drag) with
        // performDrag, which the window server tracks 1:1; dragging by the
        // background lagged behind the pointer.
        isMovableByWindowBackground = false
        minSize = NSSize(width: 360, height: 260)
        matchSystemCornerRadius(Self.cornerRadius)
        // Its own buttons replace the system ones, as in Quick Look.
        for button in [NSWindow.ButtonType.closeButton, .miniaturizeButton, .zoomButton] {
            standardWindowButton(button)?.isHidden = true
        }
        contentView = makeContent()
    }

    // MARK: Content

    func show(url: URL, title: String) {
        self.url = url
        titleLabel.stringValue = title
        previewView.previewItem = url as NSURL
        updateOpenButton(for: url)
    }

    private func makeContent() -> NSView {
        let root = PreviewTouchView()
        root.wantsLayer = true

        let backdrop = WindowBackdropView(cornerRadius: Self.cornerRadius)
        backdrop.apply(GlassPreferences.value, animated: false)

        previewView.autostarts = true
        previewView.shouldCloseWithWindow = false
        previewView.wantsLayer = true

        let header = makeHeader()

        for view in [backdrop, previewView, header] {
            view.translatesAutoresizingMaskIntoConstraints = false
            root.addSubview(view)
        }
        NSLayoutConstraint.activate([
            backdrop.leadingAnchor.constraint(equalTo: root.leadingAnchor),
            backdrop.trailingAnchor.constraint(equalTo: root.trailingAnchor),
            backdrop.topAnchor.constraint(equalTo: root.topAnchor),
            backdrop.bottomAnchor.constraint(equalTo: root.bottomAnchor),

            header.leadingAnchor.constraint(equalTo: root.leadingAnchor),
            header.trailingAnchor.constraint(equalTo: root.trailingAnchor),
            header.topAnchor.constraint(equalTo: root.topAnchor),
            header.heightAnchor.constraint(equalToConstant: Self.headerHeight),

            previewView.leadingAnchor.constraint(equalTo: root.leadingAnchor),
            previewView.trailingAnchor.constraint(equalTo: root.trailingAnchor),
            previewView.topAnchor.constraint(equalTo: header.bottomAnchor),
            previewView.bottomAnchor.constraint(equalTo: root.bottomAnchor)
        ])
        // The preview keeps inside the rounded corners.
        previewView.layer?.cornerRadius = Self.cornerRadius
        previewView.layer?.maskedCorners = [.layerMinXMinYCorner, .layerMaxXMinYCorner]
        previewView.layer?.cornerCurve = .continuous
        previewView.layer?.masksToBounds = true
        return root
    }

    private func makeHeader() -> NSView {
        let close = glassCircleButton(symbol: "xmark", label: "关闭", action: #selector(closeClicked))
        let fullScreen = glassCircleButton(symbol: "arrow.up.left.and.arrow.down.right", label: "全屏", action: #selector(fullScreenClicked))
        let share = glassCircleButton(symbol: "square.and.arrow.up", label: "共享", action: #selector(shareClicked(_:)))
        shareButton = share.button

        titleLabel.font = .systemFont(ofSize: 13, weight: .semibold)
        titleLabel.lineBreakMode = .byTruncatingMiddle
        titleLabel.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)

        openButton.isBordered = false
        openButton.font = .systemFont(ofSize: 12.5, weight: .medium)
        openButton.target = self
        openButton.action = #selector(openClicked)
        let openCapsule = PreviewGlassBar(frame: .zero, cornerRadius: 15)
        openButton.translatesAutoresizingMaskIntoConstraints = false
        openCapsule.addSubview(openButton)
        NSLayoutConstraint.activate([
            openButton.leadingAnchor.constraint(equalTo: openCapsule.leadingAnchor, constant: 14),
            openButton.trailingAnchor.constraint(equalTo: openCapsule.trailingAnchor, constant: -14),
            openButton.centerYAnchor.constraint(equalTo: openCapsule.centerYAnchor),
            openCapsule.heightAnchor.constraint(equalToConstant: 30)
        ])

        let spacer = NSView()
        spacer.setContentHuggingPriority(.init(1), for: .horizontal)
        let stack = NSStackView(views: [close.view, fullScreen.view, titleLabel, spacer, share.view, openCapsule])
        stack.orientation = .horizontal
        stack.alignment = .centerY
        stack.spacing = 8
        stack.setCustomSpacing(14, after: fullScreen.view)
        stack.edgeInsets = NSEdgeInsets(top: 0, left: 14, bottom: 0, right: 14)

        // The header is a drag area for the window, like a title bar.
        let header = HeaderDragView()
        stack.translatesAutoresizingMaskIntoConstraints = false
        header.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: header.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: header.trailingAnchor),
            stack.topAnchor.constraint(equalTo: header.topAnchor),
            stack.bottomAnchor.constraint(equalTo: header.bottomAnchor)
        ])
        return header
    }

    private func glassCircleButton(symbol: String, label: String, action: Selector) -> (view: NSView, button: NSButton) {
        let size: CGFloat = 30
        let glass = PreviewGlassBar(frame: NSRect(x: 0, y: 0, width: size, height: size), cornerRadius: size / 2)
        let button = PressScaleButton(image: NSImage(systemSymbolName: symbol, accessibilityDescription: label) ?? NSImage(), target: self, action: action)
        button.isBordered = false
        button.symbolConfiguration = .init(pointSize: 12, weight: .semibold)
        button.toolTip = label
        button.frame = glass.bounds
        button.autoresizingMask = [.width, .height]
        glass.addSubview(button)
        glass.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            glass.widthAnchor.constraint(equalToConstant: size),
            glass.heightAnchor.constraint(equalToConstant: size)
        ])
        return (glass, button)
    }

    /// "用“预览”打开", naming the app that opens this file by default.
    private func updateOpenButton(for url: URL) {
        if let app = NSWorkspace.shared.urlForApplication(toOpen: url) {
            let name = FileManager.default.displayName(atPath: app.path)
                .replacingOccurrences(of: ".app", with: "")
            openButton.title = "用“\(name)”打开"
        } else {
            openButton.title = "打开"
        }
    }

    // MARK: Actions

    @objc private func closeClicked() { onClose?() }

    @objc private func fullScreenClicked() { toggleFullScreen(nil) }

    @objc private func openClicked() {
        guard let url else { return }
        NSWorkspace.shared.open(url)
        onClose?()
    }

    @objc private func shareClicked(_ sender: NSButton) {
        guard let url else { return }
        NSSharingServicePicker(items: [url]).show(relativeTo: sender.bounds, of: sender, preferredEdge: .minY)
    }

    // MARK: Keys

    override var canBecomeKey: Bool { true }

    override func sendEvent(_ event: NSEvent) {
        if event.type == .leftMouseDown, TrackpadFingers.count >= 3 {
            performDrag(with: event)
            return
        }
        super.sendEvent(event)
    }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 49 {
            onSpace?()
        } else {
            super.keyDown(with: event)
        }
    }

    override func cancelOperation(_ sender: Any?) {
        onClose?()
    }
}

/// Lets the preview's header move the window, like a title bar.
private final class HeaderDragView: NSView {
    override var mouseDownCanMoveWindow: Bool { true }

    override func mouseDown(with event: NSEvent) {
        window?.performDrag(with: event)
    }
}

/// Counts the fingers on the trackpad over the preview (see TrackpadFingers).
private final class PreviewTouchView: NSView {
    override init(frame: NSRect) {
        super.init(frame: frame)
        allowedTouchTypes = [.indirect]
        wantsRestingTouches = true
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func touchesBegan(with event: NSEvent) { TrackpadFingers.update(event, in: self) }
    override func touchesMoved(with event: NSEvent) { TrackpadFingers.update(event, in: self) }
    override func touchesEnded(with event: NSEvent) { TrackpadFingers.update(event, in: self) }
    override func touchesCancelled(with event: NSEvent) { TrackpadFingers.update(event, in: self) }
}

/// A button whose glass capsule (its superview) gives the app's shared press
/// feedback: it swells to the same scale with the same springs while pressed,
/// and a quick click is held for a moment so it shows.
final class PressScaleButton: NSButton {
    override func mouseDown(with event: NSEvent) {
        let target = superview ?? self
        let pressedAt = CACurrentMediaTime()
        Self.animate(target, to: PressFeedback.defaultScale, response: 0.22, dampingFraction: 0.6)
        // Tracks the click until the mouse goes up (and sends the action).
        super.mouseDown(with: event)
        let wait = max(0, PressFeedback.minimumHold - (CACurrentMediaTime() - pressedAt))
        DispatchQueue.main.asyncAfter(deadline: .now() + wait) {
            Self.animate(target, to: 1, response: 0.4, dampingFraction: 0.55)
        }
    }

    /// Scales the view's layer about its centre with a spring matching
    /// SwiftUI's `.spring(response:dampingFraction:)`.
    private static func animate(_ view: NSView, to scale: CGFloat, response: Double, dampingFraction: Double) {
        view.wantsLayer = true
        guard let layer = view.layer else { return }
        let size = view.bounds.size
        // A layer-backed view scales about its corner; move the pivot to
        // the middle.
        var transform = CATransform3DMakeTranslation(size.width / 2, size.height / 2, 0)
        transform = CATransform3DScale(transform, scale, scale, 1)
        transform = CATransform3DTranslate(transform, -size.width / 2, -size.height / 2, 0)

        let spring = CASpringAnimation(keyPath: "transform")
        spring.mass = 1
        spring.stiffness = pow(2 * .pi / response, 2)
        spring.damping = 4 * .pi * dampingFraction / response
        spring.fromValue = layer.presentation()?.transform ?? layer.transform
        spring.toValue = transform
        spring.duration = spring.settlingDuration
        layer.transform = transform
        layer.add(spring, forKey: "pressScale")
    }
}
