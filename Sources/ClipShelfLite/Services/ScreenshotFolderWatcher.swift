import AppKit
import Foundation
import ImageIO

final class ScreenshotFolderWatcher: ObservableObject {
    static let shared = ScreenshotFolderWatcher()
    static let minimumRescanInterval: TimeInterval = 5

    @Published private(set) var folderURL: URL?
    @Published private(set) var isRunning = false
    @Published private(set) var statusText = "未选择截图文件夹"

    private let bookmarkKey = "screenshotFolder.bookmark"
    private let pathKey = "screenshotFolder.path"
    private let queue = DispatchQueue(label: "ClipShelf.screenshot-folder", qos: .utility)
    private var eventSource: DispatchSourceFileSystemObject?
    private var fileDescriptor: CInt = -1
    private var timer: DispatchSourceTimer?
    private var pending: [URL: PendingFile] = [:]
    private var seen = Set<String>()
    private var startedAt = Date()
    private var isFolderPanelOpen = false

    private init() {
        folderURL = loadFolderURL()
    }

    func start() {
        stop()
        startedAt = Date()

        guard let folderURL else {
            updateStatus("未选择截图文件夹", running: false)
            return
        }

        updateStatus("正在监听：\(folderURL.lastPathComponent)", running: true)
        startEventSource(for: folderURL)
        startTimer()
        queue.async { [weak self] in
            self?.scan()
        }
    }

    func stop() {
        timer?.cancel()
        timer = nil
        eventSource?.cancel()
        eventSource = nil
        pending.removeAll()
        updateStatus(folderURL == nil ? "未选择截图文件夹" : "已暂停", running: false)
    }

    func chooseFolder(attachedTo window: NSWindow? = NSApp.keyWindow) {
        debugFolderPickerLog("open panel start")
        guard !isFolderPanelOpen else {
            debugFolderPickerLog("open panel skipped because one is already open")
            return
        }
        isFolderPanelOpen = true

        let panel = NSOpenPanel()
        panel.title = "选择截图保存文件夹"
        panel.message = "把 macOS 截图设置里的保存位置选成同一个文件夹。"
        panel.prompt = "使用这个文件夹"
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = false
        panel.directoryURL = folderURL ?? systemScreenshotFolder()

        if let window = window ?? NSApp.keyWindow {
            debugFolderPickerLog("presentationMode = sheet")
            panel.beginSheetModal(for: window) { [weak self] response in
                self?.isFolderPanelOpen = false
                debugFolderPickerLog("open panel end")
                let selectedURL = response == .OK ? panel.url : nil
                debugFolderPickerLog("selected folder url exists = \(selectedURL != nil)")
                guard let url = selectedURL else { return }
                self?.applySelectedFolder(url)
            }
        } else {
            debugFolderPickerLog("presentationMode = runModal")
            let response = panel.runModal()
            isFolderPanelOpen = false
            debugFolderPickerLog("open panel end")
            let selectedURL = response == .OK ? panel.url : nil
            debugFolderPickerLog("selected folder url exists = \(selectedURL != nil)")
            guard let url = selectedURL else { return }
            applySelectedFolder(url)
        }
    }

    func applySelectedFolder(_ url: URL, completion: (() -> Void)? = nil) {
        debugFolderPickerLog("background folder processing start")
        queue.async { [weak self] in
            let bookmark = try? url.bookmarkData(
                options: [.withSecurityScope],
                includingResourceValuesForKeys: nil,
                relativeTo: nil
            )

            DispatchQueue.main.async {
                guard let self else { return }
                self.saveFolder(url, bookmark: bookmark)
                self.start()
                completion?()
            }
        }
    }

    func revealFolder() {
        guard let folderURL else { return }
        NSWorkspace.shared.activateFileViewerSelecting([folderURL])
    }

    private func startEventSource(for folderURL: URL) {
        fileDescriptor = open(folderURL.path, O_EVTONLY)
        guard fileDescriptor >= 0 else {
            updateStatus("无法监听这个文件夹", running: false)
            return
        }

        let source = DispatchSource.makeFileSystemObjectSource(
            fileDescriptor: fileDescriptor,
            eventMask: [.write, .extend, .attrib, .rename],
            queue: queue
        )

        source.setEventHandler { [weak self] in
            self?.scan()
        }

        source.setCancelHandler { [weak self] in
            guard let self else { return }
            if self.fileDescriptor >= 0 {
                close(self.fileDescriptor)
                self.fileDescriptor = -1
            }
        }

        eventSource = source
        source.resume()
    }

