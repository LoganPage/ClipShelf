import SwiftUI

// Liquid Glass versions of the standard settings controls. They share the
// same glass capsule and springy, stretchy lens as the type switcher.

/// An on/off switch. At rest the thumb is a solid knob; pressing lifts it
/// into a clear glass lens that swells past the track, slides across with a
/// stretch and settles back into a knob, like the system switch on macOS 26.
struct LiquidToggle: View {
    @Binding var isOn: Bool
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.isEnabled) private var isEnabled
    @Environment(\.glassTransparency) private var transparency
    @State private var isPressed = false
    @State private var stretch: CGFloat = 1

    private let width: CGFloat = 46
    private let height: CGFloat = 26
    private var knob: CGFloat { height - 4 }

    var body: some View {
        ZStack(alignment: .leading) {
            // Clear glass like every other control; tinted with the accent
            // colour when on.
            Color.clear
                .clipShelfGlass(in: Capsule(), tint: isOn ? Color.accentColor.opacity(colorScheme == .dark ? 0.75 : 0.8) : nil)
            thumb
                .position(x: thumbCenter, y: height / 2)
        }
        .frame(width: width, height: height)
        .contentShape(Capsule())
        .opacity(isEnabled ? 1 : 0.45)
        .gesture(
            DragGesture(minimumDistance: 0)
                .onChanged { _ in
                    guard !isPressed else { return }
                    withAnimation(.spring(response: 0.26, dampingFraction: 0.7)) { isPressed = true }
                }
                .onEnded { value in
                    let target: Bool
                    if abs(value.translation.width) > 8 {
                        target = value.translation.width > 0
                    } else {
                        target = !isOn
                    }
                    if target != isOn {
                        withAnimation(.spring(response: 0.4, dampingFraction: 0.72)) { isOn = target }
                        pulseStretch()
                    }
                    // Hold the lens a moment while it travels, then drop it.
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.16) {
                        withAnimation(.spring(response: 0.36, dampingFraction: 0.62)) { isPressed = false }
                    }
                }
        )
        .accessibilityElement()
        .accessibilityAddTraits(.isButton)
        .accessibilityValue(isOn ? "开" : "关")
        .accessibilityAction { isOn.toggle() }
    }

    private var thumbWidth: CGFloat { isPressed ? knob * 1.55 : knob }

    private var thumbCenter: CGFloat {
        isOn ? width - 2 - thumbWidth / 2 : 2 + thumbWidth / 2
    }

    @ViewBuilder
    private var thumb: some View {
        let shape = Capsule()
        let lensWidth = thumbWidth * stretch
        let lensHeight = isPressed ? knob * 1.3 : knob
        Group {
            if #available(macOS 26.0, *) {
                Color.clear
                    .glassEffect(.clear, in: shape)
                    // Solid at rest; nearly clear while lifted, so the lens
                    // shows the track through it.
                    .background(shape.fill(Color.white.opacity(isPressed ? 0.12 : 0.95)))
            } else {
                shape.fill(Color.white)
            }
        }
        .frame(width: lensWidth, height: lensHeight / sqrt(stretch))
        .shadow(color: .black.opacity(isPressed ? 0.12 : 0.22), radius: isPressed ? 6 : 2, y: isPressed ? 2 : 1)
    }

    /// The "duang": a quick stretch along the direction of travel.
    private func pulseStretch() {
        withAnimation(.spring(response: 0.18, dampingFraction: 0.9)) { stretch = 1.22 }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.14) {
            withAnimation(.spring(response: 0.45, dampingFraction: 0.55)) { stretch = 1 }
        }
    }
}

