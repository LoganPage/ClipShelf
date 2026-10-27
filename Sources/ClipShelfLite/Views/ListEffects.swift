import SwiftUI

/// Edge behaviour of a real history row (see `ScrollEffectStyle`):
///
/// - Top: once a row slides under the toolbar it blurs and fades in
///   proportion to how far in it has gone.
/// - Bottom: a row that has gone below the front of the pile hands over to
///   its copy in the pile layer (`PileCardEffect`) and hides. There is no
///   blur on ordinary rows at the bottom.
///
/// Render-only (`visualEffect`): never re-lays out rows.
struct ListEdgeEffect: ViewModifier {
    let style: ScrollEffectStyle
    /// Where the visible area starts: rows above this are under the toolbar.
    let topEdge: CGFloat
    let index: Int
    let count: Int
    /// Space below the last row.
    let bottomPadding: CGFloat

    func body(content: Content) -> some View {
        if #available(macOS 14.0, *), style != .off {
            content.visualEffect { [style, topEdge, index, count, bottomPadding] effect, geometry in
                let frame = geometry.frame(in: .scrollView(axis: .vertical))
                let viewport = geometry.bounds(of: .scrollView(axis: .vertical)) ?? .zero
                let rowHeight = max(frame.height, 1)

                // Top: blur builds up over two thirds of a row under the toolbar.
                let under = style.blursTop ? min(max((topEdge - frame.minY) / (rowHeight * 0.67), 0), 1) : 0

                var handedOver: Double = 0
                if style.stacksBottom {
                    let pile = PileGeometry(rowHeight: rowHeight, viewportHeight: viewport.height)
                    if pile.depth(naturalTop: frame.minY) > 0 {
                        handedOver = Double(pile.strength(rowMaxY: frame.maxY, index: index, count: count, bottomPadding: bottomPadding))
                    }
                }

                return effect
                    .blur(radius: 6 * under)
                    .opacity((1 - 0.45 * Double(under)) * (1 - handedOver))
            }
        } else {
            content
        }
    }
}

/// Geometry of the bottom pile, shared by real rows, pile cards, backings and
/// the selection glass so they always agree.
///
/// The front of the pile is the last row that still shows in full. Behind it
/// two more cards tuck in: the first shows a one-line title strip, the second
/// only a thin edge. Together they take about half a row.
struct PileGeometry {
    let rowHeight: CGFloat
    let viewportHeight: CGFloat

    static let maxCards: CGFloat = 2
    /// Total height the tucked cards peek out below the front card.
    static let reach: CGFloat = 32
    /// The pile layer is laid out this many rows higher than the real list so
    /// rows below the window are built; see `MainView.pileLayer`.
    static let layoutShiftRows: CGFloat = 5
    private static let thinning: CGFloat = 1.1

    /// Bottom edge of the front card.
    var frontBottom: CGFloat { viewportHeight - Self.reach - 6 }
    private var frontTop: CGFloat { frontBottom - rowHeight }

    /// How far behind the front a row is, in rows: <= 0 for rows not in the pile.
    func depth(naturalTop: CGFloat) -> CGFloat {
        (naturalTop - frontTop) / rowHeight
    }

    /// Where the bottom edge of a card `depth` rows into the pile sits.
    func cardBottom(depth: CGFloat) -> CGFloat {
        let full = 1 - exp(-Self.thinning * Self.maxCards)
        let d = min(max(depth, 0), Self.maxCards + 0.5)
        return frontBottom + Self.reach * (1 - exp(-Self.thinning * d)) / full
    }

    /// Inverse of `cardBottom`: how deep a card is, from where its bottom
    /// edge is actually drawn.
    func depth(renderedBottom: CGFloat) -> CGFloat {
        let full = 1 - exp(-Self.thinning * Self.maxCards)
        let fraction = min(max((renderedBottom - frontBottom) / Self.reach * full, 0), 0.999)
        return -log(1 - fraction) / Self.thinning
    }

    /// 0 near the end of the list (nothing below to pile up), 1 otherwise.
    func strength(rowMaxY: CGFloat, index: Int, count: Int, bottomPadding: CGFloat) -> CGFloat {
        let rowsBelow = CGFloat(max(count - 1 - index, 0))
        let remaining = rowMaxY + rowsBelow * rowHeight + bottomPadding - viewportHeight
        return min(max(remaining / (rowHeight * 1.5), 0), 1)
    }

