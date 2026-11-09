import AppKit
import Combine
import Foundation
import ImageIO
import UniformTypeIdentifiers

final class ClipStore: ObservableObject {
    static let shared = ClipStore()

    @Published private(set) var items: [ClipItem] = [] {
        didSet { revision &+= 1 }
    }
    /// Bumps on every history mutation, so views can detect changes in O(1)
    /// instead of comparing the whole item list on each render.
    private(set) var revision = 0
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
        historyWriter = HistoryWriter(destinationURL: storageURL, imageStore: .shared)

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

    /// A new image in a watched folder.
    func addWatchedImage(data: Data, sourceURL: URL, copyToClipboard: Bool) {
        addImage(data, title: sourceURL.lastPathComponent, sourcePath: sourceURL.path, copyToPasteboard: copyToClipboard)
    }

    /// Any other new file in a watched folder: the record points at the file,
    /// and copying it puts the file itself on the clipboard.
    func addWatchedFile(_ url: URL, fingerprint: String?, copyToClipboard: Bool) {
        runOnMain {
            let item = ClipItem(
                kind: .file,
                title: url.lastPathComponent,
                filePaths: [url.path],
                fileFingerprint: fingerprint
            )
            self.add(item)
            if copyToClipboard {
                self.writeToPasteboard(item)
            }
        }
    }

    /// Saves the picture to its own file off the main thread, then adds the
    /// record.
    private func addImage(
        _ data: Data,
        title: String,
        sourcePath: String? = nil,
        createdAt: Date = Date(),
        copyToPasteboard: Bool = false
    ) {
        let id = UUID()
        imageEncodingQueue.async { [weak self] in
            guard let stored = try? ImageStore.shared.store(data, id: id) else {
                NSLog("ClipShelf could not save an image record")
                return
            }
            let item = ClipItem(
                id: id,
                kind: .image,
                title: title,
                imageFile: stored.fileName,
                imageHash: stored.hash,
                sourcePath: sourcePath,
                createdAt: createdAt
            )
            DispatchQueue.main.async {
                guard let self else { return }
                self.add(item)
                if copyToPasteboard {
                    self.writeToPasteboard(item, imageData: data)
                }
            }
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
            let capturedAt = Date()
            switch imageCapture {
            case .png(let data):
                addImage(data, title: "剪贴板图片", createdAt: capturedAt)
            case .tiff(let data):
                imageEncodingQueue.async { [weak self] in
                    guard let png = ImageCaptureEncoder.pngData(fromTIFF: data) else { return }
                    self?.addImage(png, title: "剪贴板图片", createdAt: capturedAt)
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

    /// For a record just added: its picture is already in memory.
    private func writeToPasteboard(_ item: ClipItem, imageData: Data) {
        pasteboard.clearContents()
        pasteboard.writeObjects([Self.imagePasteboardItem(imageData)])
        changeCount = pasteboard.changeCount
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
                pasteboard.writeObjects([Self.imagePasteboardItem(imageData)])
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
            return Self.imagePasteboardItem(imageData)
        }
    }

    /// The picture goes on as its own format (PNG, JPEG, HEIC…); PNG and TIFF
    /// are only promised and converted when a receiving app asks for them, so
    /// copying never decodes on main. Pictures from watched folders can be any
    /// format; they used to be labelled PNG whatever they were.
    private static func imagePasteboardItem(_ data: Data) -> NSPasteboardItem {
        let pasteboardItem = NSPasteboardItem()
        let source = CGImageSourceCreateWithData(data as CFData, nil)
        let typeIdentifier = source.flatMap { CGImageSourceGetType($0) as String? } ?? UTType.png.identifier
        let ownType = NSPasteboard.PasteboardType(typeIdentifier)
        pasteboardItem.setData(data, forType: ownType)
        let promised = [NSPasteboard.PasteboardType.png, .tiff].filter { $0 != ownType }
        if !promised.isEmpty {
            pasteboardItem.setDataProvider(LazyImageProvider(data: data), forTypes: promised)
        }
        return pasteboardItem
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
            // The same file again under another name ("report (1).pdf").
            if let left = lhs.fileFingerprint, let right = rhs.fileFingerprint, left == right {
                return true
            }
            return FileHistoryBatchPlanner.sharesPath(lhs, rhs)
        case .image:
            // The same picture, wherever it came from.
            if let leftHash = lhs.imageHash, let rightHash = rhs.imageHash, leftHash == rightHash {
                return true
            }
            if let leftPath = lhs.sourcePath, let rightPath = rhs.sourcePath {
                return leftPath == rightPath
            }
            return lhs.legacyImageData != nil && lhs.legacyImageData == rhs.legacyImageData
        }
    }

    private func load() {
        do {
            let data = try Data(contentsOf: storageURL)
            items = try JSONDecoder().decode([ClipItem].self, from: data)
            sortItems()
        } catch {
            items = []
            return
        }
        migrateLegacyImages()
    }

    /// Moves pictures that earlier versions kept inside history.json into
    /// their own files, once. The old history.json is kept as a backup first;
    /// a picture that can't be written stays inside the history.
    private func migrateLegacyImages() {
        guard items.contains(where: { $0.legacyImageData != nil }) else { return }
        let backupURL = storageURL.deletingLastPathComponent()
            .appendingPathComponent("history-before-image-files.json")
        if !FileManager.default.fileExists(atPath: backupURL.path) {
            do {
                try FileManager.default.copyItem(at: storageURL, to: backupURL)
            } catch {
                NSLog("ClipShelf kept images inside the history: backup failed: \(error.localizedDescription)")
                return
            }
        }
        for index in items.indices {
            guard let data = items[index].legacyImageData,
                  let stored = try? ImageStore.shared.store(data, id: items[index].id) else { continue }
            items[index].imageFile = stored.fileName
            items[index].imageHash = stored.hash
            items[index].legacyImageData = nil
        }
        save()
        historyWriter.flush()
    }

    private func save() {
        historyWriter.schedule(items, imageFilesInUse: imageFilesInUse())
    }

    /// Pictures of the records and of deletions that can still be undone.
    private func imageFilesInUse() -> Set<String> {
        var fileNames = Set(items.compactMap(\.imageFile))
        for batch in deletionUndoStack.batches {
            for entry in batch {
                if let fileName = entry.item.imageFile { fileNames.insert(fileName) }
            }
        }
        return fileNames
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

private final class LazyImageProvider: NSObject, NSPasteboardItemDataProvider {
    private let data: Data

    init(data: Data) {
        self.data = data
    }

    func pasteboard(_ pasteboard: NSPasteboard?, item: NSPasteboardItem, provideDataForType type: NSPasteboard.PasteboardType) {
        switch type {
        case .tiff:
            guard let image = NSImage(data: data), let tiff = image.tiffRepresentation else { return }
            item.setData(tiff, forType: .tiff)
        case .png:
            // Reads any image format, not only TIFF.
            guard let png = ImageCaptureEncoder.pngData(fromTIFF: data) else { return }
            item.setData(png, forType: .png)
        default:
            return
        }
    }
}
