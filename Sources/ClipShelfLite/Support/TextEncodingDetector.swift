import CoreFoundation
import Foundation

struct DecodedTextContent: Equatable {
    let text: String
    let encodingName: String
    let usedFallback: Bool
}

enum TextEncodingDetector {
    private static let utf8BOM = Data([0xEF, 0xBB, 0xBF])
    private static let utf16LEBOM = Data([0xFF, 0xFE])
    private static let utf16BEBOM = Data([0xFE, 0xFF])
    private static let gb18030 = String.Encoding(
        rawValue: CFStringConvertEncodingToNSStringEncoding(
            CFStringEncoding(0x0632)
        )
    )
    private static let textExtensions: Set<String> = [
        "txt", "md", "markdown", "log", "json", "xml", "csv", "ini", "yaml", "yml",
        "swift", "c", "h", "cpp", "cc", "cxx", "hpp", "m", "mm", "py", "js", "ts",
        "tsx", "jsx", "java", "kt", "kts", "go", "rs", "rb", "php", "sh", "zsh", "bash",
        "fish", "sql", "html", "htm", "css", "scss", "less", "toml", "conf", "config",
        "gradle", "properties"
    ]

    static func isTextFile(_ url: URL) -> Bool {
        textExtensions.contains(url.pathExtension.lowercased())
    }

    static func decode(_ data: Data) -> DecodedTextContent {
        if data.starts(with: utf8BOM),
           let text = String(data: data.dropFirst(utf8BOM.count), encoding: .utf8) {
            return DecodedTextContent(text: text, encodingName: "UTF-8 BOM", usedFallback: false)
        }
        if data.starts(with: utf16LEBOM),
           let text = String(data: data.dropFirst(utf16LEBOM.count), encoding: .utf16LittleEndian) {
            return DecodedTextContent(text: text, encodingName: "UTF-16 LE", usedFallback: false)
        }
        if data.starts(with: utf16BEBOM),
           let text = String(data: data.dropFirst(utf16BEBOM.count), encoding: .utf16BigEndian) {
            return DecodedTextContent(text: text, encodingName: "UTF-16 BE", usedFallback: false)
        }
        if let text = String(data: data, encoding: .utf8) {
            return DecodedTextContent(text: text, encodingName: "UTF-8", usedFallback: false)
        }
        if let encoding = likelyUTF16Encoding(for: data),
           let text = String(data: data, encoding: encoding) {
            let name = encoding == .utf16LittleEndian ? "UTF-16 LE" : "UTF-16 BE"
            return DecodedTextContent(text: text, encodingName: name, usedFallback: false)
        }
        if let text = String(data: data, encoding: gb18030) {
            return DecodedTextContent(text: text, encodingName: "GB18030", usedFallback: false)
        }
        return DecodedTextContent(
            text: String(decoding: data, as: UTF8.self),
            encodingName: "未知编码（已降级显示）",
            usedFallback: true
        )
    }

    private static func likelyUTF16Encoding(for data: Data) -> String.Encoding? {
        guard data.count >= 4 else { return nil }
        let bytes = [UInt8](data.prefix(512))
        let evenZeroes = stride(from: 0, to: bytes.count, by: 2).filter { bytes[$0] == 0 }.count
        let oddZeroes = stride(from: 1, to: bytes.count, by: 2).filter { bytes[$0] == 0 }.count
        let pairCount = max(1, bytes.count / 2)
        if Double(oddZeroes) / Double(pairCount) > 0.3 { return .utf16LittleEndian }
        if Double(evenZeroes) / Double(pairCount) > 0.3 { return .utf16BigEndian }
        return nil
    }
}

struct TextFilePreviewContent {
    let decoded: DecodedTextContent
    let isTruncated: Bool
    let loadedBytes: Int
}

enum TextFilePreviewLoader {
    static let maximumPreviewBytes = 4 * 1_024 * 1_024
    static let chunkSize = 256 * 1_024

    static func load(_ url: URL) async throws -> TextFilePreviewContent {
        try await Task.detached(priority: .userInitiated) {
            let handle = try FileHandle(forReadingFrom: url)
            defer { try? handle.close() }

            var data = Data()
            data.reserveCapacity(maximumPreviewBytes)
            while data.count < maximumPreviewBytes {
                try Task.checkCancellation()
                let remaining = maximumPreviewBytes - data.count
                guard let chunk = try handle.read(upToCount: min(chunkSize, remaining)), !chunk.isEmpty else {
                    break
                }
                data.append(chunk)
            }
            let extra = try handle.read(upToCount: 1)
            return TextFilePreviewContent(
                decoded: TextEncodingDetector.decode(data),
                isTruncated: extra?.isEmpty == false,
                loadedBytes: data.count
            )
        }.value
    }
}
