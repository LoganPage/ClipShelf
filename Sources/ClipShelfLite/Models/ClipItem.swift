import Foundation

struct ClipItem: Identifiable, Codable, Hashable {
    enum Kind: String, Codable {
        case text
        case file
        case image
    }

    var id: UUID
    var kind: Kind
    var title: String
    var text: String?
    var filePaths: [String]
    /// The picture's file name in `ImageStore` (image records).
    var imageFile: String?
    /// SHA-256 of the picture, to spot the same image copied twice without
    /// reading both files.
    var imageHash: String?
    /// A picture saved inside history.json by earlier versions; moved into
    /// its own file when the history loads, and never written back.
    var legacyImageData: Data?
    /// Size plus a hash of the start and end of a file from a watched
    /// folder, so the same file downloaded again isn't kept twice.
    var fileFingerprint: String?
    var sourcePath: String?
    var createdAt: Date
    var isPinned: Bool

    enum CodingKeys: String, CodingKey {
        case id
        case kind
        case title
        case text
        case filePaths
        case imageFile
        case imageHash
        case imageData
        case fileFingerprint
        case sourcePath
        case createdAt
        case isPinned
    }

    init(
        id: UUID = UUID(),
        kind: Kind,
        title: String,
        text: String? = nil,
        filePaths: [String] = [],
        imageFile: String? = nil,
        imageHash: String? = nil,
        legacyImageData: Data? = nil,
        fileFingerprint: String? = nil,
        sourcePath: String? = nil,
        createdAt: Date = Date(),
        isPinned: Bool = false
    ) {
        self.id = id
        self.kind = kind
        self.title = title
        self.text = text
        self.filePaths = filePaths
        self.imageFile = imageFile
        self.imageHash = imageHash
        self.legacyImageData = legacyImageData
        self.fileFingerprint = fileFingerprint
        self.sourcePath = sourcePath
        self.createdAt = createdAt
        self.isPinned = isPinned
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decode(UUID.self, forKey: .id)
        kind = try container.decode(Kind.self, forKey: .kind)
        title = try container.decode(String.self, forKey: .title)
        text = try container.decodeIfPresent(String.self, forKey: .text)
        filePaths = try container.decode([String].self, forKey: .filePaths)
        imageFile = try container.decodeIfPresent(String.self, forKey: .imageFile)
        imageHash = try container.decodeIfPresent(String.self, forKey: .imageHash)
        legacyImageData = try container.decodeIfPresent(Data.self, forKey: .imageData)
        fileFingerprint = try container.decodeIfPresent(String.self, forKey: .fileFingerprint)
        sourcePath = try container.decodeIfPresent(String.self, forKey: .sourcePath)
        createdAt = try container.decode(Date.self, forKey: .createdAt)
        isPinned = try container.decodeIfPresent(Bool.self, forKey: .isPinned) ?? false
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(id, forKey: .id)
        try container.encode(kind, forKey: .kind)
        try container.encode(title, forKey: .title)
        try container.encodeIfPresent(text, forKey: .text)
        try container.encode(filePaths, forKey: .filePaths)
        try container.encodeIfPresent(imageFile, forKey: .imageFile)
        try container.encodeIfPresent(imageHash, forKey: .imageHash)
        // Only for a picture that could not be moved into its own file.
        try container.encodeIfPresent(legacyImageData, forKey: .imageData)
        try container.encodeIfPresent(fileFingerprint, forKey: .fileFingerprint)
        try container.encodeIfPresent(sourcePath, forKey: .sourcePath)
        try container.encode(createdAt, forKey: .createdAt)
        try container.encode(isPinned, forKey: .isPinned)
    }

    /// The title as a list row shows it. A text record's title is its whole
    /// text; a row has two lines, so only the start of a long one is laid out.
    var rowTitle: String {
        title.utf8.count > 4_000 ? String(title.prefix(1_000)) : title
    }

    /// Where the picture is on disk.
    var imageURL: URL? {
        imageFile.map { ImageStore.shared.url(for: $0) }
    }

    /// The picture's bytes, read from its file. Reads the disk: use it for
    /// copying and previews, not on every render.
    var imageData: Data? {
        legacyImageData ?? imageFile.flatMap { ImageStore.shared.data(for: $0) }
    }
}
