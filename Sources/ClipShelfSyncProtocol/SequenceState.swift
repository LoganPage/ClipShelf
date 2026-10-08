import Foundation

public struct SequenceAcceptance: Equatable, Sendable {
    public let acceptedThroughSequence: UInt64
    public let imported: Bool
    public let duplicate: Bool
}

public struct SequenceState: Sendable {
    private struct Fingerprint: Equatable, Sendable {
        let eventID: ProtocolUUID
        let originDeviceID: ProtocolUUID
        let sequence: UInt64
        let contentHash: String
    }

    private var eventsByID: [ProtocolUUID: Fingerprint] = [:]
    private var eventsByOriginAndSequence: [ProtocolUUID: [UInt64: Fingerprint]] = [:]
    private var watermarks: [ProtocolUUID: UInt64] = [:]

    public init() {}

    public mutating func accept(_ event: TextEvent) throws -> SequenceAcceptance {
        let fingerprint = Fingerprint(
            eventID: event.eventID,
            originDeviceID: event.originDeviceID,
            sequence: event.sequence,
            contentHash: event.contentHash
        )
        let currentWatermark = watermark(for: event.originDeviceID)

        if let existing = eventsByID[event.eventID] {
            guard existing == fingerprint else { throw ProtocolViolation(.sequenceConflict) }
            return SequenceAcceptance(
                acceptedThroughSequence: currentWatermark,
                imported: false,
                duplicate: true
            )
        }

        if let existing = eventsByOriginAndSequence[event.originDeviceID]?[event.sequence] {
            guard existing == fingerprint else { throw ProtocolViolation(.sequenceConflict) }
            return SequenceAcceptance(
                acceptedThroughSequence: currentWatermark,
                imported: false,
                duplicate: true
            )
        }

        eventsByID[event.eventID] = fingerprint
        eventsByOriginAndSequence[event.originDeviceID, default: [:]][event.sequence] = fingerprint
        advanceWatermark(for: event.originDeviceID)

        return SequenceAcceptance(
            acceptedThroughSequence: watermark(for: event.originDeviceID),
            imported: true,
            duplicate: false
        )
    }

    public func watermark(for originDeviceID: ProtocolUUID) -> UInt64 {
        watermarks[originDeviceID] ?? 0
    }

    public func validateAckClaim(_ claimed: UInt64, for originDeviceID: ProtocolUUID) throws {
        guard claimed <= watermark(for: originDeviceID) else {
            throw ProtocolViolation(.sequenceConflict)
        }
    }

    private mutating func advanceWatermark(for originDeviceID: ProtocolUUID) {
        var watermark = watermarks[originDeviceID] ?? 0
        let events = eventsByOriginAndSequence[originDeviceID] ?? [:]
        while watermark < UInt64.max, events[watermark + 1] != nil {
            watermark += 1
        }
        watermarks[originDeviceID] = watermark
    }
}
