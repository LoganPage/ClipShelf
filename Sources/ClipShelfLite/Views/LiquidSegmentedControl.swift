import SwiftUI

/// A tab-bar style switcher modelled on Apple Music's bottom bar.
///
/// At rest the selection is a soft pill inside a glass capsule. Pressing lifts
/// it into a larger clear-glass lens that follows the pointer, stretches with
/// speed, rubber-bands past the ends, and springs back into place on release.
struct LiquidSegmentedControl<Value: Hashable>: View {
    let options: [Value]
    @Binding var selection: Value
    let title: (Value) -> String
    var segmentWidth: CGFloat?
    var height: CGFloat = 36
    /// Off when it sits inside a glass capsule that is already there (an
    /// opened pull-down). The only change from the first version.
    var drawsBackground = true
    /// Told when a press starts and ends (after the click hold), e.g. so a
    /// surrounding capsule can swell with it.
    var onPressChange: ((Bool) -> Void)? = nil

    @Environment(\.colorScheme) private var colorScheme
    /// Index the resting pill sits on; animated separately from `selection`
    /// so the list behind the control never animates with it.
    @State private var restingIndex: Int?
    /// Pointer-driven centre of the lens while pressed, before rubber-banding.
    @State private var dragCenter: CGFloat?
    @State private var isPressed = false
    @State private var stretch: CGFloat = 1
    @State private var lastSample: (x: CGFloat, time: TimeInterval)?
    @State private var pressedAt: CFTimeInterval = 0
    @State private var pressToken = 0

    private let inset: CGFloat = 4
    private let settleSpring = Animation.spring(response: 0.44, dampingFraction: 0.56)
    private let pressSpring = Animation.spring(response: 0.26, dampingFraction: 0.62)
    private let followSpring = Animation.interactiveSpring(response: 0.18, dampingFraction: 0.78)

    var body: some View {
        GeometryReader { proxy in
            let layout = Layout(width: proxy.size.width, height: height, inset: inset, count: options.count)
            let resting = restingIndex ?? selectedIndex
            let center = dragCenter.map(layout.rubberBanded) ?? layout.center(of: resting)
            let highlighted = dragCenter == nil ? resting : layout.nearestIndex(to: center)

            ZStack(alignment: .topLeading) {
                bar

                restingPill(layout: layout)
                    .position(x: center, y: height / 2)
                    .opacity(isPressed ? 0 : 1)

                labels(layout: layout, highlighted: highlighted)

                lens(layout: layout)
                    .position(x: center, y: height / 2)
                    .opacity(isPressed ? 1 : 0)
                    .scaleEffect(isPressed ? 1 : 0.6)
                    .allowsHitTesting(false)
            }
            .contentShape(Capsule())
            .gesture(pressGesture(layout: layout))
            // Press feedback, as on iOS: the whole bar swells a little
            // while touched and settles back on release.
            .pressFeedback(drawsBackground && isPressed)
        }
        .frame(width: segmentWidth.map { $0 * CGFloat(options.count) + inset * 2 }, height: height)
        .onChange(of: selection) { _ in
            guard dragCenter == nil, restingIndex != selectedIndex else { return }
            withAnimation(settleSpring) {
                restingIndex = selectedIndex
            }
            pulseStretch(by: 0.22)
        }
        .accessibilityElement(children: .ignore)
        .accessibilityValue(title(selection))
        .accessibilityAdjustableAction { direction in
            let next: Int
            switch direction {
            case .increment: next = min(selectedIndex + 1, options.count - 1)
            case .decrement: next = max(selectedIndex - 1, 0)
            @unknown default: return
            }
            selection = options[next]
        }
    }

    private var selectedIndex: Int {
        options.firstIndex(of: selection) ?? 0
    }

    // MARK: Pieces

