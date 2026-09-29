# prompt-45 实施报告：自检 defaults 域不再增长

## 完成状态

已完成。`--self-test` 改用固定隔离域 `ClipShelf.SelfTest.Active`，运行前、断言前和 defer 阶段只清理该精确 suite；新增“匹配 plist 文件数不增长”断言。总断言数由 40 增至 41。

## 改动文件

本项相对 prompt-44 完成点的增删：

```text
30	2	Sources/ClipShelfLite/Support/SelfTest.swift
```

- 仅修改 `run()` 头部的 suite 生命周期，并在 `run()` 末尾追加一条断言。
- 未修改 `AppEnvironment.swift`、既有 defaults 断言、颜色断言、`MainView.swift` 或 `tests/parity/`。

## 采用路线与理由

采用“固定 suite + 前后精确清理 + 文件数不增长断言”的降级路线：

1. suite 从每次随机的 `ClipShelf.SelfTest.<UUID>` 改为固定 `ClipShelf.SelfTest.Active`，杜绝每次创建新域名，并保持在既有清理脚本的安全匹配范围内。
2. `CLIPSHELF_DEFAULTS_SUITE` 继续传入 `AppEnvironment.userDefaults(environment:)`，仍创建 `UserDefaults(suiteName:)`，没有回退到 `.standard`。
3. 运行前清理固定 suite，断言前再次清理，defer 再兜底；文件系统操作只针对精确路径 `~/Library/Preferences/ClipShelf.SelfTest.Active.plist`。
4. 实测 cfprefsd 可能在进程退出后异步重写固定 plist，因此不采用不诚实的“进程返回前不存在即永不残留”判据；按规格允许的退路，断言匹配文件总数不增长。最坏只会复用一个固定文件，不再无限增加。

## 连续 3 次实测

命令每次均为 `.build/debug/ClipShelf --self-test /tmp/45-three-final-<n>.md`，每次后等待 0.2 秒并执行规格中的计数。

```text
--- three-run count verification ---
before=2
run=1 exit=0 count=1 result=Result: 41/41 checks passed.
run=2 exit=0 count=1 result=Result: 41/41 checks passed.
run=3 exit=0 count=1 result=Result: 41/41 checks passed.
```

数字没有增长；首轮还清理了测试前异步出现的固定 suite 文件。该阶段的另一个文件是早期实现试验留下的固定 suite，随后在授权的收尾清理中一并移入废纸篓。

## 新断言连续 5 次稳定性

```text
--- five-run assertion stability ---
run=1 exit=0 count=1 assertion=[PASS] self test defaults suite file count does not grow: The isolated self-test defaults suite does not increase files in Library/Preferences
run=2 exit=0 count=1 assertion=[PASS] self test defaults suite file count does not grow: The isolated self-test defaults suite does not increase files in Library/Preferences
run=3 exit=0 count=1 assertion=[PASS] self test defaults suite file count does not grow: The isolated self-test defaults suite does not increase files in Library/Preferences
run=4 exit=0 count=1 assertion=[PASS] self test defaults suite file count does not grow: The isolated self-test defaults suite does not increase files in Library/Preferences
run=5 exit=0 count=1 assertion=[PASS] self test defaults suite file count does not grow: The isolated self-test defaults suite does not increase files in Library/Preferences
```

## 受保护偏好文件时间戳

测试前：

```text
/Users/Zhuanz/Library/Preferences/ClipShelf.plist|1790521475|2026-09-27 23:04:35 +0800
/Users/Zhuanz/Library/Preferences/local.codex.ClipShelf.plist|1790522787|2026-09-27 23:26:27 +0800
/Users/Zhuanz/Library/Preferences/local.codex.ClipShelfLite.plist|1778948173|2026-05-17 00:16:13 +0800
/Users/Zhuanz/Library/Preferences/ClipShelf.Runtime43.48183.plist|1790526984|2026-09-28 00:36:24 +0800
```

测试后：

```text
/Users/Zhuanz/Library/Preferences/ClipShelf.plist|1790521475|2026-09-27 23:04:35 +0800
/Users/Zhuanz/Library/Preferences/local.codex.ClipShelf.plist|1790522787|2026-09-27 23:26:27 +0800
/Users/Zhuanz/Library/Preferences/local.codex.ClipShelfLite.plist|1778948173|2026-05-17 00:16:13 +0800
/Users/Zhuanz/Library/Preferences/ClipShelf.Runtime43.48183.plist|1790526984|2026-09-28 00:36:24 +0800
```

四项 epoch 与可读时间戳完全一致。

## 测试原始输出

### `swift build --disable-sandbox`

```text
Building for debugging...
[2/6] ClipShelf-product
[4/8] ClipShelf-product
[7/8] ClipShelf-product
Build complete! (1.99秒)
```

