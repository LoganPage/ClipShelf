import AppKit
import Combine
import Foundation

final class ClipStore: ObservableObject {
    static let shared = ClipStore()

    @Published private(set) var items: [ClipItem] = []
    @Published private(set) var maxItems: Int
    @Published private(set) var canUndoDeletion = false
    @Published var isClipboardHistoryEnabled: Bool {
        didSet {
            AppEnvironment.userDefaults.set(isClipboardHistoryEnabled, forKey: Self.historyEnabledKey)
        }
    }

    let storageURL: URL

    private static let historyEnabledKey = "clipboardHistory.enabled"
    private let pasteboard: NSPasteboard
    private let historyWriter: HistoryWriter
    private let imageEncodingQueue = DispatchQueue(label: "ClipShelf.image-capture", qos: .userInitiated)
    private var changeCount: Int
    private var timer: Timer?
    private var deletionUndoStack = HistoryDeletionUndoStack()

    private init() {
        let pasteboard = AppEnvironment.pasteboard
        self.pasteboard = pasteboard
        maxItems = HistoryLimitPreferences.value
        changeCount = pasteboard.changeCount
        isClipboardHistoryEnabled = AppEnvironment.userDefaults.object(forKey: Self.historyEnabledKey) as? Bool ?? true
        storageURL = AppEnvironment.historyURL
        historyWriter = HistoryWriter(destinationURL: storageURL)

        load()
        if HistoryTrimmer.trim(&items, maxItems: maxItems) {
            save()
        }
        start()
    }

    func start() {
        guard timer == nil else { return }

        timer = Timer.scheduledTimer(withTimeInterval: 0.45, repeats: true) { [weak self] _ in
            self?.pollPasteboard()
        }
    }

    func copy(_ item: ClipItem) {
        writeToPasteboard(item)
    }

    func copy(_ items: [ClipItem]) {
        writeToPasteboard(items)
    }

    func addScreenshot(data: Data, sourceURL: URL) {
        runOnMain {
            let item = ClipItem(
                kind: .image,
                title: sourceURL.lastPathComponent,
                imageData: data,
                sourcePath: sourceURL.path
            )
            self.add(item)
            self.writeToPasteboard(item)
        }
    }

    func remove(_ item: ClipItem) {
        remove(ids: [item.id])
    }

    func remove(ids: Set<ClipItem.ID>) {
        guard !ids.isEmpty else { return }
        let entries = items.enumerated().compactMap { index, item in
            ids.contains(item.id)
                ? HistoryDeletionEntry(item: item, originalIndex: index)
                : nil
        }
        guard !entries.isEmpty else { return }
        deletionUndoStack.record(entries)
        updateUndoAvailability()
        items.removeAll { ids.contains($0.id) }
        save()
    }

    @discardableResult
    func removeOldUnpinnedItems(retentionDays: Int, now: Date = Date()) -> Int {
        let ids = OldHistoryCleanup.eligibleIDs(
            in: items,
            now: now,
            retentionDays: retentionDays
        )
        remove(ids: ids)
        return ids.count
    }

    func togglePinned(_ item: ClipItem) {
        togglePinned(ids: [item.id])
    }

    func togglePinned(ids: Set<ClipItem.ID>) {
        guard !ids.isEmpty else { return }
        let shouldPin = items.contains { ids.contains($0.id) && !$0.isPinned }
        setPinned(ids: ids, pinned: shouldPin)
    }

    func setPinned(ids: Set<ClipItem.ID>, pinned: Bool) {
        guard !ids.isEmpty else { return }
        for index in items.indices where ids.contains(items[index].id) {
            items[index].isPinned = pinned
        }
        sortItems()
        save()
    }

    func clearHistory() {
        guard !items.isEmpty else { return }
        deletionUndoStack.record(items.enumerated().map { index, item in
            HistoryDeletionEntry(item: item, originalIndex: index)
        })
        updateUndoAvailability()
        items.removeAll()
        save()
    }

    @discardableResult
    func undoLastDeletion() -> [ClipItem] {
        let restored = deletionUndoStack.undo(into: &items, maxItems: maxItems)
        updateUndoAvailability()
        guard !restored.isEmpty else { return [] }
        sortItems()
        save()
        return restored
    }

