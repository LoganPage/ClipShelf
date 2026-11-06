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
    private let detentHalfWidth: Double = 0.055
    private let liftSpring = Animation.spring(response: 0.32, dampingFraction: 0.72)
    private let settleSpring = Animation.spring(response: 0.5, dampingFraction: 0.55)
    /// Snapping into and out of the middle: a short, bouncy spring.
    private let detentSpring = Animation.spring(response: 0.26, dampingFraction: 0.5)
    /// A tap on the track: the thumb glides over and overshoots a little.
    private let tapSpring = Animation.spring(response: 0.38, dampingFraction: 0.62)

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
                // The first touch (a tap, or the start of a drag away from the
                // thumb) glides the thumb over with a spring, not a jump.
                let isFirstTouch = !isLifted
                if !isLifted {
                    withAnimation(liftSpring) { isLifted = true }
                }
                let raw = Double((drag.location.x - knob / 2) / usable)
                var next = min(max(raw, 0), 1)
                // Detent: the thumb springs into the middle and holds there,
                // then springs after the pointer once it is pulled away.
                var snap: Animation?
                if abs(next - detent) < detentHalfWidth {
                    next = detent
                    if !detentEntered {
                        detentEntered = true
                        snap = detentSpring
                        NSHapticFeedbackManager.defaultPerformer.perform(.alignment, performanceTime: .now)
                    }
                } else if detentEntered {
                    detentEntered = false
                    snap = detentSpring
                }
                // Past an end: pull with growing resistance, like iOS.
                let beyond = raw < 0 ? CGFloat(raw) * usable : (raw > 1 ? CGFloat(raw - 1) * usable : 0)
                let resisted = beyond / (1 + abs(beyond) / 70) * 0.75
                if (raw < 0 || raw > 1), pull == 0 {
                    NSHapticFeedbackManager.defaultPerformer.perform(.levelChange, performanceTime: .now)
                }
                if isFirstTouch { snap = tapSpring }
                var motion = Transaction(animation: snap)
                motion.disablesAnimations = snap == nil
                withTransaction(motion) {
                    pull = resisted
                    dragValue = next
                }
                let now = CACurrentMediaTime()
                // Only the small sample follows, so it can follow every frame.
                if now - lastPreview > 1.0 / 60.0 {
                    lastPreview = now
                    onPreview(next)
                }
            }
            .onEnded { _ in
                let final = dragValue ?? value
                onPreview(final)
                value = final
                // Commit once the thumb has settled: applying the level
                // redraws every window, which froze the settle animation.
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.38) {
                    guard dragValue == nil else { return }
                    onCommit(final)
                }
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
        // A thin rim instead of a shadow: a blurred shadow had to be redrawn
        // on every frame of the lift.
        .overlay(shape.strokeBorder(Color.black.opacity(isLifted ? 0.06 : 0.14), lineWidth: 0.5))
        // Sized frame by frame, so at rest it is a true circle.
        .modifier(AnimatedSize(width: width, height: height))
    }
}

/// Settings buttons as Liquid Glass capsules: the system's interactive glass
/// gives the press its liquid response, and the label stays fully legible
/// in dark mode. `isActive` (e.g. while recording a shortcut) tints the
/// glass with the accent colour instead of switching to a flat blue fill.
struct LiquidGlassButtonStyle: ButtonStyle {
    var isActive = false
    /// A set width, animated frame by frame (the glass then follows the
    /// capsule exactly as it grows, instead of morphing on its own).
    var width: CGFloat?

    func makeBody(configuration: Configuration) -> some View {
        LiquidGlassButtonBody(configuration: configuration, isActive: isActive, width: width)
    }
}

/// Lays out at each frame's size while animating.
private struct AnimatedSize: ViewModifier, Animatable {
    var width: CGFloat
    var height: CGFloat

    var animatableData: AnimatablePair<CGFloat, CGFloat> {
        get { AnimatablePair(width, height) }
        set { width = newValue.first; height = newValue.second }
    }

    func body(content: Content) -> some View {
        content.frame(width: width, height: height)
    }
}

