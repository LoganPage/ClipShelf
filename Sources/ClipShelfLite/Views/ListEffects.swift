import SwiftUI

/// Edge blur of a history row (see `ScrollEffectStyle`): once a row slides
/// under the toolbar at the top, or past the bottom edge of the window, it
/// blurs and fades in proportion to how far it has gone.
///
/// Render-only (`visualEffect`): never re-lays out rows.
struct ListEdgeEffect: ViewModifier {
    let style: ScrollEffectStyle
    /// Where the visible area starts: rows above this are under the toolbar.
    let topEdge: CGFloat

    /// Where blurring starts at the bottom, above the window's lower edge:
    /// rows are gone before they reach the window's rounded corners.
    private static let bottomInset: CGFloat = 6

    func body(content: Content) -> some View {
        if #available(macOS 14.0, *), style.blursEdges {
            content.visualEffect { [topEdge] effect, geometry in
                let frame = geometry.frame(in: .scrollView(axis: .vertical))
                let viewport = geometry.bounds(of: .scrollView(axis: .vertical)) ?? .zero
                // Builds up over two thirds of a row at the top.
                let ramp = max(frame.height, 1) * 0.67
                let top = min(max((topEdge - frame.minY) / ramp, 0), 1)
                // The bottom ramp is half as tall: a slimmer fade zone.
                let bottomRamp = ramp / 2
                let bottomEdge = viewport.height - Self.bottomInset
                let bottom = viewport.height > 0 ? min(max((frame.maxY - bottomEdge) / bottomRamp, 0), 1) : 0
                let amount = max(top, bottom)
                // Top: nearly gone under the toolbar (the band there is
                // slim, so the text mustn't show between the controls).
                // Bottom: fully faded.
                return effect
                    .blur(radius: 6 * amount)
                    .opacity(min(1 - 0.9 * Double(top), 1 - Double(bottom)))
            }
        } else {
            content
        }
    }
}

/// A switch of record type, driving the entrance of the rows that follow it.
struct ListEntrance: Equatable {
    let style: ListEntranceStyle
    /// -1 when the lens moved left, 1 when it moved right.
    let direction: CGFloat
    /// Horizontal distance from the list's centre to the lens.
    let lensOffsetX: CGFloat
    let start: Date
}

/// Plays the chosen entrance on rows built right after a type switch. Rows
/// that appear later (by scrolling) or further down than the first screen
/// just appear.
struct ListEntranceEffect: ViewModifier {
    let entrance: ListEntrance?
    let index: Int
    let rowHeight: CGFloat
    @State private var progress: CGFloat

    init(entrance: ListEntrance?, index: Int, rowHeight: CGFloat) {
        self.entrance = entrance
        self.index = index
        self.rowHeight = rowHeight
        _progress = State(initialValue: Self.shouldAnimate(entrance, index: index) ? 0 : 1)
    }

    private static func shouldAnimate(_ entrance: ListEntrance?, index: Int) -> Bool {
        guard let entrance, entrance.style != .off else { return false }
        // Only the first screenful; rows further down just appear.
        return index < 9 && Date().timeIntervalSince(entrance.start) < 0.35
    }

    func body(content: Content) -> some View {
        let p = progress
        let style = entrance?.style ?? .off
        let direction = entrance?.direction ?? 1
        let lensX = entrance?.lensOffsetX ?? 0
        let i = CGFloat(index)

        let offsetX: CGFloat
        let offsetY: CGFloat
        var scaleX: CGFloat = 1
        var scaleY: CGFloat = 1
        let blur: CGFloat
        let opacity: Double
        switch style {
        case .cascade:
            offsetX = 0
            offsetY = 26 * (1 - p)
            scaleX = 0.97 + 0.03 * p
            scaleY = scaleX
            blur = 0
            opacity = Double(p)
        case .slide:
            offsetX = direction * 90 * (1 - p)
            offsetY = 0
            blur = 0
            opacity = Double(p)
        case .dissolve:
            offsetX = 0
            offsetY = 0
            scaleX = 1.015 - 0.015 * p
            scaleY = scaleX
            blur = 10 * (1 - p)
            opacity = Double(p)
        case .pour:
            // Starts as a small drop at the lens, runs down while it is still
            // narrow, then spreads out to full width: a pour, not a throw.
            offsetX = lensX * (1 - p)
            offsetY = -(i * rowHeight + 48) * (1 - p)
            scaleX = 0.14 + 0.86 * p * p
            scaleY = 0.3 + 0.7 * p
            blur = 6 * (1 - p)
            opacity = Double(min(1, p * 2.2))
        case .off:
            offsetX = 0
            offsetY = 0
            blur = 0
            opacity = 1
        }

        return moved(content, x: offsetX, y: offsetY, scaleX: scaleX, scaleY: scaleY, blur: blur, opacity: opacity)
            .onAppear {
                guard progress < 1, let entrance else { return }
                withAnimation(Self.animation(for: entrance.style, index: index)) {
                    progress = 1
                }
            }
    }

    /// Render-only: moving rows with layout-level modifiers made SwiftUI
    /// re-track every row's hover regions on every frame of the entrance.
    /// One fixed structure, so a row is never rebuilt when it settles.
    @ViewBuilder
    private func moved(_ content: Content, x: CGFloat, y: CGFloat, scaleX: CGFloat, scaleY: CGFloat, blur: CGFloat, opacity: Double) -> some View {
        if #available(macOS 14.0, *) {
            content.visualEffect { effect, _ in
                effect
                    .offset(x: x, y: y)
                    .scaleEffect(x: scaleX, y: scaleY, anchor: .top)
                    .blur(radius: blur)
                    .opacity(opacity)
            }
        } else {
            content
                .offset(x: x, y: y)
                .scaleEffect(x: scaleX, y: scaleY, anchor: .top)
                .blur(radius: blur)
                .opacity(opacity)
        }
    }

    private static func animation(for style: ListEntranceStyle, index: Int) -> Animation {
        let i = Double(index)
        switch style {
        // Fixed-length curves rather than springs: a spring keeps redrawing
        // every row for its long, invisible settling tail, which made each
        // type switch cost about twice the work for the same look.
        case .cascade:
            return .timingCurve(0.2, 0.9, 0.3, 1, duration: 0.4).delay(i * 0.03)
        case .slide:
            return .timingCurve(0.2, 0.9, 0.3, 1, duration: 0.4).delay(i * 0.012)
        case .dissolve:
            return .easeOut(duration: 0.35)
        case .pour:
            // No overshoot: liquid flows and settles, it doesn't bounce.
            return .timingCurve(0.22, 0.8, 0.3, 1, duration: 0.6).delay(i * 0.055)
        case .off:
            return .default
        }
    }
}
