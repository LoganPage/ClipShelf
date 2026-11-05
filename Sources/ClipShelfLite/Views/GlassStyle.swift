import AppKit
import QuartzCore
import SwiftUI

/// Shared measurements and surfaces so the main window, settings and
/// switchers all speak the same visual language.
enum GlassMetrics {
    /// Search field, switchers and toolbar capsules share one height.
    static let controlHeight: CGFloat = 38
    /// One spacing between stacked toolbar controls and the first record.
    static let stackSpacing: CGFloat = 10
    /// Shared left/right inset for the toolbar and the record list.
    static let edgeInset: CGFloat = 16
    static let cardRadius: CGFloat = 16
    /// Main window corners, concentric with the record cards inside
    /// (card radius + the inset between card and window edge).
    static let windowRadius: CGFloat = cardRadius + edgeInset
    /// Settings fills the window below the title row, so it shares the
    /// window's corners.
    static let panelRadius: CGFloat = windowRadius
    static let thumbnailRadius: CGFloat = 10
}

// MARK: - Transparency preference

/// How see-through the glass is, set steplessly like the Liquid Glass slider
/// in Apple's settings: 0 is the clearest glass, 1 the most tinted.
struct GlassTransparency: Equatable {
    var level: Double

    static let standard = GlassTransparency(level: 0.8)

    init(level: Double) {
        self.level = min(max(level, 0), 1)
    }

    /// How strongly the glass is tinted or frosted. The slider's full range
    /// covers what used to be its left half: the old midpoint is the new
    /// right end, so the most solid setting still reads as glass.
    var strength: Double { level * 0.5 }

    // The window and settings panel are frosted Liquid Glass with low
    // transparency (one blur for the whole window); the cards on top are
    // crystal-clear glass, drawn as an almost-clear pane with lit edges, so
    // scrolling costs next to nothing. Controls are clear glass. The slider
    // (clear ↔ tinted) adds tint throughout.

    /// Tint over the frosted window / settings panel glass: 0.22 → 0.62.
    var windowGlassTint: Double { 0.22 + 0.4 * strength }

    /// Dark mode: smoke over the window glass, darker than the system's
    /// focused glass but lighter than its inactive look.
    var darkWindowSmoke: Double { 0.12 + 0.3 * strength }

    /// Light mode: grey tint over the window glass, calming its glare. The
    /// same at every slider position.
    var lightWindowTint: Double { 0.44 }

    /// Light mode: white over a card.
    /// From nearly clear glass to frosted white as the slider goes right.
    var lightCardFill: Double { 0.18 + 0.64 * strength }


    /// Tint of a card's clear glass: nearly clear, milkier towards tinted.
    func cardTint(isDark: Bool) -> Double { isDark ? 0.3 + 0.25 * strength : 0.14 + 0.3 * strength }

    // Kept for the pre-macOS-26 fallback.
    var backdropOpacity: Double { 0.75 + 0.25 * strength }
    var backdropTint: Double { 0.3 * strength }
}

enum GlassPreferences {
    static let changedNotification = Notification.Name("ClipShelfGlassTransparencyChanged")
    /// The slider position on the current scale (see `strength`).
    private static let key = "glass.slider"
    /// The earlier scale, twice as strong per step.
    private static let previousKey = "glass.level"
    private static let legacyKey = "glass.transparency"


    static var value: GlassTransparency {
        get {
            let defaults = AppEnvironment.userDefaults
            if defaults.object(forKey: key) != nil {
                return GlassTransparency(level: defaults.double(forKey: key))
            }
            // Earlier settings, converted so they look the same as before.
            if defaults.object(forKey: previousKey) != nil {
                return GlassTransparency(level: defaults.double(forKey: previousKey) * 2)
            }
            switch defaults.string(forKey: legacyKey) {
            case "clear": return GlassTransparency(level: 0.2)
            case "tinted", "opaque": return GlassTransparency(level: 1)
            default: return .standard
            }
        }
        set {
            AppEnvironment.userDefaults.set(newValue.level, forKey: key)
            // Glass tints don't animate by themselves, so the new look used
            // to switch in one frame. Every visible window cross-fades from
            // its old picture to the new one instead (done by the window
            // server: smooth even while the views re-render).
            let windows = NSApp.windows.filter { $0.isVisible && $0.alphaValue > 0 && $0.frame.minX > -10_000 }
            CATransaction.begin()
            for window in windows {
                guard let layer = window.contentView?.layer else { continue }
                let fade = CATransition()
                fade.type = .fade
                fade.duration = 0.4
                fade.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
                layer.add(fade, forKey: "glassChange")
            }
            NotificationCenter.default.post(name: changedNotification, object: newValue)
            // Render the new look now, inside the same transaction, so the
            // cross-fade has both pictures.
            for window in windows {
                window.contentView?.layoutSubtreeIfNeeded()
                window.displayIfNeeded()
            }
            CATransaction.commit()
        }
    }
}

