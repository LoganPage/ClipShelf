# Sync Protocol v1 LF / Manifest Hash Fix Report

Date: 2026-10-08 (Asia/Shanghai)

## Result

The Sync Protocol v1 generated JSON is now byte-stable across Windows, macOS, and Git:

- Protocol JSON line-ending policy: UTF-8 without BOM, LF only, exactly one final LF.
- Fixture protocol semantics changed: **0**.
- Regenerated fixture tracked-file changes: **0**.
- Regenerated manifest changes: timestamp plus **59** fixture hashes.
- Schema hashes changed: **0**.
- Protocol validation: **70/70 passed; 0 failed**.
- Temporary Git-index manifest verification: **64/64 hashes matched**.
- Temporary Git-index JSON byte verification: **65/65 files passed** BOM and line-ending checks.
- SyncProtocolContract Release build: **passed, 0 warnings, 0 errors**.
- Windows WPF application Release build: **not runnable to completion on this macOS host**; 0 warnings, 1 host-toolchain error because the macOS .NET SDK does not ship `Microsoft.NET.Sdk.WindowsDesktop`.

No merge, pull request, release, package, or product installation was performed.

## Repository And Baseline

- Repository: `https://github.com/LoganPage/ClipShelf.git`
- Fixed starting commit: `ad8c812e68700a7a8cb7317d90f70d6cd10d83e7`
- Verified starting HEAD: `ad8c812e68700a7a8cb7317d90f70d6cd10d83e7`
- Local branch: `codex/sync-protocol-v1-lf-fixtures`
- Independent Windows checkout: `/Users/Zhuanz/Documents/Codex/2026-05-16/clipshelf-windows-sync-protocol-v1`

The existing macOS checkout at `/Users/Zhuanz/Documents/Codex/2026-05-16/macos-windows` was not modified by this task.

## Modified Files

- Added `.gitattributes`
  - `sync-protocol/v1/**/*.json text eol=lf`
- Modified `tools/SyncProtocolContract/FixtureGenerator.cs`
  - Replaced platform-dependent newline output with normalized LF output.
  - Writes UTF-8 without BOM.
  - Ensures exactly one final LF.
- Modified `tools/SyncProtocolContract/FixtureRunner.cs`
  - Existing `manifest-hashes` check now rejects BOM, CR, CRLF, missing final LF, and multiple final LF bytes.
  - Requires exactly 64 manifest entries.
  - Requires the manifest paths to equal the actual schema/fixture JSON set.
  - Continues hashing the actual normalized file bytes without weakening integrity checks.
- Regenerated `sync-protocol/v1/manifest.json`
  - `generatedAtUtc` changed.
  - 59 fixture hashes changed from Windows CRLF hashes to repository LF-byte hashes.
  - Five schema hashes did not change.
- Added this report.

The 59 files under `sync-protocol/v1/fixtures/` have no tracked diff because their committed blobs were already LF. Regeneration now reproduces those exact bytes on every platform.

## Semantic Equivalence

Before and after regeneration, every fixture was parsed and serialized to sorted compact JSON, then SHA-256 hashed. The 59-entry semantic-hash lists were identical.

The manifest was also compared after removing only `generatedAtUtc` and each entry's `sha256`. The remaining manifest structure was identical.

Measured delta:

```text
fixture_count=59
fixture_semantic_differences=0
generatedAtUtc_changed=True
schema_hashes_changed=0
fixture_hashes_changed=59
other_manifest_semantics_changed=False
```

Example after regeneration, `fixtures/valid/hello-minimal.json`:

```text
manifest SHA-256: 9d141c9491e25f9fbad9603ea86903d8bcd0f18f98fc562eefa8c3050f2dee7b
Git/LF file SHA-256: 9d141c9491e25f9fbad9603ea86903d8bcd0f18f98fc562eefa8c3050f2dee7b
```

## Protocol Validation

Command:

```text
dotnet run --project tools/SyncProtocolContract/SyncProtocolContract.csproj \
  -c Release -- validate <repository-root> <temporary-report-directory>
```

Result:

```text
Sync Protocol v1: 70/70 checks passed; 0 failed.
```

This retains the existing 70-check total. The new BOM/LF/final-newline and manifest-set checks are part of the existing `manifest-hashes` check rather than an extra check added merely to change the count.

## Git Blob-Level Verification

A separate temporary Git index was created from HEAD. The modified attributes, generator, validator, manifest, and protocol JSON were added to that temporary index only. No files were staged in the real working index.

Each manifest entry was then read using `git show :<path>` from the temporary index and SHA-256 hashed. The manifest itself and every indexed protocol JSON were also read from the temporary index for BOM/line-ending validation.

```text
index_manifest_entries=64
index_manifest_hash_matches=64
index_manifest_hash_failures=0
index_protocol_json_files=65
index_line_ending_or_bom_failures=0
exit=0
```

This check uses bytes that Git would store, rather than relying on a Windows working tree that may be transformed by `core.autocrlf`.

## Release Builds

### Protocol Tool

```text
dotnet build tools/SyncProtocolContract/SyncProtocolContract.csproj -c Release --no-restore

Build succeeded.
0 Warning(s)
0 Error(s)
```

### Windows Application

Attempted command:

```text
dotnet build ClipShelf/ClipShelf.csproj -c Release -p:EnableWindowsTargeting=true
```

Measured result on macOS:

```text
Build FAILED.
0 Warning(s)
1 Error(s)

MSB4019: Microsoft.NET.Sdk.WindowsDesktop.targets was not found.
```

The installed .NET 8 macOS SDK does not contain the WindowsDesktop SDK required by this WPF application. This is a host-toolchain limitation, not a C# compiler diagnostic from the modified files. A real Windows environment must run the same Release build before final delivery; this report does not claim that acceptance item as passed.

## Safety And Scope

- No protocol field, behavior, error code, fixture expectation, or fixture semantic value changed.
- No manifest integrity check was removed or weakened.
- No macOS source or repository file was modified.
- No user data was read, modified, or deleted.
- No network, clipboard, device discovery, pairing, encryption, or product integration was added.
- No merge, PR, or release was created.
