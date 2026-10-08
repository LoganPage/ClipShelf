# ClipShelf Sync Protocol v1 macOS Validation Report

Date: 2026-10-08 (Asia/Shanghai)

## Result

The independent Swift codec is compatible with the corrected, byte-stable Sync Protocol v1 authority.

- Authoritative commit: `cb21f62b2cd620be48e77b9892564664919bdf07`
- Manifest entries: **64/64 SHA-256 matches**
- Schema hashes: **5/5**
- Fixture hashes: **59/59**
- Fixture semantic outcomes: **59/59**
- Swift protocol tests: **12/12 passed, 0 failures**
- Full package test command: **passed**
- Release build: **passed, 0 warnings, 0 errors**

The previous fixture-hash blocker is resolved by the new frozen upstream commit. No manifest entry, fixture hash, fixture expectation, or integrity assertion was modified locally to obtain this result.

## Source And Baseline

- Authoritative repository: `https://github.com/LoganPage/ClipShelf.git`
- Authoritative branch: `codex/sync-protocol-v1-lf-fixtures`
- Required and verified commit: `cb21f62b2cd620be48e77b9892564664919bdf07`
- Local branch: `codex/macos-sync-protocol-v1-codec`
- Local HEAD during outbound validation: `21379bd6e9f70afe48e3f1f16a138ba9c49375c0`
- Local remote: `origin https://github.com/Applebook743/ClipShelf.git`

The fixed commit was fetched directly and `FETCH_HEAD` was verified to equal the required full SHA. No Windows branch history was merged, rebased, or cherry-picked. Only these authoritative inputs were imported from that commit:

- `.gitattributes`
- `docs/sync-protocol-v1.md`
- `sync-protocol/v1/`

Existing modified and untracked files were preserved.

## Authoritative Input Update

Compared with the previous frozen input, the protocol document and fixture payload semantics are unchanged. The corrected authority adds the scoped LF rule:

```text
sync-protocol/v1/**/*.json text eol=lf
```

Its manifest now authenticates the actual LF Git blobs. Independent validation of the imported files produced:

```text
authoritative_commit=cb21f62b2cd620be48e77b9892564664919bdf07
schemas=5 schema_hash_matches=5
fixtures=59 fixture_hash_matches=59
manifest_entries=64 hash_matches=64 hash_failures=0
protocol_json_files=65 bom_or_line_ending_failures=0
```

This check hashes the bytes in each imported schema and fixture directly, independently of the Swift test implementation.

## Swift Implementation

The `ClipShelfSyncProtocol` library remains independent from ClipShelf product models and runtime state. It provides:

- Strong models for Envelope, Hello, TextEvent, Ack, and Error.
- Strict UTF-8 JSON decoding and known-field canonical re-encoding.
- UUID, UTC millisecond timestamp, UInt64, Unicode scalar, capability, text-size, and stable error-code validation.
- Preservation of syntactically valid unknown capabilities without enabling them.
- Ignoring unknown optional fields and omitting them during canonical re-encoding.
- CryptoKit SHA-256 over the protocol-defined original text bytes without trimming or Unicode normalization.
- Four-byte unsigned big-endian incremental framing for arbitrary chunks, consecutive frames, truncation, UTF-8 validation, and size limits.
- An in-memory sequence/ACK reference state machine for duplicates, gaps, contiguous watermarks, and conflicts.
- Outbound validation that canonically encodes, reuses the complete inbound decoder rules, and requires the decoded result to remain semantically equal to the public model before any bytes are returned.

No persistent synchronization state file was added.

## Test Results

Protocol-only test command, run from a clean `/tmp` scratch path:

```text
swift test --disable-sandbox --scratch-path /tmp/clipshelf-outbound-protocol.1bRo58 --filter SyncProtocolV1Tests

Executed 12 tests, with 0 failures (0 unexpected)
exit=0
```

The twelve passing tests include manifest integrity, all 59 fixture outcomes, typed canonical round trips, Unicode scalar boundaries, exact content hashing, framing edge cases, unknown capabilities, unknown optional fields, UInt64 limits, and sequence/ACK behavior.

Four dedicated outbound test groups additionally verify:

- Valid Hello, TextEvent, Ack, and Error envelopes encode and decode to semantically identical strong models.
- Hello rejects an empty `deviceName`, absence of protocol version 1, and duplicate capabilities.
- TextEvent rejects sequence zero, a mismatched content hash, all-whitespace text, and text exceeding the UTF-8 byte limit.
- Error rejects 257 Unicode scalars for ASCII, Chinese, and Emoji input.

Every rejection asserts the same stable error code produced by the existing inbound decoder. No separate outbound rule table was introduced.

Full package test command, run from a second clean `/tmp` scratch path:

```text
swift test --disable-sandbox --scratch-path /tmp/clipshelf-outbound-full.g6sj5V

Test Suite 'All tests' passed.
Executed 12 tests, with 0 failures (0 unexpected)
Existing ClipShelf self-test checks: all reported PASS
exit=0
```

Fixture semantics inside the test run were **59/59**, including acceptance of every valid fixture and rejection of every invalid fixture with its declared stable error code.

Release build command, also using a clean `/tmp` scratch path:

```text
swift build --disable-sandbox -c release --scratch-path /tmp/clipshelf-outbound-release.BB0cX2

Build complete! (18.76 seconds)
warnings=0
errors=0
exit=0
```

## Files Added Or Modified For The Protocol Work

- Modified: `Package.swift`
- Added from authority: `.gitattributes`
- Added: `Sources/ClipShelfSyncProtocol/Codec.swift`
- Added: `Sources/ClipShelfSyncProtocol/Framing.swift`
- Added: `Sources/ClipShelfSyncProtocol/Hashing.swift`
- Added: `Sources/ClipShelfSyncProtocol/Models.swift`
- Added: `Sources/ClipShelfSyncProtocol/SequenceState.swift`
- Added: `Sources/ClipShelfSyncProtocol/Validation.swift`
- Added: `MacTests/ClipShelfSyncProtocolTests/SyncProtocolV1Tests.swift`
- Imported from authority: `docs/sync-protocol-v1.md`
- Imported from authority: `sync-protocol/v1/`
- Updated: `docs/sync-protocol-v1-macos-validation-report.md`

The outbound-hardening follow-up modifies only `Sources/ClipShelfSyncProtocol/Codec.swift`, `MacTests/ClipShelfSyncProtocolTests/SyncProtocolV1Tests.swift`, and this report. Schema, manifest, fixtures, and protocol semantics remain unchanged.

## Product Isolation

`git diff --name-only -- Sources/ClipShelfLite` is empty. This work does not modify or connect to:

- `ClipStore` or `ClipItem`
- UI
- current `history.json` format
- user history or preferences
- clipboard capture
- networking or device discovery
- pairing, authentication, encryption, replay protection, or synchronization settings

No data under `~/Library/Application Support/ClipShelf` was modified or deleted. No installation, packaging, new commit, push, pull request, merge, or release was performed during the outbound-hardening follow-up.

## Compatibility Conclusion

The macOS Swift reference implementation passes the corrected frozen v1 package without weakening integrity checks: **64/64 manifest hashes, 59/59 fixture semantics, 12/12 protocol tests, and a clean Release build with 0 warnings and 0 errors**. Public models in invalid protocol states are rejected by `encode`, while valid envelopes retain identical strong-model semantics. Product runtime integration remains intentionally out of scope.