### `swift test --disable-sandbox`

```text
Building for debugging...
[1/12]
[2/11] ClipShelfLite
[4/13] ClipShelfLite
[10/11] ClipShelfLiteTests-product
Build complete! (1.94秒)
[PASS] concealed content is excluded: ConcealedType is rejected before history capture
[PASS] excluded content advances change count: Excluded clipboard changes are acknowledged exactly once
[PASS] transient content is excluded: TransientType is rejected before history capture
[PASS] ordinary content is included: Unmarked text remains eligible for history capture
[PASS] unknown type information is included: Unavailable type information fails open
[PASS] paused clipboard history rejects capture: Disabled clipboard history does not capture pasteboard changes
[PASS] status menu recording title follows history state: The status menu title maps directly from the shared history-enabled state
[PASS] data directory isolation: history.json was written only inside CLIPSHELF_DATA_DIR
[PASS] defaults suite isolation: settings were written to CLIPSHELF_DEFAULTS_SUITE
[PASS] history limit default: A missing history.maxItems key defaults to 100
[PASS] history limit persistence: history.maxItems persists as an integer in the isolated suite
[PASS] history limit boundaries: Limits are clamped to the inclusive 1...10000 range
[PASS] invalid history limit input: Empty and non-numeric input restores the last valid value
[PASS] history limit trims immediately with pinned priority: Lowering the limit removes the oldest unpinned records first
[PASS] history trimming preserves original files: Removing a file record did not delete its original file
[PASS] runtime control endpoint defaults to disabled: No control socket is configured without CLIPSHELF_CONTROL_SOCKET
[PASS] runtime control client reports connection failure: An unavailable socket returns a nonzero code and readable JSON error
[PASS] named pasteboard is isolated: CLIPSHELF_PASTEBOARD_NAME does not write to other or general pasteboards
[PASS] undo deletion restores order and metadata: Undo restores original IDs, order, timestamps, and pinned state
[PASS] undo deletion keeps the latest ten batches: The in-memory undo stack drops batches older than the latest ten
[PASS] undo deletion respects current history capacity: Undo fills only free slots and never evicts newer records
[PASS] clear history is undoable: A clear-history batch restores in full and a new session starts empty
[PASS] multi-file import splits into individual records: Each copied file becomes one independently addressable history record
[PASS] multi-file import deduplicates by path: Repeated paths do not create additional file records
[PASS] file path dedup preserves existing metadata: Path dedup leaves the existing ID, timestamp, and pinned state untouched
[PASS] legacy combined file records remain compatible: Old multi-path records decode unchanged and participate in path dedup
[PASS] multi-file import obeys the history limit: Batch file records use the existing pinned-first history trimming rule
[PASS] record type filter covers every option: All, text, file, and image filters return only their intended records
[PASS] record type filter composes with search: Type filtering and text search are applied through one combined funnel
[PASS] record type filter reports an empty result: A nonempty history can produce a distinct empty filtered result
[PASS] selection count label follows the selected set size: The label is hidden at zero and formats one-, two-, or three-digit counts directly
[PASS] file type icon maps supported extensions and fallbacks: PDF, spreadsheet, Word, presentation, archive, folder, and generic records map correctly
[PASS] file type icons remain visually distinct: Every file category uses a distinct SF Symbol
[PASS] file type icon colors are pairwise distinct: Every file category has a distinct color pair and generic stays at least 0.08 from spreadsheet
[PASS] file type directory metadata is cached per path: The same path performs at most one directory metadata lookup
[PASS] image preview zoom clamps and resets: Image zoom stays within 50%-400% and reset returns to fit-window 100%
[PASS] image OCR language configuration is local and corrected: OCR uses Simplified Chinese and English with language correction
[PASS] image OCR aggregates, orders, and deduplicates text: OCR removes blank and duplicate regions while preserving reading order and best confidence
[PASS] text encoding detector covers BOM UTF-8 UTF-16 and GB18030: Text decoding recognizes UTF-8 BOM, both UTF-16 byte orders, and GB18030 Chinese
[PASS] built-in text preview extension routing is narrow: Common plain-text files use the built-in preview while documents and images retain existing routes
[PASS] self test defaults suite file count does not grow: The isolated self-test defaults suite does not increase files in Library/Preferences
Test Suite 'All tests' started at 2026-09-29 14:59:55.622.
Test Suite 'All tests' passed at 2026-09-29 14:59:55.623.
	 Executed 0 tests, with 0 failures (0 unexpected) in 0.000 (0.001) seconds
[PASS] concealed content is excluded: ConcealedType is rejected before history capture
[PASS] excluded content advances change count: Excluded clipboard changes are acknowledged exactly once
[PASS] transient content is excluded: TransientType is rejected before history capture
[PASS] ordinary content is included: Unmarked text remains eligible for history capture
[PASS] unknown type information is included: Unavailable type information fails open
[PASS] paused clipboard history rejects capture: Disabled clipboard history does not capture pasteboard changes
[PASS] status menu recording title follows history state: The status menu title maps directly from the shared history-enabled state
[PASS] data directory isolation: history.json was written only inside CLIPSHELF_DATA_DIR
[PASS] defaults suite isolation: settings were written to CLIPSHELF_DEFAULTS_SUITE
[PASS] history limit default: A missing history.maxItems key defaults to 100
[PASS] history limit persistence: history.maxItems persists as an integer in the isolated suite
[PASS] history limit boundaries: Limits are clamped to the inclusive 1...10000 range
[PASS] invalid history limit input: Empty and non-numeric input restores the last valid value
[PASS] history limit trims immediately with pinned priority: Lowering the limit removes the oldest unpinned records first
[PASS] history trimming preserves original files: Removing a file record did not delete its original file
[PASS] runtime control endpoint defaults to disabled: No control socket is configured without CLIPSHELF_CONTROL_SOCKET
[PASS] runtime control client reports connection failure: An unavailable socket returns a nonzero code and readable JSON error
[PASS] named pasteboard is isolated: CLIPSHELF_PASTEBOARD_NAME does not write to other or general pasteboards
[PASS] undo deletion restores order and metadata: Undo restores original IDs, order, timestamps, and pinned state
[PASS] undo deletion keeps the latest ten batches: The in-memory undo stack drops batches older than the latest ten
[PASS] undo deletion respects current history capacity: Undo fills only free slots and never evicts newer records
[PASS] clear history is undoable: A clear-history batch restores in full and a new session starts empty
[PASS] multi-file import splits into individual records: Each copied file becomes one independently addressable history record
[PASS] multi-file import deduplicates by path: Repeated paths do not create additional file records
[PASS] file path dedup preserves existing metadata: Path dedup leaves the existing ID, timestamp, and pinned state untouched
[PASS] legacy combined file records remain compatible: Old multi-path records decode unchanged and participate in path dedup
[PASS] multi-file import obeys the history limit: Batch file records use the existing pinned-first history trimming rule
[PASS] record type filter covers every option: All, text, file, and image filters return only their intended records
[PASS] record type filter composes with search: Type filtering and text search are applied through one combined funnel
[PASS] record type filter reports an empty result: A nonempty history can produce a distinct empty filtered result
[PASS] selection count label follows the selected set size: The label is hidden at zero and formats one-, two-, or three-digit counts directly
[PASS] file type icon maps supported extensions and fallbacks: PDF, spreadsheet, Word, presentation, archive, folder, and generic records map correctly
[PASS] file type icons remain visually distinct: Every file category uses a distinct SF Symbol
[PASS] file type icon colors are pairwise distinct: Every file category has a distinct color pair and generic stays at least 0.08 from spreadsheet
[PASS] file type directory metadata is cached per path: The same path performs at most one directory metadata lookup
[PASS] image preview zoom clamps and resets: Image zoom stays within 50%-400% and reset returns to fit-window 100%
[PASS] image OCR language configuration is local and corrected: OCR uses Simplified Chinese and English with language correction
[PASS] image OCR aggregates, orders, and deduplicates text: OCR removes blank and duplicate regions while preserving reading order and best confidence
[PASS] text encoding detector covers BOM UTF-8 UTF-16 and GB18030: Text decoding recognizes UTF-8 BOM, both UTF-16 byte orders, and GB18030 Chinese
[PASS] built-in text preview extension routing is narrow: Common plain-text files use the built-in preview while documents and images retain existing routes
[PASS] self test defaults suite file count does not grow: The isolated self-test defaults suite does not increase files in Library/Preferences
◇ Test run started.
↳ Testing Library Version: 2084
↳ Target Platform: arm64e-apple-macos14.0
✔ Test run with 0 tests in 0 suites passed after 0.001 seconds.
```