    @discardableResult
    func injectControlText(_ text: String, additionalTypeName: String?) -> Bool {
        pasteboard.clearContents()
        let pasteboardItem = NSPasteboardItem()
        pasteboardItem.setString(text, forType: .string)
        if let additionalTypeName,
           !additionalTypeName.isEmpty,
           additionalTypeName != NSPasteboard.PasteboardType.string.rawValue {
            pasteboardItem.setString(
                text,
                forType: NSPasteboard.PasteboardType(additionalTypeName)
            )
        }
        return pasteboard.writeObjects([pasteboardItem])
    }

    func setHistoryLimit(_ value: Int) {
        let normalizedValue = HistoryLimitPreferences.save(value, to: AppEnvironment.userDefaults)
        maxItems = normalizedValue
        HistoryTrimmer.trim(&items, maxItems: normalizedValue)
        save()
    }

    func revealStorage() {
        NSWorkspace.shared.activateFileViewerSelecting([storageURL])
    }

    func flushHistory() {
        historyWriter.flush()
    }

    private func pollPasteboard() {
        guard ClipboardHistoryPolicy.shouldCapture(
            historyEnabled: isClipboardHistoryEnabled,
            observedChangeCount: pasteboard.changeCount,
            previousChangeCount: &changeCount,
            typeNames: pasteboard.types?.map(\.rawValue)
        ) else { return }

        if let filePaths = currentFilePaths() {
            addFilePaths(filePaths)
            return
        }

        if let text = pasteboard.string(forType: .string)?.trimmingCharacters(in: .whitespacesAndNewlines),
           !text.isEmpty {
            add(ClipItem(kind: .text, title: text, text: text))
            return
        }

        if let imageCapture = currentImageCapture() {
            switch imageCapture {
            case .png(let data):
                add(ClipItem(kind: .image, title: "剪贴板图片", imageData: data))
            case .tiff(let data):
                let capturedAt = Date()
                imageEncodingQueue.async { [weak self] in
                    guard let png = ImageCaptureEncoder.pngData(fromTIFF: data) else { return }
                    DispatchQueue.main.async {
                        self?.add(ClipItem(
                            kind: .image,
                            title: "剪贴板图片",
                            imageData: png,
                            createdAt: capturedAt
                        ))
                    }
                }
            }
        }
    }

    private func currentFilePaths() -> [String]? {
        let options: [NSPasteboard.ReadingOptionKey: Any] = [.urlReadingFileURLsOnly: true]
        guard let urls = pasteboard.readObjects(forClasses: [NSURL.self], options: options) as? [URL] else {
            return nil
        }

        let paths = urls
            .filter(\.isFileURL)
            .map(\.path)
            .filter { FileManager.default.fileExists(atPath: $0) }

        guard !paths.isEmpty else { return nil }
        return paths
    }

    private func currentImageCapture() -> ImageCapture? {
        if let png = pasteboard.data(forType: .png) {
            return .png(png)
        }

        if let tiff = pasteboard.data(forType: .tiff) {
            return .tiff(tiff)
        }

        if let tiff = NSImage(pasteboard: pasteboard)?.tiffRepresentation {
            return .tiff(tiff)
        }

        return nil
    }

    private func writeToPasteboard(_ item: ClipItem) {
        writeToPasteboard([item])
    }

    private func writeToPasteboard(_ items: [ClipItem]) {
        guard !items.isEmpty else { return }
        if items.count == 1, let item = items.first {
            writeSingleItemToPasteboard(item)
            return
        }

        pasteboard.clearContents()

        if items.allSatisfy({ $0.kind == .text }) {
            pasteboard.setString(combinedText(for: items), forType: .string)
            changeCount = pasteboard.changeCount
            return
        }

        if let urls = fileURLsForMultiCopy(items) {
            pasteboard.writeObjects(urls)
            changeCount = pasteboard.changeCount
            return
        }

        let writableObjects = items.compactMap { pasteboardObject(for: $0) }
        if !writableObjects.isEmpty, pasteboard.writeObjects(writableObjects) {
            changeCount = pasteboard.changeCount
            return
        }

        pasteboard.setString(combinedText(for: items), forType: .string)
        changeCount = pasteboard.changeCount
    }