/// Lays out at each frame's width while animating.
private struct AnimatedWidth: ViewModifier, Animatable {
    var width: CGFloat?

    var animatableData: CGFloat {
        get { width ?? 0 }
        set { width = width == nil ? nil : newValue }
    }

    func body(content: Content) -> some View {
        if let width {
            // Trailing: the capsule grows out to the left of a right-aligned
            // control, and the old and new labels cross-fade in place
            // instead of sliding sideways.
            content.frame(width: width, alignment: .trailing)
        } else {
            content
        }
    }
}

/// One height for every control on a settings row, so they line up.
enum SettingsControlMetrics {
    static let height: CGFloat = 34
}

private struct LiquidGlassButtonBody: View {
    let configuration: ButtonStyleConfiguration
    let isActive: Bool
    var width: CGFloat?
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
            .modifier(AnimatedWidth(width: width))
            .background {
                // Clear glass, the same as the 设置 capsule and the type
                // switcher's lens.
                Color.clear.clipShelfGlass(
                    in: Capsule(),
                    // Not the system's interactive glass: its own press glow
                    // and bounce would differ from the shared press feedback.
                    interactive: false,
                    tint: isActive ? Color.accentColor.opacity(isDark ? 0.36 : 0.24) : nil
                )
            }
            // Always there (only shown while active), so it grows and shrinks
            // with the capsule instead of appearing at its final size.
            .overlay {
                BreathingRim(isActive: isActive)
            }
            .contentShape(Capsule())
            .pressFeedback(configuration.isPressed)
            .animation(.spring(response: 0.35, dampingFraction: 0.75), value: isActive)
            .opacity(isEnabled ? 1 : 0.45)
    }
}

/// A softly pulsing accent rim, e.g. while a shortcut is being recorded.
private struct BreathingRim: View {
    let isActive: Bool
    @State private var bright = false

    var body: some View {
        Capsule()
            .strokeBorder(Color.accentColor.opacity(bright ? 0.95 : 0.35), lineWidth: 1.5)
            .opacity(isActive ? 1 : 0)
            .onChange(of: isActive) { active in
                if active {
                    withAnimation(.easeInOut(duration: 0.85).repeatForever(autoreverses: true)) {
                        bright = true
                    }
                } else {
                    var plain = Transaction()
                    plain.disablesAnimations = true
                    withTransaction(plain) { bright = false }
                }
            }
            .allowsHitTesting(false)
    }
}

// MARK: - Pull-down menu

/// A choice shown as a glass capsule with the current option. Clicking it
/// grows the capsule itself sideways (to the left, like the shortcut
/// buttons) into a switcher with every option, laid out like the type
/// switcher; picking one shrinks it back to the capsule.
struct LiquidMenu<Value: Hashable>: View {
    let options: [Value]
    @Binding var selection: Value
    let title: (Value) -> String
    @ObservedObject private var presenter = LiquidMenuPresenter.shared
    @State private var id = UUID()
    @State private var frameBox = FrameBox()
    /// The capsule has grown to the full switcher width.
    @State private var isExpanded = false
    /// The switcher's options are showing (after the growth, before the shrink).
    @State private var showsOptions = false
    @State private var clickMonitor: Any?
    @State private var isCapsulePressed = false
    @State private var isSwitcherPressed = false

    private let height = SettingsControlMetrics.height
    private let inset: CGFloat = 4
    private static var growth: Animation { .spring(response: 0.4, dampingFraction: 0.74) }

