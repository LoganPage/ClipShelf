# prompt-42：文本预览增强（行号 / 换行 / 查找 / 编码）

依据 `AGENTS.md` 的 Prompt and collaboration guidelines。协作约定见 `prompts/README.md`。
前置：prompt-31 已落地（测试底座可用）。本提示词只交付**文本预览增强**这一件事。

> 来源说明：本项由 Mac 端上一轮勘察识别（对应 `docs/parity/codex-prompt.md` 的 P2-11），**未列入 Windows 交接说明的六项缺口清单**。它是**功能**而非交互规则，故按用户口径纳入本轮；优先级最低，可最后做。

## 1. Goal

内置文本预览支持：**行号开关**、**自动换行开关**、**查找**（`Cmd+F` 打开、`F3` 下一个、`Shift+F3` 上一个、`Esc` 关闭）；对常见文本文件（`txt / md / log / json / xml / csv / ini / yaml` 及常见代码扩展名）用**内置文本预览**而非 QuickLook。

功能基准：Windows 端 ClipShelf 1.4.0 的文本预览支持行号、自动换行、`Cmd+F` 查找与 `F3` / `Shift+F3` 跳转。

## 2. Evidence

**初测环境**：2026-09-27，仓库 `/Users/Zhuanz/Documents/Codex/2026-05-16/macos-windows`，HEAD `355acec`。
**复测**：2026-09-28，HEAD `0860ab3` —— `Services/PreviewController.swift` 自 `355acec` 以来 **0 次改动**，下列行号**全部仍然准确**；`isHorizontallyResizable` / `widthTracksTextView` 全仓检索**仍为零命中**。

- `Sources/ClipShelfLite/Services/PreviewController.swift:174-191` `showTextPreview(_:title:)`：`NSScrollView` + **只读** `NSTextView`（`isEditable = false`、`font = .systemFont(ofSize: 15)`、`textContainerInset = NSSize(width: 18, height: 18)`）。**没有行号栏、没有查找条、没有换行开关**。
- `Services/PreviewController.swift:116-140` `showBuiltInPreviewIfPossible(_:)`：`.text` 记录走内置预览；`.file` 记录**只有能被 `NSImage(contentsOf:)` 打开时才走内置预览**（L131-138），因此 `.txt` / `.md` / `.log` / `.json` 等文本文件目前一律**回落到 QuickLook**。
- `Services/PreviewController.swift:70-92` `previewURL(for:)`：`.text` 与无源路径的 `.image` 会写临时文件（`ClipShelfPreview-<uuid>.txt` / `.png`），由 `cleanupTemporaryFiles()`（`:371-376`）清理。
- `Services/PreviewController.swift:328-347` `startKeyMonitor()`：本地 `keyDown` 监听（`Space` / `Esc` 关闭、方向键切换记录）—— 新增的 `Cmd+F` / `F3` 需与它协调，避免互相吞掉事件。

## 3. Scope

**受影响**：`Services/PreviewController.swift`；新增 `Support/TextEncodingDetector.swift`（或同等职责的独立文件）；`README.md` 功能列表（中英双语）。

**本批（批次 C）额外定死的事项** —— 与 `prompt-41` 同批交付，两份都改 `startKeyMonitor()`，**必须共存**：

- **本项必须保留 `prompt-41` 的 `Cmd+=` / `Cmd+-` / `Cmd+0`**（41 先交付）。
- **本项只允许改 `.text` 分支与 `.file` 分支的「回落判定」**（`PreviewController.swift:116-140`）：  
  → `.file` 记录中**图片判定必须排在文本扩展名判定之前**（现状 `NSImage(contentsOf:)` 成功的走 `showImagePreview`）。若把文本判定提前，**图片文件（如 `.png`）会被误当成文本打开**，这是回归。
  → `.image` 分支**不得改动**（归 41）。
