import AppKit
import CoreServices
import CryptoKit
import Foundation
import ImageIO

/// What a watched folder takes in, and what happens to it.
struct WatchedFolderRule: Codable, Equatable {
    enum Content: String, Codable, CaseIterable {
        /// Screenshot-named images only (the Desktop holds much else).
        case screenshots
        /// Any image.
        case images
        /// Any file: images become image records, the rest file records.
        case files

        var title: String {
            switch self {
            case .screenshots: "截图"
            case .images: "图片"
            case .files: "所有文件"
            }
        }
    }

    var content: Content
    /// Also put each new record on the clipboard, as screenshots do.
    var copiesToClipboard: Bool
    /// Also take files that land in folders inside this one.
    var includesSubfolders = false

    init(content: Content, copiesToClipboard: Bool, includesSubfolders: Bool = false) {
        self.content = content
        self.copiesToClipboard = copiesToClipboard
        self.includesSubfolders = includesSubfolders
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        content = try container.decode(Content.self, forKey: .content)
        copiesToClipboard = try container.decode(Bool.self, forKey: .copiesToClipboard)
        // Rules saved before subfolders were an option.
        includesSubfolders = try container.decodeIfPresent(Bool.self, forKey: .includesSubfolders) ?? false
    }

    private enum CodingKeys: String, CodingKey {
        case content, copiesToClipboard, includesSubfolders
    }

    /// The screenshot folder keeps the original behaviour; a folder added on
    /// purpose (often Downloads) takes images without taking over the
    /// clipboard.
    static func standard(isScreenshotFolder: Bool) -> WatchedFolderRule {
        isScreenshotFolder
            ? WatchedFolderRule(content: .screenshots, copiesToClipboard: true)
            : WatchedFolderRule(content: .images, copiesToClipboard: false)
    }
}

/// Whether a watched folder can be watched right now.
enum WatchedFolderStatus: Equatable {
    case watching
    /// Deleted, moved, or on a disk that isn't connected.
    case missing
    /// There, but macOS doesn't let ClipShelf read it.
    case unreadable

    var problem: String? {
        switch self {
        case .watching: nil
        case .missing: "找不到这个文件夹：可能已删除、移走，或所在的磁盘没有连接"
        case .unreadable: "无法读取这个文件夹：请在「系统设置 › 隐私与安全性 › 文件与文件夹」里允许 ClipShelf 访问"
        }
    }
}

final class ScreenshotFolderWatcher: ObservableObject {
    static let shared = ScreenshotFolderWatcher()
    /// How often each folder is checked for still being there (a cheap
    /// look at the folder itself, not a listing of its files).
    static let statusCheckInterval: TimeInterval = 10
    /// How many folders can be watched at once.
    static let maximumFolders = 8

    /// The watched folders, in the order they were added.
    @Published private(set) var folderURLs: [URL] = []
    /// The first watched folder.
    var folderURL: URL? { folderURLs.first }
    var canAddFolder: Bool { folderURLs.count < Self.maximumFolders }
    /// Each folder's rule, by path; a folder without one uses the standard rule.
    @Published private(set) var rules: [String: WatchedFolderRule] = [:]
    /// Each folder's status, by path.
    @Published private(set) var statuses: [String: WatchedFolderStatus] = [:]

    // The single folder of earlier versions, read once to migrate.
    private let bookmarkKey = "screenshotFolder.bookmark"
    private let pathKey = "screenshotFolder.path"
    private let bookmarksKey = "screenshotFolders.bookmarks"
    private let pathsKey = "screenshotFolders.paths"
    private let rulesKey = "screenshotFolders.rules"
    private let queue = DispatchQueue(label: "ClipShelf.screenshot-folder", qos: .utility)
    /// One FSEvents stream for all folders: it names each file that changed,
    /// so nothing has to list a whole folder (thousands of files in
    /// Downloads) to find the new one.
    private var stream: FSEventStreamRef?
    private var statusTimer: DispatchSourceTimer?
    /// The watched folders with their rules; only touched on `queue`.
    private var watchedFolders: [WatchedFolder] = []
    private var pending: [URL: PendingFile] = [:]
    private var seen = Set<String>()
    private var startedAt = Date()
    private var isFolderPanelOpen = false
    /// A quick follow-up scan is queued for files still being written.
    private var isFollowUpScheduled = false

    private init() {
        folderURLs = loadFolderURLs()
        rules = loadRules()
    }

    func rule(for url: URL) -> WatchedFolderRule {
        rules[Self.key(for: url)] ?? .standard(isScreenshotFolder: isScreenshotFolder(url))
    }

