# ClipShelf Sync Protocol v1 (`draft-1`)

This directory is the cross-platform wire contract for the first ClipShelf sync stage. Windows and macOS implementations do not share source code or storage files; compatibility is defined by this document, the Draft 2020-12 schemas, the machine-readable fixtures, and `manifest.json`.

Protocol v1 synchronizes **new text capture events only**. It does not implement transport, discovery, pairing, encryption, history backfill, image/file events, deletion, clearing, undo, pinning, settings, or automatic writes to a remote system clipboard.

## Wire and framing

- One message is one UTF-8 JSON object with no BOM.
- A network transport must prefix every JSON payload with a four-byte unsigned big-endian length.
- The length is the JSON UTF-8 byte count only; the four prefix bytes are excluded.
- Maximum JSON frame: **1,200,000 bytes**.
- Maximum `payload.text`: **1,048,576 UTF-8 bytes**.
- Zero length, an oversized declaration, invalid UTF-8, BOM, malformed JSON, or an incomplete final frame is rejected.
- Multiple frames may be concatenated. A decoder must support receiving any frame across arbitrary input chunks.
- A frame contains exactly one envelope. JSON property order has no meaning.
- Unknown optional properties are accepted and ignored. A typed decoder does not retain them, so canonical re-encoding omits them. Unknown message types and incompatible versions are rejected.

## Envelope

Every message contains:

```json
{
  "protocolVersion": 1,
  "messageType": "textEvent",
  "messageId": "00000000-0000-4000-8000-000000000001",
  "sentAtUtc": "2026-10-07T08:00:00.000Z",
  "body": {}
}
```

UUIDs use lowercase canonical `8-4-4-4-12` form and may not be all zero. Timestamps use invariant UTC `yyyy-MM-dd'T'HH:mm:ss.fff'Z'`. v1 message types are `hello`, `textEvent`, `ack`, and `error`.

## Hello

`hello.body` requires `deviceId`, `deviceName`, `appVersion`, `supportedProtocolVersions`, and `capabilities`.

- `deviceName`: 1–128 Unicode scalar values (JSON Schema code points).
- `appVersion`: 1–64 Unicode scalar values and diagnostic only.
- `supportedProtocolVersions`: non-empty array of integers greater than or equal to 1 and must contain `1` for a v1 session.
- capability syntax: lowercase ASCII `[a-z][a-z0-9._-]{0,31}`.
- capability values must be unique. v1 recognizes only `text`; unknown syntactically valid capabilities are retained through typed decode/re-encode but remain disabled.
- Hello never contains clipboard content, history, or keys.

## Text event

```json
{
  "protocolVersion": 1,
  "messageType": "textEvent",
  "messageId": "00000000-0000-4000-8000-000000000001",
  "sentAtUtc": "2026-10-07T08:00:00.000Z",
  "body": {
    "eventId": "33333333-3333-4333-8333-000000000001",
    "originDeviceId": "11111111-1111-4111-8111-111111111111",
    "sequence": 1,
    "capturedAtUtc": "2026-10-07T08:00:00.000Z",
    "contentHash": "sha256:...",
    "payload": { "text": "example" }
  }
}
```

- `eventId` permanently identifies one capture. It is the primary deduplication key.
- `sequence` is an unsigned 64-bit integer, starts at 1, is persisted per origin device, and strictly increases for new events.
- `capturedAtUtc` is untrusted display/diagnostic metadata. A receiver records its own local `receivedAtUtc`; that field is not transmitted in this event.
- Text preserves exact Unicode scalar content, CR/LF choices, combining characters, and leading/trailing whitespace. No trim or normalization is permitted.
- Empty and all-whitespace text is invalid. Size is counted in UTF-8 bytes.
- Re-copying identical text later creates a different event ID and sequence; a content hash does not replace event identity.

### Content hash

```text
SHA-256(UTF8("text") + 0x00 + UTF8(payload.text))
```

The wire form is `sha256:` followed by 64 lowercase hexadecimal characters. Receivers recompute it from the decoded, unmodified text and compare with a fixed-time comparison or a platform-equivalent safe operation. CRLF/LF, surrounding spaces, and NFC/NFD-distinct text intentionally produce different values.

## ACK and sequence state

ACK body:

```json
{ "originDeviceId": "11111111-1111-4111-8111-111111111111", "acceptedThroughSequence": 42 }
```

The watermark covers only the contiguous, durably persisted sequence for one origin. `0` means none accepted. Duplicate event IDs return the current ACK without re-import. An old event is not re-imported. A different event ID already bound to the same origin/sequence, or the same event ID bound to different sequence/content, returns `sequence_conflict`. Out-of-order events above a gap may be retained, but cannot move the ACK across the gap. Filling the gap advances through all now-contiguous pending events.

## Errors

Stable codes are:

`invalid_message`, `invalid_frame`, `frame_too_large`, `unsupported_protocol_version`, `unsupported_message_type`, `unsupported_capability`, `invalid_device_id`, `invalid_event_id`, `invalid_sequence`, `invalid_timestamp`, `invalid_text`, `text_too_large`, `invalid_content_hash`, `content_hash_mismatch`, `sequence_conflict`.

An error body requires `code` and may contain a non-zero related message UUID and a short log-safe message of at most 256 Unicode scalar values (JSON Schema code points). ASCII, Chinese, and emoji therefore use the same 256-scalar boundary even though their UTF-8 and UTF-16 sizes differ. It must never echo full clipboard content or secrets. Image/file message types are rejected as `unsupported_message_type`; they are never converted to path text.

## Schemas, fixtures, and manifest

- `schema/` contains Draft 2020-12 cross-language schemas.
- Each fixture is a machine-readable descriptor containing its input and expected acceptance/error. Generated near-limit text stores a deterministic character and repeat count rather than a 1 MiB source blob.
- `fixtures/valid/` currently contains 21 fixtures; `fixtures/invalid/` contains 38 fixtures.
- `manifest.json` records every schema/fixture path, SHA-256, protocol limits, status, and validator version. Paths are repository-relative and use `/`.
- The C# validator performs strict equivalent checks without claiming to be a general JSON Schema engine. Its schema-alignment test verifies message types, required fields, JSON types, string/number bounds, uniqueness, `contains`, patterns, zero-UUID exclusions, version, and error enums before running the same fixtures. Byte limits, hash/content equality, all-whitespace rejection, and sequence-state conflicts remain explicitly documented protocol-semantic checks because standard JSON Schema cannot express them portably.
- The reference codec decodes every envelope into one of four strong types and canonically re-encodes known fields. Every message type is exercised by a fixture → typed model → canonical JSON → typed model semantic round trip.
- Fixtures are the authoritative compatibility inputs for independent C# and Swift codecs. Regenerate the manifest whenever a schema or fixture changes.

Run from the Windows repository root:

```powershell
dotnet run --project tools/SyncProtocolContract -c Release -- generate .
dotnet run --project tools/SyncProtocolContract -c Release -- validate . artifacts/sync-protocol-v1-validation
```

Changing an existing v1 fixture's meaning is a protocol break. Future capabilities or a v2 protocol may add behavior but must not reinterpret accepted v1 messages.
