# prompt-44 实施报告：通用文件类型中性灰

## 完成状态

已完成。`generic` 文件类型改为中性灰，新增 `file type icon colors are pairwise distinct` 断言，自检总数由 39 增至 40。

## 改动文件

`git diff --numstat`：

```text
53	72	Sources/ClipShelfLite/Support/AppTheme.swift
28	0	Sources/ClipShelfLite/Support/SelfTest.swift
```

- `AppTheme.swift`：增加单一来源的 `FileTypePalette`，由同一份 RGB 数据同时驱动 SwiftUI 颜色和自检；其余六类 RGB 逐位保持原值。
- `SelfTest.swift`：只在文件类型图标断言区追加颜色组合唯一性与 `generic`/`spreadsheet` 背景色距检查；既有断言未改。

## 实际 RGB 与色距

通用回退采用：

| 模式 | 背景 RGB | 前景 RGB | 三通道最大差 |
| --- | --- | --- | --- |
| 浅色 | `(0.88, 0.88, 0.88)` | `(0.34, 0.34, 0.34)` | `0.00` / `0.00` |
| 深色 | `(0.24, 0.24, 0.24)` | `(0.72, 0.72, 0.72)` | `0.00` / `0.00` |

与表格背景的实算色距，使用 `max(|dR|, |dG|, |dB|)`：

- 浅色：`max(|0.88-0.89|, |0.88-0.97|, |0.88-0.91|) = 0.09`
- 深色：`max(|0.24-0.12|, |0.24-0.25|, |0.24-0.17|) = 0.12`

两者均大于等于 `0.08`。

## AppTheme.swift diff

以下 diff 展示了文件类型颜色改为单一调色板来源。六个非 `generic` 分类的数值逐位照抄原值，唯一发生数值变化的是 `.generic`。

```diff
@@
+    struct FileTypePalette: Equatable {
+        let lightBackground: SIMD3<Double>
+        let darkBackground: SIMD3<Double>
+        let lightForeground: SIMD3<Double>
+        let darkForeground: SIMD3<Double>
+    }
@@
-    static let filePreviewBackground = adaptive(
-        light: color(red: 0.91, green: 0.96, blue: 0.92),
-        dark: color(red: 0.13, green: 0.24, blue: 0.18)
-    )
-    static let filePreviewForeground = adaptive(
-        light: color(red: 0.12, green: 0.38, blue: 0.20),
-        dark: color(red: 0.45, green: 0.86, blue: 0.67)
-    )
-    // pdf/spreadsheet/word/presentation/archive/folder constants removed;
-    // their exact values are retained below in FileTypePalette.
@@
     static func fileTypeBackground(_ category: FileTypeIconCategory) -> Color {
-        switch category { ... }
+        let palette = fileTypePalette(category)
+        return adaptive(light: color(palette.lightBackground), dark: color(palette.darkBackground))
     }

     static func fileTypeForeground(_ category: FileTypeIconCategory) -> Color {
+        let palette = fileTypePalette(category)
+        return adaptive(light: color(palette.lightForeground), dark: color(palette.darkForeground))
+    }
+
+    static func fileTypePalette(_ category: FileTypeIconCategory) -> FileTypePalette {
         switch category {
-        case .pdf: pdfPreviewForeground
-        case .spreadsheet: spreadsheetPreviewForeground
-        case .word: wordPreviewForeground
-        case .presentation: presentationPreviewForeground
-        case .archive: archivePreviewForeground
-        case .folder: folderPreviewForeground
-        case .generic: filePreviewForeground
+        case .pdf:
+            FileTypePalette(
+                lightBackground: SIMD3(1.0, 0.91, 0.91), darkBackground: SIMD3(0.30, 0.13, 0.15),
+                lightForeground: SIMD3(0.72, 0.13, 0.16), darkForeground: SIMD3(1.0, 0.52, 0.54)
+            )
+        case .spreadsheet:
+            FileTypePalette(
+                lightBackground: SIMD3(0.89, 0.97, 0.91), darkBackground: SIMD3(0.12, 0.25, 0.17),
+                lightForeground: SIMD3(0.08, 0.43, 0.20), darkForeground: SIMD3(0.43, 0.87, 0.59)
+            )
+        case .word:
+            FileTypePalette(
+                lightBackground: SIMD3(0.89, 0.94, 1.0), darkBackground: SIMD3(0.11, 0.21, 0.34),
+                lightForeground: SIMD3(0.10, 0.36, 0.70), darkForeground: SIMD3(0.45, 0.72, 1.0)
+            )
+        case .presentation:
+            FileTypePalette(
+                lightBackground: SIMD3(1.0, 0.93, 0.86), darkBackground: SIMD3(0.31, 0.19, 0.10),
+                lightForeground: SIMD3(0.76, 0.34, 0.06), darkForeground: SIMD3(1.0, 0.67, 0.34)
+            )
+        case .archive:
+            FileTypePalette(
+                lightBackground: SIMD3(0.95, 0.90, 0.99), darkBackground: SIMD3(0.24, 0.16, 0.31),
+                lightForeground: SIMD3(0.47, 0.20, 0.67), darkForeground: SIMD3(0.78, 0.57, 1.0)
+            )
+        case .folder:
+            FileTypePalette(
+                lightBackground: SIMD3(1.0, 0.96, 0.82), darkBackground: SIMD3(0.30, 0.24, 0.11),
+                lightForeground: SIMD3(0.67, 0.45, 0.04), darkForeground: SIMD3(0.96, 0.76, 0.31)
+            )
+        case .generic:
+            FileTypePalette(
+                lightBackground: SIMD3(0.88, 0.88, 0.88), darkBackground: SIMD3(0.24, 0.24, 0.24),
+                lightForeground: SIMD3(0.34, 0.34, 0.34), darkForeground: SIMD3(0.72, 0.72, 0.72)
+            )
         }
     }
```

