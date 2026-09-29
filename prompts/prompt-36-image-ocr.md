# prompt-36：缺口 5 · 图片记录 OCR（按需触发）

依据 `AGENTS.md` 的 Prompt and collaboration guidelines。协作约定见 `prompts/README.md`。
前置：prompt-31 已落地（测试底座可用）。本提示词只交付**图片取字**这一件事。成本最高，排在最后。

## 1. Goal

图片预览提供「识别文字」入口，**按需**调用本地 OCR；识别完成后可在图上**拖框选择**文字区域，复制所选或全部。

功能基准：Windows 端 ClipShelf 1.4.0，`ImageOcrService.cs` —— 在预览工具栏**按需**调用系统 OCR；识别后可拖框选择文字，复制所选或全部。两条明确的设计约束：**不在捕获或打开预览时自动识别**；识别耗时与降采样有明确反馈。

> macOS 侧说明：Vision 文字识别框架是**系统能力**，无需自研识别引擎；但「从图片记录取字、可选择、可复制」这个**功能**需要新建。

## 2. Evidence

**初测环境**：2026-09-27，仓库 `/Users/Zhuanz/Documents/Codex/2026-05-16/macos-windows`，HEAD `355acec`。
**复测**：2026-09-28，HEAD `0860ab3` —— `Services/PreviewController.swift` 自 `355acec` 以来 **0 次改动**，下列行号**全部仍然准确**；`Vision` / `VNRecognize` / `OCR` 全仓检索**仍为零命中**。

- `Sources/ClipShelfLite/Services/PreviewController.swift:154-172` `showImagePreview(_:title:)`：`NSPanel` + `NSScrollView` + `NSImageView`（`imageScaling = .scaleProportionallyUpOrDown`），**没有工具栏，没有任何识别相关代码**。
- `Services/PreviewController.swift:116-140` `showBuiltInPreviewIfPossible(_:)`：按 `.image` / `.text` / `.file` 分流到内置预览或 QuickLook。
- `Services/PreviewController.swift:299-317` `previewPanel(title:size:)`：统一创建 `NSPanel`（`.titled, .closable, .resizable, .utilityWindow`），`onSpace` 关闭。
- `Services/PreviewController.swift:328-347` `startKeyMonitor()`：本地 `keyDown` 监听（`Esc`/`Space` 关闭、`←→` 切换记录）；`:353-362` `previewNavigationDirection` 只识别 `keyCode 123/124`。
- `Services/PreviewController.swift:371-376` `cleanupTemporaryFiles()`：每次预览前清理上一批临时文件。
- 全仓检索 `Vision`、`VNRecognize`、`OCR` → **零命中**（实测）。

## 3. Scope

**受影响**：`Services/PreviewController.swift`；新增 `Services/ImageTextRecognizer.swift`（或同等职责的独立文件）；`README.md` 功能列表（中英双语）。

**本批（批次 C）额外定死的事项** —— 与 `prompt-41` 同批交付，两份都改图片面板，**必须分区**：

- **「识别文字」入口放面板「顶部」工具条**；`prompt-41` 的缩放控件占面板「右下角」浮层。**不得越界**。
- **本项不得占用任何快捷键**（`Cmd+=/-/0` 归 41，`Cmd+F` / `F3` / `Shift+F3` 归 42）。
- **拖框选区必须按「图片坐标」换算**：`prompt-41` 会让图片面板支持 0.5–4.0 缩放，屏幕坐标 ≠ 图片坐标。选区换算请用「图片实际显示尺寸」而非「图片像素尺寸」，否则在非 100% 缩放下选区会错位。
- **执行顺序**：本项排在 41 **之后**（41 先定下图片面板的缩放/滚动结构）。若 41 未完成，请停下并在报告中标注「拖框选择未验证」。

**不得改变的部分**
- 文字预览分支（`PreviewController.swift:174-191`）与 QuickLook 分支的行为。
- 图片预览的**默认呈现**：打开时仍是原图（`scaleProportionallyUpOrDown` 适配），不得因为本项改变默认缩放或窗口尺寸策略（`:205-248`）。
- 捕获剪贴板图片时**不做**任何识别（`ClipStore.addScreenshot` / `pollPasteboard` 不得被牵连改动）。
- `history.json` 的字段与格式（识别结果**不落盘**）。

## 4. Constraints

1. **仅点击按钮才识别**：捕获时、打开预览时**都不得**自动触发。
2. 使用系统 Vision：`VNRecognizeTextRequest`，`recognitionLanguages = ["zh-Hans", "en-US"]`，`usesLanguageCorrection = true`；识别在**后台队列**执行，主线程不阻塞。
3. **不联网**；零第三方依赖（只允许 Apple 官方框架）。
4. 识别中要有**可见的进行状态**；失败或无文字时给出**可理解的提示**，且**不阻塞预览**（预览仍可用、可关闭）。
5. 大图需**降采样**后再识别，避免卡顿；降采样与耗时在 UI 上有可感知的反馈。
6. 关闭预览时清理识别过程占用的资源（与既有 `cleanupTemporaryFiles()` 一并处理）。
7. 沿用 prompt-31 的测试底座：可自动断言的部分（如文本聚合、去重、语言配置）进 `swift test` 与 `--self-test`；**纯 UI 交互部分（拖框选择）列入手工验收**，不得为了自动化而弱化断言或伪造通过。
8. 不得为了让测试通过而弱化断言。

## 5. Acceptance

- **A** 打开图片预览**不会**自动识别（未点击按钮时无任何识别活动、无耗时增加）。
- **B** 点击「识别文字」后能拿到识别结果，并可**复制全部**文本。
- **C** 能在图上**拖框选择**区域，并**只复制所选**区域的文字。
- **D** 无文字的图片 → 给出可理解提示，不崩溃、不阻塞预览。
- **E** **断网可用**（识别完全本地）。
- **F** 大图（如 4000×3000）识别时预览窗口不卡死，可正常关闭。
- **G** 关闭预览后无残留资源（临时文件被清理）。
- **H** 回归：文字预览、QuickLook 分支、`Space`/`Esc`/`←→` 行为与改动前一致。
- **I** `swift build` 与 `swift test` 通过；`--self-test` 退出码 0。
- **J** `README.md` 中英双语功能列表补充本项。
- **K** 报告：改动文件清单、测试输出、手工验证步骤（含「未点击不识别」的实测说明与一张真实图片的识别结果）。

## 6. Handoff

- **实现**：Codex。**独立测试与复核**：WorkBuddy（只读，不改源码）。
- **授权（未勾选即未授权）**：`[ ]` 本地检查 · `[ ]` Release 构建 · `[ ]` 覆盖安装 · `[ ]` `git commit` · `[ ]` `push` · `[ ]` GitHub 发布。
  **本轮建议**：仅本地检查。
- **提交信息建议**：`Add on-demand OCR for image previews`