private struct GlassTransparencyKey: EnvironmentKey {
    static let defaultValue: GlassTransparency = .standard
}

private struct GlassStandInKey: EnvironmentKey {
    static let defaultValue = false
}



extension EnvironmentValues {
    var glassTransparency: GlassTransparency {
        get { self[GlassTransparencyKey.self] }
        set { self[GlassTransparencyKey.self] = newValue }
    }

    /// Set while a view is rendered on the CPU (`cacheDisplay`), where real
    /// glass cannot be drawn: surfaces then draw a frosted stand-in instead.
    var glassStandIn: Bool {
        get { self[GlassStandInKey.self] }
        set { self[GlassStandInKey.self] = newValue }
    }
}

// MARK: - Surfaces

/// One glass surface for every control: Apple's clear glass, the same as
/// the type switcher's lens, tinted only as the slider moves towards tinted.
private struct GlassSurface<S: Shape>: ViewModifier {
    @Environment(\.glassStandIn) private var standIn
    @Environment(\.colorScheme) private var colorScheme
    let shape: S
    var interactive = false
    var tint: Color?
    /// Frosted (blurs what's behind) instead of crystal-clear.
    var frosted = false
    /// Draws the hairline outline (off when the caller draws its own).
    var outlined = true

    func body(content: Content) -> some View {
        let isDark = colorScheme == .dark
        if standIn {
            content
                .background(shape.fill(Color.white.opacity(isDark ? 0.12 : 0.62)))
                .overlay(shape.stroke(Color.white.opacity(isDark ? 0.16 : 0.85), lineWidth: 0.6))
        } else if #available(macOS 26.0, *) {
            // A hairline outline keeps every control distinct from the card
            // it sits on, however frosted that card is.
            content
                .glassEffect(glass(isDark: isDark), in: shape)
                .overlay(shape.stroke(isDark ? Color.white.opacity(0.16) : Color.black.opacity(0.1), lineWidth: 0.75).opacity(outlined ? 1 : 0))
        } else {
            content
                .background(.ultraThinMaterial, in: shape)
                .overlay(shape.stroke(Color.white.opacity(isDark ? 0.16 : 0.7), lineWidth: 0.6))
        }
    }

    /// Always crystal-clear: the slider frosts cards and records, never the
    /// controls, so they keep standing out from the cards.
    @available(macOS 26.0, *)
    private func glass(isDark: Bool) -> Glass {
        var glass = frosted ? Glass.regular : Glass.clear
        if let tint {
            glass = glass.tint(tint)
        }
        return interactive ? glass.interactive() : glass
    }
}

private struct CardGlass: ViewModifier {
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.glassTransparency) private var transparency
    let isHovered: Bool
    let isHidden: Bool

    func body(content: Content) -> some View {
        let isDark = colorScheme == .dark
        let shape = RoundedRectangle(cornerRadius: GlassMetrics.cardRadius, style: .continuous)
        if #available(macOS 26.0, *) {
            // Glass cards on the tinted window glass, like dark mode: white
            // in light mode, smoked in dark mode so white text keeps contrast.
            let fill = isDark
                ? Color.black.opacity(transparency.cardTint(isDark: true))
                : Color.white.opacity(transparency.lightCardFill)
            // Hover is a plain highlight inside the card: re-tinting one
            // card's glass redrew every card in the container (a flash
            // down the whole list as the pointer moved).
            content
                .background {
                    if isHovered && !isHidden {
                        shape.fill(Color.white.opacity(isDark ? 0.06 : 0.35))
                            .transition(.opacity)
                    }
                }
                .glassEffect(isHidden ? .identity : .clear.tint(fill), in: shape)
                .animation(.easeOut(duration: 0.12), value: isHovered)
        } else {
            content.background(shape.fill(.regularMaterial).opacity(isHidden ? 0 : 1))
        }
    }
}

extension View {
    func clipShelfGlass<S: Shape>(in shape: S, interactive: Bool = false, tint: Color? = nil, frosted: Bool = false, outlined: Bool = true) -> some View {
        modifier(GlassSurface(shape: shape, interactive: interactive, tint: tint, frosted: frosted, outlined: outlined))
    }

    /// A card of system Liquid Glass with this view as its content, so the
    /// content is drawn on top of its own glass (inside a container, glass
    /// that is only a background gets composited over its siblings).
    /// `isHidden` drops the glass (e.g. while the selection glass shows).
    func cardGlass(isHovered: Bool = false, isHidden: Bool = false) -> some View {
        modifier(CardGlass(isHovered: isHovered, isHidden: isHidden))
    }

