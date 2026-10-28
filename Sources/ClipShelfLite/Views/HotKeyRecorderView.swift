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

    private static let growth = Animation.spring(response: 0.38, dampingFraction: 0.72)

    var body: some View {
        Button {
            toggleRecording()
        } label: {
            Text(isRecording ? "请按快捷键" : configuration.displayText)
                .contentTransition(.opacity)
        }
        // Stays glass while recording, tinted with the accent colour.
        .buttonStyle(LiquidGlassButtonStyle(isActive: isRecording))
        // The capsule itself grows to fit the prompt (and back), springing
        // out from its trailing edge.
        .animation(Self.growth, value: isRecording)
        .animation(Self.growth, value: configuration)
        .onDisappear {
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