    func setRule(_ rule: WatchedFolderRule, for url: URL) {
        rules[Self.key(for: url)] = rule
        saveRules()
        let folders = currentWatchedFolders()
        queue.async { [weak self] in
            self?.watchedFolders = folders
        }
    }

    func status(for url: URL) -> WatchedFolderStatus {
        statuses[Self.key(for: url)] ?? .watching
    }

    private func currentWatchedFolders() -> [WatchedFolder] {
        folderURLs.map { url in
            WatchedFolder(url: url, root: Self.realPath(of: url), rule: rule(for: url))
        }
    }

    func isScreenshotFolder(_ url: URL) -> Bool {
        guard let screenshotFolder = systemScreenshotFolder() else { return false }
        return Self.key(for: url) == Self.key(for: screenshotFolder)
    }

    /// Plain path, no resolving: `standardizedFileURL` only shortens
    /// /private/tmp to /tmp while the folder exists, so a deleted folder
    /// lost its status and rule.
    private static func key(for url: URL) -> String {
        let path = url.path
        return path.count > 1 && path.hasSuffix("/") ? String(path.dropLast()) : path
    }

    /// The path as FSEvents reports it. Not `resolvingSymlinksInPath`, which
    /// shortens /private/tmp to /tmp so event paths never matched.
    private static func realPath(of url: URL) -> String {
        guard let resolved = realpath(url.path, nil) else { return url.path }
        defer { free(resolved) }
        return String(cString: resolved)
    }

    func start() {
        stop()
        startedAt = Date()

        let folders = currentWatchedFolders()
        guard !folders.isEmpty else {
            DispatchQueue.main.async { self.statuses = [:] }
            return
        }

        startStream(roots: folders.map(\.root))
        startStatusTimer()
        queue.async { [weak self] in
            guard let self else { return }
            self.watchedFolders = folders
            self.refreshStatuses()
            // Anything that landed just before watching began.
            folders.forEach(self.scan)
        }
    }

    func stop() {
        statusTimer?.cancel()
        statusTimer = nil
        if let stream {
            FSEventStreamStop(stream)
            FSEventStreamInvalidate(stream)
            FSEventStreamRelease(stream)
            self.stream = nil
        }
        queue.async { [weak self] in
            self?.pending.removeAll()
            self?.watchedFolders = []
        }
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
        panel.title = "选择要监听的文件夹"
        panel.message = "之后新放进这个文件夹的图片或文件会自动进入历史，收什么可以在设置里改。"
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
        let removed = urls.remove(at: index)
        if bookmarks.indices.contains(index) { bookmarks.remove(at: index) }
        saveFolders(urls, bookmarks: bookmarks)
        rules.removeValue(forKey: Self.key(for: removed))
        saveRules()
        start()
    }

    func revealFolder(at index: Int = 0) {
        guard folderURLs.indices.contains(index) else { return }
        NSWorkspace.shared.activateFileViewerSelecting([folderURLs[index]])
    }

    private func startStream(roots: [String]) {
        var context = FSEventStreamContext(
            version: 0,
            info: Unmanaged.passUnretained(self).toOpaque(),
            retain: nil,
            release: nil,
            copyDescription: nil
        )
        let callback: FSEventStreamCallback = { _, info, count, eventPaths, eventFlags, _ in
            guard let info else { return }
            let watcher = Unmanaged<ScreenshotFolderWatcher>.fromOpaque(info).takeUnretainedValue()
            let paths = (unsafeBitCast(eventPaths, to: NSArray.self) as? [String]) ?? []
            let flags = Array(UnsafeBufferPointer(start: eventFlags, count: count))
            watcher.handle(paths: paths, flags: flags)
        }
        let createFlags = FSEventStreamCreateFlags(
            kFSEventStreamCreateFlagUseCFTypes
                | kFSEventStreamCreateFlagFileEvents
                | kFSEventStreamCreateFlagNoDefer
                | kFSEventStreamCreateFlagWatchRoot
        )
        guard let stream = FSEventStreamCreate(
            nil,
            callback,
            &context,
            roots as CFArray,
            FSEventStreamEventId(kFSEventStreamEventIdSinceNow),
            0.05,
            createFlags
        ) else {
            NSLog("ClipShelf could not start watching folders")
            return
        }
        FSEventStreamSetDispatchQueue(stream, queue)
        FSEventStreamStart(stream)
        self.stream = stream
    }

