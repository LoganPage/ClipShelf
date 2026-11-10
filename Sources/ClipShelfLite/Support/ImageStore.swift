import CryptoKit
import Foundation
import ImageIO
import UniformTypeIdentifiers

/// Image records' pictures, one file each in `images/` beside history.json.
/// Kept inside history.json they made every save rewrite every picture and
/// every launch decode them all (62 MB for 22 pictures took ~1.4 s).
final class ImageStore: @unchecked Sendable {
    static let shared = ImageStore(
        directory: AppEnvironment.dataDirectory.appendingPathComponent("images", isDirectory: true)
    )

    struct StoredImage: Equatable {
        let fileName: String
        let hash: String
    }

    let directory: URL

    init(directory: URL) {
        self.directory = directory
    }

    func url(for fileName: String) -> URL {
        directory.appendingPathComponent(fileName, isDirectory: false)
    }

    func data(for fileName: String) -> Data? {
        try? Data(contentsOf: url(for: fileName))
    }

    /// Writes a record's picture. Call off the main thread: it hashes and
    /// writes the whole image.
    func store(_ data: Data, id: UUID) throws -> StoredImage {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let fileName = "\(id.uuidString).\(Self.fileExtension(for: data))"
        try data.write(to: url(for: fileName), options: .atomic)
        return StoredImage(fileName: fileName, hash: Self.hash(data))
    }

    /// Deletes pictures that no record (and no undoable deletion) uses.
    /// Files younger than `minimumAge` stay: one may belong to a record that
    /// is still on its way into the history.
    /// Returns how many unused files were left for being too new.
    @discardableResult
    func removeUnreferenced(keeping fileNames: Set<String>, minimumAge: TimeInterval = 60, now: Date = Date()) -> Int {
        guard let files = try? FileManager.default.contentsOfDirectory(
            at: directory,
            includingPropertiesForKeys: [.creationDateKey],
            options: [.skipsHiddenFiles]
        ) else { return 0 }

        var leftForLater = 0
        for file in files where !fileNames.contains(file.lastPathComponent) {
            let created = (try? file.resourceValues(forKeys: [.creationDateKey]))?.creationDate ?? .distantPast
            guard now.timeIntervalSince(created) >= minimumAge else {
                leftForLater += 1
                continue
            }
            try? FileManager.default.removeItem(at: file)
        }
        return leftForLater
    }

    static func hash(_ data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }

    /// The picture's own format (read from its header), so the file opens
    /// as what it is; PNG when it can't be told.
    static func fileExtension(for data: Data) -> String {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              let typeIdentifier = CGImageSourceGetType(source) as String?,
              let fileExtension = UTType(typeIdentifier)?.preferredFilenameExtension else {
            return "png"
        }
        return fileExtension
    }
}
