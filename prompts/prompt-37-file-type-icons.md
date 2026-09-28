# prompt-37：补充项 1 · 按文件类型的差异化图标

依据 `AGENTS.md` 的 Prompt and collaboration guidelines。协作约定见 `prompts/README.md`。
前置：prompt-31 已落地（测试底座可用）。本提示词只交付**文件类型图标**这一件事。

## 1. Goal

文件类记录按扩展名显示**可区分的图标与配色**：PDF / 表格 / Word / 演示 / 压缩包 / 文件夹 / 未知类型，浅色与深色主题各一套；未知类型回退到通用文档图标。

功能基准：Windows 端 ClipShelf 1.4.0 —— PDF 红 / 表格绿 / Word 蓝 / 演示橙 / 压缩包紫 / 文件夹金，未知类型回退通用文档图标；浅色与深色分别配色；**绘制时不读取文件内容、不调用 Shell**。

## 2. Evidence

**实测环境**：2026-09-27 初测 / **2026-09-28 复测**，仓库 `/Users/Zhuanz/Documents/Codex/2026-05-16/macos-windows`。

> ⚠ **`Views/MainView.swift` 的行号已随 prompt-34 漂移**（34 新增了工具栏撤销按钮、搜索框 `@FocusState`、键盘监听的 `Cmd+Z` 分支）。
> 下列行号为 **2026-09-28 复测值**；**若与你的 checkout 不符，以符号名为准**。
> 注意漂移量**并不统一**（文件头部只 +1，尾部 +27），**不要按固定偏移量换算**。
> **`Support/AppTheme.swift` 未被改动**，其行号**仍然准确**。

- `Sources/ClipShelfLite/Views/MainView.swift:1052-1080` `ClipRow.preview`：`.file` 分支（**L1059-1062**）**统一**用 `RoundedRectangle(cornerRadius: 10)` + SF Symbol `doc` + `AppTheme.filePreviewBackground` / `filePreviewForeground` —— **没有类型维度**。
- `Views/MainView.swift:958-960`：缩略图容器尺寸 `52 × 40`，`.clipped()`；`cornerRadius: 10` 出现在文字分支（L1056）、文件分支（L1060）与图片分支（L1073 / L1075）。
- `Support/AppTheme.swift:53-60`：`filePreviewBackground` / `filePreviewForeground`（浅色偏绿、深色偏绿）—— 目前只有**一套**文件配色。
- `Support/AppTheme.swift:86-95`：`color(red:green:blue:alpha:)` 与 `adaptive(light:dark:)` 两个现成助手，新增配色直接复用。
- `Views/MainView.swift:1082-1088` `kindText`：`item.filePaths.count > 1 ? "\(count) 个文件" : "文件"`。
- 其它类型的配色范式可参考 `AppTheme.swift:45-72`（文字 / 文件 / 图片预览前景与背景）。

## 3. Scope

**受影响**：`Views/MainView.swift`（`ClipRow.preview` 的 `.file` 分支）、`Support/AppTheme.swift`、新增 `Support/FileTypeIcon.swift`、`README.md` 功能列表（中英双语）。

**不得改变的部分**
- 缩略图容器尺寸 `52 × 40` 与圆角 10；行高 58 / 74 与整体布局。
- 文字记录与图片记录的缩略图呈现（`MainView.swift:1055-1058`、`:1063-1078`）。
- 行内按钮、分隔线、选中底色等既有视觉规则。
- 不在绘制路径上新增任何 I/O。

## 4. Constraints

1. 映射关系放 `Support/FileTypeIcon.swift`（扩展名 → SF Symbol + 前景色 + 背景色），配色值加到 `AppTheme`，**浅色与深色各一套**。
2. **不读文件内容、不调用 Shell、不做磁盘探测** —— 只按路径的**扩展名**判定（Windows 端明确避免在绘制时读文件；macOS 侧同理，否则会拖慢列表滚动）。
3. **文件夹**判定不能靠扩展名：可用 `isDirectory` 之类的**一次轻量 stat**（`URLResourceValues` 缓存结果），或先按扩展名判定、仅在需要区分文件夹时做一次判定并缓存。**不得**在绘制时重复 stat 同一路径。
4. **未知扩展名回退通用文档图标**（当前 `doc` + 现有配色），不得出现空白或无图标。
5. 多文件记录按**首个路径**判定；取不到则回退通用文档图标。
6. 深色主题下前景 / 背景对比必须可读（参考 `AppTheme` 现有深色取值区间）。
7. 沿用 prompt-31 的测试底座：**扩展名 → 图标映射是纯函数**，必须有 `swift test` 用例（含未知类型回退）；断言逻辑只实现一次。
8. 不得为了让测试通过而弱化断言。

## 5. Acceptance

- **A** `.pdf` / `.xlsx` / `.docx` / `.pptx` / `.zip` / 文件夹 / 未知扩展名 → 显示**互不相同**的图标与配色。
- **B** 未知扩展名与无扩展名都回退到通用文档图标，不出现空白。
- **C** 浅色与深色主题下均清晰可读（附两张截图说明）。
- **D** 列表滚动时**不产生文件读取**（附证据：在报告中说明判定路径，或给出滚动期间的 I/O 观测）。
- **E** 文件夹与文件能区分；同一路径不重复 stat。
- **F** 多文件记录按首个路径判定；首个路径缺失时回退通用图标。
- **G** 回归：文字 / 图片缩略图、行高、圆角、分隔线、选中底色与改动前一致。
- **H** `swift build` 与 `swift test` 通过；`swift test` 覆盖各类型映射与未知回退。
- **I** `ClipShelf --self-test <报告>` 退出码 0。
- **J** `README.md` 中英双语功能列表补充本项。
- **K** 报告：改动文件清单、测试输出、手工验证步骤与截图。

## 6. Handoff

- **实现**：Codex。**独立测试与复核**：WorkBuddy（只读，不改源码）。
- **授权（未勾选即未授权）**：`[ ]` 本地检查 · `[ ]` Release 构建 · `[ ]` 覆盖安装 · `[ ]` `git commit` · `[ ]` `push` · `[ ]` GitHub 发布。
  **本轮建议**：仅本地检查。
- **提交信息建议**：`Show file-type specific icons in history list`
