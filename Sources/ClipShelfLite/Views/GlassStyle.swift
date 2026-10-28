import AppKit
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
    static let panelRadius: CGFloat = 26
    static let thumbnailRadius: CGFloat = 10
}

// MARK: - Transparency preference

/// How see-through the glass is, set steplessly like the Liquid Glass slider
/// in Apple's settings: 0 is the clearest glass, 1 the most tinted.
struct GlassTransparency: Equatable {
    var level: Double

    static let standard = GlassTransparency(level: 0.4)

    init(level: Double) {
        self.level = min(max(level, 0), 1)
    }

    // Each layer has its own range, so they always read as separate panes:
    // at the clearest setting the window behind is ~80% see-through while
    // cards stay ~40% see-through; towards tinted both firm up.

    /// How much of the window's frosted backdrop shows (the rest is the
    /// desktop itself): 0.2 → 0.95.
    var backdropOpacity: Double { 0.2 + 0.75 * level }

    /// Extra frosting laid over the backdrop: 0 → 0.4.
    var backdropTint: Double { 0.4 * level }

    /// Tint laid over control glass: light frosting in light mode, smoked in dark.
    func tintOpacity(isDark: Bool) -> Double {
        isDark ? 0.14 + 0.4 * level : 0.32 + 0.45 * level
    }

    /// Fill of the record / settings cards: 0.6 → 0.92 in light mode.
    func cardOpacity(isDark: Bool) -> Double {
        isDark ? 0.09 + 0.07 * level : 0.6 + 0.32 * level
    }
}

enum GlassPreferences {
    static let changedNotification = Notification.Name("ClipShelfGlassTransparencyChanged")
    private static let key = "glass.level"
    private static let legacyKey = "glass.transparency"

    static var value: GlassTransparency {
        get {
            let defaults = AppEnvironment.userDefaults
            if defaults.object(forKey: key) != nil {
                return GlassTransparency(level: defaults.double(forKey: key))
            }
            // The old four-step setting, mapped onto the slider.
            switch defaults.string(forKey: legacyKey) {
            case "clear": return GlassTransparency(level: 0.1)
            case "tinted": return GlassTransparency(level: 0.75)
            case "opaque": return GlassTransparency(level: 1)
            default: return .standard
            }
        }
        set {
            AppEnvironment.userDefaults.set(newValue.level, forKey: key)
            NotificationCenter.default.post(name: changedNotification, object: newValue)
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

/// One glass surface for every control, honouring the transparency setting.
/// A bright rim along the top edge and (in light mode) a soft shadow give it
/// the raised, refractive look of system glass instead of a flat plate.
private struct GlassSurface<S: Shape>: ViewModifier {
    @Environment(\.glassTransparency) private var transparency
    @Environment(\.glassStandIn) private var standIn
    @Environment(\.colorScheme) private var colorScheme
    let shape: S
    var interactive = false
    var tint: Color?

    func body(content: Content) -> some View {
        let isDark = colorScheme == .dark
        if standIn {
            content
                .background(shape.fill(Color.white.opacity(isDark ? 0.12 : 0.62)))
                .overlay(shape.stroke(Color.white.opacity(isDark ? 0.16 : 0.85), lineWidth: 0.6))
        } else if #available(macOS 26.0, *) {
            content
                .glassEffect(glass(isDark: isDark), in: shape)
                .overlay(shape.stroke(rim(isDark: isDark), lineWidth: 0.8).allowsHitTesting(false))
                .shadow(color: .black.opacity(isDark ? 0 : 0.09), radius: 6, y: 2)
        } else {
            content
                .background(material, in: shape)
                .overlay(shape.stroke(rim(isDark: isDark), lineWidth: 0.8).allowsHitTesting(false))
        }
    }

    @available(macOS 26.0, *)
    private func glass(isDark: Bool) -> Glass {
        // Always clear glass, frosted continuously by the tint, so the
        // slider changes smoothly from one end to the other.
        var glass = Glass.clear.tint(
            (isDark ? Color.black : Color.white).opacity(transparency.tintOpacity(isDark: isDark))
        )
        if let tint {
            glass = glass.tint(tint)
        }
        return interactive ? glass.interactive() : glass
    }

    private func rim(isDark: Bool) -> LinearGradient {
        LinearGradient(
            colors: [
                Color.white.opacity(isDark ? 0.28 : 0.95),
                Color.white.opacity(isDark ? 0.05 : 0.35),
                Color.white.opacity(isDark ? 0.12 : 0.6)
            ],
            startPoint: .top,
            endPoint: .bottom
        )
    }

    private var material: Material {
        transparency.level < 0.33 ? .ultraThinMaterial : (transparency.level < 0.66 ? .regularMaterial : .thickMaterial)
    }
}

/// The translucent card used for history records and settings groups.
struct GlassCardBackground: View {
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.glassTransparency) private var transparency
    var isHovered = false

    private var shape: RoundedRectangle {
        RoundedRectangle(cornerRadius: GlassMetrics.cardRadius, style: .continuous)
    }

    var body: some View {
        ZStack {
            shape.fill(Color.white.opacity(fillOpacity))
            shape.strokeBorder(Color.white.opacity(colorScheme == .dark ? 0.14 : 0.8), lineWidth: 0.6)
        }
        .animation(.easeOut(duration: 0.12), value: isHovered)
    }

    private var fillOpacity: Double {
        let isDark = colorScheme == .dark
        let base = transparency.cardOpacity(isDark: isDark)
        return isHovered ? base + (isDark ? 0.04 : 0.2) : base
    }
}

extension View {
    func clipShelfGlass<S: Shape>(in shape: S, interactive: Bool = false, tint: Color? = nil) -> some View {
        modifier(GlassSurface(shape: shape, interactive: interactive, tint: tint))
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
            .clipShelfGlass(in: Capsule(), interactive: true)
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
final class BackdropContainerView: NSView {
    let effect = NSVisualEffectView()
    private let tint = NSView()

    init(content: NSView) {
        super.init(frame: .zero)
        // Always the active look: settings sits in its own window, and the
        // main window used to turn flat grey whenever settings had focus.
        effect.state = .active
        effect.material = .underWindowBackground
        effect.blendingMode = .behindWindow
        tint.wantsLayer = true
        for view in [effect, tint, content] {
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
        // Below full strength the desktop itself shows through.
        effect.alphaValue = transparency.backdropOpacity
        effectiveAppearance.performAsCurrentDrawingAppearance {
            tint.layer?.backgroundColor = NSColor.windowBackgroundColor
                .withAlphaComponent(transparency.backdropTint).cgColor
        }
    }

    override func viewDidChangeEffectiveAppearance() {
        super.viewDidChangeEffectiveAppearance()
        apply(GlassPreferences.value)
    }
}
