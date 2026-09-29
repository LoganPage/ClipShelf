# prompt-41：图片预览缩放（50%–400%）

依据 `AGENTS.md` 的 Prompt and collaboration guidelines。协作约定见 `prompts/README.md`。
前置：prompt-31 已落地（测试底座可用）。本提示词只交付**图片缩放**这一件事。

> 来源说明：本项由 Mac 端上一轮勘察识别（对应 `docs/parity/codex-prompt.md` 的 P1-9），**未列入 Windows 交接说明的六项缺口清单**。它是**功能**而非交互规则，故按用户口径纳入本轮。

## 1. Goal

图片预览支持 **50%–400%** 缩放；缩放后保留当前画面位置；关闭再打开时复位到 100%。

功能基准：Windows 端 ClipShelf 1.4.0 的图片预览支持 50%–400% 缩放。

## 2. Evidence

**初测环境**：2026-09-27，仓库 `/Users/Zhuanz/Documents/Codex/2026-05-16/macos-windows`，HEAD `355acec`。
**复测**：2026-09-28，HEAD `0860ab3` —— `Services/PreviewController.swift` 自 `355acec` 以来 **0 次改动**，下列行号**全部仍然准确**。

- `Sources/ClipShelfLite/Services/PreviewController.swift:154-172` `showImagePreview(_:title:)`：`NSPanel` + `NSScrollView`（`hasVerticalScroller` / `hasHorizontalScroller`）+ `NSImageView`，`imageScaling = .scaleProportionallyUpOrDown`。**没有缩放控件，没有缩放状态**。
- `Services/PreviewController.swift:116-140` `showBuiltInPreviewIfPossible(_:)`：`.image` 与「能作为图片打开的文件」走 `showImagePreview`；`.text` 走 `showTextPreview`；其余回落到 QuickLook。
- `Services/PreviewController.swift:328-347` `startKeyMonitor()`：本地 `keyDown` 监听，处理 `keyCode 49`（Space / Esc 关闭）与方向键切换记录。
- `Services/PreviewController.swift:353-362` `previewNavigationDirection(for:)`：**只要带有 `command` / `option` / `control` 任一修饰键就直接返回 `nil`** —— 因此 `Cmd+=` / `Cmd+-` / `Cmd+0` **不会**与既有的 `←→` 切换记录冲突（这点已核实，可放心使用带 `Cmd` 的快捷键）。
- `Services/PreviewController.swift:205-248`：窗口推荐尺寸与上限策略（`recommendedImageWindowSize` / `maximumPreviewWindowSize` / `constrainedPreviewSize`）。
- `Services/PreviewController.swift:299-317` `previewPanel(title:size:)`：统一创建 `NSPanel`（`.titled, .closable, .resizable, .utilityWindow`）。

## 3. Scope

**受影响**：`Services/PreviewController.swift`、`README.md` 功能列表（中英双语）。

**本批（批次 C）额外定死的事项** —— 与 `prompt-36` 同批交付，两份都改图片面板，**必须分区**：

- **缩放控件放面板「右下角」浮层**；`prompt-36` 的「识别文字」工具条放面板「顶部」。**不得越界**。
- **100% 的定义**：**100% ≜ 适配窗口的比例**（即现状 `scaleProportionallyUpOrDown` 的呈现）。打开预览时显示 **100%**，画面为适配窗口；「复位」回到 100%；`Cmd + 滚轮` 是在**适配比例之上**再乘 0.5–4.0。**不要把 100% 实现成 1:1 像素** —— 那会改变「打开时仍是适配窗口」这条既有呈现（§3 已列为不得改变）。
- **新增的 `scrollWheel` 监听必须在 `stopKeyMonitor()`（`PreviewController.swift:364-369`）里一并移除**。现有 `stopKeyMonitor()` 只移除一个 `keyMonitor`；本项引入第二个监听后若不在那里清掉，**关闭预览后滚轮仍会被拦截**。
- **保留给 `prompt-42` 的快捷键**：`Cmd+F` / `F3` / `Shift+F3` 属 42，本项**不要**占用。

**不得改变的部分**
- **文字预览分支**（`PreviewController.swift:174-191`）与 **QuickLook 分支**的行为。
- 图片预览的**默认呈现**：打开时仍是「适配窗口」的 100% 视图；窗口尺寸策略（`:205-248`）不变。
- 既有键盘行为：`Space` / `Esc` 关闭、`←→` 切换记录、`onNavigate` 回调语义。
- `history.json` 的字段与格式（缩放比例**不落盘**，每次打开复位）。

## 4. Constraints

1. 缩放范围 **0.5–4.0**（50%–400%），**越界必须被夹紧**，不得放大到超出上限或缩小到 0。
2. 交互方式：`Cmd + 滚轮`，以及窗口角落的 `+` / `−` / `复位` 按钮；**并显示当前百分比**。
3. 缩放锚点取**当前视口中心**：缩放后保持画面位置（不要跳回左上角）。
4. 可选快捷键：`Cmd+=` / `Cmd+-` / `Cmd+0`（复位）。已核实不会与 `←→` 冲突（见 §2）。
5. **关闭预览再打开时复位到 100%**（`preview(_:)` 每次都会新建面板，天然满足；但需确认缩放状态没有残留到下一次打开）。
6. 只依赖 AppKit，零第三方依赖。
7. 沿用 prompt-31 的测试底座：缩放比例的**夹紧与复位规则**是纯逻辑，必须有 `swift test` 用例；实际渲染属**手工验收**。
8. 不得为了让测试通过而弱化断言。

## 5. Acceptance

- **A** `Cmd + 滚轮` 可缩放；缩放比例被限制在 50%–400% 之内。
- **B** 角落按钮 `+` / `−` / `复位` 均可用；`复位` 回到 100%。
- **C** 当前百分比显示正确，且随操作即时更新。
- **D** 缩放后画面位置保持在视口中心附近（不跳回左上角）。
- **E** 关闭预览再打开同一张图 → 缩放回到 100%。
- **F** 回归：`Space` / `Esc` 关闭、`←→` 切换记录、`onNavigate` 同步主列表选中 —— 与改动前一致。
- **G** 回归：文字预览与 QuickLook 分支行为不变（打开一个 `.txt` 与一个 `.pdf` 各验证一次）。
- **H** 极大图片（如 4000×3000）缩放到 400% 时不卡死、可正常关闭。
- **I** `swift build` 与 `swift test` 通过；`--self-test` 退出码 0。
- **J** `README.md` 中英双语功能列表补充本项。
- **K** 报告：改动文件清单、测试输出、手工验证步骤与截图。

## 6. Handoff

- **实现**：Codex。**独立测试与复核**：WorkBuddy（只读，不改源码）。
- **授权（未勾选即未授权）**：`[ ]` 本地检查 · `[ ]` Release 构建 · `[ ]` 覆盖安装 · `[ ]` `git commit` · `[ ]` `push` · `[ ]` GitHub 发布。
  **本轮建议**：仅本地检查。
- **提交信息建议**：`Add zoom support to image previews (50%-400%)`