    /// Size of a card `depth` rows into the pile.
    static func scale(depth: CGFloat) -> CGFloat { 1 - 0.04 * min(max(depth, 0), maxCards + 0.5) }

    /// How much the pile card behind a real row overlaps it, 0...1. The row
    /// is in front, so it needs a solid backing there.
    func cover(naturalTop: CGFloat, index: Int, count: Int, bottomPadding: CGFloat) -> CGFloat {
        guard index + 1 < count, depth(naturalTop: naturalTop) <= 0 else { return 0 }
        let nextNatural = naturalTop + rowHeight
        let nextDepth = depth(naturalTop: nextNatural)
        guard nextDepth > 0 else { return 0 }
        let strength = strength(rowMaxY: nextNatural + rowHeight, index: index + 1, count: count, bottomPadding: bottomPadding)
        guard strength > 0 else { return 0 }
        let naturalBottom = nextNatural + rowHeight
        let bottom = naturalBottom + (cardBottom(depth: nextDepth) - naturalBottom) * strength
        let nextTop = bottom - rowHeight * (1 - (1 - Self.scale(depth: nextDepth)) * strength)
        let overlap = naturalTop + rowHeight - nextTop
        return min(max(overlap / 10, 0), 1) * strength
    }
}

/// What a row needs to know to take part in the bottom pile.
struct PileContext: Equatable {
    let style: ScrollEffectStyle
    let index: Int
    let count: Int
    let bottomPadding: CGFloat
}

/// Places one layer of a pile-layer copy of a row into the pile. Each layer
/// (card, full content, title strip) gets this separately with the same
/// transform, because geometry read inside an already-moved view is no
/// longer usable. The copy is laid out `layoutShift` points above its real
/// position and moved down to its place behind the front card: smaller,
/// softer, dimmer and tilted back the deeper it is.
struct PileCardEffect: ViewModifier {
    enum Layer {
        /// The card surface: visible whenever the copy is in the pile.
        case card
        /// The full row: shows until the card is mostly tucked away.
        case content
        /// One-line title along the bottom edge: shows once tucked.
        case strip
    }

    let layer: Layer
    let index: Int
    let count: Int
    let rowHeight: CGFloat
    let bottomPadding: CGFloat
    let layoutShift: CGFloat

    /// Depth at which the title strip takes over from the full row.
    private static let swapDepth: CGFloat = 0.86

    func body(content: Content) -> some View {
        if #available(macOS 14.0, *) {
            content.visualEffect { [layer, index, count, rowHeight, bottomPadding, layoutShift] effect, geometry in
                let frame = geometry.frame(in: .scrollView(axis: .vertical))
                let viewport = geometry.bounds(of: .scrollView(axis: .vertical)) ?? .zero
                let pile = PileGeometry(rowHeight: rowHeight, viewportHeight: viewport.height)
                // Every layer shares the row's bottom edge.
                let naturalBottom = frame.maxY + layoutShift
                let naturalTop = naturalBottom - rowHeight
                let depth = pile.depth(naturalTop: naturalTop)
                let strength = pile.strength(rowMaxY: naturalBottom, index: index, count: count, bottomPadding: bottomPadding)
                let inPile = depth > 0 && depth < PileGeometry.maxCards + 0.6

                let d = max(depth, 0)
                let bottom = naturalBottom + (pile.cardBottom(depth: d) - naturalBottom) * strength
                let lift = bottom - frame.maxY
                let scale = 1 - (1 - PileGeometry.scale(depth: d)) * strength
                let tilt = 6 * Double(min(d, PileGeometry.maxCards)) * Double(strength)
                // The first card's title strip stays sharp; only the edge behind softens.
                let softness = 1.4 * max(min(d, PileGeometry.maxCards) - 1, 0) * strength
                let dim = 0.04 * Double(min(d, PileGeometry.maxCards)) * Double(strength)
                // The last card fades out as the next one tucks in behind it.
                let fade = min(max((PileGeometry.maxCards + 0.6 - d) / 0.6, 0), 1)

                // Content fades out completely before the strip fades in, so
                // text never doubles up.
                let layerOpacity: CGFloat
                switch layer {
                case .card:
                    layerOpacity = 1
                case .content:
                    layerOpacity = 1 - min(max((d - Self.swapDepth + 0.1) / 0.1, 0), 1)
                case .strip:
                    layerOpacity = min(max((d - Self.swapDepth) / 0.1, 0), 1)
                }

                return effect
                    // Lying back into the pile: the bottom edge stays, the top
                    // tips away from the viewer.
                    .rotation3DEffect(.degrees(tilt), axis: (x: 1, y: 0, z: 0), anchor: .bottom, perspective: 0.6)
                    .scaleEffect(scale, anchor: .bottom)
                    .offset(y: lift)
                    .brightness(-dim)
                    .blur(radius: softness)
                    .opacity(inPile ? Double(strength * fade * layerOpacity) : 0)
            }
        } else {
            content.hidden()
        }
    }
}