    var body: some View {
        ZStack(alignment: .trailing) {
            if showsOptions {
                LiquidSegmentedControl(
                    options: options,
                    selection: Binding(get: { selection }, set: { choose($0) }),
                    title: title,
                    segmentWidth: segmentWidth,
                    height: height,
                    drawsBackground: false,
                    onPressChange: { isSwitcherPressed = $0 }
                )
                .transition(.opacity)
            } else {
                Text(title(selection))
                    .font(.system(size: 13, weight: .medium))
                    .fixedSize()
                    .padding(.horizontal, 16)
                    // The whole capsule takes the click, not just the text.
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .trailing)
                .opacity(isExpanded ? 0 : 1)
                .contentShape(Capsule())
                .gesture(
                    DragGesture(minimumDistance: 0)
                        .onChanged { _ in
                            if !isCapsulePressed { isCapsulePressed = true }
                        }
                        .onEnded { value in
                            isCapsulePressed = false
                            if abs(value.translation.width) < 12, abs(value.translation.height) < 12 {
                                open()
                            }
                        }
                )
                .transition(.opacity)
            }
        }
        // One capsule of glass whose width grows frame by frame (trailing
        // anchored), so it stretches out instead of a panel appearing.
        .modifier(AnimatedFrameWidth(width: isExpanded ? expandedWidth : collapsedWidth))
        .frame(height: height)
        .background {
            Color.clear.clipShelfGlass(in: Capsule())
        }
        .fixedSize()
        // The whole capsule answers a press, closed or open.
        .pressFeedback(isCapsulePressed || isSwitcherPressed)
        .background(FrameReader(box: frameBox))
        .onChange(of: presenter.openID) { openID in
            if openID != id, isExpanded { close() }
        }
        .onDisappear { removeClickMonitor() }
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isButton)
    }

    // MARK: Sizes

    private func textWidth(_ text: String, bold: Bool) -> CGFloat {
        let font = NSFont.systemFont(ofSize: 13, weight: bold ? .semibold : .medium)
        return ceil((text as NSString).size(withAttributes: [.font: font]).width)
    }

    private var collapsedWidth: CGFloat {
        textWidth(title(selection), bold: false) + 32
    }

    private var segmentWidth: CGFloat {
        (options.map { textWidth(title($0), bold: true) }.max() ?? 40) + 28
    }

    private var expandedWidth: CGFloat {
        segmentWidth * CGFloat(options.count) + inset * 2
    }

    // MARK: Opening and closing

    private func open() {
        guard !isExpanded else { return }
        presenter.didOpen(id) { close() }
        withAnimation(Self.growth) { isExpanded = true }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.16) {
            guard isExpanded else { return }
            withAnimation(.easeOut(duration: 0.18)) { showsOptions = true }
        }
        installClickMonitor()
    }

    private func choose(_ value: Value) {
        selection = value
        // Let the lens land on the choice, then fold back.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.42) { close() }
    }

    private func close() {
        guard isExpanded else { return }
        removeClickMonitor()
        presenter.didClose(id)
        withAnimation(.easeOut(duration: 0.12)) { showsOptions = false }
        withAnimation(Self.growth.delay(0.06)) { isExpanded = false }
    }

    /// A click anywhere outside the open switcher folds it back, like a menu.
    private func installClickMonitor() {
        removeClickMonitor()
        clickMonitor = NSEvent.addLocalMonitorForEvents(matching: .leftMouseDown) { event in
            if let window = event.window, let content = window.contentView {
                let point = content.convert(event.locationInWindow, from: nil)
                // The settings page's coordinate space starts at the content
                // view's top-left.
                let flipped = CGPoint(x: point.x, y: content.isFlipped ? point.y : content.bounds.height - point.y)
                if !frameBox.rect.insetBy(dx: -2, dy: -2).contains(flipped) {
                    DispatchQueue.main.async { close() }
                }
            }
            return event
        }
    }

    private func removeClickMonitor() {
        if let clickMonitor {
            NSEvent.removeMonitor(clickMonitor)
            self.clickMonitor = nil
        }
    }
}

/// Lays out at each frame's width while animating (trailing anchored).
private struct AnimatedFrameWidth: ViewModifier, Animatable {
    var width: CGFloat

    var animatableData: CGFloat {
        get { width }
        set { width = newValue }
    }

