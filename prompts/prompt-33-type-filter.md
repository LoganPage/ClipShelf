# prompt-33：缺口 3 · 按记录类型筛选（全部 / 文字 / 文件 / 图片）

依据 `AGENTS.md` 的 Prompt and collaboration guidelines。协作约定见 `prompts/README.md`。
前置：prompt-31 已落地（测试底座可用）。本提示词只交付**类型筛选**这一件事。

## 1. Goal

在搜索之外增加一个**记录类型**维度，取值 `全部 / 文字 / 文件 / 图片`；**与搜索叠加**生效；切换筛选后清空选择并把列表滚动位置复位到顶部。

功能基准：Windows 端 ClipShelf 1.4.0，`HistoryTypeFilter.cs`（`All` / `Text` / `File` / `Image`），与搜索叠加，切换筛选后列表滚动位置重置。

## 2. Evidence

**实测环境**：2026-09-27 初测 / **2026-09-28 复测**，仓库 `/Users/Zhuanz/Documents/Codex/2026-05-16/macos-windows`。

> ⚠ **`Views/MainView.swift` 的行号已随 prompt-34 漂移**（34 新增了工具栏撤销按钮、搜索框 `@FocusState`、键盘监听的 `Cmd+Z` 分支）。
> 下列行号为 **2026-09-28 复测值**；**若与你的 checkout 不符，以符号名为准**（函数名比行号可靠）。
> 注意漂移量**并不统一**（文件头部只 +1，尾部 +27），**不要按固定偏移量换算**。

- `Sources/ClipShelfLite/Views/MainView.swift:37-42`：`liveFilteredItems` **只做 `searchText` 文本匹配**（`SearchMatcher.matches`），**没有类型维度**。全仓检索 `kindFilter` → 零命中。
- `Views/MainView.swift:44-46`：`filteredItems` = `dragSnapshotItems ?? liveFilteredItems` —— 这是列表渲染、选择、批量操作的**唯一漏斗**（`MainView.swift:130-151` 渲染、`:519-541` 动作、`:568-572` 全选、`:923-925` 可见项都走它）。
- `Views/MainView.swift:7` `searchText`；`:208-210` `.onChange(of: searchText) { clearSelection() }` —— 搜索变化会清空选择。
- `Views/MainView.swift:342-356`：搜索栏 `HStack`（`magnifyingglass` + `TextField`，圆角 12、`AppTheme.subtleBorder` 边框）。
- `Views/MainView.swift:927-940`：`emptyView` 文案固定为「还没有记录 / 复制文字或文件，或者截一张图。」，**不区分筛选导致的空结果**。
- `Models/ClipItem.swift:4-8`：`enum Kind: String, Codable { case text, file, image }`。
- `Support/SearchMatcher.swift:3-34`：搜索匹配实现（大小写 / 全半角 / 拼音 / 首字母 / 模糊），**本项不改动它**。

> ⚠ Windows 端在这条上踩过坑：新增筛选维度若不接进「列表刷新的唯一漏斗」，会出现筛选后滚动位置与选择错位。macOS 侧同样必须走 `filteredItems`。

> **布局提示（与 prompt-38 同批交付时）**：38 要在**搜索栏右侧**放「已选 N 条」标签，因此本项的筛选控件请放在**搜索栏下方单独一行**，避免争用同一块空间。见 `prompts/batch-B.md` §1.2。

## 3. Scope

**受影响**：`Views/MainView.swift`；新增 `Support/ClipKindFilter.swift`（或放 `Models/`）；`README.md` 功能列表（中英双语）。

**不得改变的部分**
- 搜索行为本身：`Support/SearchMatcher.swift` 一行不改；搜索与筛选**叠加**，不是替换。
- 既有列表布局：行高 74 / 58（`MainView.swift:32`、`:985`）、圆角、内边距、分隔线逻辑。
- 既有快捷键语义：`Cmd+A` / `Cmd+C` / `Cmd+V`、`↑↓`、`Space`、`Enter`、`Delete`。
- 行内按钮的作用范围语义（见 `prompts/README.md` 第 4 节「刻意不对齐」）。

## 4. Constraints

1. 新增枚举 `ClipKindFilter`（`all / text / file / image`），默认 `.all`。
2. 筛选**必须**接进 `filteredItems` 漏斗（`MainView.swift:43-45`）：先按 `kind` 过滤，再走 `SearchMatcher.matches`。
3. 切换筛选时：**清空选择**（与 `searchText` 变化的行为一致）并把列表**滚动位置复位到顶部**。
4. UI 控件放在搜索栏附近（搜索栏下方一行，或搜索栏右侧），沿用现有圆角 12 / `AppTheme.subtleBorder` 边框风格；**不得挤动**搜索框的宽度与位置。
5. 筛选后无结果时，`emptyView` 文案要能区分「没有匹配当前筛选的记录」与「还没有任何记录」。
6. 沿用 prompt-31 的测试底座：断言同时进 `swift test` 与 `--self-test`；断言逻辑只实现一次。
7. 不得为了让测试通过而弱化断言。

## 5. Acceptance

- **A** 选「图片」只显示 `kind == .image` 的记录；「文字」「文件」同理；「全部」恢复完整列表。
- **B** 筛选与搜索**叠加**正确：选「文件」+ 搜索某关键词 → 只出现同时满足两者的记录。
- **C** `Cmd+A` 只选中当前筛选结果；工具栏复制 / 粘贴 / 置顶 / 删除只作用于当前筛选结果。
- **D** 切换筛选后选择被清空，滚动位置回到顶部（不出现位置与选择错位）。
- **E** 筛选无结果时文案与「无任何记录」不同。
- **F** 回归：搜索的拼音 / 首字母 / 模糊 / 全半角行为与改动前一致（`SearchMatcher` 未被改动）。
- **G** `swift build` 与 `swift test` 通过；`swift test` 至少覆盖：各筛选取值、筛选与搜索叠加、空结果判定。
- **H** `ClipShelf --self-test <报告>` 退出码 0。
- **I** `README.md` 中英双语功能列表补充本项。
- **J** 报告：改动文件清单、测试输出、手工验证步骤（含截图或录屏说明）。

## 6. Handoff

- **实现**：Codex。**独立测试与复核**：WorkBuddy（只读，不改源码）。
- **授权（未勾选即未授权）**：`[ ]` 本地检查 · `[ ]` Release 构建 · `[ ]` 覆盖安装 · `[ ]` `git commit` · `[ ]` `push` · `[ ]` GitHub 发布。
  **本轮建议**：仅本地检查。
- **提交信息建议**：`Add record type filter to history list`
