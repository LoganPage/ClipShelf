import AppKit
import Carbon.HIToolbox
import SwiftUI

struct HotKeyRecorderView: View {
    /// How many recorders are listening, so other key handlers stand aside.
    static var activeRecorders = 0

    @Binding var configuration: HotKeyConfiguration
    var save: (HotKeyConfiguration) -> Void = HotKeyDefaults.save
    var pausesGlobalHotKey = true
    @State private var isRecording = false
    @State private var monitor: Any?
    @State private var bounce = false

    private static let growth = Animation.spring(response: 0.38, dampingFraction: 0.72)

    /// The capsule's width for `text` (label plus the style's padding).
    private static func width(of text: String, bold: Bool) -> CGFloat {
        let font = NSFont.systemFont(ofSize: 13.5, weight: bold ? .semibold : .medium)
        let textWidth = (text as NSString).size(withAttributes: [.font: font]).width
        return max(ceil(textWidth) + 34, SettingsControlMetrics.height)
    }

    var body: some View {
        let text = isRecording ? "请按快捷键" : configuration.displayText
        Button {
            toggleRecording()
        } label: {
            Text(text)
                .fixedSize()
                .contentTransition(.opacity)
        }
        // Stays glass while recording, tinted with the accent colour; its
        // width grows frame by frame, so the capsule itself stretches out.
        .buttonStyle(LiquidGlassButtonStyle(isActive: isRecording, width: Self.width(of: text, bold: isRecording)))
        // The capsule itself grows to fit the prompt (and back), springing
        // out from its trailing edge.
        .animation(Self.growth, value: isRecording)
        .animation(Self.growth, value: configuration)
        // A little bounce whenever the shortcut changes (recorded or reset).
        .scaleEffect(bounce ? 1.08 : 1)
        .onChange(of: configuration) { _ in
            withAnimation(.spring(response: 0.18, dampingFraction: 0.6)) { bounce = true }
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.14) {
                withAnimation(.spring(response: 0.36, dampingFraction: 0.5)) { bounce = false }
            }
        }
        .onDisappear {
            stopRecording()
        }
        // Settings is only parked off screen when closed, so it never
        // disappears: stop listening for a shortcut when it closes.
        .onReceive(NotificationCenter.default.publisher(for: SettingsWindowController.willCloseNotification)) { _ in
            stopRecording()
        }
    }

    private func toggleRecording() {
        isRecording ? stopRecording() : startRecording()
    }

    private func startRecording() {
        guard !isRecording else { return }
        isRecording = true
        Self.activeRecorders += 1
        if pausesGlobalHotKey {
            HotKeyManager.shared.unregister()
        }

        monitor = NSEvent.addLocalMonitorForEvents(matching: [.keyDown]) { event in
            handle(event)
            return nil
        }
    }

    private func stopRecording() {
        if isRecording {
            Self.activeRecorders = max(Self.activeRecorders - 1, 0)
        }
        isRecording = false
        if let monitor {
            NSEvent.removeMonitor(monitor)
            self.monitor = nil
        }
        if pausesGlobalHotKey {
            HotKeyManager.shared.register(configuration: configuration)
        }
    }

    private func handle(_ event: NSEvent) {
        if event.keyCode == UInt16(kVK_Escape) {
            stopRecording()
            return
        }

        let modifiers = HotKeyConfiguration.carbonModifiers(from: event.modifierFlags)
        guard modifiers != 0 else { return }

        let newConfiguration = HotKeyConfiguration(
            keyCode: UInt32(event.keyCode),
            modifiers: modifiers
        )
        if pausesGlobalHotKey, newConfiguration.conflictsWithSystemWindowSwitching {
            NSSound.beep()
            stopRecording()
            return
        }

        configuration = newConfiguration
        save(newConfiguration)
        stopRecording()
    }
}