/// A stepless slider like the Liquid Glass one in Apple's settings: clear
/// glass on the left, tinted on the right.
///
/// - Pressing lifts the knob into a big clear glass lens; releasing drops
///   it back with a soft bounce.
/// - Pulling past either end stretches the whole track out like a noodle:
///   it gets longer and thinner, the knob stretches with it, and both spring
///   back together on release.
/// - It sticks for a moment at the middle (with a haptic tick), so the
///   centre is easy to land on.
/// - While dragging only the slider and `onPreview` (throttled) run; the
///   app-wide `onCommit` fires once, on release.
struct GlassLevelSlider: View {
    @Binding var value: Double
    let onPreview: (Double) -> Void
    let onCommit: (Double) -> Void
    @Environment(\.colorScheme) private var colorScheme
    @State private var dragValue: Double?
    @State private var isLifted = false
    /// Signed pull past an end, after resistance (points).
    @State private var pull: CGFloat = 0
    @State private var lastPreview: CFTimeInterval = 0
    @State private var detentEntered = false

    private let knob: CGFloat = 24
    private let detent: Double = 0.5
    private let detentHalfWidth: Double = 0.035
    private let liftSpring = Animation.spring(response: 0.32, dampingFraction: 0.72)
    private let settleSpring = Animation.spring(response: 0.5, dampingFraction: 0.55)

    private var shown: Double { dragValue ?? value }

    var body: some View {
        HStack(spacing: 14) {
            Image(systemName: "square.on.square")
                .font(.system(size: 15, weight: .medium))
                .foregroundStyle(Color.primary.opacity(0.7))
                .accessibilityHidden(true)
            GeometryReader { proxy in
                let usable = max(proxy.size.width - knob, 1)
                let midY = proxy.size.height / 2
                let extra = abs(pull)
                // The track lengthens by the pull and thins as it does.
                let trackWidth = usable + extra
                let trackX = knob / 2 + (pull < 0 ? pull : 0)
                let trackHeight = max(5 / (1 + extra / 26), 1.6)
                let knobX = knob / 2 + usable * CGFloat(shown) + pull
                ZStack(alignment: .topLeading) {
                    Capsule()
                        .fill(Color.primary.opacity(colorScheme == .dark ? 0.24 : 0.14))
                        .frame(width: trackWidth, height: trackHeight)
                        .position(x: trackX + trackWidth / 2, y: midY)
                    Circle()
                        .fill(Color.primary.opacity(0.32))
                        .frame(width: 3.5, height: 3.5)
                        .position(x: knob / 2 + usable * CGFloat(detent), y: midY + 13)
                    thumb
                        .position(x: knobX, y: midY)
                }
                .contentShape(Rectangle())
                .gesture(drag(usable: usable))
            }
            .frame(height: 34)
            Image(systemName: "square.fill.on.square.fill")
                .font(.system(size: 15, weight: .medium))
                .foregroundStyle(Color.primary.opacity(0.7))
                .accessibilityHidden(true)
        }
        .accessibilityElement()
        .accessibilityLabel("玻璃效果")
        .accessibilityValue("\(Int(value * 100))%")
        .accessibilityAdjustableAction { direction in
            switch direction {
            case .increment: value = min(value + 0.1, 1)
            case .decrement: value = max(value - 0.1, 0)
            @unknown default: break
            }
            onCommit(value)
        }
    }

    private func drag(usable: CGFloat) -> some Gesture {
        DragGesture(minimumDistance: 0)
            .onChanged { drag in
                if !isLifted {
                    withAnimation(liftSpring) { isLifted = true }
                }
                let raw = Double((drag.location.x - knob / 2) / usable)
                var next = min(max(raw, 0), 1)
                // Detent: hold at the middle for a moment.
                if abs(next - detent) < detentHalfWidth {
                    next = detent
                    if !detentEntered {
                        detentEntered = true
                        NSHapticFeedbackManager.defaultPerformer.perform(.alignment, performanceTime: .now)
                    }
                } else {
                    detentEntered = false
                }
                // Past an end: pull with growing resistance, like iOS.
                let beyond = raw < 0 ? CGFloat(raw) * usable : (raw > 1 ? CGFloat(raw - 1) * usable : 0)
                let resisted = beyond / (1 + abs(beyond) / 70) * 0.75
                if (raw < 0 || raw > 1), pull == 0 {
                    NSHapticFeedbackManager.defaultPerformer.perform(.levelChange, performanceTime: .now)
                }
                var plain = Transaction()
                plain.disablesAnimations = true
                withTransaction(plain) {
                    pull = resisted
                    dragValue = next
                }
                let now = CACurrentMediaTime()
                if now - lastPreview > 0.05 {
                    lastPreview = now
                    onPreview(next)
                }
            }
            .onEnded { _ in
                let final = dragValue ?? value
                onPreview(final)
                value = final
                onCommit(final)
                dragValue = nil
                detentEntered = false
                withAnimation(settleSpring) {
                    isLifted = false
                    pull = 0
                }
            }
    }

