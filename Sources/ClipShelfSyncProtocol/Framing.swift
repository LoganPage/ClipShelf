import Foundation

public enum FrameEncoder {
    public static func frame(_ payload: Data) throws -> Data {
        guard !payload.isEmpty else { throw ProtocolViolation(.invalidFrame) }
        guard payload.count <= SyncProtocolV1.maximumFrameBytes else {
            throw ProtocolViolation(.frameTooLarge)
        }
        var length = UInt32(payload.count).bigEndian
        var framed = withUnsafeBytes(of: &length) { Data($0) }
        framed.append(payload)
        return framed
    }
}

public struct IncrementalFrameDecoder: Sendable {
    private var buffer = Data()

    public init() {}

    public mutating func append(_ chunk: Data) throws -> [Data] {
        buffer.append(chunk)
        var frames: [Data] = []

        while buffer.count >= 4 {
            let length = buffer.prefix(4).reduce(UInt32(0)) { ($0 << 8) | UInt32($1) }
            guard length > 0 else {
                buffer.removeAll(keepingCapacity: false)
                throw ProtocolViolation(.invalidFrame)
            }
            guard length <= UInt32(SyncProtocolV1.maximumFrameBytes) else {
                buffer.removeAll(keepingCapacity: false)
                throw ProtocolViolation(.frameTooLarge)
            }

            let total = 4 + Int(length)
            guard buffer.count >= total else { break }
            let payloadStart = buffer.index(buffer.startIndex, offsetBy: 4)
            let payloadEnd = buffer.index(buffer.startIndex, offsetBy: total)
            let payload = Data(buffer[payloadStart..<payloadEnd])
            buffer = Data(buffer.dropFirst(total))
            guard String(data: payload, encoding: .utf8) != nil else {
                buffer.removeAll(keepingCapacity: false)
                throw ProtocolViolation(.invalidFrame)
            }
            frames.append(payload)
        }
        return frames
    }

    public mutating func finish() throws {
        guard buffer.isEmpty else {
            buffer.removeAll(keepingCapacity: false)
            throw ProtocolViolation(.invalidFrame)
        }
    }
}