- **`Esc` 的真实机制（重要，本提示词 §2 的表述过于笼统）**：现有 `startKeyMonitor()`（L328-347）**只处理 `keyCode 49`（= Space）**，`SpaceClosablePanel.keyDown`（L384-394）同样只认 `49`。**代码里没有任何 `Esc`（keyCode 53）分支** —— 也就是说「`Esc` 关闭预览」目前是**响应链**给的（`NSWindow.cancelOperation:` → `performClose:`），**不是**键盘监听给的。
  → 因此查找条的 `Esc` **必须在 `startKeyMonitor()` 里拦截并 `return nil`**（否则事件会继续走到响应链，直接把预览窗口关掉）；查找条未打开时**必须 `return event`**，让 `Esc` 照旧能关预览。
  → **注意 `NSTextView` 是第一响应者**（`showTextPreview` 的 `firstResponder: textView`，L190），它自己也响应 `Esc`。请**手工确认**「查找条关闭时 `Esc` 仍能关预览」这一条（验收 C / G），若发现既有行为本来就不成立，**在报告中如实写明**，不要为了让验收项通过而伪造。

**不得改变的部分**
- **图片 / PDF / DOCX / PPTX / XLSX 仍走现有 QuickLook 分支** —— **不要**自写文档渲染引擎（macOS QuickLook 已覆盖）。
- 文字记录（`.text`）的既有内置预览入口与窗口尺寸策略。
- 既有键盘行为：`Space` / `Esc` 关闭、`←→` 切换记录（prompt-41 若已落地，`Cmd+=/-/0` 缩放也需保持）。
- `history.json` 的字段与格式（编码检测结果不落盘）。

## 4. Constraints

1. **行号栏**可开关；**自动换行**可开关（`NSTextView` 的 `isHorizontallyResizable` / `textContainer.widthTracksTextView` 组合）。
2. **查找条**：`Cmd+F` 打开；`F3` 下一个、`Shift+F3` 上一个；`Esc` 关闭查找条（**不要**与「关闭预览」的 `Esc` 冲突 —— 查找条打开时 `Esc` 先关查找条）。
3. **编码检测**：至少覆盖 BOM / UTF-8 / UTF-16 / GB18030；检测失败时给出可读的降级显示，不崩溃。
4. **大文件分段读取**，不要一次性全量载入内存；打开大文本不得卡顿。
5. 文本文件扩展名走内置文本预览：`txt / md / log / json / xml / csv / ini / yaml` 及常见代码扩展名；其余仍回落 QuickLook。
6. 只依赖 AppKit / Foundation，零第三方依赖。
7. 沿用 prompt-31 的测试底座：**编码检测是纯函数**，必须有 `swift test` 用例（含 GB18030 与 BOM 样本）；UI 交互属**手工验收**。
8. 不得为了让测试通过而弱化断言。

## 5. Acceptance

- **A** 行号开关即时生效（开 / 关各验证一次）。
- **B** 自动换行开关即时生效（长行不再横向滚动 / 恢复横向滚动）。
- **C** `Cmd+F` 打开查找条，输入关键词可定位；`F3` / `Shift+F3` 可在匹配项间跳转；`Esc` 关闭查找条且**预览不关闭**。
- **D** 编码检测正确：UTF-8（含 BOM）、UTF-16、GB18030 的中文文本均显示正常，无乱码。
- **E** 大文件（如 20 MB 的 `.log`）打开不卡顿，可正常滚动与关闭。
- **F** `.txt` / `.md` / `.json` / `.csv` 等走**内置文本预览**；`.pdf` / `.docx` / `.pptx` / `.xlsx` 仍走 **QuickLook**。
- **G** 回归：`Space` / `Esc` 关闭预览、`←→` 切换记录、图片预览分支与改动前一致。
- **H** `swift build` 与 `swift test` 通过；`swift test` 覆盖编码检测各分支。
- **I** `ClipShelf --self-test <报告>` 退出码 0。
- **J** `README.md` 中英双语功能列表补充本项。
- **K** 报告：改动文件清单、测试输出、手工验证步骤。

## 6. Handoff

- **实现**：Codex。**独立测试与复核**：WorkBuddy（只读，不改源码）。
- **授权（未勾选即未授权）**：`[ ]` 本地检查 · `[ ]` Release 构建 · `[ ]` 覆盖安装 · `[ ]` `git commit` · `[ ]` `push` · `[ ]` GitHub 发布。
  **本轮建议**：仅本地检查。
- **提交信息建议**：`Enhance built-in text preview (line numbers, wrap, find, encoding)`