    @ViewBuilder
    private var thumb: some View {
        let shape = Capsule()
        let stretch = 1 + min(abs(pull) / 45, 0.7)
        let width = (isLifted ? knob * 1.9 : knob) * stretch
        let height = (isLifted ? knob * 1.45 : knob) / sqrt(stretch)
        Group {
            if #available(macOS 26.0, *) {
                // Not interactive glass: the lens is animated here, and the
                // system's own press response would fight that motion.
                Color.clear
                    .glassEffect(.clear, in: shape)
                    .background(shape.fill(Color.white.opacity(isLifted ? 0.06 : 0.97)))
            } else {
                shape.fill(Color.white.opacity(isLifted ? 0.6 : 1))
            }
        }
        .frame(width: width, height: height)
        .shadow(color: .black.opacity(isLifted ? 0.12 : 0.22), radius: isLifted ? 8 : 2.5, y: isLifted ? 3 : 1)
    }
}

/// Settings buttons as Liquid Glass capsules: the system's interactive glass
/// gives the press its liquid response, and the label stays fully legible
/// in dark mode. `isActive` (e.g. while recording a shortcut) tints the
/// glass with the accent colour instead of switching to a flat blue fill.
struct LiquidGlassButtonStyle: ButtonStyle {
    var isActive = false

    func makeBody(configuration: Configuration) -> some View {
        LiquidGlassButtonBody(configuration: configuration, isActive: isActive)
    }
}

/// One height for every control on a settings row, so they line up.
enum SettingsControlMetrics {
    static let height: CGFloat = 34
}

private struct LiquidGlassButtonBody: View {
    let configuration: ButtonStyleConfiguration
    let isActive: Bool
    @Environment(\.isEnabled) private var isEnabled
    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        let isDark = colorScheme == .dark
        configuration.label
            .font(.system(size: 13.5, weight: isActive ? .semibold : .medium))
            .lineLimit(1)
            .foregroundStyle(.primary)
            .padding(.horizontal, 16)
            .frame(minWidth: SettingsControlMetrics.height, minHeight: SettingsControlMetrics.height)
            .background {
                // Clear glass, the same as the 设置 capsule and the type
                // switcher's lens.
                Color.clear.clipShelfGlass(
                    in: Capsule(),
                    interactive: true,
                    tint: isActive ? Color.accentColor.opacity(isDark ? 0.36 : 0.24) : nil
                )
            }
            .overlay {
                if isActive {
                    BreathingRim()
                }
            }
            .contentShape(Capsule())
            .scaleEffect(configuration.isPressed ? 0.95 : 1)
            .animation(.spring(response: 0.25, dampingFraction: 0.62), value: configuration.isPressed)
            .animation(.spring(response: 0.35, dampingFraction: 0.75), value: isActive)
            .opacity(isEnabled ? 1 : 0.45)
    }
}

/// A softly pulsing accent rim, e.g. while a shortcut is being recorded.
private struct BreathingRim: View {
    @State private var bright = false

    var body: some View {
        Capsule()
            .strokeBorder(Color.accentColor.opacity(bright ? 0.95 : 0.35), lineWidth: 1.5)
            .onAppear {
                withAnimation(.easeInOut(duration: 0.85).repeatForever(autoreverses: true)) {
                    bright = true
                }
            }
            .allowsHitTesting(false)
    }
}

// MARK: - Pull-down menu

