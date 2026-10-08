import Foundation

public enum SyncProtocolV1 {
    public static let version = 1
    public static let maximumFrameBytes = 1_200_000
    public static let maximumTextUTF8Bytes = 1_048_576
    public static let maximumErrorMessageScalars = 256
}

public enum MessageType: String, CaseIterable, Sendable {
    case hello
    case textEvent
    case ack
    case error
}

public enum ErrorCode: String, CaseIterable, Sendable, Error {
    case invalidMessage = "invalid_message"
    case invalidFrame = "invalid_frame"
    case frameTooLarge = "frame_too_large"
    case unsupportedProtocolVersion = "unsupported_protocol_version"
    case unsupportedMessageType = "unsupported_message_type"
    case unsupportedCapability = "unsupported_capability"
    case invalidDeviceID = "invalid_device_id"
    case invalidEventID = "invalid_event_id"
    case invalidSequence = "invalid_sequence"
    case invalidTimestamp = "invalid_timestamp"
    case invalidText = "invalid_text"
    case textTooLarge = "text_too_large"
    case invalidContentHash = "invalid_content_hash"
    case contentHashMismatch = "content_hash_mismatch"
    case sequenceConflict = "sequence_conflict"
}

public struct ProtocolViolation: Error, Equatable, Sendable {
    public let code: ErrorCode
    public let detail: String

    public init(_ code: ErrorCode, _ detail: String = "") {
        self.code = code
        self.detail = detail
    }
}

public struct ProtocolUUID: Hashable, Sendable, CustomStringConvertible {
    public let value: String

    public init(_ value: String) throws {
        guard ProtocolValidation.isCanonicalNonzeroUUID(value) else {
            throw ProtocolViolation(.invalidMessage, "UUID is not canonical and nonzero")
        }
        self.value = value
    }

    init(validated value: String) {
        self.value = value
    }

    public var description: String { value }
}

public struct UTCTimestamp: Hashable, Sendable, CustomStringConvertible {
    public let value: String

    public init(_ value: String) throws {
        guard ProtocolValidation.isValidTimestamp(value) else {
            throw ProtocolViolation(.invalidTimestamp, "Timestamp is not canonical UTC")
        }
        self.value = value
    }

    init(validated value: String) {
        self.value = value
    }

    public var description: String { value }
}

public struct Hello: Equatable, Sendable {
    public let deviceID: ProtocolUUID
    public let deviceName: String
    public let appVersion: String
    public let supportedProtocolVersions: [Int]
    public let capabilities: [String]

    public init(
        deviceID: ProtocolUUID,
        deviceName: String,
        appVersion: String,
        supportedProtocolVersions: [Int],
        capabilities: [String]
    ) {
        self.deviceID = deviceID
        self.deviceName = deviceName
        self.appVersion = appVersion
        self.supportedProtocolVersions = supportedProtocolVersions
        self.capabilities = capabilities
    }

    public var enabledCapabilities: Set<String> {
        capabilities.contains("text") ? ["text"] : []
    }
}

public struct TextPayload: Equatable, Sendable {
    public let text: String

    public init(text: String) {
        self.text = text
    }
}

public struct TextEvent: Equatable, Sendable {
    public let eventID: ProtocolUUID
    public let originDeviceID: ProtocolUUID
    public let sequence: UInt64
    public let capturedAtUTC: UTCTimestamp
    public let contentHash: String
    public let payload: TextPayload

    public init(
        eventID: ProtocolUUID,
        originDeviceID: ProtocolUUID,
        sequence: UInt64,
        capturedAtUTC: UTCTimestamp,
        contentHash: String,
        payload: TextPayload
    ) {
        self.eventID = eventID
        self.originDeviceID = originDeviceID
        self.sequence = sequence
        self.capturedAtUTC = capturedAtUTC
        self.contentHash = contentHash
        self.payload = payload
    }
}

public struct Ack: Equatable, Sendable {
    public let originDeviceID: ProtocolUUID
    public let acceptedThroughSequence: UInt64

    public init(originDeviceID: ProtocolUUID, acceptedThroughSequence: UInt64) {
        self.originDeviceID = originDeviceID
        self.acceptedThroughSequence = acceptedThroughSequence
    }
}

public struct ProtocolErrorMessage: Equatable, Sendable {
    public let code: ErrorCode
    public let relatedMessageID: ProtocolUUID?
    public let message: String?

    public init(code: ErrorCode, relatedMessageID: ProtocolUUID? = nil, message: String? = nil) {
        self.code = code
        self.relatedMessageID = relatedMessageID
        self.message = message
    }
}

public enum MessageBody: Equatable, Sendable {
    case hello(Hello)
    case textEvent(TextEvent)
    case ack(Ack)
    case error(ProtocolErrorMessage)

    public var messageType: MessageType {
        switch self {
        case .hello: .hello
        case .textEvent: .textEvent
        case .ack: .ack
        case .error: .error
        }
    }
}

public struct Envelope: Equatable, Sendable {
    public let protocolVersion: Int
    public let messageID: ProtocolUUID
    public let sentAtUTC: UTCTimestamp
    public let body: MessageBody

    public init(
        protocolVersion: Int = SyncProtocolV1.version,
        messageID: ProtocolUUID,
        sentAtUTC: UTCTimestamp,
        body: MessageBody
    ) {
        self.protocolVersion = protocolVersion
        self.messageID = messageID
        self.sentAtUTC = sentAtUTC
        self.body = body
    }

    public var messageType: MessageType { body.messageType }
}
