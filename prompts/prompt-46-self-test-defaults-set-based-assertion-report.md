# prompt-46 实施报告：自检 defaults 集合断言

## 完成状态

已完成。原第 41 条断言由计数型判据改为名字/集合型判据，断言总数保持 **41**。检查文件集合前调用 `CFPreferencesAppSynchronize` 强制同步；固定域仍为 `ClipShelf.SelfTest.Active`。

## 改动文件

```text
13	8	Sources/ClipShelfLite/Support/SelfTest.swift
```

仅修改 suite 生命周期区与原第 41 条断言。其余 40 条断言名称和实现均未修改，`tests/parity/`、产品功能代码和报告格式均未改变。

## 新判据

1. `fixedSuiteName` 固定为 `ClipShelf.SelfTest.Active`。
2. 允许集合只有 `ClipShelf.SelfTest.Active.plist`。
3. 文件扫描返回所有 `ClipShelf.SelfTest.*.plist` 的文件名集合，不再比较数量。
4. 检查前执行 `CFPreferencesAppSynchronize(suiteName as CFString)`。
5. 条件同时要求：实际 suite 等于固定域名，且磁盘集合减去允许集合后为空。

因此固定 plist 存在或缺席都合法；任何 UUID 或其它域名都会失败。

## 构建与测试

### `swift build --disable-sandbox`

```text
Building for debugging...
[Planning deferred tasks]
[1/1]
[2/6] ClipShelf-product
[11/90]
[13/90]
[15/90]
[23/90]
[25/90]
[26/90]
[32/90]
[34/90]
[36/90]
[39/90]
[44/90]
[47/90]
[49/90]
[50/90]
[59/90]
[66/90]
[67/90]
[73/90]
[79/90]
[80/90]
[83/90]
[86/90] ClipShelf-product
[89/90] ClipShelf-product
Build complete! (9.63秒)
```

仓库内默认 `.build` 的测试包被 Documents 文件提供器附加 `FinderInfo`，第一次 `swift test --disable-sandbox` 在 codesign 阶段失败。按规格允许的方式改用：

```text
swift test --disable-sandbox --scratch-path /tmp/cs-46
```

最终输出关键部分：

```text
Build complete! (16.18秒)
[PASS] defaults suite isolation: settings were written to CLIPSHELF_DEFAULTS_SUITE
[PASS] history limit default: A missing history.maxItems key defaults to 100
[PASS] history limit persistence: history.maxItems persists as an integer in the isolated suite
[PASS] self test defaults suite file count does not grow: The isolated self-test defaults suite does not increase files in Library/Preferences
Test Suite 'All tests' passed at 2026-09-30 14:07:50.093.
	 Executed 0 tests, with 0 failures (0 unexpected) in 0.000 (0.002) seconds
◇ Test run started.
↳ Testing Library Version: 2084
↳ Target Platform: arm64e-apple-macos14.0
✔ Test run with 0 tests in 0 suites passed after 0.001 seconds.
```

## A：正常实现连续 3 次

以下三次均执行 `.build/debug/ClipShelf --self-test /tmp/46-normal-<n>.md`。

### 第 1 次原始输出

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
Self-test report: /tmp/46-normal-1.md
exit=0 result=Result: 41/41 checks passed.
```

### 第 2 次原始输出

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
Self-test report: /tmp/46-normal-2.md
exit=0 result=Result: 41/41 checks passed.
```