/// A pull-down menu shown as a glass capsule. Its options open in a glass
/// panel that pours out of the capsule (see `LiquidMenuOverlay`), instead of
/// a system menu.
struct LiquidMenu<Value: Hashable>: View {
    let options: [Value]
    @Binding var selection: Value
    let title: (Value) -> String
    @ObservedObject private var presenter = LiquidMenuPresenter.shared
    @State private var id = UUID()
    @State private var frameBox = FrameBox()

    var body: some View {
        let isOpen = presenter.request?.id == id
        HStack(spacing: 6) {
            Text(title(selection))
                .font(.system(size: 13, weight: .medium))
            Image(systemName: "chevron.up.chevron.down")
                .font(.system(size: 10, weight: .semibold))
                .foregroundStyle(.secondary)
        }
        .padding(.horizontal, 16)
        .frame(height: SettingsControlMetrics.height)
        .clipShelfGlass(in: Capsule(), interactive: true)
        .contentShape(Capsule())
        // Pressed-in look while its panel is out.
        .scaleEffect(isOpen ? 0.96 : 1)
        .animation(.spring(response: 0.3, dampingFraction: 0.7), value: isOpen)
        .fixedSize()
        .background(FrameReader(box: frameBox))
        .onTapGesture { open() }
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isButton)
    }

    private func open() {
        let titles = options.map(title)
        let selectedIndex = options.firstIndex(of: selection)
        let options = options
        presenter.present(LiquidMenuPresenter.Request(
            id: id,
            anchor: frameBox.rect,
            titles: titles,
            selectedIndex: selectedIndex,
            onSelect: { index in
                guard options.indices.contains(index) else { return }
                selection = options[index]
            }
        ))
    }
}

/// Holds a frame without re-rendering anything when it changes.
final class FrameBox {
    var rect: CGRect = .zero
}

/// Records a view's frame in the settings panel's coordinate space.
private struct FrameReader: View {
    let box: FrameBox

    var body: some View {
        GeometryReader { proxy in
            let rect = proxy.frame(in: .named(SettingsPanelSpace.name))
            Color.clear
                .onAppear { box.rect = rect }
                .onChange(of: rect) { box.rect = $0 }
        }
    }
}

final class LiquidMenuPresenter: ObservableObject {
    static let shared = LiquidMenuPresenter()

    struct Request: Identifiable {
        let id: UUID
        let anchor: CGRect
        let titles: [String]
        let selectedIndex: Int?
        let onSelect: (Int) -> Void
    }

    @Published private(set) var request: Request?
    /// Grown out of the capsule into the full panel.
    @Published var isExpanded = false

    var isOpen: Bool { request != nil }

    func present(_ request: Request) {
        self.request = request
        isExpanded = false
        DispatchQueue.main.async {
            withAnimation(.spring(response: 0.42, dampingFraction: 0.78)) {
                self.isExpanded = true
            }
        }
    }

    /// Shrinks the panel back into its capsule, then removes it.
    func dismiss(choosing index: Int? = nil) {
        guard let request else { return }
        if let index { request.onSelect(index) }
        withAnimation(.spring(response: 0.32, dampingFraction: 0.86)) {
            isExpanded = false
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.26) {
            guard self.request?.id == request.id, !self.isExpanded else { return }
            self.request = nil
        }
    }

    /// Closes the menu without animation (e.g. when settings closes).
    func dismissImmediately() {
        request = nil
        isExpanded = false
    }
}