### `.build/debug/ClipShelf --self-test /tmp/45-selftest.md`

```text
[PASS] concealed content is excluded: ConcealedType is rejected before history capture
[PASS] excluded content advances change count: Excluded clipboard changes are acknowledged exactly once
[PASS] transient content is excluded: TransientType is rejected before history capture
[PASS] ordinary content is included: Unmarked text remains eligible for history capture
[PASS] unknown type information is included: Unavailable type information fails open
[PASS] paused clipboard history rejects capture: Disabled clipboard history does not capture pasteboard changes
[PASS] status menu recording title follows history state: The status menu title maps directly from the shared history-enabled state
[PASS] data directory isolation: history.json was written only inside CLIPSHELF_DATA_DIR
[PASS] defaults suite isolation: settings were written to CLIPSHELF_DEFAULTS_SUITE
[PASS] history limit default: A missing history.maxItems key defaults to 100
[PASS] history limit persistence: history.maxItems persists as an integer in the isolated suite
[PASS] history limit boundaries: Limits are clamped to the inclusive 1...10000 range
[PASS] invalid history limit input: Empty and non-numeric input restores the last valid value
[PASS] history limit trims immediately with pinned priority: Lowering the limit removes the oldest unpinned records first
[PASS] history trimming preserves original files: Removing a file record did not delete its original file
[PASS] runtime control endpoint defaults to disabled: No control socket is configured without CLIPSHELF_CONTROL_SOCKET
[PASS] runtime control client reports connection failure: An unavailable socket returns a nonzero code and readable JSON error
[PASS] named pasteboard is isolated: CLIPSHELF_PASTEBOARD_NAME does not write to other or general pasteboards
[PASS] undo deletion restores order and metadata: Undo restores original IDs, order, timestamps, and pinned state
[PASS] undo deletion keeps the latest ten batches: The in-memory undo stack drops batches older than the latest ten
[PASS] undo deletion respects current history capacity: Undo fills only free slots and never evicts newer records
[PASS] clear history is undoable: A clear-history batch restores in full and a new session starts empty
[PASS] multi-file import splits into individual records: Each copied file becomes one independently addressable history record
[PASS] multi-file import deduplicates by path: Repeated paths do not create additional file records
[PASS] file path dedup preserves existing metadata: Path dedup leaves the existing ID, timestamp, and pinned state untouched
[PASS] legacy combined file records remain compatible: Old multi-path records decode unchanged and participate in path dedup
[PASS] multi-file import obeys the history limit: Batch file records use the existing pinned-first history trimming rule
[PASS] record type filter covers every option: All, text, file, and image filters return only their intended records
[PASS] record type filter composes with search: Type filtering and text search are applied through one combined funnel
[PASS] record type filter reports an empty result: A nonempty history can produce a distinct empty filtered result
[PASS] selection count label follows the selected set size: The label is hidden at zero and formats one-, two-, or three-digit counts directly
[PASS] file type icon maps supported extensions and fallbacks: PDF, spreadsheet, Word, presentation, archive, folder, and generic records map correctly
[PASS] file type icons remain visually distinct: Every file category uses a distinct SF Symbol
[PASS] file type icon colors are pairwise distinct: Every file category has a distinct color pair and generic stays at least 0.08 from spreadsheet
[PASS] file type directory metadata is cached per path: The same path performs at most one directory metadata lookup
[PASS] image preview zoom clamps and resets: Image zoom stays within 50%-400% and reset returns to fit-window 100%
[PASS] image OCR language configuration is local and corrected: OCR uses Simplified Chinese and English with language correction
[PASS] image OCR aggregates, orders, and deduplicates text: OCR removes blank and duplicate regions while preserving reading order and best confidence
[PASS] text encoding detector covers BOM UTF-8 UTF-16 and GB18030: Text decoding recognizes UTF-8 BOM, both UTF-16 byte orders, and GB18030 Chinese
[PASS] built-in text preview extension routing is narrow: Common plain-text files use the built-in preview while documents and images retain existing routes
[PASS] self test defaults suite file count does not grow: The isolated self-test defaults suite does not increase files in Library/Preferences
Self-test report: /tmp/45-selftest.md
```