    private func writeSingleItemToPasteboard(_ item: ClipItem) {
        pasteboard.clearContents()

        switch item.kind {
        case .text:
            pasteboard.setString(item.text ?? item.title, forType: .string)
        case .file:
            let urls = item.filePaths.map { NSURL(fileURLWithPath: $0) }
            pasteboard.writeObjects(urls)
        case .image:
            if let imageData = item.imageData {
                pasteboard.setData(imageData, forType: .png)
                if let image = NSImage(data: imageData), let tiff = image.tiffRepresentation {
                    pasteboard.setData(tiff, forType: .tiff)
                }
            }
        }

        changeCount = pasteboard.changeCount
    }

    private func pasteboardObject(for item: ClipItem) -> NSPasteboardWriting? {
        switch item.kind {
        case .text:
            let pasteboardItem = NSPasteboardItem()
            pasteboardItem.setString(item.text ?? item.title, forType: .string)
            return pasteboardItem
        case .file:
            guard let firstPath = item.filePaths.first else { return nil }
            return NSURL(fileURLWithPath: firstPath)
        case .image:
            guard let imageData = item.imageData else { return nil }
            let pasteboardItem = NSPasteboardItem()
            pasteboardItem.setData(imageData, forType: .png)
            if let image = NSImage(data: imageData), let tiff = image.tiffRepresentation {
                pasteboardItem.setData(tiff, forType: .tiff)
            }
            return pasteboardItem
        }
    }

    private func fileURLsForMultiCopy(_ items: [ClipItem]) -> [NSURL]? {
        var urls: [NSURL] = []

        for item in items {
            switch item.kind {
            case .file:
                let paths = item.filePaths.filter { FileManager.default.fileExists(atPath: $0) }
                guard !paths.isEmpty else { return nil }
                urls.append(contentsOf: paths.map { NSURL(fileURLWithPath: $0) })
            case .image:
                guard let path = item.sourcePath,
                      FileManager.default.fileExists(atPath: path) else {
                    return nil
                }
                urls.append(NSURL(fileURLWithPath: path))
            case .text:
                return nil
            }
        }

        return urls.isEmpty ? nil : urls
    }

    private func combinedText(for items: [ClipItem]) -> String {
        items
            .map { item in
                switch item.kind {
                case .text:
                    return item.text ?? item.title
                case .file:
                    return item.filePaths.isEmpty ? item.title : item.filePaths.joined(separator: "\n")
                case .image:
                    return item.sourcePath ?? item.title
                }
            }
            .filter { !$0.isEmpty }
            .joined(separator: "\n")
    }

    private func add(_ item: ClipItem) {
        if isDuplicate(items.first, item) {
            return
        }

        items.removeAll { isDuplicate($0, item) }
        items.insert(item, at: 0)
        sortItems()

        HistoryTrimmer.trim(&items, maxItems: maxItems)

        save()
    }

    private func addFilePaths(_ paths: [String]) {
        let newItems = FileHistoryBatchPlanner.newItems(
            for: paths,
            existingItems: items
        )
        guard !newItems.isEmpty else { return }

        items.append(contentsOf: newItems)
        sortItems()
        HistoryTrimmer.trim(&items, maxItems: maxItems)
        save()
    }

    private func sortItems() {
        items.sort { first, second in
            if first.isPinned != second.isPinned {
                return first.isPinned && !second.isPinned
            }

            return first.createdAt > second.createdAt
        }
    }

    private func isDuplicate(_ lhs: ClipItem?, _ rhs: ClipItem) -> Bool {
        guard let lhs else { return false }

        if lhs.kind != rhs.kind {
            return false
        }

        switch rhs.kind {
        case .text:
            return lhs.text == rhs.text
        case .file:
            return FileHistoryBatchPlanner.sharesPath(lhs, rhs)
        case .image:
            if let leftPath = lhs.sourcePath, let rightPath = rhs.sourcePath {
                return leftPath == rightPath
            }
            return lhs.imageData == rhs.imageData
        }
    }

    private func load() {
        do {
            let data = try Data(contentsOf: storageURL)
            items = try JSONDecoder().decode([ClipItem].self, from: data)
            sortItems()
        } catch {
            items = []
        }
    }

    private func save() {
        historyWriter.schedule(items)
    }

    private func updateUndoAvailability() {
        canUndoDeletion = deletionUndoStack.canUndo
    }

    private func runOnMain(_ action: @escaping () -> Void) {
        if Thread.isMainThread {
            action()
        } else {
            DispatchQueue.main.async(execute: action)
        }
    }

    private enum ImageCapture {
        case png(Data)
        case tiff(Data)
    }
}
