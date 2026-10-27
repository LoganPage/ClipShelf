import SwiftUI

/// A tab-bar style switcher modelled on Apple Music's bottom bar.
///
/// At rest the selection is a soft pill inside a glass capsule. Pressing lifts
/// it into a larger clear-glass lens. Dragging moves the lens 1:1 with the
/// pointer, stretching with speed and rubber-banding past the ends; a tap
/// glides the lifted lens to the tapped option. Either way it then drops back
/// into the bar with a springy overshoot.
struct LiquidSegmentedControl<Value: Hashable>: View {
    let options: [Value]
    @Binding var selection: Value
    let title: (Value) -> String
    var segmentWidth: CGFloat?
    var height: CGFloat = GlassMetrics.controlHeight

    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.glassTransparency) private var transparency
    /// Index the resting pill sits on; animated separately from `selection`
    /// so the list behind the control never animates with it.
    @State private var restingIndex: Int?
    /// Unclamped centre of the lens while lifted.
    @State private var lensCenter: CGFloat?
    @State private var isLifted = false
    @State private var stretch: CGFloat = 1
    @State private var isTracking = false
    @State private var didDrag = false
    @State private var lastSample: (x: CGFloat, time: TimeInterval)?
    /// Invalidates a pending tap "drop" when a new press starts.
    @State private var interactionToken = 0

    private let inset: CGFloat = 4
    private let liftSpring = Animation.spring(response: 0.24, dampingFraction: 0.66)
    private let glideSpring = Animation.spring(response: 0.3, dampingFraction: 0.74)
    private let settleSpring = Animation.spring(response: 0.42, dampingFraction: 0.55)
    private let tapHoldDuration: TimeInterval = 0.26

    var body: some View {
        GeometryReader { proxy in
            let layout = Layout(width: proxy.size.width, height: height, inset: inset, count: options.count)
            let resting = restingIndex ?? selectedIndex
            let center = lensCenter.map(layout.rubberBanded) ?? layout.center(of: resting)
            let highlighted = lensCenter == nil ? resting : layout.nearestIndex(to: center)

            ZStack(alignment: .topLeading) {
                Color.clear
                    .clipShelfGlass(in: Capsule())

                restingPill(layout: layout)
                    .position(x: center, y: height / 2)
                    .opacity(isLifted ? 0 : 1)

                labels(layout: layout, highlighted: highlighted)

                // Only exists while lifted, so an idle bar never pays for a hidden glass layer.
                if isLifted {
                    lens(layout: layout)
                        .position(x: center, y: height / 2)
                        .transition(.scale(scale: 0.55).combined(with: .opacity))
                        .allowsHitTesting(false)
                }
            }
            .contentShape(Capsule())
            .gesture(pressGesture(layout: layout))
        }
        .frame(width: segmentWidth.map { $0 * CGFloat(options.count) + inset * 2 }, height: height)
        .onChange(of: selection) { _ in
            guard !isTracking, lensCenter == nil, restingIndex != selectedIndex else { return }
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
        Group {
            if transparency == .opaque {
                base
                    .background(lensShape.fill(AppTheme.searchBackground))
                    .overlay(lensShape.stroke(AppTheme.subtleBorder, lineWidth: 0.5))
                    .shadow(color: .black.opacity(0.18), radius: 8, y: 3)
            } else if #available(macOS 26.0, *) {
                base.glassEffect(.clear.interactive(), in: lensShape)
            } else {
                base
                    .background(.ultraThinMaterial, in: lensShape)
                    .overlay(lensShape.stroke(Color.white.opacity(0.5), lineWidth: 0.75))
                    .shadow(color: .black.opacity(0.18), radius: 10, y: 4)
            }
        }
        .scaleEffect(x: stretch, y: 1 / sqrt(stretch))
    }

    private func labels(layout: Layout, highlighted: Int) -> some View {
        HStack(spacing: 0) {
            ForEach(Array(options.enumerated()), id: \.offset) { index, option in
                Text(title(option))
                    .font(.system(size: 13, weight: index == highlighted ? .semibold : .medium))
                    .foregroundStyle(index == highlighted ? Color.accentColor : Color.primary.opacity(0.78))
                    .lineLimit(1)
                    .frame(width: layout.segment, height: height)
                    .scaleEffect(isLifted && index == highlighted ? 1.08 : 1)
            }
        }
        .padding(.horizontal, inset)
        .animation(liftSpring, value: highlighted)
        .animation(liftSpring, value: isLifted)
        .allowsHitTesting(false)
    }

    // MARK: Interaction

    private func pressGesture(layout: Layout) -> some Gesture {
        DragGesture(minimumDistance: 0, coordinateSpace: .local)
            .onChanged { value in
                let now = value.time.timeIntervalSinceReferenceDate
                let x = value.location.x

                if !isTracking {
                    // Press: lift the lens out of the bar where the selection sits.
                    isTracking = true
                    didDrag = false
                    interactionToken &+= 1
                    let resting = restingIndex ?? selectedIndex
                    restingIndex = resting
                    lastSample = (x, now)
                    withAnimation(liftSpring) {
                        lensCenter = lensCenter ?? layout.center(of: resting)
                        isLifted = true
                    }
                    return
                }

                if !didDrag {
                    guard abs(value.translation.width) > 3 else { return }
                    didDrag = true
                    // Catch up to the pointer once, then track it 1:1.
                    withAnimation(.spring(response: 0.18, dampingFraction: 0.8)) {
                        lensCenter = x
                    }
                    lastSample = (x, now)
                    return
                }

                var velocity: CGFloat = 0
                if let lastSample, now > lastSample.time {
                    velocity = (x - lastSample.x) / CGFloat(now - lastSample.time)
                }
                lastSample = (x, now)

                let overshoot = layout.overshoot(x)
                let speedStretch = min(abs(velocity) / 2400, 0.34)
                // Pulling past an end squashes the lens instead of stretching it.
                let target = max(0.82, 1 + speedStretch - min(overshoot / 220, 0.18))
                // No animation transaction per event: position follows the pointer
                // directly and stretch is smoothed by hand, which keeps dragging light.
                var transaction = Transaction()
                transaction.disablesAnimations = true
                withTransaction(transaction) {
                    lensCenter = x
                    stretch += (target - stretch) * 0.35
                }
            }
            .onEnded { value in
                isTracking = false
                lastSample = nil
                let landing = didDrag
                    ? layout.nearestIndex(to: layout.rubberBanded(value.location.x))
                    : layout.nearestIndex(to: value.location.x)
                if options[landing] != selection {
                    selection = options[landing]
                }

                guard !didDrag else {
                    drop(at: landing)
                    return
                }

                // Tap: glide the lifted lens to the tapped option, hold, then drop.
                let token = interactionToken
                let distance = abs(layout.center(of: landing) - (lensCenter ?? layout.center(of: landing)))
                withAnimation(glideSpring) {
                    lensCenter = layout.center(of: landing)
                }
                pulseStretch(by: min(distance / max(layout.segment, 1) * 0.14, 0.3))
                DispatchQueue.main.asyncAfter(deadline: .now() + tapHoldDuration) {
                    guard token == interactionToken, !isTracking else { return }
                    drop(at: landing)
                }
            }
    }

    private func drop(at index: Int) {
        withAnimation(settleSpring) {
            restingIndex = index
            lensCenter = nil
            isLifted = false
            stretch = 1
        }
    }

    /// Elongates briefly, then wobbles back: the "duang" on every jump.
    private func pulseStretch(by amount: CGFloat) {
        guard amount > 0.01 else { return }
        withAnimation(.spring(response: 0.14, dampingFraction: 0.9)) {
            stretch = 1 + amount
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.12) {
            guard !isTracking else { return }
            withAnimation(.spring(response: 0.46, dampingFraction: 0.42)) {
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