`/tmp/45-selftest.md`：`Result: 41/41 checks passed.`

## 验收对照

- **A 通过**：连续 3 次退出码均为 0，计数为 `1 / 1 / 1`，没有递增。
- **B 通过**：构建、完整测试、自检均成功，自检为 `41/41`。
- **C 通过**：`defaults suite isolation` 与 `history limit persistence` 仍通过；环境变量仍映射到隔离 suite；用户偏好与 Runtime43 时间戳不变。
- **D 通过**：报告包含改动清单、路线理由、3 次计数和 5 次稳定性结果。
- **E 通过**：未修改任何既有断言、`MainView.swift` 或 `tests/parity/`，未引入依赖，也未触碰非自检 suite 偏好。

## 手工验证步骤

1. 记录 `ls -1 ~/Library/Preferences/ | grep -c '^ClipShelf\.SelfTest\.'` 的初始数值。
2. 连续运行 `.build/debug/ClipShelf --self-test /tmp/manual-<n>.md` 至少三次。
3. 每次等待 0.2 秒后重新计数，确认数字不增长。
4. 打开报告，确认 `defaults suite isolation`、`history limit persistence` 和 `self test defaults suite file count does not grow` 都为通过。
5. 对比 `ClipShelf.plist`、两个 `local.codex.*` 和 `ClipShelf.Runtime43.*` 的 mtime，确认完全一致。