    func body(content: Content) -> some View {
        content.frame(width: width, alignment: .trailing)
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

/// Which pull-down is open (one at a time), so Esc and closing settings can
/// fold it back.
final class LiquidMenuPresenter: ObservableObject {
    static let shared = LiquidMenuPresenter()

    @Published private(set) var openID: UUID?
    private var closeAction: (() -> Void)?

    var isOpen: Bool { openID != nil }

    func didOpen(_ id: UUID, close: @escaping () -> Void) {
        if openID != id { closeAction?() }
        openID = id
        closeAction = close
    }

    func didClose(_ id: UUID) {
        guard openID == id else { return }
        openID = nil
        closeAction = nil
    }

    /// Folds the open one back.
    func dismiss() {
        closeAction?()
    }

    /// Closes without waiting (e.g. when settings closes).
    func dismissImmediately() {
        closeAction?()
        openID = nil
        closeAction = nil
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
        .buttonStyle(PressFeedbackButtonStyle())
        .foregroundStyle(Color.primary.opacity(0.75))
    }
}

// MARK: - Press feedback

/// The press response shared by every button, as on iOS 26: the control
/// swells a little while pressed and springs back on release. A quick click
/// is held for a moment, so it shows just as clearly as a long press.
struct PressFeedback: ViewModifier {
    let isPressed: Bool
    var scale: CGFloat = PressFeedback.defaultScale

    static let defaultScale: CGFloat = 1.06
    /// The shortest time the pressed look stays, so a click is visible.
    static let minimumHold: TimeInterval = 0.2
    static let pressSpring = Animation.spring(response: 0.22, dampingFraction: 0.6)
    static let releaseSpring = Animation.spring(response: 0.4, dampingFraction: 0.55)

    @Environment(\.isInPressGroup) private var isInPressGroup
    @State private var isShown = false
    @State private var pressedAt: CFTimeInterval = 0
    @State private var pressToken = 0

    func body(content: Content) -> some View {
        scaled(content)
            .onChange(of: isPressed) { pressed in
                update(pressed: pressed)
            }
    }

    /// Inside a group (a glass capsule of several buttons) the whole capsule
    /// swells instead, so the press reads the same everywhere; only there is
    /// the press reported upwards (rows in the list have no group to tell).
    @ViewBuilder
    private func scaled(_ content: Content) -> some View {
        if isInPressGroup {
            content.preference(key: GroupPressKey.self, value: isPressed)
        } else {
            content.scaleEffect(isShown ? scale : 1)
        }
    }

    private func update(pressed: Bool) {
        if pressed {
            pressToken += 1
            pressedAt = CACurrentMediaTime()
            withAnimation(Self.pressSpring) { isShown = true }
        } else {
            let token = pressToken
            let wait = max(0, Self.minimumHold - (CACurrentMediaTime() - pressedAt))
            DispatchQueue.main.asyncAfter(deadline: .now() + wait) {
                // A new press in the meantime keeps it up.
                guard token == pressToken else { return }
                withAnimation(Self.releaseSpring) { isShown = false }
            }
        }
    }
}

/// A press anywhere inside a press group.
private struct GroupPressKey: PreferenceKey {
    static var defaultValue = false
    static func reduce(value: inout Bool, nextValue: () -> Bool) {
        value = value || nextValue()
    }
}

private struct InPressGroupKey: EnvironmentKey {
    static let defaultValue = false
}

extension EnvironmentValues {
    var isInPressGroup: Bool {
        get { self[InPressGroupKey.self] }
        set { self[InPressGroupKey.self] = newValue }
    }
}

/// Several buttons on one capsule: pressing any of them swells the whole
/// capsule with the shared press feedback.
private struct PressGroup: ViewModifier {
    @State private var isPressed = false

    func body(content: Content) -> some View {
        content
            .environment(\.isInPressGroup, true)
            .onPreferenceChange(GroupPressKey.self) { isPressed = $0 }
            .transformPreference(GroupPressKey.self) { $0 = false }
            .pressFeedback(isPressed)
            .environment(\.isInPressGroup, false)
    }
}

extension View {
    /// Makes this capsule swell as a whole when a button on it is pressed.
    func pressGroup() -> some View {
        modifier(PressGroup())
    }

    func pressFeedback(_ isPressed: Bool, scale: CGFloat = PressFeedback.defaultScale) -> some View {
        modifier(PressFeedback(isPressed: isPressed, scale: scale))
    }
}

/// A plain button with the shared press feedback.
struct PressFeedbackButtonStyle: ButtonStyle {
    var scale: CGFloat = PressFeedback.defaultScale

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .pressFeedback(configuration.isPressed, scale: scale)
    }
}
