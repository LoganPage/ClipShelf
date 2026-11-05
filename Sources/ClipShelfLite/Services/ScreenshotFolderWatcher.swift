import AppKit
import Foundation
import ImageIO

final class ScreenshotFolderWatcher: ObservableObject {
    static let shared = ScreenshotFolderWatcher()
    static let minimumRescanInterval: TimeInterval = 5
    /// How many folders can be watched at once.
    static let maximumFolders = 3

    /// The watched folders, in the order they were added.
    @Published private(set) var folderURLs: [URL] = []
    /// The first watched folder.
    var folderURL: URL? { folderURLs.first }
    var canAddFolder: Bool { folderURLs.count < Self.maximumFolders }
    @Published private(set) var isRunning = false
    @Published private(set) var statusText = "未选择截图文件夹"

    // The single folder of earlier versions, read once to migrate.
    private let bookmarkKey = "screenshotFolder.bookmark"
    private let pathKey = "screenshotFolder.path"
    private let bookmarksKey = "screenshotFolders.bookmarks"
    private let pathsKey = "screenshotFolders.paths"
    private let queue = DispatchQueue(label: "ClipShelf.screenshot-folder", qos: .utility)
    /// One file-system source per watched folder (each closes its own
    /// descriptor when cancelled).
    private var eventSources: [DispatchSourceFileSystemObject] = []
    /// The folders `scan` reads; only touched on `queue`.
    private var scannedURLs: [URL] = []
    private var timer: DispatchSourceTimer?
    private var pending: [URL: PendingFile] = [:]
    private var seen = Set<String>()
    private var startedAt = Date()
    private var isFolderPanelOpen = false
    /// A quick follow-up scan is queued for files still being written.
    private var isFollowUpScheduled = false

    private init() {
        folderURLs = loadFolderURLs()
    }

    func start() {
        stop()
        startedAt = Date()

        let urls = folderURLs
        guard !urls.isEmpty else {
            updateStatus("未选择截图文件夹", running: false)
            return
        }

        let names = urls.map(\.lastPathComponent).joined(separator: "、")
        updateStatus("正在监听：\(names)", running: true)
        for url in urls {
            startEventSource(for: url)
        }
        startTimer()
        queue.async { [weak self] in
            self?.scannedURLs = urls
            self?.scan()
        }
    }

    func stop() {
        timer?.cancel()
        timer = nil
        eventSources.forEach { $0.cancel() }
        eventSources.removeAll()
        queue.async { [weak self] in
            self?.pending.removeAll()
            self?.scannedURLs = []
        }
        updateStatus(folderURLs.isEmpty ? "未选择截图文件夹" : "已暂停", running: false)
    }

    /// Picks a folder to watch: it replaces the folder at `index`, or is
    /// added (replacing the last one when all slots are taken).
    func chooseFolder(attachedTo window: NSWindow? = NSApp.keyWindow, replacing index: Int? = nil) {
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
        panel.directoryURL = index.flatMap { folderURLs.indices.contains($0) ? folderURLs[$0] : nil }
            ?? folderURL ?? systemScreenshotFolder()

        if let window = window ?? NSApp.keyWindow {
            debugFolderPickerLog("presentationMode = sheet")
            panel.beginSheetModal(for: window) { [weak self] response in
                self?.isFolderPanelOpen = false
                debugFolderPickerLog("open panel end")
                let selectedURL = response == .OK ? panel.url : nil
                debugFolderPickerLog("selected folder url exists = \(selectedURL != nil)")
                guard let url = selectedURL else { return }
                self?.applySelectedFolder(url, replacing: index)
            }
        } else {
            debugFolderPickerLog("presentationMode = runModal")
            let response = panel.runModal()
            isFolderPanelOpen = false
            debugFolderPickerLog("open panel end")
            let selectedURL = response == .OK ? panel.url : nil
            debugFolderPickerLog("selected folder url exists = \(selectedURL != nil)")
            guard let url = selectedURL else { return }
            applySelectedFolder(url, replacing: index)
        }
    }

    func applySelectedFolder(_ url: URL, replacing index: Int? = nil, completion: (() -> Void)? = nil) {
        debugFolderPickerLog("background folder processing start")
        queue.async { [weak self] in
            let bookmark = try? url.bookmarkData(
                options: [.withSecurityScope],
                includingResourceValuesForKeys: nil,
                relativeTo: nil
            )

            DispatchQueue.main.async {
                guard let self else { return }
                self.setFolder(url, bookmark: bookmark, replacing: index)
                self.start()
                completion?()
            }
        }
    }

    /// Stops watching the folder at `index`.
    func removeFolder(at index: Int) {
        guard folderURLs.indices.contains(index) else { return }
        var urls = folderURLs
        var bookmarks = savedBookmarks()
        urls.remove(at: index)
        if bookmarks.indices.contains(index) { bookmarks.remove(at: index) }
        saveFolders(urls, bookmarks: bookmarks)
        start()
    }