## 测试原始输出

### `swift build --disable-sandbox`

```text
Building for debugging...
[2/6] ClipShelf-product
[5/9] ClipShelf-product
[8/12] ClipShelf-product
[9/12] ClipShelf-product
[11/12] ClipShelf-product
Build complete! (3.71秒)
```

### `swift test --disable-sandbox`

```text
Building for debugging...
[Planning deferred tasks]
[18/43] ClipShelfLite
[27/52] ClipShelfLite
[35/52] ClipShelfLite
[41/53] ClipShelfLiteTests-product
[47/65]
[49/65]
[59/65] ClipShelfLiteTests-product
[61/65] ClipShelfLiteTests-product
[64/65] ClipShelfLiteTests-product
Build complete! (6.64秒)
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
Test Suite 'All tests' started at 2026-09-29 14:51:54.464.
Test Suite 'All tests' passed at 2026-09-29 14:51:54.466.
	 Executed 0 tests, with 0 failures (0 unexpected) in 0.000 (0.002) seconds
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
◇ Test run started.
↳ Testing Library Version: 2084
↳ Target Platform: arm64e-apple-macos14.0
✔ Test run with 0 tests in 0 suites passed after 0.001 seconds.
```

### `.build/debug/ClipShelf --self-test /tmp/44-selftest.md`

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
Self-test report: /tmp/44-selftest.md
```

`/tmp/44-selftest.md`：`Result: 40/40 checks passed.`

## 验收对照

- **A 通过**：浅色和深色的 `generic` 背景、前景均为严格中性灰，通道最大差为 `0.00`。
- **B 通过**：与 `spreadsheet` 背景色距分别为 `0.09`、`0.12`；断言遍历七类并检查浅/深模式的背景+前景组合两两不同。
- **C 通过**：构建、完整测试、自检均退出 0；自检为 `40/40`。
- **D 通过**：其余六类 RGB 数值逐位不变；既有 `file type icons remain visually distinct` 与映射断言仍通过。
- **E 通过**：本报告包含文件清单、RGB、色距、测试原始输出和手工验证步骤。

## 手工验证步骤

1. 启动 ClipShelf，准备一个 `.xlsx` 文件记录和一个无扩展名文件记录。
2. 在浅色模式观察：表格应为绿色，通用文件应为中性灰，二者可一眼区分。
3. 切换到深色模式重复观察，确认通用文件仍为中性灰且不与表格绿色混淆。
4. 确认 PDF、Word、PPT、压缩包、文件夹的既有颜色未变化。

## 环境说明

当前仓库位于受文件提供器管理的 Documents 路径，原 `.build` 测试包会被自动附加 `FinderInfo`，导致代码签名拒绝。为保持规格要求的命令原样不变，仅将被忽略的 `.build` 派生目录链接到 `/tmp/clipshelf-batch-D-build`；未修改源码、测试或 `tests/parity/` 来规避测试。