/// Draws the open menu above the whole settings page, so it is never
/// clipped by the scroll view or covered by the rows below it.
struct LiquidMenuOverlay: View {
    @ObservedObject private var presenter = LiquidMenuPresenter.shared
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.glassTransparency) private var transparency
    @State private var hovered: Int?

    private let rowHeight: CGFloat = 30
    private let inset: CGFloat = 6
    /// Gap between the capsule and the panel: the same as the panel's inner
    /// inset, so the two read as one aligned group.
    private let gap: CGFloat = 6

    var body: some View {
        GeometryReader { proxy in
            if let request = presenter.request {
                let target = panelRect(for: request, in: proxy.size)
                let rect = presenter.isExpanded ? target : request.anchor
                let radius = presenter.isExpanded ? 15 : request.anchor.height / 2
                ZStack(alignment: .topLeading) {
                    // Clicking anywhere else closes it, like a menu.
                    Color.black.opacity(0.001)
                        .onTapGesture { presenter.dismiss() }

                    panel(request, radius: radius, size: target.size)
                        .frame(width: rect.width, height: rect.height, alignment: .top)
                        .clipShape(RoundedRectangle(cornerRadius: radius, style: .continuous))
                        .offset(x: rect.minX, y: rect.minY)
                }
                .frame(width: proxy.size.width, height: proxy.size.height, alignment: .topLeading)
            }
        }
    }

    private func panelRect(for request: Request, in size: CGSize) -> CGRect {
        let longest = request.titles.map { CGFloat($0.count) * 13.5 }.max() ?? 60
        let width = max(request.anchor.width, longest + 52)
        let height = CGFloat(request.titles.count) * rowHeight + inset * 2
        var y = request.anchor.maxY + gap
        if y + height > size.height - 8 {
            y = request.anchor.minY - gap - height
        }
        // Trailing edges line up with the capsule.
        return CGRect(x: request.anchor.maxX - width, y: y, width: width, height: height)
    }

    private typealias Request = LiquidMenuPresenter.Request

    private func panel(_ request: Request, radius: CGFloat, size: CGSize) -> some View {
        VStack(spacing: 0) {
            ForEach(Array(request.titles.enumerated()), id: \.offset) { index, title in
                HStack(spacing: 8) {
                    Image(systemName: "checkmark")
                        .font(.system(size: 11, weight: .bold))
                        .opacity(index == request.selectedIndex ? 1 : 0)
                        .frame(width: 14)
                    Text(title)
                        .font(.system(size: 13, weight: index == request.selectedIndex ? .semibold : .medium))
                        .lineLimit(1)
                    Spacer(minLength: 0)
                }
                .foregroundStyle(Color.primary)
                .padding(.horizontal, 12)
                .frame(height: rowHeight)
                .background {
                    Capsule()
                        .fill(Color.accentColor.opacity(colorScheme == .dark ? 0.34 : 0.16))
                        .opacity(hovered == index ? 1 : 0)
                }
                .contentShape(Rectangle())
                .onHover { inside in
                    withAnimation(.easeOut(duration: 0.12)) {
                        hovered = inside ? index : (hovered == index ? nil : hovered)
                    }
                }
                .onTapGesture { presenter.dismiss(choosing: index) }
            }
        }
        .padding(inset)
        .frame(width: size.width, height: size.height, alignment: .top)
        // Options appear once the glass has poured out.
        .opacity(presenter.isExpanded ? 1 : 0)
        .animation(.easeOut(duration: presenter.isExpanded ? 0.22 : 0.1).delay(presenter.isExpanded ? 0.08 : 0), value: presenter.isExpanded)
        .background {
            Color.clear.clipShelfGlass(in: RoundedRectangle(cornerRadius: radius, style: .continuous))
        }
        .shadow(color: .black.opacity(colorScheme == .dark ? 0.35 : 0.14), radius: 14, y: 6)
    }
}

/// A number with − / + in one glass capsule; the number itself can be typed.
struct LiquidStepper: View {
    @Binding var text: String
    let onIncrement: () -> Void
    let onDecrement: () -> Void
    let onCommit: () -> Void
    var focus: FocusState<Bool>.Binding

    var body: some View {
        HStack(spacing: 0) {
            stepButton("minus", action: onDecrement)
            TextField("", text: $text)
                .textFieldStyle(.plain)
                .multilineTextAlignment(.center)
                .font(.system(size: 13, weight: .medium).monospacedDigit())
                .frame(width: 56)
                .focused(focus)
                .onSubmit(onCommit)
            stepButton("plus", action: onIncrement)
        }
        .frame(height: SettingsControlMetrics.height)
        .clipShelfGlass(in: Capsule())
    }

    private func stepButton(_ symbol: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 11, weight: .bold))
                .frame(width: SettingsControlMetrics.height, height: SettingsControlMetrics.height)
                .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .foregroundStyle(Color.primary.opacity(0.75))
    }
}
