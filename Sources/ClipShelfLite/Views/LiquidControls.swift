import SwiftUI

// Liquid Glass versions of the standard settings controls. They share the
// same glass capsule and springy, stretchy thumb as the type switcher.

/// An on/off switch: a glass track with a lens-like thumb that stretches as
/// it slides and lands with a soft bounce.
struct LiquidToggle: View {
    @Binding var isOn: Bool
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.isEnabled) private var isEnabled
    @State private var isPressed = false

    private let width: CGFloat = 46
    private let height: CGFloat = 26

    var body: some View {
        ZStack(alignment: isOn ? .trailing : .leading) {
            Capsule()
                .fill(isOn ? Color.accentColor.opacity(colorScheme == .dark ? 0.75 : 0.85) : Color.primary.opacity(0.1))
                .clipShelfGlass(in: Capsule())
            thumb
                .padding(2)
        }
        .frame(width: width, height: height)
        .contentShape(Capsule())
        .opacity(isEnabled ? 1 : 0.45)
        .onTapGesture {
            withAnimation(.spring(response: 0.38, dampingFraction: 0.68)) {
                isOn.toggle()
            }
        }
        .simultaneousGesture(
            DragGesture(minimumDistance: 0)
                .onChanged { _ in
                    if !isPressed {
                        withAnimation(.spring(response: 0.22, dampingFraction: 0.7)) { isPressed = true }
                    }
                }
                .onEnded { _ in
                    withAnimation(.spring(response: 0.3, dampingFraction: 0.6)) { isPressed = false }
                }
        )
        .accessibilityElement()
        .accessibilityAddTraits(.isButton)
        .accessibilityValue(isOn ? "开" : "关")
        .accessibilityAction { isOn.toggle() }
    }

    @ViewBuilder
    private var thumb: some View {
        let size = height - 4
        let shape = Capsule()
        Group {
            if #available(macOS 26.0, *) {
                Color.clear.glassEffect(.clear.interactive(), in: shape)
                    .background(shape.fill(Color.white.opacity(0.92)))
            } else {
                shape.fill(Color.white)
            }
        }
        // Pressing swells the thumb into a wider lens, like the system switch.
        .frame(width: isPressed ? size * 1.4 : size, height: size)
        .shadow(color: .black.opacity(0.18), radius: 2, y: 1)
    }
}

/// A slider with a glass track and a lens thumb that grows while dragged.
struct LiquidSlider: View {
    @Binding var value: Double
    let range: ClosedRange<Double>
    var step: Double = 0
    @Environment(\.colorScheme) private var colorScheme
    @State private var isDragging = false

    private let trackHeight: CGFloat = 6
    private let thumbSize: CGFloat = 22

    var body: some View {
        GeometryReader { proxy in
            let usable = max(proxy.size.width - thumbSize, 1)
            let fraction = CGFloat((value - range.lowerBound) / max(range.upperBound - range.lowerBound, .ulpOfOne))
            let x = usable * min(max(fraction, 0), 1)
            ZStack(alignment: .leading) {
                Capsule()
                    .fill(Color.primary.opacity(0.1))
                    .frame(height: trackHeight)
                    .clipShelfGlass(in: Capsule())
                Capsule()
                    .fill(Color.accentColor.opacity(0.85))
                    .frame(width: x + thumbSize / 2, height: trackHeight)
                thumb
                    .offset(x: x)
            }
            .frame(height: proxy.size.height)
            .contentShape(Rectangle())
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { drag in
                        if !isDragging {
                            withAnimation(.spring(response: 0.25, dampingFraction: 0.7)) { isDragging = true }
                        }
                        let fraction = min(max((drag.location.x - thumbSize / 2) / usable, 0), 1)
                        var newValue = range.lowerBound + Double(fraction) * (range.upperBound - range.lowerBound)
                        if step > 0 {
                            newValue = (newValue / step).rounded() * step
                        }
                        if newValue != value {
                            value = min(max(newValue, range.lowerBound), range.upperBound)
                        }
                    }
                    .onEnded { _ in
                        withAnimation(.spring(response: 0.35, dampingFraction: 0.6)) { isDragging = false }
                    }
            )
        }
        .frame(height: thumbSize + 6)
    }

    @ViewBuilder
    private var thumb: some View {
        let shape = Capsule()
        Group {
            if #available(macOS 26.0, *) {
                Color.clear.glassEffect(.clear.interactive(), in: shape)
                    .background(shape.fill(Color.white.opacity(isDragging ? 0.55 : 0.92)))
            } else {
                shape.fill(Color.white)
            }
        }
        .frame(width: isDragging ? thumbSize * 1.45 : thumbSize, height: isDragging ? thumbSize * 1.1 : thumbSize)
        .shadow(color: .black.opacity(0.2), radius: 2, y: 1)
        .frame(width: thumbSize, height: thumbSize)
    }
}

/// A pull-down menu shown as a glass capsule.
struct LiquidMenu<Value: Hashable>: View {
    let options: [Value]
    @Binding var selection: Value
    let title: (Value) -> String

    var body: some View {
        Menu {
            ForEach(options, id: \.self) { option in
                Button {
                    selection = option
                } label: {
                    if option == selection {
                        Label(title(option), systemImage: "checkmark")
                    } else {
                        Text(title(option))
                    }
                }
            }
        } label: {
            HStack(spacing: 6) {
                Text(title(selection))
                    .font(.system(size: 13, weight: .medium))
                Image(systemName: "chevron.up.chevron.down")
                    .font(.system(size: 10, weight: .semibold))
                    .foregroundStyle(.secondary)
            }
            .padding(.horizontal, 14)
            .frame(height: 30)
            .clipShelfGlass(in: Capsule(), interactive: true)
            .contentShape(Capsule())
        }
        .menuStyle(.button)
        .buttonStyle(.plain)
        .menuIndicator(.hidden)
        .fixedSize()
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
        .frame(height: 30)
        .clipShelfGlass(in: Capsule())
    }

    private func stepButton(_ symbol: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 11, weight: .bold))
                .frame(width: 30, height: 30)
                .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .foregroundStyle(.secondary)
    }
}
