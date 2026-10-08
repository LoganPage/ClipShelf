import Foundation

public struct SyncProtocolCodec: Sendable {
    public init() {}

    public func decode(_ data: Data) throws -> Envelope {
        guard !data.isEmpty, data.count <= SyncProtocolV1.maximumFrameBytes else {
            throw ProtocolViolation(data.count > SyncProtocolV1.maximumFrameBytes ? .frameTooLarge : .invalidMessage)
        }
        guard String(data: data, encoding: .utf8) != nil else {
            throw ProtocolViolation(.invalidMessage, "Message is not UTF-8")
        }
        if data.starts(with: [0xEF, 0xBB, 0xBF]) {
            throw ProtocolViolation(.invalidMessage, "UTF-8 BOM is forbidden")
        }

        let object: Any
        do {
            object = try JSONSerialization.jsonObject(with: data)
        } catch {
            throw ProtocolViolation(.invalidMessage, "Malformed JSON")
        }
        guard let root = object as? [String: Any] else {
            throw ProtocolViolation(.invalidMessage, "Envelope must be an object")
        }

        let version = try protocolVersion(root["protocolVersion"])
        let messageType = try decodeMessageType(root["messageType"])
        let messageID = try uuid(root["messageId"], code: .invalidMessage)
        let sentAtUTC = try timestamp(root["sentAtUtc"])
        guard let bodyObject = root["body"] as? [String: Any] else {
            throw ProtocolViolation(.invalidMessage, "Body must be an object")
        }

        let body: MessageBody
        switch messageType {
        case .hello:
            body = .hello(try decodeHello(bodyObject))
        case .textEvent:
            body = .textEvent(try decodeTextEvent(bodyObject))
        case .ack:
            body = .ack(try decodeAck(bodyObject))
        case .error:
            body = .error(try decodeError(bodyObject))
        }

        return Envelope(
            protocolVersion: version,
            messageID: messageID,
            sentAtUTC: sentAtUTC,
            body: body
        )
    }

    public func encode(_ envelope: Envelope) throws -> Data {
        let object: [String: Any] = [
            "protocolVersion": envelope.protocolVersion,
            "messageType": envelope.messageType.rawValue,
            "messageId": envelope.messageID.value,
            "sentAtUtc": envelope.sentAtUTC.value,
            "body": bodyObject(envelope.body)
        ]
        let data = try JSONSerialization.data(withJSONObject: object, options: [.sortedKeys, .withoutEscapingSlashes])
        let validatedEnvelope = try decode(data)
        guard validatedEnvelope == envelope else {
            throw ProtocolViolation(.invalidMessage, "Encoded message changed envelope semantics")
        }
        return data
    }

    private func protocolVersion(_ value: Any?) throws -> Int {
        guard let value, let version = ProtocolValidation.positiveInt(value) else {
            throw ProtocolViolation(.invalidMessage, "protocolVersion must be an integer")
        }
        guard version == SyncProtocolV1.version else {
            throw ProtocolViolation(.unsupportedProtocolVersion)
        }
        return version
    }

    private func decodeMessageType(_ value: Any?) throws -> MessageType {
        guard let raw = value as? String else {
            throw ProtocolViolation(.invalidMessage, "messageType must be a string")
        }
        guard let type = MessageType(rawValue: raw) else {
            throw ProtocolViolation(.unsupportedMessageType)
        }
        return type
    }

    private func decodeHello(_ body: [String: Any]) throws -> Hello {
        let deviceID = try uuid(body["deviceId"], code: .invalidDeviceID)
        let deviceName = try boundedString(body["deviceName"], minimum: 1, maximum: 128)
        let appVersion = try boundedString(body["appVersion"], minimum: 1, maximum: 64)

        guard let rawVersions = body["supportedProtocolVersions"] as? [Any], !rawVersions.isEmpty else {
            throw ProtocolViolation(.invalidMessage, "supportedProtocolVersions must be nonempty")
        }
        var versions: [Int] = []
        for raw in rawVersions {
            guard let version = ProtocolValidation.positiveInt(raw) else {
                throw ProtocolViolation(.unsupportedProtocolVersion)
            }
            versions.append(version)
        }
        guard versions.contains(SyncProtocolV1.version) else {
            throw ProtocolViolation(.unsupportedProtocolVersion)
        }

        guard let rawCapabilities = body["capabilities"] as? [Any] else {
            throw ProtocolViolation(.invalidMessage, "capabilities must be an array")
        }
        var capabilities: [String] = []
        var seen = Set<String>()
        for raw in rawCapabilities {
            guard let capability = raw as? String,
                  ProtocolValidation.isValidCapability(capability),
                  seen.insert(capability).inserted else {
                throw ProtocolViolation(.unsupportedCapability)
            }
            capabilities.append(capability)
        }

        return Hello(
            deviceID: deviceID,
            deviceName: deviceName,
            appVersion: appVersion,
            supportedProtocolVersions: versions,
            capabilities: capabilities
        )
    }

