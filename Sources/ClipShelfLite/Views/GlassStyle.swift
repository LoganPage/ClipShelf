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

/// How see-through the glass is, like the iOS / macOS Liquid Glass setting.
enum GlassTransparency: String, CaseIterable, Identifiable {
    case clear
    case regular
    case tinted
    case opaque

    var id: String { rawValue }

    var title: String {
        switch self {
        case .clear: "清透"
        case .regular: "标准"
        case .tinted: "着色"
        case .opaque: "不透明"
        }
    }
}

enum GlassPreferences {
    static let changedNotification = Notification.Name("ClipShelfGlassTransparencyChanged")
    private static let key = "glass.transparency"

    static var value: GlassTransparency {
        get {
            AppEnvironment.userDefaults.string(forKey: key).flatMap(GlassTransparency.init(rawValue:)) ?? .regular
        }
        set {
            AppEnvironment.userDefaults.set(newValue.rawValue, forKey: key)
            NotificationCenter.default.post(name: changedNotification, object: newValue)
        }
    }
}

private struct GlassTransparencyKey: EnvironmentKey {
    static let defaultValue: GlassTransparency = .regular
}

extension EnvironmentValues {
    var glassTransparency: GlassTransparency {
        get { self[GlassTransparencyKey.self] }
        set { self[GlassTransparencyKey.self] = newValue }
    }
}

// MARK: - Surfaces

/// One glass surface for every control, honouring the transparency setting.
private struct GlassSurface<S: Shape>: ViewModifier {
    @Environment(\.glassTransparency) private var transparency
    @Environment(\.colorScheme) private var colorScheme
    let shape: S
    var interactive = false
    var tint: Color?

    func body(content: Content) -> some View {
        if transparency == .opaque {
            content
                .background(shape.fill(AppTheme.searchBackground))
                .overlay(shape.stroke(AppTheme.subtleBorder, lineWidth: 0.5))
        } else if #available(macOS 26.0, *) {
            content.glassEffect(glass, in: shape)
        } else {
            content
                .background(material, in: shape)
                .overlay(shape.stroke(Color.primary.opacity(0.08), lineWidth: 0.5))
        }
    }

    @available(macOS 26.0, *)
    private var glass: Glass {
        var glass: Glass
        switch transparency {
        case .clear:
            glass = .clear
        case .regular, .opaque:
            glass = .regular
        case .tinted:
            glass = .regular.tint(colorScheme == .dark ? Color.black.opacity(0.42) : Color.white.opacity(0.62))
        }
        if let tint {
            glass = glass.tint(tint)
        }
        return interactive ? glass.interactive() : glass
    }

    private var material: Material {
        switch transparency {
        case .clear: .ultraThinMaterial
        case .regular, .opaque: .regularMaterial
        case .tinted: .thickMaterial
        }
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
            if transparency == .opaque {
                shape.fill(AppTheme.rowBackground)
                shape.fill(Color.primary.opacity(isHovered ? 0.04 : 0))
                shape.strokeBorder(AppTheme.subtleBorder, lineWidth: 0.5)
            } else {
                shape.fill(Color.white.opacity(fillOpacity))
                shape.strokeBorder(Color.white.opacity(colorScheme == .dark ? 0.08 : 0.7), lineWidth: 0.5)
            }
        }
        .animation(.easeOut(duration: 0.12), value: isHovered)
    }

    private var fillOpacity: Double {
        let isDark = colorScheme == .dark
        let base: Double
        switch transparency {
        case .clear: base = isDark ? 0.035 : 0.38
        case .regular, .opaque: base = isDark ? 0.06 : 0.58
        case .tinted: base = isDark ? 0.11 : 0.8
        }
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

    func clipShelfGlassPanel(cornerRadius: CGFloat = GlassMetrics.panelRadius) -> some View {
        modifier(GlassPanel(cornerRadius: cornerRadius))
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

    func body(content: Content) -> some View {
        let shape = RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
        if transparency == .opaque {
            content
                .background(AppTheme.settingsBackground)
                .clipShape(shape)
                .shadow(color: Color.black.opacity(0.18), radius: 24, x: 0, y: 12)
        } else {
            content
                .clipShape(shape)
                .clipShelfGlass(in: shape)
        }
    }
}

// MARK: - Window backdrop

/// The window's backdrop: blurred desktop for glass modes, plain window
/// background when transparency is off.
enum WindowBackdrop {
    static func apply(_ transparency: GlassTransparency, to view: NSVisualEffectView) {
        if transparency == .opaque {
            view.material = .windowBackground
            view.blendingMode = .withinWindow
        } else {
            view.material = .underWindowBackground
            view.blendingMode = .behindWindow
        }
    }
}
