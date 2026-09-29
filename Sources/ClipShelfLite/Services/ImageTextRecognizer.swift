import AppKit
import CoreGraphics
import Foundation
@preconcurrency import Vision

struct ImageRecognizedTextRegion: Equatable {
    let text: String
    let boundingBox: CGRect
    let confidence: Float
}

struct ImageTextRecognitionResult {
    let regions: [ImageRecognizedTextRegion]
    let didDownsample: Bool
    let duration: TimeInterval

    var allText: String {
        regions.map(\.text).joined(separator: "\n")
    }
}

enum ImageTextRecognitionRules {
    static let recognitionLanguages = ["zh-Hans", "en-US"]
    static let usesLanguageCorrection = true

    static func aggregate(_ candidates: [ImageRecognizedTextRegion]) -> [ImageRecognizedTextRegion] {
        var bestByKey = [String: ImageRecognizedTextRegion]()

        for candidate in candidates {
            let text = candidate.text.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !text.isEmpty else { continue }
            let normalized = text.folding(options: [.caseInsensitive, .widthInsensitive], locale: .current)
            let box = candidate.boundingBox
            let key = [
                normalized,
                quantized(box.minX),
                quantized(box.minY),
                quantized(box.width),
                quantized(box.height)
            ].joined(separator: "|")
            let normalizedCandidate = ImageRecognizedTextRegion(
                text: text,
                boundingBox: box,
                confidence: candidate.confidence
            )
            if bestByKey[key]?.confidence ?? -1 < candidate.confidence {
                bestByKey[key] = normalizedCandidate
            }
        }

        return bestByKey.values.sorted { lhs, rhs in
            if abs(lhs.boundingBox.midY - rhs.boundingBox.midY) > 0.015 {
                return lhs.boundingBox.midY > rhs.boundingBox.midY
            }
            return lhs.boundingBox.minX < rhs.boundingBox.minX
        }
    }

    private static func quantized(_ value: CGFloat) -> String {
        String(Int((value * 1_000).rounded()))
    }
}

enum ImageTextRecognizerError: LocalizedError {
    case imageUnavailable

    var errorDescription: String? {
        switch self {
        case .imageUnavailable: "无法读取这张图片。"
        }
    }
}

enum ImageTextRecognizer {
    private static let maximumDimension = 3_000

    static func recognize(cgImage: CGImage) async throws -> ImageTextRecognitionResult {
        let startedAt = CFAbsoluteTimeGetCurrent()
        let prepared = prepareImage(cgImage)
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        request.recognitionLanguages = ImageTextRecognitionRules.recognitionLanguages
        request.usesLanguageCorrection = ImageTextRecognitionRules.usesLanguageCorrection

        let observations: [VNRecognizedTextObservation] = try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                DispatchQueue.global(qos: .userInitiated).async {
                    do {
                        let handler = VNImageRequestHandler(cgImage: prepared.image, options: [:])
                        try handler.perform([request])
                        continuation.resume(returning: request.results ?? [])
                    } catch {
                        continuation.resume(throwing: error)
                    }
                }
            }
        } onCancel: {
            request.cancel()
        }

        try Task.checkCancellation()
        let candidates = observations.compactMap { observation -> ImageRecognizedTextRegion? in
            guard let candidate = observation.topCandidates(1).first else { return nil }
            return ImageRecognizedTextRegion(
                text: candidate.string,
                boundingBox: observation.boundingBox,
                confidence: candidate.confidence
            )
        }
        return ImageTextRecognitionResult(
            regions: ImageTextRecognitionRules.aggregate(candidates),
            didDownsample: prepared.didDownsample,
            duration: CFAbsoluteTimeGetCurrent() - startedAt
        )
    }

    private static func prepareImage(_ image: CGImage) -> (image: CGImage, didDownsample: Bool) {
        let largestDimension = max(image.width, image.height)
        guard largestDimension > maximumDimension else {
            return (image, false)
        }

        let scale = CGFloat(maximumDimension) / CGFloat(largestDimension)
        let width = max(1, Int((CGFloat(image.width) * scale).rounded()))
        let height = max(1, Int((CGFloat(image.height) * scale).rounded()))
        guard let context = CGContext(
            data: nil,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else {
            return (image, false)
        }

        context.interpolationQuality = .high
        context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        guard let downsampled = context.makeImage() else {
            return (image, false)
        }
        return (downsampled, true)
    }
}