    func revealFolder(at index: Int = 0) {
        guard folderURLs.indices.contains(index) else { return }
        NSWorkspace.shared.activateFileViewerSelecting([folderURLs[index]])
    }

    private func startEventSource(for folderURL: URL) {
        let fileDescriptor = open(folderURL.path, O_EVTONLY)
        guard fileDescriptor >= 0 else {
            updateStatus("无法监听：\(folderURL.lastPathComponent)", running: false)
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

        source.setCancelHandler {
            close(fileDescriptor)
        }

        eventSources.append(source)
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
        guard !scannedURLs.isEmpty else { return }
        var files: [URL] = []
        for folderURL in scannedURLs {
            guard let contents = try? FileManager.default.contentsOfDirectory(
                at: folderURL,
                includingPropertiesForKeys: [.contentModificationDateKey, .isRegularFileKey],
                options: [.skipsHiddenFiles]
            ) else {
                updateStatus("无法读取：\(folderURL.lastPathComponent)", running: false)
                continue
            }
            files += contents
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
        // A screenshot still settling is checked again right away, not on
        // the next folder event or the 5 s fallback scan: it used to land
        // seconds late, so the clipboard still held the previous record.
        // Give up on files that never settle into a valid image.
        pending = pending.filter { Date().timeIntervalSince($0.value.firstSeenAt) < 10 }
        if !pending.isEmpty, !isFollowUpScheduled {
            isFollowUpScheduled = true
            queue.asyncAfter(deadline: .now() + 0.15) { [weak self] in
                self?.isFollowUpScheduled = false
                self?.scan()
            }
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
              Date().timeIntervalSince(item.firstSeenAt) >= 0.15,
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

    /// Puts `url` at `index`, or adds it (replacing the last folder when
    /// all slots are taken). A folder already watched is not added twice.
    private func setFolder(_ url: URL, bookmark: Data?, replacing index: Int?) {
        var urls = folderURLs
        var bookmarks = savedBookmarks()
        // Bookmarks line up with folders; pad any that are missing.
        while bookmarks.count < urls.count { bookmarks.append(Data()) }
        let standardized = url.standardizedFileURL.path
        if let existing = urls.firstIndex(where: { $0.standardizedFileURL.path == standardized }) {
            // Already watched: only refresh its bookmark.
            if let bookmark { bookmarks[existing] = bookmark }
        } else if let index, urls.indices.contains(index) {
            urls[index] = url
            bookmarks[index] = bookmark ?? Data()
        } else if urls.count < Self.maximumFolders {
            urls.append(url)
            bookmarks.append(bookmark ?? Data())
        } else {
            urls[urls.count - 1] = url
            bookmarks[urls.count - 1] = bookmark ?? Data()
        }
        saveFolders(urls, bookmarks: bookmarks)
    }

    private func saveFolders(_ urls: [URL], bookmarks: [Data]) {
        folderURLs = urls
        let defaults = AppEnvironment.userDefaults
        defaults.set(urls.map(\.path), forKey: pathsKey)
        defaults.set(Array(bookmarks.prefix(urls.count)), forKey: bookmarksKey)
    }

    private func savedBookmarks() -> [Data] {
        AppEnvironment.userDefaults.array(forKey: bookmarksKey) as? [Data] ?? []
    }

    private func resolveBookmark(_ data: Data) -> URL? {
        guard !data.isEmpty else { return nil }
        var isStale = false
        guard let url = try? URL(
            resolvingBookmarkData: data,
            options: [.withSecurityScope],
            relativeTo: nil,
            bookmarkDataIsStale: &isStale
        ) else { return nil }
        _ = url.startAccessingSecurityScopedResource()
        return url
    }

    private func loadFolderURLs() -> [URL] {
        let defaults = AppEnvironment.userDefaults
        if let paths = defaults.stringArray(forKey: pathsKey) {
            let bookmarks = savedBookmarks()
            return paths.enumerated().map { index, path in
                (bookmarks.indices.contains(index) ? resolveBookmark(bookmarks[index]) : nil)
                    ?? URL(fileURLWithPath: path, isDirectory: true)
            }
        }

        // Earlier versions kept a single folder.
        if let data = defaults.data(forKey: bookmarkKey), let url = resolveBookmark(data) {
            saveFolders([url], bookmarks: [data])
            return [url]
        }
        if let path = defaults.string(forKey: pathKey) {
            let url = URL(fileURLWithPath: path, isDirectory: true)
            saveFolders([url], bookmarks: [])
            return [url]
        }

        return systemScreenshotFolder().map { [$0] } ?? []
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