    private func startTimer() {
        let timer = DispatchSource.makeTimerSource(queue: queue)
        let interval = DispatchTimeInterval.milliseconds(Int(Self.minimumRescanInterval * 1_000))
        timer.schedule(deadline: .now() + interval, repeating: interval)
        timer.setEventHandler { [weak self] in
            self?.scan()
        }
        self.timer = timer
        timer.resume()
    }

    private func scan() {
        guard let folderURL else { return }
        guard let files = try? FileManager.default.contentsOfDirectory(
            at: folderURL,
            includingPropertiesForKeys: [.contentModificationDateKey, .isRegularFileKey],
            options: [.skipsHiddenFiles]
        ) else {
            DispatchQueue.main.async {
                self.updateStatus("无法读取截图文件夹", running: false)
            }
            return
        }

        let candidates = files.compactMap { url -> (url: URL, modifiedAt: Date)? in
            guard isLikelyScreenshot(url),
                  !seen.contains(url.path),
                  let values = try? url.resourceValues(forKeys: [.contentModificationDateKey, .isRegularFileKey]),
                  values.isRegularFile == true,
                  let modifiedAt = values.contentModificationDate,
                  modifiedAt >= startedAt.addingTimeInterval(-3),
                  Date().timeIntervalSince(modifiedAt) < 120 else {
                return nil
            }

            return (url, modifiedAt)
        }
        .sorted { $0.modifiedAt < $1.modifiedAt }

        for candidate in candidates {
            importWhenStable(candidate.url)
        }
    }

    private func importWhenStable(_ url: URL) {
        guard let attributes = try? FileManager.default.attributesOfItem(atPath: url.path),
              let size = attributes[.size] as? UInt64,
              size > 0 else {
            return
        }

        var item = pending[url] ?? PendingFile(lastSize: size)
        if item.lastSize == size {
            item.stableTicks += 1
        } else {
            item.lastSize = size
            item.stableTicks = 0
        }
        pending[url] = item

        guard item.stableTicks >= 1,
              Date().timeIntervalSince(item.firstSeenAt) >= 0.2,
              let data = try? Data(contentsOf: url),
              isValidImageData(data) else {
            return
        }

        seen.insert(url.path)
        pending.removeValue(forKey: url)
        ClipStore.shared.addScreenshot(data: data, sourceURL: url)
    }

    func isLikelyScreenshot(_ url: URL) -> Bool {
        let ext = url.pathExtension.lowercased()
        guard ["png", "jpg", "jpeg", "heic", "tiff"].contains(ext) else { return false }

        let name = url.lastPathComponent.lowercased()
        return name.hasPrefix("截屏")
            || name.hasPrefix("屏幕快照")
            || name.hasPrefix("screenshot")
            || name.hasPrefix("screen shot")
    }

    private func isValidImageData(_ data: Data) -> Bool {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil) else {
            return false
        }

        return CGImageSourceGetCount(source) > 0
    }

    private func saveFolder(_ url: URL, bookmark: Data? = nil) {
        folderURL = url
        AppEnvironment.userDefaults.set(url.path, forKey: pathKey)

        if let data = bookmark {
            AppEnvironment.userDefaults.set(data, forKey: bookmarkKey)
        }
    }

    private func loadFolderURL() -> URL? {
        if let data = AppEnvironment.userDefaults.data(forKey: bookmarkKey) {
            var isStale = false
            if let url = try? URL(
                resolvingBookmarkData: data,
                options: [.withSecurityScope],
                relativeTo: nil,
                bookmarkDataIsStale: &isStale
            ) {
                _ = url.startAccessingSecurityScopedResource()
                if isStale {
                    saveFolder(url)
                }
                return url
            }
        }

        if let path = AppEnvironment.userDefaults.string(forKey: pathKey) {
            return URL(fileURLWithPath: path, isDirectory: true)
        }

        return systemScreenshotFolder()
    }

    private func systemScreenshotFolder() -> URL? {
        if let location = UserDefaults(suiteName: "com.apple.screencapture")?.string(forKey: "location"),
           !location.isEmpty {
            return URL(fileURLWithPath: NSString(string: location).expandingTildeInPath, isDirectory: true)
        }

        return FileManager.default.urls(for: .desktopDirectory, in: .userDomainMask).first
    }

    private func updateStatus(_ text: String, running: Bool) {
        DispatchQueue.main.async {
            self.statusText = text
            self.isRunning = running
        }
    }

    private struct PendingFile {
        var lastSize: UInt64
        var stableTicks = 0
        var firstSeenAt = Date()
    }
}

func debugFolderPickerLog(_ message: String) {
#if DEBUG
    let timestamp = String(format: "%.6f", CFAbsoluteTimeGetCurrent())
    print("ClipShelf folder picker \(message) \(timestamp)")
#endif
}