    /// Groups many glass shapes (cards in a list) in one system
    /// GlassEffectContainer, so they are rendered together, which is Apple's
    /// recommended way to keep lots of glass cheap. Spacing 0: cards never
    /// melt into each other.
    @ViewBuilder
    func glassGroup() -> some View {
        if #available(macOS 26.0, *) {
            GlassEffectContainer(spacing: 0) { self }
        } else {
            self
        }
    }

    /// Buttons inside settings and sheets: Liquid Glass on macOS 26.
    @ViewBuilder
    func clipShelfGlassButtons(prominent: Bool = false) -> some View {
        if #available(macOS 26.0, *) {
            if prominent {
                buttonStyle(.glassProminent)
            } else {
                buttonStyle(.glass)
            }
        } else if prominent {
            buttonStyle(.borderedProminent)
        } else {
            buttonStyle(.bordered)
        }
    }

    func clipShelfGlassGroup() -> some View {
        padding(.horizontal, 2)
            .clipShelfGlass(in: Capsule())
            .pressGroup()
    }

    func clipShelfGlassPanel(cornerRadius: CGFloat = GlassMetrics.panelRadius, isActive: Bool = true) -> some View {
        modifier(GlassPanel(cornerRadius: cornerRadius, isActive: isActive))
    }

    func clipShelfLiquidGlassSurface(cornerRadius: CGFloat) -> some View {
        clipShelfGlass(in: RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
    }

    @ViewBuilder
    func clipShelfLiquidGlassButtonStyle() -> some View {
        if #available(macOS 26.0, *) {
            buttonStyle(.glass)
        } else {
            buttonStyle(.borderless)
        }
    }
}

private struct GlassPanel: ViewModifier {
    @Environment(\.glassTransparency) private var transparency
    let cornerRadius: CGFloat
    var isActive = true

    func body(content: Content) -> some View {
        let shape = RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
        if !isActive {
            content.clipShape(shape)
        } else {
            content
                .clipShape(shape)
                .clipShelfGlass(in: shape)
        }
    }
}

// MARK: - Window backdrop

/// The main window's content: a frosted backdrop and a tint layer behind
/// the SwiftUI content. Only the backdrop layers fade with the glass slider;
/// the content on top always stays fully opaque.
/// The window's base, the same in the main window and settings:
/// - dark mode: frosted Liquid Glass (bends the desktop at the edges), smoked;
/// - light mode: the same glass with a grey tint, so it doesn't glare.
/// Neither changes when the window loses focus (the windows report an active look).
final class WindowBackdropView: NSView {
    private let glass: NSView?
    private let frosted = NSVisualEffectView()
    private let tint = NSView()
    private let radius: CGFloat
    /// Extra grey in light mode, so the white cards on top stand apart.
    private let lightDepth: Double
    private var shownLevel: Double?
    private var easeTimer: Timer?

    init(cornerRadius: CGFloat, lightDepth: Double = 1) {
        radius = cornerRadius
        self.lightDepth = lightDepth
        if #available(macOS 26.0, *) {
            let view = NSGlassEffectView()
            view.style = .regular
            view.cornerRadius = cornerRadius
            glass = view
        } else {
            glass = nil
        }
        super.init(frame: .zero)
        wantsLayer = true
        frosted.material = .underWindowBackground
        frosted.blendingMode = .behindWindow
        frosted.state = .active
        frosted.wantsLayer = true
        frosted.layer?.cornerRadius = cornerRadius
        frosted.layer?.cornerCurve = .continuous
        frosted.layer?.masksToBounds = true
        tint.wantsLayer = true
        tint.layer?.cornerRadius = cornerRadius
        tint.layer?.cornerCurve = .continuous
        for view in [glass, frosted, tint].compactMap({ $0 }) {
            view.translatesAutoresizingMaskIntoConstraints = false
            addSubview(view)
            NSLayoutConstraint.activate([
                view.leadingAnchor.constraint(equalTo: leadingAnchor),
                view.trailingAnchor.constraint(equalTo: trailingAnchor),
                view.topAnchor.constraint(equalTo: topAnchor),
                view.bottomAnchor.constraint(equalTo: bottomAnchor)
            ])
        }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    private var isDark: Bool {
        effectiveAppearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
    }

