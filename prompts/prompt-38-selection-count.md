# prompt-38：补充项 2 · 工具栏显示当前选中数量

依据 `AGENTS.md` 的 Prompt and collaboration guidelines。协作约定见 `prompts/README.md`。
前置：prompt-31 已落地（测试底座可用）。本提示词只交付**选中数量显示**这一件事。

## 1. Goal

在搜索栏右侧显示当前选中条数（如「已选 3 条」）；**无选择时不显示**；出现与消失**不得挤动搜索框**。

功能基准：Windows 端 ClipShelf 1.4.0 —— 选中数量显示在搜索栏右侧。

## 2. Evidence

**实测环境**：2026-09-27 初测 / **2026-09-28 复测**，仓库 `/Users/Zhuanz/Documents/Codex/2026-05-16/macos-windows`。

> ⚠ **`Views/MainView.swift` 的行号已随 prompt-34 漂移**（34 新增了工具栏撤销按钮、搜索框 `@FocusState`、键盘监听的 `Cmd+Z` 分支）。
> 下列行号为 **2026-09-28 复测值**；**若与你的 checkout 不符，以符号名为准**。
> 注意漂移量**并不统一**（文件头部只 +1，尾部 +27），**不要按固定偏移量换算**。

- `Sources/ClipShelfLite/Views/MainView.swift:342-356`：搜索栏是一个 `HStack`（`magnifyingglass` 图标 + `TextField`），外层 `.padding(.horizontal, 12)` / `.padding(.vertical, 9)`、圆角 12、`AppTheme.subtleBorder` 边框 —— **右侧没有任何计数显示**。
- `Views/MainView.swift:9`：`@State private var selectedIDs = Set<ClipItem.ID>()` —— 选中集合的**唯一来源**（此行号未漂移）。
- `Views/MainView.swift:543-549`：`copyActionHelp` / `pasteActionHelp` 已含「选中的 N 条记录」文案，但**只出现在 tooltip 里**，界面上看不到。
- `Views/MainView.swift:568-572` `selectAllVisibleItems()`：`Cmd+A` 全选当前筛选结果；`:384-391` `clearSelection()` 清空。
- `Views/MainView.swift:130-151`：列表由 `filteredItems` 驱动，行内选中态由 `selectedIDs.contains(item.id)` 决定。

> **布局提示（与 prompt-33 同批交付时）**：33 会在**搜索栏下方单独一行**放类型筛选控件，因此本项的计数标签请放在**搜索栏右侧**。见 `prompts/batch-B.md` §1.2。
> **前置依赖**：本项验收 **C**（全选后数值等于当前**筛选结果**条数）与 **E**（切换**类型筛选**后标签消失）依赖 prompt-33 已交付。

## 3. Scope

**受影响**：`Views/MainView.swift`（`toolbar` 的搜索栏 `HStack`）、`README.md` 功能列表（中英双语）。

**不得改变的部分**
- 搜索框的**宽度与位置**：计数标签出现 / 消失时搜索框不得发生位移（用固定宽度占位或 `overlay` 对齐）。
- 搜索框的行为：输入、`Esc` 清空、`onChange(of: searchText) → clearSelection()`（`MainView.swift:207-209`）不变。
- 工具栏第一行（应用图标、状态文字、更新按钮、文件夹 / 设置 / 复制 / 粘贴 / 置顶 / 删除按钮）的既有顺序与间距。
- 行内按钮与 tooltip 的既有文案。

## 4. Constraints

1. 标签文案形如「已选 N 条」；`selectedIDs.isEmpty` 时**隐藏**（不显示「已选 0 条」，也不留空白占位导致视觉抖动）。
2. 布局上必须保证：标签出现 / 消失 / 数字位数变化（1 位 → 3 位）时，**搜索框的宽度与水平位置不变**。
3. 数值必须来自 `selectedIDs` 这一唯一来源，**不要另建计数状态**，避免与拖选、全选、清空、筛选切换不同步。
4. 视觉沿用现有风格（caption 字号、`Color.secondary`），不得引入新配色或新控件样式。
5. 沿用 prompt-31 的测试底座：可自动断言的部分（如计数文案生成规则）进 `swift test`；纯 UI 位移部分列**手工验收**，不得为了自动化而弱化断言。
6. 不得为了让测试通过而弱化断言。

## 5. Acceptance

- **A** 无选择时不显示计数标签。
- **B** 单选 1 条 → 显示「已选 1 条」；多选 3 条 → 「已选 3 条」。
- **C** `Cmd+A` 全选 → 数值等于当前筛选结果条数。
- **D** 清空选择（`Esc` / 点击空白 / 失活）→ 标签消失。
- **E** **切换类型筛选或改变搜索词后**（选择被清空）标签同步消失，不出现残留计数。
- **F** 三指拖移选择过程中数值实时正确（拖选结束后的最终值正确）。
- **G** 标签出现 / 消失 / 位数变化时，**搜索框宽度与位置不变**（附前后截图对比）。
- **H** 回归：搜索框输入、`Esc` 清空搜索、`Cmd+A/C/V` 行为与改动前一致。
- **I** `swift build` 与 `swift test` 通过；`--self-test` 退出码 0。
- **J** `README.md` 中英双语功能列表补充本项。
- **K** 报告：改动文件清单、测试输出、手工验证步骤与截图。

## 6. Handoff

- **实现**：Codex。**独立测试与复核**：WorkBuddy（只读，不改源码）。
- **授权（未勾选即未授权）**：`[ ]` 本地检查 · `[ ]` Release 构建 · `[ ]` 覆盖安装 · `[ ]` `git commit` · `[ ]` `push` · `[ ]` GitHub 发布。
  **本轮建议**：仅本地检查。
- **提交信息建议**：`Show selected record count in the toolbar`