/// Solid card colour for pile copies, so cards further back never show
/// through the ones in front.
struct PileCardFill: View {
    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        RoundedRectangle(cornerRadius: GlassMetrics.cardRadius, style: .continuous)
            .fill(colorScheme == .dark ? Color(white: 0.24) : Color(white: 0.95))
            .shadow(color: .black.opacity(colorScheme == .dark ? 0.45 : 0.14), radius: 5, y: 2)
    }
}

/// An opaque card-coloured layer behind a real row's glass card that shows
/// only while a pile card is tucked behind it, so it never shows through.
/// Everywhere else it is invisible and the card stays fully translucent.
struct PileBacking: View {
    @Environment(\.colorScheme) private var colorScheme
    let context: PileContext
    /// Vertical inset of the card inside its row.
    let inset: CGFloat

    var body: some View {
        if #available(macOS 14.0, *), context.style.stacksBottom {
            let shape = RoundedRectangle(cornerRadius: GlassMetrics.cardRadius, style: .continuous)
            shape
                .fill(colorScheme == .dark ? Color(white: 0.24) : Color(white: 0.95))
                .visualEffect { [context, inset] effect, geometry in
                    let frame = geometry.frame(in: .scrollView(axis: .vertical))
                    let viewport = geometry.bounds(of: .scrollView(axis: .vertical)) ?? .zero
                    let rowHeight = frame.height + inset * 2
                    let pile = PileGeometry(rowHeight: rowHeight, viewportHeight: viewport.height)
                    let cover = pile.cover(
                        naturalTop: frame.minY - inset,
                        index: context.index,
                        count: context.count,
                        bottomPadding: context.bottomPadding
                    )
                    return effect.opacity(Double(cover))
                }
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
        return index < 12 && Date().timeIntervalSince(entrance.start) < 0.35
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

        return content
            .offset(x: offsetX, y: offsetY)
            .scaleEffect(x: scaleX, y: scaleY, anchor: .top)
            .blur(radius: blur)
            .opacity(opacity)
            .onAppear {
                guard progress < 1, let entrance else { return }
                withAnimation(Self.animation(for: entrance.style, index: index)) {
                    progress = 1
                }
            }
    }

    private static func animation(for style: ListEntranceStyle, index: Int) -> Animation {
        let i = Double(index)
        switch style {
        case .cascade:
            return .spring(response: 0.42, dampingFraction: 0.72).delay(i * 0.035)
        case .slide:
            return .spring(response: 0.45, dampingFraction: 0.86).delay(i * 0.012)
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

/// A row's copy in the pile layer: its card, the full row, and a one-line
/// title strip for when only its bottom edge peeks out.
struct PileCardContent<Row: View>: View {
    let title: String
    let symbol: String
    let index: Int
    let count: Int
    let rowHeight: CGFloat
    let bottomPadding: CGFloat
    let layoutShift: CGFloat
    @ViewBuilder let row: Row

    var body: some View {
        ZStack(alignment: .bottomLeading) {
            ZStack {
                PileCardFill()
                GlassCardBackground()
            }
            .padding(.vertical, 3)
            .modifier(effect(.card))

            row
                .modifier(effect(.content))

            HStack(spacing: 8) {
                Image(systemName: symbol)
                    .font(.system(size: 11, weight: .medium))
                    .foregroundStyle(.secondary)
                Text(title)
                    .font(.system(size: 12.5, weight: .semibold))
                    .lineLimit(1)
                    .truncationMode(.tail)
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 26)
            .frame(height: 22)
            .padding(.bottom, 5)
            .modifier(effect(.strip))
        }
    }

    private func effect(_ layer: PileCardEffect.Layer) -> PileCardEffect {
        PileCardEffect(
            layer: layer,
            index: index,
            count: count,
            rowHeight: rowHeight,
            bottomPadding: bottomPadding,
            layoutShift: layoutShift
        )
    }
}
