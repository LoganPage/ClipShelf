import AppKit
import Foundation
import ImageIO

final class ImageThumbnailCache: @unchecked Sendable {
    private struct Entry {
        let image: CGImage
        var lastAccess: UInt64
    }

    private let queue = DispatchQueue(label: "ClipShelf.image-thumbnails", qos: .userInitiated)
    private let lock = NSLock()
    private var entries = [ClipItem.ID: Entry]()
    private var accessCounter: UInt64 = 0
    private var maximumEntryCount: Int
    private var storedDecodeCount = 0

    init(maximumEntryCount: Int = 8) {
        self.maximumEntryCount = max(0, maximumEntryCount)
    }

    var count: Int {
        lock.withThumbnailLock { entries.count }
    }

    var decodeCount: Int {
        lock.withThumbnailLock { storedDecodeCount }
    }

    func setMaximumEntryCount(_ count: Int) {
        lock.withThumbnailLock {
            maximumEntryCount = max(0, count)
            evictIfNeeded()
        }
    }

    func thumbnail(for id: ClipItem.ID, data: Data) async -> CGImage? {
        await withCheckedContinuation { continuation in
            thumbnail(for: id, data: data) { image in
                continuation.resume(returning: image)
            }
        }
    }

    func thumbnail(for id: ClipItem.ID, data: Data, completion: @escaping (CGImage?) -> Void) {
        if let cached = lock.withThumbnailLock({ cachedImage(for: id) }) {
            completion(cached)
            return
        }

        queue.async { [weak self] in
            guard let self else {
                completion(nil)
                return
            }
            if let cached = self.lock.withThumbnailLock({ self.cachedImage(for: id) }) {
                completion(cached)
                return
            }

            let thumbnail = Self.makeThumbnail(from: data)
            if let thumbnail {
                self.store(thumbnail, for: id)
            }
            completion(thumbnail)
        }
    }

    func thumbnailSynchronouslyForTesting(for id: ClipItem.ID, data: Data) -> CGImage? {
        if let cached = lock.withThumbnailLock({ cachedImage(for: id) }) {
            return cached
        }
        guard let thumbnail = Self.makeThumbnail(from: data) else { return nil }
        store(thumbnail, for: id)
        return thumbnail
    }

    private func store(_ thumbnail: CGImage, for id: ClipItem.ID) {
        lock.withThumbnailLock {
            accessCounter &+= 1
            storedDecodeCount += 1
            entries[id] = Entry(image: thumbnail, lastAccess: accessCounter)
            evictIfNeeded()
        }
    }

    private func cachedImage(for id: ClipItem.ID) -> CGImage? {
        guard var entry = entries[id] else { return nil }
        accessCounter &+= 1
        entry.lastAccess = accessCounter
        entries[id] = entry
        return entry.image
    }

    private func evictIfNeeded() {
        while entries.count > maximumEntryCount,
              let oldestID = entries.min(by: { $0.value.lastAccess < $1.value.lastAccess })?.key {
            entries.removeValue(forKey: oldestID)
        }
    }

    private static func makeThumbnail(from data: Data) -> CGImage? {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              let sourceImage = CGImageSourceCreateThumbnailAtIndex(
                source,
                0,
                [
                    kCGImageSourceCreateThumbnailFromImageAlways: true,
                    kCGImageSourceCreateThumbnailWithTransform: true,
                    kCGImageSourceThumbnailMaxPixelSize: 104
                ] as CFDictionary
              ),
              let context = CGContext(
                data: nil,
                width: 104,
                height: 80,
                bitsPerComponent: 8,
                bytesPerRow: 104 * 4,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
              ) else {
            return nil
        }

        let scale = min(104 / CGFloat(sourceImage.width), 80 / CGFloat(sourceImage.height))
        let width = CGFloat(sourceImage.width) * scale
        let height = CGFloat(sourceImage.height) * scale
        context.interpolationQuality = .high
        context.draw(
            sourceImage,
            in: CGRect(x: (104 - width) / 2, y: (80 - height) / 2, width: width, height: height)
        )
        return context.makeImage()
    }
}

private extension NSLock {
    func withThumbnailLock<T>(_ operation: () throws -> T) rethrows -> T {
        lock()
        defer { unlock() }
        return try operation()
    }
}