    private func startStatusTimer() {
        let timer = DispatchSource.makeTimerSource(queue: queue)
        let interval = DispatchTimeInterval.milliseconds(Int(Self.statusCheckInterval * 1_000))
        timer.schedule(deadline: .now() + interval, repeating: interval)
        timer.setEventHandler { [weak self] in
            self?.refreshStatuses()
        }
        statusTimer = timer
        timer.resume()
    }

    /// On `queue`.
    private func handle(paths: [String], flags: [FSEventStreamEventFlags]) {
        var foldersToScan = Set<String>()
        for (path, flag) in zip(paths, flags) {
            let flag = Int(flag)
            if flag & (kFSEventStreamEventFlagRootChanged | kFSEventStreamEventFlagMount | kFSEventStreamEventFlagUnmount) != 0 {
                refreshStatuses()
                continue
            }
            guard let folder = watchedFolders.first(where: { path == $0.root || path.hasPrefix($0.root + "/") }) else {
                continue
            }
            // Events were dropped or merged: look through the folder itself.
            if flag & (kFSEventStreamEventFlagMustScanSubDirs | kFSEventStreamEventFlagUserDropped | kFSEventStreamEventFlagKernelDropped) != 0 {
                foldersToScan.insert(folder.root)
                continue
            }
            guard flag & kFSEventStreamEventFlagItemIsFile != 0, path != folder.root else { continue }
            let relative = path.dropFirst(folder.root.count + 1)
            guard folder.rule.includesSubfolders || !relative.contains("/") else { continue }
            // Nothing inside hidden folders or app bundles.
            guard !relative.split(separator: "/").contains(where: { $0.hasPrefix(".") }) else { continue }
            consider(URL(fileURLWithPath: path), rule: folder.rule)
        }
        for folder in watchedFolders where foldersToScan.contains(folder.root) {
            scan(folder)
        }
        scheduleFollowUpIfNeeded()
    }

    /// Lists a whole folder. Only when starting and when FSEvents says it
    /// lost track; normally each changed file is named by its event.
    private func scan(_ folder: WatchedFolder) {
        let keys = Self.fileKeys
        let files: [URL]
        if folder.rule.includesSubfolders {
            let enumerator = FileManager.default.enumerator(
                at: folder.url,
                includingPropertiesForKeys: keys,
                options: [.skipsHiddenFiles, .skipsPackageDescendants]
            )
            files = (enumerator?.allObjects as? [URL]) ?? []
        } else {
            files = (try? FileManager.default.contentsOfDirectory(
                at: folder.url,
                includingPropertiesForKeys: keys,
                options: [.skipsHiddenFiles]
            )) ?? []
        }
        for url in files {
            consider(url, rule: folder.rule)
        }
        scheduleFollowUpIfNeeded()
    }

    /// Takes the file in if it is new and the folder's rule wants it.
    private func consider(_ url: URL, rule: WatchedFolderRule) {
        guard !seen.contains(url.path),
              Self.isImportable(url, content: rule.content),
              let values = try? url.resourceValues(forKeys: Set(Self.fileKeys)),
              values.isRegularFile == true,
              let arrivedAt = Self.arrivalDate(values),
              arrivedAt >= startedAt.addingTimeInterval(-3),
              Date().timeIntervalSince(arrivedAt) < 120 else {
            return
        }
        importWhenStable(url, rule: rule)
    }

    /// A file still being written is checked again right away rather than
    /// waiting for another event: writing into a file raises none for the
    /// folder, and a screenshot used to land seconds late. Files that never
    /// settle are given up after 10 s.
    private func scheduleFollowUpIfNeeded() {
        pending = pending.filter { Date().timeIntervalSince($0.value.firstSeenAt) < 10 }
        guard !pending.isEmpty, !isFollowUpScheduled else { return }
        isFollowUpScheduled = true
        queue.asyncAfter(deadline: .now() + 0.15) { [weak self] in
            guard let self else { return }
            self.isFollowUpScheduled = false
            for (url, file) in self.pending {
                self.importWhenStable(url, rule: file.rule)
            }
            self.scheduleFollowUpIfNeeded()
        }
    }

    /// On `queue`.
    private func refreshStatuses() {
        var updated = [String: WatchedFolderStatus]()
        for folder in watchedFolders {
            var isDirectory: ObjCBool = false
            let status: WatchedFolderStatus
            if !FileManager.default.fileExists(atPath: folder.url.path, isDirectory: &isDirectory) || !isDirectory.boolValue {
                status = .missing
            } else if !FileManager.default.isReadableFile(atPath: folder.url.path) {
                status = .unreadable
            } else {
                status = .watching
            }
            updated[Self.key(for: folder.url)] = status
        }
        DispatchQueue.main.async {
            if self.statuses != updated { self.statuses = updated }
        }
    }

