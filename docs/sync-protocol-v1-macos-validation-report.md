# ClipShelf Sync Protocol v1 macOS Validation Report

Date: 2026-10-08 (Asia/Shanghai)

## Result

The independent Swift codec is compatible with the corrected, byte-stable Sync Protocol v1 authority.

- Authoritative commit: `cb21f62b2cd620be48e77b9892564664919bdf07`
- Manifest entries: **64/64 SHA-256 matches**
- Schema hashes: **5/5**
- Fixture hashes: **59/59**
- Fixture semantic outcomes: **59/59**
- Swift protocol tests: **8/8 passed, 0 failures**
- Full package test command: **passed**
- Release build: **passed, 0 warnings, 0 errors**

The previous fixture-hash blocker is resolved by the new frozen upstream commit. No manifest entry, fixture hash, fixture expectation, or integrity assertion was modified locally to obtain this result.

## Source And Baseline

- Authoritative repository: `https://github.com/LoganPage/ClipShelf.git`
- Authoritative branch: `codex/sync-protocol-v1-lf-fixtures`
- Required and verified commit: `cb21f62b2cd620be48e77b9892564664919bdf07`
- Local branch: `codex/macos-sync-protocol-v1-codec`
- Local HEAD during validation: `0fa9d95318d2d628fdbecbb0ee7653a17c0ebab6`
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

The uncommitted `ClipShelfSyncProtocol` library remains independent from ClipShelf product models and runtime state. It provides:

- Strong models for Envelope, Hello, TextEvent, Ack, and Error.
- Strict UTF-8 JSON decoding and known-field canonical re-encoding.
- UUID, UTC millisecond timestamp, UInt64, Unicode scalar, capability, text-size, and stable error-code validation.
- Preservation of syntactically valid unknown capabilities without enabling them.
- Ignoring unknown optional fields and omitting them during canonical re-encoding.
- CryptoKit SHA-256 over the protocol-defined original text bytes without trimming or Unicode normalization.
- Four-byte unsigned big-endian incremental framing for arbitrary chunks, consecutive frames, truncation, UTF-8 validation, and size limits.
- An in-memory sequence/ACK reference state machine for duplicates, gaps, contiguous watermarks, and conflicts.

No persistent synchronization state file was added.

## Test Results

Protocol-only test command, run from a clean `/tmp` scratch path:

```text
swift test --disable-sandbox --scratch-path /tmp/clipshelf-macos-sync-protocol.gNQF9a --filter SyncProtocolV1Tests

Executed 8 tests, with 0 failures (0 unexpected)
exit=0
```

The eight passing tests include manifest integrity, all 59 fixture outcomes, typed canonical round trips, Unicode scalar boundaries, exact content hashing, framing edge cases, unknown capabilities, unknown optional fields, UInt64 limits, and sequence/ACK behavior.

Full package test command, run from a second clean `/tmp` scratch path:

```text
swift test --disable-sandbox --scratch-path /tmp/clipshelf-macos-sync-full.WMugfS

Test Suite 'All tests' passed.
Executed 8 tests, with 0 failures (0 unexpected)
Existing ClipShelf self-test checks: all reported PASS
exit=0
```

Fixture semantics inside the test run were **59/59**, including acceptance of every valid fixture and rejection of every invalid fixture with its declared stable error code.

Release build command, also using a clean `/tmp` scratch path:

```text
swift build --disable-sandbox -c release --scratch-path /tmp/clipshelf-macos-sync-release.65eHp7

Build complete! (18.80 seconds)
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

## Product Isolation

`git diff --name-only -- Sources/ClipShelfLite` is empty. This work does not modify or connect to:

- `ClipStore` or `ClipItem`
- UI
- current `history.json` format
- user history or preferences
- clipboard capture
- networking or device discovery
- pairing, authentication, encryption, replay protection, or synchronization settings

No data under `~/Library/Application Support/ClipShelf` was modified or deleted. No installation, packaging, commit, push, pull request, merge, or release was performed.

## Compatibility Conclusion

The macOS Swift reference implementation passes the corrected frozen v1 package without weakening integrity checks: **64/64 manifest hashes, 59/59 fixture semantics, 8/8 protocol tests, and a clean Release build with 0 warnings and 0 errors**. Product runtime integration remains intentionally out of scope.
