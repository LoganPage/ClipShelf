import Foundation

enum FileTypeIconCategory: String, CaseIterable {
    case pdf
    case spreadsheet
    case word
    case presentation
    case archive
    case folder
    case generic

    var symbolName: String {
        switch self {
        case .pdf: "doc.richtext"
        case .spreadsheet: "tablecells"
        case .word: "doc.text"
        case .presentation: "rectangle.on.rectangle"
        case .archive: "archivebox"
        case .folder: "folder"
        case .generic: "doc"
        }
    }
}

enum FileTypeIcon {
    private static let spreadsheetExtensions: Set<String> = ["xls", "xlsx", "xlsm", "csv", "ods", "numbers"]
    private static let wordExtensions: Set<String> = ["doc", "docx", "rtf", "odt", "pages"]
    private static let presentationExtensions: Set<String> = ["ppt", "pptx", "odp", "key"]
    private static let archiveExtensions: Set<String> = ["zip", "rar", "7z", "tar", "gz", "bz2", "xz", "dmg"]

    static func category(forPath path: String?, isDirectory: Bool = false) -> FileTypeIconCategory {
        guard let path, !path.isEmpty else { return .generic }
        if isDirectory { return .folder }

        let pathExtension = URL(fileURLWithPath: path).pathExtension.lowercased()
        if pathExtension == "pdf" { return .pdf }
        if spreadsheetExtensions.contains(pathExtension) { return .spreadsheet }
        if wordExtensions.contains(pathExtension) { return .word }
        if presentationExtensions.contains(pathExtension) { return .presentation }
        if archiveExtensions.contains(pathExtension) { return .archive }
        return .generic
    }
}

final class FileTypeIconDirectoryCache: @unchecked Sendable {
    static let shared = FileTypeIconDirectoryCache()

    private let lock = NSLock()
    private var values = [String: Bool]()

    func cachedValue(for path: String) -> Bool? {
        lock.lock()
        defer { lock.unlock() }
        return values[path]
    }

    func resolve(
        path: String,
        loader: (URL) -> Bool = FileTypeIconDirectoryCache.readIsDirectory
    ) -> Bool {
        lock.lock()
        defer { lock.unlock() }

        if let cached = values[path] {
            return cached
        }

        let value = loader(URL(fileURLWithPath: path))
        values[path] = value
        return value
    }

    private static func readIsDirectory(_ url: URL) -> Bool {
        (try? url.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true
    }
}

enum FileTypeIconResolver {
    static func initialCategory(for path: String?) -> FileTypeIconCategory {
        guard let path, !path.isEmpty else { return .generic }
        let isDirectory = FileTypeIconDirectoryCache.shared.cachedValue(for: path) ?? false
        return FileTypeIcon.category(forPath: path, isDirectory: isDirectory)
    }

    static func resolvedCategory(for path: String?) async -> FileTypeIconCategory {
        guard let path, !path.isEmpty else { return .generic }
        let isDirectory = await Task.detached(priority: .utility) {
            FileTypeIconDirectoryCache.shared.resolve(path: path)
        }.value
        return FileTypeIcon.category(forPath: path, isDirectory: isDirectory)
    }
}