    private func importWhenStable(_ url: URL, rule: WatchedFolderRule) {
        guard let attributes = try? FileManager.default.attributesOfItem(atPath: url.path),
              let size = attributes[.size] as? UInt64 else {
            pending.removeValue(forKey: url)
            return
        }

        var item = pending[url] ?? PendingFile(lastSize: size, rule: rule)
        if item.lastSize == size {
            item.stableTicks += 1
        } else {
            item.lastSize = size
            item.stableTicks = 0
        }
        // Kept pending while still empty: writing into a file raises no
        // folder event, so a file first seen at 0 bytes needs the re-check.
        pending[url] = item

        guard size > 0,
              item.stableTicks >= 1,
              Date().timeIntervalSince(item.firstSeenAt) >= 0.15 else {
            return
        }

        if Self.isImage(url) {
            // An image that doesn't decode yet is still being written.
            guard let data = try? Data(contentsOf: url), isValidImageData(data) else { return }
            seen.insert(url.path)
            pending.removeValue(forKey: url)
            ClipStore.shared.addWatchedImage(data: data, sourceURL: url, copyToClipboard: rule.copiesToClipboard)
        } else {
            seen.insert(url.path)
            pending.removeValue(forKey: url)
            ClipStore.shared.addWatchedFile(
                url,
                fingerprint: Self.fingerprint(of: url, size: size),
                copyToClipboard: rule.copiesToClipboard
            )
        }
    }

    /// Tells copies of one file apart from different files without reading
    /// all of a large one: its size and the first and last 64 KB.
    static func fingerprint(of url: URL, size: UInt64) -> String? {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        let chunk = 64 * 1024
        var hasher = SHA256()
        guard let head = try? handle.read(upToCount: chunk) else { return nil }
        hasher.update(data: head)
        if size > UInt64(chunk * 2) {
            try? handle.seek(toOffset: size - UInt64(chunk))
            if let tail = try? handle.read(upToCount: chunk) { hasher.update(data: tail) }
        }
        let digest = hasher.finalize().map { String(format: "%02x", $0) }.joined()
        return "\(size)-\(digest)"
    }

    private static let fileKeys: [URLResourceKey] = [
        .isRegularFileKey, .addedToDirectoryDateKey, .creationDateKey, .contentModificationDateKey,
    ]
    static let imageExtensions: Set<String> = [
        "png", "jpg", "jpeg", "heic", "heif", "tiff", "tif", "gif", "webp", "bmp",
    ]

    /// When the file showed up in the folder. A file copied or moved in keeps
    /// its old modification date, which made it look old and get skipped;
    /// an old image edited in place keeps its arrival date, so it isn't
    /// taken again.
    private static func arrivalDate(_ values: URLResourceValues) -> Date? {
        values.addedToDirectoryDate ?? values.creationDate ?? values.contentModificationDate
    }

    /// Names browsers and downloaders give files still on their way; the
    /// finished file turns up under its own name.
    static let unfinishedExtensions: Set<String> = [
        "crdownload", "download", "part", "partial", "tmp", "opdownload", "filepart", "aria2", "!ut",
    ]

    static func isImage(_ url: URL) -> Bool {
        imageExtensions.contains(url.pathExtension.lowercased())
    }

    static func isImportable(_ url: URL, content: WatchedFolderRule.Content) -> Bool {
        switch content {
        case .screenshots:
            return isImage(url) && hasScreenshotName(url)
        case .images:
            return isImage(url)
        case .files:
            // Not Office's "~$" lock files either.
            return !unfinishedExtensions.contains(url.pathExtension.lowercased())
                && !url.lastPathComponent.hasPrefix("~$")
        }
    }

    func isLikelyScreenshot(_ url: URL) -> Bool {
        Self.isImportable(url, content: .screenshots)
    }

    private static func hasScreenshotName(_ url: URL) -> Bool {
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

    private func loadRules() -> [String: WatchedFolderRule] {
        guard let data = AppEnvironment.userDefaults.data(forKey: rulesKey),
              let rules = try? JSONDecoder().decode([String: WatchedFolderRule].self, from: data) else {
            return [:]
        }
        return rules
    }

    private func saveRules() {
        guard let data = try? JSONEncoder().encode(rules) else { return }
        AppEnvironment.userDefaults.set(data, forKey: rulesKey)
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

    private struct WatchedFolder {
        let url: URL
        /// The folder's real path, as FSEvents reports paths in it.
        let root: String
        let rule: WatchedFolderRule
    }

    private struct PendingFile {
        var lastSize: UInt64
        let rule: WatchedFolderRule
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
