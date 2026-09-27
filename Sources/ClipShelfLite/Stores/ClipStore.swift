import AppKit
import Combine
import Foundation

final class ClipStore: ObservableObject {
    static let shared = ClipStore()

    @Published private(set) var items: [ClipItem] = []
    @Published var isClipboardHistoryEnabled: Bool {
        didSet {
            AppEnvironment.userDefaults.set(isClipboardHistoryEnabled, forKey: Self.historyEnabledKey)
        }
    }

    let storageURL: URL

    private static let historyEnabledKey = "clipboardHistory.enabled"
    private let pasteboard = NSPasteboard.general
    private let maxItems = 100
    private var changeCount: Int
    private var timer: Timer?

    private init() {
        changeCount = pasteboard.changeCount
        isClipboardHistoryEnabled = AppEnvironment.userDefaults.object(forKey: Self.historyEnabledKey) as? Bool ?? true
        storageURL = AppEnvironment.historyURL

        load()
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

    func paste(_ item: ClipItem) {
        writeToPasteboard(item)
        PasteController.paste()
    }

    func paste(_ items: [ClipItem]) {
        writeToPasteboard(items)
        PasteController.paste()
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
        items.removeAll { $0.id == item.id }
        save()
    }

    func remove(ids: Set<ClipItem.ID>) {
        guard !ids.isEmpty else { return }
        items.removeAll { ids.contains($0.id) }
        save()
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
        items.removeAll()
        save()
    }

    func revealStorage() {
        NSWorkspace.shared.activateFileViewerSelecting([storageURL])
    }

    private func pollPasteboard() {
        guard ClipboardHistoryPolicy.shouldCapture(
            historyEnabled: isClipboardHistoryEnabled,
            observedChangeCount: pasteboard.changeCount,
            previousChangeCount: &changeCount,
            typeNames: pasteboard.types?.map(\.rawValue)
        ) else { return }

        if let fileItem = currentFileItem() {
            add(fileItem)
            return
        }

        if let text = pasteboard.string(forType: .string)?.trimmingCharacters(in: .whitespacesAndNewlines),
           !text.isEmpty {
            add(ClipItem(kind: .text, title: text, text: text))
            return
        }

        if let imageData = currentImageData() {
            add(ClipItem(kind: .image, title: "剪贴板图片", imageData: imageData))
        }
    }

    private func currentFileItem() -> ClipItem? {
        let options: [NSPasteboard.ReadingOptionKey: Any] = [.urlReadingFileURLsOnly: true]
        guard let urls = pasteboard.readObjects(forClasses: [NSURL.self], options: options) as? [URL] else {
            return nil
        }

        let paths = urls
            .filter(\.isFileURL)
            .map(\.path)
            .filter { FileManager.default.fileExists(atPath: $0) }

        guard !paths.isEmpty else { return nil }

        let title = paths.count == 1
            ? URL(fileURLWithPath: paths[0]).lastPathComponent
            : "\(paths.count) 个文件"

        return ClipItem(kind: .file, title: title, filePaths: paths)
    }

    private func currentImageData() -> Data? {
        if let png = pasteboard.data(forType: .png) {
            return png
        }

        if let tiff = pasteboard.data(forType: .tiff),
           let image = NSImage(data: tiff) {
            return pngData(from: image)
        }

        if let image = NSImage(pasteboard: pasteboard) {
            return pngData(from: image)
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

    private func pngData(from image: NSImage) -> Data? {
        guard let tiff = image.tiffRepresentation,
              let bitmap = NSBitmapImageRep(data: tiff) else {
            return nil
        }

        return bitmap.representation(using: .png, properties: [:])
    }

    private func add(_ item: ClipItem) {
        if isDuplicate(items.first, item) {
            return
        }

        items.removeAll { isDuplicate($0, item) }
        items.insert(item, at: 0)
        sortItems()

        if items.count > maxItems {
            trimToMaxItems()
        }

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

    private func trimToMaxItems() {
        while items.count > maxItems {
            if let lastUnpinnedIndex = items.lastIndex(where: { !$0.isPinned }) {
                items.remove(at: lastUnpinnedIndex)
            } else {
                items.removeLast()
            }
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
            return lhs.filePaths == rhs.filePaths
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
        do {
            try FileManager.default.createDirectory(
                at: storageURL.deletingLastPathComponent(),
                withIntermediateDirectories: true
            )
            let data = try JSONEncoder().encode(items)
            try data.write(to: storageURL, options: .atomic)
        } catch {
            NSLog("ClipShelf save failed: \(error.localizedDescription)")
        }
    }

    private func runOnMain(_ action: @escaping () -> Void) {
        if Thread.isMainThread {
            action()
        } else {
            DispatchQueue.main.async(execute: action)
        }
    }
}
