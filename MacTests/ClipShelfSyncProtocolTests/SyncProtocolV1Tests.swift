import Foundation
import XCTest
@testable import ClipShelfSyncProtocol

final class SyncProtocolV1Tests: XCTestCase {
    private struct Manifest: Decodable {
        struct Entry: Decodable {
            let path: String
            let sha256: String
        }

        let protocolVersion: Int
        let status: String
        let schemas: [Entry]
        let fixtures: [Entry]
    }

    private let codec = SyncProtocolCodec()

    private var repositoryRoot: URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
    }

    private var protocolRoot: URL {
        repositoryRoot.appendingPathComponent("sync-protocol/v1", isDirectory: true)
    }

    func testManifestHashesMatchAllSchemasAndFixtures() throws {
        let manifestData = try Data(contentsOf: protocolRoot.appendingPathComponent("manifest.json"))
        let manifest = try JSONDecoder().decode(Manifest.self, from: manifestData)

        XCTAssertEqual(manifest.protocolVersion, 1)
        XCTAssertEqual(manifest.status, "draft-1")
        XCTAssertEqual(manifest.schemas.count, 5)
        XCTAssertEqual(manifest.fixtures.count, 59)

        for entry in manifest.schemas + manifest.fixtures {
            let data = try Data(contentsOf: protocolRoot.appendingPathComponent(entry.path))
            XCTAssertEqual(ContentHasher.sha256Hex(data), entry.sha256, "SHA-256 mismatch: \(entry.path)")
        }

        for entry in manifest.schemas {
            let data = try Data(contentsOf: protocolRoot.appendingPathComponent(entry.path))
            let schema = try XCTUnwrap(try JSONSerialization.jsonObject(with: data) as? [String: Any])
            XCTAssertNotNil(schema["$schema"], "Missing $schema: \(entry.path)")
            XCTAssertNotNil(schema["$id"], "Missing $id: \(entry.path)")
        }
    }

    func testAllFiftyNineFixturesMatchExpectedResults() throws {
        let manifestData = try Data(contentsOf: protocolRoot.appendingPathComponent("manifest.json"))
        let manifest = try JSONDecoder().decode(Manifest.self, from: manifestData)
        XCTAssertEqual(manifest.fixtures.count, 59)

        var matched = 0
        for entry in manifest.fixtures {
            let data = try Data(contentsOf: protocolRoot.appendingPathComponent(entry.path))
            let fixture = try XCTUnwrap(try JSONSerialization.jsonObject(with: data) as? [String: Any])
            try validateFixture(fixture, path: entry.path)
            matched += 1
        }
        XCTAssertEqual(matched, 59)
    }

    func testCanonicalRoundTripPreservesUnknownCapabilityButDoesNotEnableIt() throws {
        let fixture = try fixture(named: "fixtures/valid/hello-unknown-capability.json")
        let envelope = try codec.decode(try messageData(from: fixture))
        guard case .hello(let hello) = envelope.body else {
            return XCTFail("Expected hello")
        }

        XCTAssertEqual(hello.capabilities, ["text", "future-note"])
        XCTAssertEqual(hello.enabledCapabilities, ["text"])
        let roundTripped = try codec.decode(codec.encode(envelope))
        XCTAssertEqual(roundTripped, envelope)
    }

    func testUnknownOptionalFieldsAreIgnoredAndOmittedByCanonicalEncoding() throws {
        let fixture = try fixture(named: "fixtures/valid/unknown-optional-field.json")
        let envelope = try codec.decode(try messageData(from: fixture))
        let encoded = try codec.encode(envelope)
        let object = try XCTUnwrap(try JSONSerialization.jsonObject(with: encoded) as? [String: Any])
        XCTAssertNil(object["futureEnvelopeField"])
        let body = try XCTUnwrap(object["body"] as? [String: Any])
        XCTAssertNil(body["futureBodyField"])
        XCTAssertEqual(try codec.decode(encoded), envelope)
    }

    func testErrorMessageUnicodeScalarBoundariesForASCIIChineseAndEmoji() throws {
        for scalar in ["a", "\u{4E2D}", "\u{1F600}"] {
            let valid = try makeErrorEnvelope(message: String(repeating: scalar, count: 256))
            XCTAssertEqual(try codec.decode(codec.encode(valid)), valid)

            var object = try XCTUnwrap(
                try JSONSerialization.jsonObject(with: codec.encode(valid)) as? [String: Any]
            )
            var body = try XCTUnwrap(object["body"] as? [String: Any])
            body["message"] = String(repeating: scalar, count: 257)
            object["body"] = body
            XCTAssertEqual(violationCode { _ = try self.codec.decode(try self.jsonData(object)) }, .invalidMessage)
        }
    }

    func testContentHashPreservesWhitespaceLineEndingsAndUnicodeComposition() {
        XCTAssertNotEqual(ContentHasher.textHash("line\nnext"), ContentHasher.textHash("line\r\nnext"))
        XCTAssertNotEqual(ContentHasher.textHash("text"), ContentHasher.textHash(" text "))
        XCTAssertNotEqual(ContentHasher.textHash("\u{00E9}"), ContentHasher.textHash("e\u{0301}"))
        XCTAssertTrue(ContentHasher.matchesText(" text ", hash: ContentHasher.textHash(" text ")))
    }

    func testFramingSupportsByteChunksConsecutiveFramesAndTruncation() throws {
        let first = try messageData(from: fixture(named: "fixtures/valid/hello-minimal.json"))
        let second = try messageData(from: fixture(named: "fixtures/valid/ack-watermark.json"))
        let stream = try FrameEncoder.frame(first) + FrameEncoder.frame(second)
        var decoder = IncrementalFrameDecoder()
        var frames: [Data] = []
        for byte in stream {
            frames.append(contentsOf: try decoder.append(Data([byte])))
        }
        try decoder.finish()
        XCTAssertEqual(frames, [first, second])
        XCTAssertNoThrow(try frames.forEach { _ = try codec.decode($0) })

        var truncated = IncrementalFrameDecoder()
        _ = try truncated.append(Data(stream.dropLast()))
        XCTAssertEqual(violationCode { try truncated.finish() }, .invalidFrame)
    }

    func testFrameSizeAndUInt64Boundaries() throws {
        XCTAssertEqual(
            violationCode { _ = try FrameEncoder.frame(Data(repeating: 0x61, count: SyncProtocolV1.maximumFrameBytes + 1)) },
            .frameTooLarge
        )

        let origin = try ProtocolUUID("11111111-1111-4111-8111-111111111111")
        let envelope = Envelope(
            messageID: try ProtocolUUID("00000000-0000-4000-8000-000000000001"),
            sentAtUTC: try UTCTimestamp("2026-10-07T08:00:00.000Z"),
            body: .ack(Ack(originDeviceID: origin, acceptedThroughSequence: UInt64.max))
        )
        XCTAssertEqual(try codec.decode(codec.encode(envelope)), envelope)
    }

    private func validateFixture(_ fixture: [String: Any], path: String) throws {
        let id = try XCTUnwrap(fixture["id"] as? String)
        let kind = try XCTUnwrap(fixture["kind"] as? String)
        let expected = try XCTUnwrap(fixture["expected"] as? [String: Any])
        let accepted = try XCTUnwrap(expected["accepted"] as? Bool)

        let actualCode: ErrorCode?
        switch kind {
        case "json":
            actualCode = violationCode {
                let envelope = try codec.decode(try messageData(from: fixture))
                let expectedType = expected["messageType"] as? String
                XCTAssertEqual(envelope.messageType.rawValue, expectedType, id)
                let canonical = try codec.encode(envelope)
                XCTAssertEqual(try codec.decode(canonical), envelope, id)
            }
        case "rawJson":
            actualCode = violationCode {
                let encoded = try XCTUnwrap(fixture["rawUtf8Base64"] as? String)
                let data = try XCTUnwrap(Data(base64Encoded: encoded))
                _ = try codec.decode(data)
            }
        case "framing":
            actualCode = violationCode { try validateFramingFixture(fixture, expected: expected, id: id) }
        case "rawFrame":
            actualCode = violationCode { try validateRawFrameFixture(fixture) }
        case "state":
            actualCode = violationCode { try validateStateFixture(fixture, expected: expected, id: id) }
        case "stateConflict":
            actualCode = violationCode { try validateStateConflictFixture(fixture) }
        case "invalidAckClaim":
            actualCode = violationCode { try validateInvalidAckClaimFixture(fixture, expected: expected) }
        default:
            return XCTFail("Unknown fixture kind \(kind): \(path)")
        }

        if accepted {
            XCTAssertNil(actualCode, "Fixture rejected: \(id) (\(path))")
        } else {
            let expectedCode = ErrorCode(rawValue: try XCTUnwrap(expected["errorCode"] as? String))
            XCTAssertEqual(actualCode, expectedCode, "Wrong error code: \(id) (\(path))")
        }
    }

    private func validateFramingFixture(
        _ fixture: [String: Any],
        expected: [String: Any],
        id: String
    ) throws {
        let messages = try XCTUnwrap(fixture["messages"] as? [[String: Any]])
        let chunkSizes = try XCTUnwrap(fixture["chunkSizes"] as? [Int])
        var stream = Data()
        for message in messages {
            let payload = try jsonData(message)
            _ = try codec.decode(payload)
            stream.append(try FrameEncoder.frame(payload))
        }

        var decoder = IncrementalFrameDecoder()
        var output: [Data] = []
        var offset = 0
        var chunkIndex = 0
        while offset < stream.count {
            let size = min(chunkSizes[chunkIndex % chunkSizes.count], stream.count - offset)
            output.append(contentsOf: try decoder.append(stream.subdata(in: offset..<(offset + size))))
            offset += size
            chunkIndex += 1
        }
        try decoder.finish()
        XCTAssertEqual(output.count, expected["messageCount"] as? Int, id)
        try output.forEach { _ = try codec.decode($0) }
    }

    private func validateRawFrameFixture(_ fixture: [String: Any]) throws {
        let encoded = try XCTUnwrap(fixture["bytesBase64"] as? String)
        let bytes = try XCTUnwrap(Data(base64Encoded: encoded))
        var decoder = IncrementalFrameDecoder()
        let frames = try decoder.append(bytes)
        try frames.forEach { _ = try codec.decode($0) }
        try decoder.finish()
    }

    private func validateStateFixture(
        _ fixture: [String: Any],
        expected: [String: Any],
        id: String
    ) throws {
        let operations = try XCTUnwrap(fixture["operations"] as? [[String: Any]])
        var state = SequenceState()
        var finalAck: UInt64 = 0
        for operation in operations {
            let message = try XCTUnwrap(operation["message"] as? [String: Any])
            let event = try textEvent(from: message)
            let result = try state.accept(event)
            finalAck = result.acceptedThroughSequence
            XCTAssertEqual(result.acceptedThroughSequence, uint64(operation["expectedAck"]), id)
            XCTAssertEqual(result.imported, operation["imported"] as? Bool, id)
            XCTAssertEqual(result.duplicate, operation["duplicate"] as? Bool, id)
        }
        XCTAssertEqual(finalAck, uint64(expected["finalAck"]), id)
    }

    private func validateStateConflictFixture(_ fixture: [String: Any]) throws {
        let operations = try XCTUnwrap(fixture["operations"] as? [[String: Any]])
        var state = SequenceState()
        for operation in operations {
            _ = try state.accept(try textEvent(from: operation))
        }
    }

    private func validateInvalidAckClaimFixture(
        _ fixture: [String: Any],
        expected: [String: Any]
    ) throws {
        let operations = try XCTUnwrap(fixture["operations"] as? [[String: Any]])
        var state = SequenceState()
        var origin: ProtocolUUID?
        for operation in operations {
            let event = try textEvent(from: operation)
            origin = event.originDeviceID
            _ = try state.accept(event)
        }
        let unwrappedOrigin = try XCTUnwrap(origin)
        XCTAssertEqual(state.watermark(for: unwrappedOrigin), uint64(expected["actualAck"]))
        try state.validateAckClaim(try XCTUnwrap(uint64(fixture["claimedAck"])), for: unwrappedOrigin)
    }

    private func textEvent(from object: [String: Any]) throws -> TextEvent {
        let envelope = try codec.decode(try jsonData(object))
        guard case .textEvent(let event) = envelope.body else {
            throw ProtocolViolation(.invalidMessage, "Expected textEvent")
        }
        return event
    }

    private func messageData(from fixture: [String: Any]) throws -> Data {
        var message = try XCTUnwrap(fixture["message"] as? [String: Any])
        if let generated = fixture["generatedText"] as? [String: Any] {
            let character = try XCTUnwrap(generated["character"] as? String)
            let repeatCount = try XCTUnwrap(generated["repeatCount"] as? Int)
            var body = try XCTUnwrap(message["body"] as? [String: Any])
            var payload = try XCTUnwrap(body["payload"] as? [String: Any])
            payload["text"] = String(repeating: character, count: repeatCount)
            body["payload"] = payload
            message["body"] = body
        }
        return try jsonData(message)
    }

    private func fixture(named path: String) throws -> [String: Any] {
        let data = try Data(contentsOf: protocolRoot.appendingPathComponent(path))
        return try XCTUnwrap(try JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    private func jsonData(_ object: Any) throws -> Data {
        try JSONSerialization.data(withJSONObject: object, options: [.sortedKeys, .withoutEscapingSlashes])
    }

    private func uint64(_ value: Any?) -> UInt64? {
        guard let number = value as? NSNumber else { return nil }
        return UInt64(number.stringValue)
    }

    private func violationCode(_ work: () throws -> Void) -> ErrorCode? {
        do {
            try work()
            return nil
        } catch let violation as ProtocolViolation {
            return violation.code
        } catch {
            XCTFail("Unexpected error: \(error)")
            return nil
        }
    }

    private func makeErrorEnvelope(message: String) throws -> Envelope {
        Envelope(
            messageID: try ProtocolUUID("00000000-0000-4000-8000-000000000001"),
            sentAtUTC: try UTCTimestamp("2026-10-07T08:00:00.000Z"),
            body: .error(ProtocolErrorMessage(code: .invalidMessage, message: message))
        )
    }
}
