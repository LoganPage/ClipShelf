import CryptoKit
import Foundation

public enum ContentHasher {
    public static func textHash(_ text: String) -> String {
        var data = Data("text".utf8)
        data.append(0)
        data.append(contentsOf: text.utf8)
        return "sha256:" + sha256Hex(data)
    }

    public static func sha256Hex(_ data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }

    public static func matchesText(_ text: String, hash: String) -> Bool {
        constantTimeEqual(Array(textHash(text).utf8), Array(hash.utf8))
    }

    private static func constantTimeEqual(_ lhs: [UInt8], _ rhs: [UInt8]) -> Bool {
        let count = max(lhs.count, rhs.count)
        var difference = UInt8(truncatingIfNeeded: lhs.count ^ rhs.count)
        for index in 0..<count {
            let left = index < lhs.count ? lhs[index] : 0
            let right = index < rhs.count ? rhs[index] : 0
            difference |= left ^ right
        }
        return difference == 0
    }
}