    /// Eases to the new level over ~0.45 s instead of jumping.
    func apply(_ transparency: GlassTransparency, animated: Bool = true) {
        easeTimer?.invalidate()
        let from = shownLevel ?? transparency.level
        let to = transparency.level
        guard animated, abs(to - from) > 0.005 else { set(level: to); return }
        let start = CACurrentMediaTime()
        easeTimer = Timer.scheduledTimer(withTimeInterval: 1.0 / 60.0, repeats: true) { [weak self] timer in
            let t = min((CACurrentMediaTime() - start) / 0.45, 1)
            let eased = t * t * (3 - 2 * t)
            self?.set(level: from + (to - from) * eased)
            if t >= 1 { timer.invalidate(); self?.easeTimer = nil }
        }
    }

    private func set(level: Double) {
        shownLevel = level
        let transparency = GlassTransparency(level: level)
        let useGlass = glass != nil
        glass?.isHidden = !useGlass
        frosted.isHidden = useGlass
        if #available(macOS 26.0, *), let glass = glass as? NSGlassEffectView {
            if isDark {
                glass.tintColor = NSColor(white: 0.06, alpha: transparency.darkWindowSmoke)
            } else {
                // Grey, not white: a white tint lets light glass glare.
                // The base's brightness never changes with the slider (it
                // used to darken towards "tinted"); only the cards and
                // controls on it turn from clear to frosted.
                glass.tintColor = NSColor(
                    white: 0.72 - 0.12 * lightDepth,
                    alpha: min(transparency.lightWindowTint + 0.2 * lightDepth, 0.95)
                )
            }
            tint.layer?.backgroundColor = nil
            return
        }
        effectiveAppearance.performAsCurrentDrawingAppearance {
            tint.layer?.backgroundColor = NSColor.windowBackgroundColor
                .withAlphaComponent(isDark ? transparency.backdropTint : transparency.lightWindowTint).cgColor
        }
    }

    override func viewDidChangeEffectiveAppearance() {
        super.viewDidChangeEffectiveAppearance()
        set(level: shownLevel ?? GlassPreferences.value.level)
    }
}

/// The main window's content view: the backdrop with the SwiftUI content on
/// top, clipped to the window's rounded corners so records scrolling past
/// the bottom never poke out of them.
final class BackdropContainerView: NSView {
    private let backdrop = WindowBackdropView(cornerRadius: GlassMetrics.windowRadius)

    init(content: NSView) {
        super.init(frame: .zero)
        // Layer-backed, so a change of glass level can cross-fade it.
        wantsLayer = true
        // Count the fingers resting on the trackpad (see TrackpadFingers).
        allowedTouchTypes = [.indirect]
        wantsRestingTouches = true
        content.wantsLayer = true
        content.layer?.cornerRadius = GlassMetrics.windowRadius
        content.layer?.cornerCurve = .continuous
        content.layer?.masksToBounds = true
        for view in [backdrop, content] {
            view.translatesAutoresizingMaskIntoConstraints = false
            addSubview(view)
            NSLayoutConstraint.activate([
                view.leadingAnchor.constraint(equalTo: leadingAnchor),
                view.trailingAnchor.constraint(equalTo: trailingAnchor),
                view.topAnchor.constraint(equalTo: topAnchor),
                view.bottomAnchor.constraint(equalTo: bottomAnchor)
            ])
        }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func apply(_ transparency: GlassTransparency) {
        backdrop.apply(transparency)
    }

    override func touchesBegan(with event: NSEvent) { TrackpadFingers.update(event, in: self) }
    override func touchesMoved(with event: NSEvent) { TrackpadFingers.update(event, in: self) }
    override func touchesEnded(with event: NSEvent) { TrackpadFingers.update(event, in: self) }
    override func touchesCancelled(with event: NSEvent) { TrackpadFingers.update(event, in: self) }
}

/// How many fingers are on the trackpad right now. A three-finger drag
/// (an accessibility setting) arrives as an ordinary mouse drag; this tells
/// it apart, so it can move the window instead of selecting records.
enum TrackpadFingers {
    private(set) static var count = 0

    static func update(_ event: NSEvent, in view: NSView) {
        count = event.touches(matching: .touching, in: view).count
    }
}


/// An overlay scroller with no track: on hover the system scroller widens
/// and draws an opaque white track, which showed as a stark white strip
/// over the glass (and stayed after the pointer left). Only the knob is
/// drawn here.
final class GlassScroller: NSScroller {
    override class var isCompatibleWithOverlayScrollers: Bool { true }

    override func drawKnobSlot(in slotRect: NSRect, highlight flag: Bool) {}

    static func install(in scrollView: NSScrollView) {
        guard !(scrollView.verticalScroller is GlassScroller) else { return }
        let scroller = GlassScroller()
        scroller.controlSize = .regular
        scrollView.verticalScroller = scroller
        scrollView.scrollerStyle = .overlay
        scrollView.autohidesScrollers = true
        scrollView.flashScrollers()
    }
}