    @ViewBuilder
    private var bar: some View {
        if !drawsBackground {
            Color.clear
        } else if #available(macOS 26.0, *) {
            Color.clear
                .glassEffect(.regular, in: Capsule())
        } else {
            Capsule()
                .fill(.regularMaterial)
                .overlay(Capsule().stroke(Color.primary.opacity(0.08), lineWidth: 0.5))
        }
    }

    private func restingPill(layout: Layout) -> some View {
        Capsule()
            .fill(Color.primary.opacity(colorScheme == .dark ? 0.16 : 0.09))
            .frame(width: layout.segment, height: height - inset * 2)
            .scaleEffect(x: stretch, y: 1 / sqrt(stretch))
    }

    @ViewBuilder
    private func lens(layout: Layout) -> some View {
        let lensShape = Capsule()
        let base = Color.clear
            .frame(width: layout.segment + 16, height: height + 10)
            .scaleEffect(x: stretch, y: 1 / sqrt(stretch))
        if #available(macOS 26.0, *) {
            base.glassEffect(.clear.interactive(), in: lensShape)
        } else {
            base
                .background(.ultraThinMaterial, in: lensShape)
                .overlay(lensShape.stroke(Color.white.opacity(0.5), lineWidth: 0.75))
                .shadow(color: .black.opacity(0.18), radius: 10, y: 4)
        }
    }

    private func labels(layout: Layout, highlighted: Int) -> some View {
        HStack(spacing: 0) {
            ForEach(Array(options.enumerated()), id: \.offset) { index, option in
                Text(title(option))
                    // The same size and colour as the other buttons' labels.
                    .font(.system(size: 13.5, weight: index == highlighted ? .semibold : .medium))
                    .foregroundStyle(index == highlighted ? Color.accentColor : Color.primary)
                    .lineLimit(1)
                    .frame(width: layout.segment, height: height)
                    .scaleEffect(isPressed && index == highlighted ? 1.08 : 1)
            }
        }
        .padding(.horizontal, inset)
        .animation(pressSpring, value: highlighted)
        .animation(pressSpring, value: isPressed)
        .allowsHitTesting(false)
    }

    // MARK: Interaction

    private func pressGesture(layout: Layout) -> some Gesture {
        DragGesture(minimumDistance: 0, coordinateSpace: .local)
            .onChanged { value in
                let x = value.location.x
                // A new press (also one that lands while a click's lens is
                // still held up).
                if !isPressed || lastSample == nil {
                    beginPress(at: x, time: value.time.timeIntervalSinceReferenceDate, layout: layout)
                    return
                }

                let now = value.time.timeIntervalSinceReferenceDate
                var velocity: CGFloat = 0
                if let lastSample, now > lastSample.time {
                    velocity = (x - lastSample.x) / CGFloat(now - lastSample.time)
                }
                lastSample = (x, now)

                let overshoot = layout.overshoot(x)
                let speedStretch = min(abs(velocity) / 2400, 0.34)
                // Pulling past an end squashes the lens instead of stretching it.
                let target = max(0.82, 1 + speedStretch - min(overshoot / 220, 0.18))
                withAnimation(followSpring) {
                    dragCenter = x
                    stretch = target
                }
            }
            .onEnded { value in
                // A click so quick that no press was seen yet still lifts the lens.
                if lastSample == nil {
                    beginPress(at: value.location.x, time: value.time.timeIntervalSinceReferenceDate, layout: layout)
                }
                let landing = layout.nearestIndex(to: layout.rubberBanded(value.location.x))
                lastSample = nil
                if options[landing] != selection {
                    selection = options[landing]
                }
                // A quick click keeps the lens up for a moment, gliding onto
                // its choice, so it shows as clearly as a long press.
                let token = pressToken
                let wait = max(0, PressFeedback.minimumHold - (CACurrentMediaTime() - pressedAt))
                withAnimation(followSpring) {
                    dragCenter = layout.center(of: landing)
                }
                DispatchQueue.main.asyncAfter(deadline: .now() + wait) {
                    guard token == pressToken else { return }
                    onPressChange?(false)
                    withAnimation(settleSpring) {
                        restingIndex = landing
                        dragCenter = nil
                        isPressed = false
                        stretch = 1
                    }
                }
            }
    }

    /// Lifts the lens from the current selection, then glides it to the
    /// pointer, so a tap elsewhere still visibly travels.
    private func beginPress(at x: CGFloat, time: TimeInterval, layout: Layout) {
        restingIndex = restingIndex ?? selectedIndex
        pressToken += 1
        pressedAt = CACurrentMediaTime()
        onPressChange?(true)
        withAnimation(pressSpring) {
            isPressed = true
            dragCenter = x
        }
        let distance = abs(x - layout.center(of: restingIndex ?? selectedIndex))
        pulseStretch(by: min(distance / max(layout.segment, 1) * 0.16, 0.32))
        lastSample = (x, time)
    }

    /// Elongates briefly, then wobbles back: the "duang" on every jump.
    private func pulseStretch(by amount: CGFloat) {
        guard amount > 0.01 else { return }
        withAnimation(.spring(response: 0.16, dampingFraction: 0.9)) {
            stretch = 1 + amount
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.13) {
            guard lastSample == nil || !isPressed else { return }
            withAnimation(.spring(response: 0.5, dampingFraction: 0.42)) {
                stretch = 1
            }
        }
    }

    private struct Layout {
        let width: CGFloat
        let height: CGFloat
        let inset: CGFloat
        let count: Int

        var segment: CGFloat {
            max((width - inset * 2) / CGFloat(max(count, 1)), 1)
        }

        var minCenter: CGFloat { center(of: 0) }
        var maxCenter: CGFloat { center(of: count - 1) }

        func center(of index: Int) -> CGFloat {
            inset + segment * (CGFloat(index) + 0.5)
        }

        func nearestIndex(to x: CGFloat) -> Int {
            let raw = Int(((x - inset) / segment).rounded(.down))
            return min(max(raw, 0), count - 1)
        }

        func overshoot(_ x: CGFloat) -> CGFloat {
            if x < minCenter { return minCenter - x }
            if x > maxCenter { return x - maxCenter }
            return 0
        }

        /// Follows the pointer 1:1 inside the bar and with growing resistance past it.
        func rubberBanded(_ x: CGFloat) -> CGFloat {
            let limit: CGFloat = 26
            func resist(_ distance: CGFloat) -> CGFloat {
                limit * (1 - 1 / (distance / limit * 0.55 + 1))
            }
            if x < minCenter { return minCenter - resist(minCenter - x) }
            if x > maxCenter { return maxCenter + resist(x - maxCenter) }
            return x
        }
    }
}
