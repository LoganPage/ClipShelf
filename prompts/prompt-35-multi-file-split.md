# prompt-35：缺口 6 · 多文件复制拆分为独立记录

依据 `AGENTS.md` 的 Prompt and collaboration guidelines。协作约定见 `prompts/README.md`。
前置：prompt-31 已落地（测试底座可用）。本提示词只交付**多文件拆分**这一件事。

> 排序说明：本项在交接说明中排在 OCR 之前 —— 它改动的是历史数据结构，越早做，后续功能越不必回头适配。

## 1. Goal

一次复制多个文件时，**每个文件独立成一条记录**，可单独复制 / 预览 / 置顶 / 删除；按**单路径**去重；批量导入只排序与保存一次；遵守历史上限。

功能基准：Windows 端 ClipShelf 1.4.0（自 1.0.16 起）—— 多文件拆成独立记录；批量导入只排序、保存、通知一次；按路径去重并保留既有置顶状态。

## 2. Evidence

**实测环境**：2026-09-27 初测 / 2026-09-28 复测，仓库 `/Users/Zhuanz/Documents/Codex/2026-05-16/macos-windows`。

> ⚠ **`Stores/ClipStore.swift` 的行号已随 prompt-32 漂移，且交付时 prompt-43 可能仍在修改该文件。**
> 下列行号为 2026-09-28 实测值，**仅供参考**；若与你的 checkout 不符，**以符号名为准**（函数名比行号可靠）。
> 另注：prompt-32 已把原 `trimToMaxItems()` 替换为 `HistoryTrimmer.trim(_:maxItems:)`（在 `Support/HistoryLimitPreferences.swift`），本提示词中的裁剪调用以新符号为准。

- `Sources/ClipShelfLite/Stores/ClipStore.swift:163` `currentFileItem()`：把**多个路径塞进一条** `.file` 记录（`filePaths: paths`），标题为 `"\(paths.count) 个文件"`。
- `Stores/ClipStore.swift:139` `pollPasteboard()`：`if let fileItem = currentFileItem() { add(fileItem); return }` —— 每次只 `add` 一条。
- `Stores/ClipStore.swift:324` `add(_:)`：去重（`isDuplicate`）→ `removeAll` 旧重复 → `insert(at: 0)` → `sortItems()` → `HistoryTrimmer.trim(_:maxItems:)` → `save()`。**每调用一次就写一次盘**。
- `Stores/ClipStore.swift:348` `isDuplicate(_:_:)`：`.file` 分支是 `lhs.filePaths == rhs.filePaths` —— **整数组相等**，拆分后需改为按单路径判定。
- `Stores/ClipStore.swift:276` `fileURLsForMultiCopy(_:)`：多选复制的**写出**路径已支持把多条 `.file` 记录的路径合并写回剪贴板，**无需改动**。
- `Views/MainView.swift:1055-1061` `kindText`：`item.filePaths.count > 1 ? "\(count) 个文件" : "文件"`。
- `Services/PreviewController.swift:132`：文件预览要求 `item.filePaths.count == 1`（合并记录会走 QuickLook 而非内置预览）。
- `Models/ClipItem.swift:14`：`var filePaths: [String]`（保持数组类型，不改字段）。

## 3. Scope

**受影响**：`Stores/ClipStore.swift`、`README.md` 功能列表（中英双语）。

**不得改变的部分**
- `ClipItem` 的字段与 `history.json` 格式（`filePaths` 仍是 `[String]`）。
- 单文件复制的既有行为（标题 = 文件名，一条记录）。
- 多选**复制到剪贴板**的写出逻辑（`fileURLsForMultiCopy`、`writeToPasteboard(_ items:)`）。
- 图片 / 文字记录的采集与去重规则。
- **旧的合并记录不自动拆分**（向后兼容，见 §4.4）。

## 4. Constraints

1. 多文件时生成 **N 条** `.file` 记录，每条 `filePaths` **只含 1 个路径**，`title` = 该文件的 `lastPathComponent`。
2. **去重按单路径**：某路径已在历史中存在时不重复插入，且**保留其原有 `id`、`createdAt` 与 `isPinned`**（不重置置顶状态）。
3. **批量导入只排序与保存一次**：整批处理完后调用一次 `sortItems()` + `HistoryTrimmer.trim(_:maxItems:)` + `save()`，**不要 N 次写盘**。
4. **旧数据兼容**：`filePaths.count > 1` 的历史记录**必须能正常读取、正常显示、正常复制**，且**不被自动拆分**。
5. 遵守当前历史上限（含 prompt-32 引入的**可调上限**）；裁剪仍走 `HistoryTrimmer.trim(_:maxItems:)`（prompt-32 已用它替换原 `trimToMaxItems()`）。
6. 沿用 prompt-31 的测试底座：断言同时进 `swift test` 与 `--self-test`；断言逻辑只实现一次。
7. 不得为了让测试通过而弱化断言。

## 5. Acceptance

- **A** 一次复制 3 个文件 → 历史新增 **3 条** `.file`，每条 `filePaths.count == 1`，`title` 为各自文件名。
- **B** 再次复制**同样**的 3 个文件 → 文件类记录条数**不变**（按单路径去重）。
- **C** 单独复制其中 1 个已存在的文件 → 不新增记录，且其原**置顶状态与 `createdAt` 保持不变**。
- **D** 拆分出的每条记录均可单独复制、单独预览、单独置顶、单独删除。
- **E** 复制 3 条拆分记录（多选）→ 剪贴板里能一次拿到全部 3 个文件路径（回归 `fileURLsForMultiCopy`）。
- **F** 写入一份含 `filePaths.count > 1` 的**旧格式** `history.json` → 启动不崩溃、记录可读、条数正确、**未被自动拆分**。
- **G** 批量导入 3 个文件时只发生**一次**落盘（可在报告中用日志或写入次数断言说明）。
- **H** 超过历史上限时按既有规则裁剪（置顶优先保留）。
- **I** `swift build` 与 `swift test` 通过；`swift test` 至少覆盖：拆分粒度、按路径去重且保留置顶、旧数据兼容。
- **J** `ClipShelf --self-test <报告>` 退出码 0。
- **K** `README.md` 中英双语功能列表补充本项。
- **L** 报告：改动文件清单、测试输出、手工验证步骤。

## 6. Handoff

- **实现**：Codex。**独立测试与复核**：WorkBuddy（只读，不改源码）。
- **授权（未勾选即未授权）**：`[ ]` 本地检查 · `[ ]` Release 构建 · `[ ]` 覆盖安装 · `[ ]` `git commit` · `[ ]` `push` · `[ ]` GitHub 发布。
  **本轮建议**：仅本地检查。
- **提交信息建议**：`Split multi-file clipboard copies into individual records`
