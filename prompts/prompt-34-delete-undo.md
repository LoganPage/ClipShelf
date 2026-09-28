# prompt-34：缺口 4 · 删除可撤销（本次运行内最近 10 批）

依据 `AGENTS.md` 的 Prompt and collaboration guidelines。协作约定见 `prompts/README.md`。
前置：prompt-31 已落地（测试底座可用）。本提示词只交付**删除撤销**这一件事。

## 1. Goal

删除与清空历史可撤销：`Cmd+Z` 或工具栏撤销按钮可恢复最近一批删除，保留原内容、时间与置顶状态；**本次运行内最多 10 批**；**不支持重做**。

功能基准：Windows 端 ClipShelf 1.4.0，`MainWindow.Commands.cs` 的 `UndoHistoryDelete`（`Ctrl+Z` 或工具栏按钮，最近 10 批；搜索框内的 `Ctrl+Z` 仍是文字编辑撤销）。

## 2. Evidence

**实测环境**：2026-09-27 初测 / 2026-09-28 复测，仓库 `/Users/Zhuanz/Documents/Codex/2026-05-16/macos-windows`。

> ⚠ **`Stores/ClipStore.swift` 的行号已随 prompt-32 漂移，且交付时 prompt-43 可能仍在修改该文件。**
> 下列行号为 2026-09-28 实测值，**仅供参考**；若与你的 checkout 不符，**以符号名为准**。

- `Sources/ClipShelfLite/Stores/ClipStore.swift:77` `remove(_ item:)`：`items.removeAll { $0.id == item.id }` + `save()`，**无任何记录/入栈**。
- `Stores/ClipStore.swift:82` `remove(ids:)`：同上。
- `Stores/ClipStore.swift:107` `clearHistory()`：`items.removeAll()` + `save()`，**无入栈**。
- `Stores/ClipStore.swift:338` `sortItems()`：置顶优先、其余按 `createdAt` 降序 —— 恢复后须复用它。
- `Views/MainView.swift:363-370` `deleteSelectedOrClear()`：无选择时 `clearHistory()`，有选择时 `remove(ids:)`。
- `Views/MainView.swift:468-479` `handleCommandKey(_:)`：**只处理 `a` / `c` / `v`**，没有 `z`。全仓检索 `undo`、`撤销` → **零命中**（实测）。
- `Views/MainView.swift:408-448` `installCommandKeyMonitorIfNeeded()`：本地 `keyDown` 监听，L440-444 只拦截 `Cmd+A/C/V`；`Views/MainView.swift:450-459` `shouldHandleMainWindowKeyEvent` 只排除「设置面板打开 / 非 key window / 标题不是 ClipShelf」，**不排除搜索框聚焦**。
- `Views/MainView.swift:322-328` 工具栏删除按钮；`:301 / :310 / :319` 有现成的「无操作对象时淡显」写法 `actionItems.isEmpty ? Color.secondary.opacity(0.38) : Color.secondary`。
- `Views/MainView.swift:977-983` `ClipRow` 行内删除按钮（`store.remove(item)`）。

## 3. Scope

**受影响**：`Stores/ClipStore.swift`、`Views/MainView.swift`、`README.md` 功能列表（中英双语）。

**不得改变的部分**
- 删除的既有语义与入口：工具栏删除按钮、`Delete` 键、行内删除按钮、设置面板「清空历史」，行为结果不变（只是现在可撤销）。
- **既有的 `Cmd+A` / `Cmd+C` / `Cmd+V` 拦截行为不在本提示词范围内，不要顺带改动。**
- 行内按钮的作用范围语义（`MainView.swift:505-514`，见 `prompts/README.md` 第 4 节「刻意不对齐」）。
- `history.json` 的字段与格式。

## 4. Constraints

1. 撤销栈结构：每批记录**被删项本身**及其在 `items` 中的**原始索引**（用于恢复顺序）；容量 **10 批**，仅**本次运行内**有效（应用启动时栈为空）。
2. 恢复时保留原 `id` / `createdAt` / `isPinned`；恢复后重新 `sortItems()` 并 `save()`。
3. **容量边界**：若恢复会超过当前历史上限，只恢复「剩余空间内」的记录，**不得挤掉删除之后新收到的记录**。
4. `clearHistory()`（清空全部）**也要入栈**，可被撤销。
5. **不支持重做**；撤销栈空时撤销入口禁用并淡显（复用 §2 提到的现成淡显写法）。
6. **关键约束**：搜索框获得焦点时，`Cmd+Z` 必须仍然走系统文字撤销 —— 在按键监听层（`shouldHandleMainWindowKeyEvent` 或 `installCommandKeyMonitorIfNeeded`）排除「搜索框聚焦」场景，**不要拦截**。
7. 沿用 prompt-31 的测试底座：断言同时进 `swift test` 与 `--self-test`；断言逻辑只实现一次。
8. 不得为了让测试通过而弱化断言。

## 5. Acceptance

- **A** 删除 3 条 → `Cmd+Z` → 3 条全部恢复，**顺序、`createdAt`、置顶状态与删除前一致**。
- **B** 连续执行 11 批删除 → 只能撤销最近 10 批；第 11 批之前的不再可撤销。
- **C** 清空全部 → `Cmd+Z` → 全部恢复。
- **D** 重启后撤销栈为空（撤销入口禁用）。
- **E** 撤销栈为空时按钮禁用并淡显。
- **F** **搜索框内 `Cmd+Z` 仍是文字撤销**（不触发记录撤销）—— 本项为最高优先级回归。
- **G** 删除后新复制内容再撤销时，新内容**不被挤掉**（容量边界正确）。
- **H** 回归：`Delete` 键、工具栏删除、行内删除、清空历史的结果与改动前一致；`Cmd+A/C/V` 行为未变。
- **I** `swift build` 与 `swift test` 通过；`swift test` 至少覆盖：10 批上限、容量边界、顺序与置顶恢复、清空可撤销。
- **J** `ClipShelf --self-test <报告>` 退出码 0。
- **K** `README.md` 中英双语功能列表补充本项。
- **L** 报告：改动文件清单、测试输出、手工验证步骤（含搜索框 `Cmd+Z` 的实测说明）。

## 6. Handoff

- **实现**：Codex。**独立测试与复核**：WorkBuddy（只读，不改源码）。
- **授权（未勾选即未授权）**：`[ ]` 本地检查 · `[ ]` Release 构建 · `[ ]` 覆盖安装 · `[ ]` `git commit` · `[ ]` `push` · `[ ]` GitHub 发布。
  **本轮建议**：仅本地检查。
- **提交信息建议**：`Add undo for history deletions (last 10 batches per session)`