### 第 3 次原始输出

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
Self-test report: /tmp/46-normal-3.md
exit=0 result=Result: 41/41 checks passed.
```

## B：UUID 变异对照

临时变异：

```diff
-        let suiteName = fixedSuiteName
+        let suiteName = "ClipShelf.SelfTest.\(UUID().uuidString)"
```

变异构建成功。变异自检原始输出的末段：

```text
[PASS] defaults suite isolation: settings were written to CLIPSHELF_DEFAULTS_SUITE
[PASS] history limit default: A missing history.maxItems key defaults to 100
[PASS] history limit persistence: history.maxItems persists as an integer in the isolated suite
[PASS] built-in text preview extension routing is narrow: Common plain-text files use the built-in preview while documents and images retain existing routes
[FAIL] self test defaults suite file count does not grow: Unexpected self-test defaults domains: ["ClipShelf.SelfTest.35EB63CB-D0D5-4231-9C78-1B00D19ECA9F.plist"]
Self-test report: /tmp/46-mutation.md
build_exit=0 selftest_exit=1
Result: 40/41 checks passed.
```

只有目标断言失败；原计数型判据在同样 UUID 变异下会通过，本次集合型判据成功抓住原始缺陷。

变异随后立即还原。首次还原运行仍检测到变异遗留文件并正确失败；将该精确文件移动到废纸篓后，还原版本原始输出末段为：

```text
mutation_cleanup=/Users/Zhuanz/.Trash/clipshelf-selftest-mutation-46-20260930-141001
[PASS] defaults suite isolation: settings were written to CLIPSHELF_DEFAULTS_SUITE
[PASS] history limit default: A missing history.maxItems key defaults to 100
[PASS] history limit persistence: history.maxItems persists as an integer in the isolated suite
[PASS] self test defaults suite file count does not grow: The isolated self-test defaults suite does not increase files in Library/Preferences
Self-test report: /tmp/46-restored.md
restored_result=Result: 41/41 checks passed.
```

## 连续 5 次稳定性

```text
run=1 exit=0 result=Result: 41/41 checks passed. assertion=[PASS] self test defaults suite file count does not grow: The isolated self-test defaults suite does not increase files in Library/Preferences
run=2 exit=0 result=Result: 41/41 checks passed. assertion=[PASS] self test defaults suite file count does not grow: The isolated self-test defaults suite does not increase files in Library/Preferences
run=3 exit=0 result=Result: 41/41 checks passed. assertion=[PASS] self test defaults suite file count does not grow: The isolated self-test defaults suite does not increase files in Library/Preferences
run=4 exit=0 result=Result: 41/41 checks passed. assertion=[PASS] self test defaults suite file count does not grow: The isolated self-test defaults suite does not increase files in Library/Preferences
run=5 exit=0 result=Result: 41/41 checks passed. assertion=[PASS] self test defaults suite file count does not grow: The isolated self-test defaults suite does not increase files in Library/Preferences
```

## D：回归与偏好文件时间戳

既有 40 条断言名称对比 HEAD：新增 0、删除 0。以下关键断言均通过：

- `defaults suite isolation`
- `history limit default`
- `history limit persistence`

测试前：

```text
/Users/Zhuanz/Library/Preferences/ClipShelf.plist|1790666251|2026-09-29 15:17:31 +0800
/Users/Zhuanz/Library/Preferences/local.codex.ClipShelf.plist|1790522787|2026-09-27 23:26:27 +0800
/Users/Zhuanz/Library/Preferences/local.codex.ClipShelfLite.plist|1778948173|2026-05-17 00:16:13 +0800
```

测试后：

```text
/Users/Zhuanz/Library/Preferences/ClipShelf.plist|1790666251|2026-09-29 15:17:31 +0800
/Users/Zhuanz/Library/Preferences/local.codex.ClipShelf.plist|1790522787|2026-09-27 23:26:27 +0800
/Users/Zhuanz/Library/Preferences/local.codex.ClipShelfLite.plist|1778948173|2026-05-17 00:16:13 +0800
```

三项 epoch 和可读时间戳完全一致；测试前后均无 `ClipShelf.Runtime43.*.plist`。

## 验收对照

- **A 通过**：正常实现连续 3 次均退出 0、`41/41`。
- **B 通过**：UUID 变异时退出 1、`40/41`，目标断言明确列出随机域；还原并清理变异文件后恢复 `41/41`。
- **C 通过**：构建成功；测试使用规格允许的 `/tmp/cs-46` scratch 路径成功。
- **D 通过**：其余 40 条断言零修改、零删除；关键 defaults 断言通过；真实偏好 mtime 不变。
- **E 通过**：本报告包含改动清单、三次正常输出、变异前后对照和连续五次稳定性结果。

## 手工验证步骤

1. 运行 `.build/debug/ClipShelf --self-test /tmp/46-manual.md`，确认 `41/41`。
2. 临时把 `suiteName` 改为 `ClipShelf.SelfTest.<UUID>` 并重新构建。
3. 再运行自检，确认目标断言失败并列出 UUID plist，结果为 `40/41`。
4. 还原固定域并只清理变异产生的精确 UUID plist。
5. 重新构建、自检，确认恢复 `41/41`。
