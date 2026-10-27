import AppKit
import Carbon.HIToolbox
import SwiftUI

struct HotKeyRecorderView: View {
    @Binding var configuration: HotKeyConfiguration
    var save: (HotKeyConfiguration) -> Void = HotKeyDefaults.save
    var pausesGlobalHotKey = true
    @State private var isRecording = false
    @State private var monitor: Any?

    var body: some View {
        Button(isRecording ? "请按快捷键" : configuration.displayText) {
            toggleRecording()
        }
        .clipShelfGlassButtons(prominent: isRecording)
        .onDisappear {
            stopRecording()
        }
    }

    private func toggleRecording() {
        isRecording ? stopRecording() : startRecording()
    }

    private func startRecording() {
        isRecording = true
        if pausesGlobalHotKey {
            HotKeyManager.shared.unregister()
        }

        monitor = NSEvent.addLocalMonitorForEvents(matching: [.keyDown]) { event in
            handle(event)
            return nil
        }
    }

    private func stopRecording() {
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