    private func decodeTextEvent(_ body: [String: Any]) throws -> TextEvent {
        let eventID = try uuid(body["eventId"], code: .invalidEventID)
        let originDeviceID = try uuid(body["originDeviceId"], code: .invalidDeviceID)
        guard let rawSequence = body["sequence"],
              let sequence = ProtocolValidation.uint64(rawSequence), sequence >= 1 else {
            throw ProtocolViolation(.invalidSequence)
        }
        let capturedAtUTC = try timestamp(body["capturedAtUtc"])
        guard let contentHash = body["contentHash"] as? String,
              ProtocolValidation.isValidContentHash(contentHash) else {
            throw ProtocolViolation(.invalidContentHash)
        }
        guard let payload = body["payload"] as? [String: Any],
              let text = payload["text"] as? String,
              !text.isEmpty,
              !ProtocolValidation.isAllWhitespace(text) else {
            throw ProtocolViolation(.invalidText)
        }
        guard text.utf8.count <= SyncProtocolV1.maximumTextUTF8Bytes else {
            throw ProtocolViolation(.textTooLarge)
        }
        guard ContentHasher.matchesText(text, hash: contentHash) else {
            throw ProtocolViolation(.contentHashMismatch)
        }

        return TextEvent(
            eventID: eventID,
            originDeviceID: originDeviceID,
            sequence: sequence,
            capturedAtUTC: capturedAtUTC,
            contentHash: contentHash,
            payload: TextPayload(text: text)
        )
    }

    private func decodeAck(_ body: [String: Any]) throws -> Ack {
        let originDeviceID = try uuid(body["originDeviceId"], code: .invalidDeviceID)
        guard let rawSequence = body["acceptedThroughSequence"],
              let sequence = ProtocolValidation.uint64(rawSequence) else {
            throw ProtocolViolation(.invalidSequence)
        }
        return Ack(originDeviceID: originDeviceID, acceptedThroughSequence: sequence)
    }

    private func decodeError(_ body: [String: Any]) throws -> ProtocolErrorMessage {
        guard let rawCode = body["code"] as? String,
              let code = ErrorCode(rawValue: rawCode) else {
            throw ProtocolViolation(.invalidMessage, "Unknown error code")
        }

        let relatedMessageID: ProtocolUUID?
        if body["relatedMessageId"] == nil || body["relatedMessageId"] is NSNull {
            relatedMessageID = nil
        } else {
            relatedMessageID = try uuid(body["relatedMessageId"], code: .invalidMessage)
        }

        let message: String?
        if body["message"] == nil {
            message = nil
        } else if let raw = body["message"] as? String,
                  ProtocolValidation.scalarCount(raw) <= SyncProtocolV1.maximumErrorMessageScalars {
            message = raw
        } else {
            throw ProtocolViolation(.invalidMessage, "Error message exceeds scalar limit")
        }

        return ProtocolErrorMessage(code: code, relatedMessageID: relatedMessageID, message: message)
    }

    private func uuid(_ value: Any?, code: ErrorCode) throws -> ProtocolUUID {
        guard let raw = value as? String,
              ProtocolValidation.isCanonicalNonzeroUUID(raw) else {
            throw ProtocolViolation(code)
        }
        return ProtocolUUID(validated: raw)
    }

    private func timestamp(_ value: Any?) throws -> UTCTimestamp {
        guard let raw = value as? String,
              ProtocolValidation.isValidTimestamp(raw) else {
            throw ProtocolViolation(.invalidTimestamp)
        }
        return UTCTimestamp(validated: raw)
    }

    private func boundedString(_ value: Any?, minimum: Int, maximum: Int) throws -> String {
        guard let string = value as? String else { throw ProtocolViolation(.invalidMessage) }
        let count = ProtocolValidation.scalarCount(string)
        guard count >= minimum, count <= maximum else { throw ProtocolViolation(.invalidMessage) }
        return string
    }

    private func bodyObject(_ body: MessageBody) -> [String: Any] {
        switch body {
        case .hello(let hello):
            return [
                "deviceId": hello.deviceID.value,
                "deviceName": hello.deviceName,
                "appVersion": hello.appVersion,
                "supportedProtocolVersions": hello.supportedProtocolVersions,
                "capabilities": hello.capabilities
            ]
        case .textEvent(let event):
            return [
                "eventId": event.eventID.value,
                "originDeviceId": event.originDeviceID.value,
                "sequence": NSNumber(value: event.sequence),
                "capturedAtUtc": event.capturedAtUTC.value,
                "contentHash": event.contentHash,
                "payload": ["text": event.payload.text]
            ]
        case .ack(let ack):
            return [
                "originDeviceId": ack.originDeviceID.value,
                "acceptedThroughSequence": NSNumber(value: ack.acceptedThroughSequence)
            ]
        case .error(let error):
            var object: [String: Any] = ["code": error.code.rawValue]
            if let relatedMessageID = error.relatedMessageID {
                object["relatedMessageId"] = relatedMessageID.value
            }
            if let message = error.message {
                object["message"] = message
            }
            return object
        }
    }
}
