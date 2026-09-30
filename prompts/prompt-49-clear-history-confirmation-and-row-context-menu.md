# prompt-49：清空历史加确认 + 记录行右键菜单

依据 `AGENTS.md` 的 Prompt and collaboration guidelines。协作约定、编号与两端口径对齐见 `prompts/README.md`；本批全局口径与同批分工见 **`prompts/batch-E.md`**（**必读**）。

> **来源**：`docs/parity/windows-1.4.1-vs-macos.md` §5.4 与 §3.4 第二行。
> **依赖**：**prompt-47 必须先交付**（本份的断言块要接在 47 的断言块之后）。

## 1. Goal

1. **「清空全部」需要确认**（对齐 Windows 的功能）：macOS 现在从三个 UI 入口都能**一次性、无提示地**清空全部历史，Windows 侧要求确认。**补上确认**。
2. **记录行右键菜单**（对齐 Windows 的记录菜单功能）：macOS 现在记录行上**没有右键菜单**，批量操作只能走工具栏。**加一个 macOS 原生右键菜单**作为功能入口。

> ⚠️ **本批口径（用户 2026-09-30 明确）：只同步功能，不同步 UI / 美术风格 / 交互逻辑。**
> 所以：**能力要补，样子和交互一律按 macOS 自己的习惯**。不照搬 Windows 的菜单分组、图标、自绘对话框、`Shift+F10` 键位。

## 2. Evidence（2026-09-30 实测，HEAD `5b899fc`）

### 2.1 `clearHistory()` 一共有 4 个调用点，但只有 3 个是 UI

| # | 位置 | 入口 | 是否要加确认 |
| --- | --- | --- | --- |
| 1 | `Sources/ClipShelfLite/App/AppDelegate.swift:113-114` | 菜单栏「清空历史」 | ✅ 要 |
| 2 | `Sources/ClipShelfLite/Views/SettingsView.swift:293-295` | 设置页「清空历史」按钮 | ✅ 要 |
| 3 | `Sources/ClipShelfLite/Views/MainView.swift:422-429` `deleteSelectedOrClear()` | 工具栏垃圾桶按钮 / `Delete` 键（**无选择时**走 `store.clearHistory()`） | ✅ 要 |
| 4 | `Sources/ClipShelfLite/Services/RuntimeControlServer.swift:196-205` | `--ctl clear`（**脚本驱动通道**） | ⛔ **绝对不能加** |

> 🔴 **第 4 个是本次最大的坑**：`--ctl clear` 由 `tests/parity/` 的脚本驱动。**任何弹窗都会让自动化挂死。**
> → **确认逻辑一律不得写进 `ClipStore.clearHistory()`**，只能加在 3 个 UI 入口处。

### 2.2 macOS 现在没有任何确认对话框

```
grep -n "alert\|NSAlert\|confirmationDialog" Sources/ClipShelfLite/**/*.swift   →  零命中
```

`MainView.swift:422-429`：

```swift
private func deleteSelectedOrClear() {
    if selectedIDs.isEmpty {
        store.clearHistory()          // ← 无选择时直接清空全部，零确认
    } else {
        store.remove(ids: selectedIDs)
    }
    clearSelection()
}
```

`SettingsView.swift:293-295`：`Button("清空历史", role: .destructive) { store.clearHistory() }` —— 直接执行。
`AppDelegate.swift:113-114`：`@objc func clearHistory() { store.clearHistory() }` —— 直接执行。

### 2.3 `ClipStore.clearHistory()` 的语义（**不许改**）

`Sources/ClipShelfLite/Stores/ClipStore.swift:116-125`：

```swift
func clearHistory() {
    guard !items.isEmpty else { return }
    deletionUndoStack.record(items.enumerated().map { index, item in
        HistoryDeletionEntry(item: item, originalIndex: index)
    })
    updateUndoAvailability()
    items.removeAll()
    save()
}
```

→ 已支持 `Cmd+Z` 撤销（prompt-34）。**加确认不能动这里。**

### 2.4 记录行现在没有右键菜单

- 全仓 `contextMenu` **零命中**。
- `NSMenu` 只用在菜单栏状态项：`App/AppDelegate.swift:170` `rebuildMenu(_:)`。
- `Views/MainView.swift:990-1002` `private struct ClipRow` 的入参：

  ```swift
  let item: ClipItem
  @ObservedObject var store: ClipStore
  let isFocused, isSelected: Bool
  let selectionColor: Color
  let selectionColorIsPreset: Bool
  let showsSeparator: Bool
  let handleClick: (NSEvent?) -> Void
  let handleCopy: () -> Void
  let handlePaste: () -> Void
  ```

  行内已有的 4 个按钮（`MainView.swift:1027-1057`）：

  | 按钮 | 调用 | 作用域 |
  | --- | --- | --- |
  | 置顶 | `store.togglePinned(item)` | **只本行** |
  | 复制 | `handleCopy()` → `MainView.swift:147` 传的是 `store.copy(actionItems(for: item))` | **属于当前选择则全部选中** |
  | 粘贴 | `handlePaste()` → `MainView.swift:148` 传的是 `pasteRowItems(actionItems(for: item))` | 同上 |
  | 删除 | `store.remove(item)` | **只本行** |

- 既有作用域规则在 `MainView.swift:579-588`：

  ```swift
  private func actionItems(for rowItem: ClipItem) -> [ClipItem] {
      if selectedIDs.contains(rowItem.id) {
          let selected = filteredItems.filter { selectedIDs.contains($0.id) }
          if !selected.isEmpty { return selected }
      }
      return [rowItem]
  }
  ```

### 2.5 可用的 store API（右键菜单只能用这些，不许新增行为）

`Stores/ClipStore.swift`：`copy(_:)` / `copy(_ items:)` / `paste(_:)` / `paste(_ items:)` / `remove(_:)` / `remove(ids:)` / `togglePinned(_:)` / `togglePinned(ids:)` / `clearHistory()`。

### 2.6 Windows 的做法（**只作功能参照**）

- `Delete` / 工具栏删除：**只删除选中项，无选择时不执行**。
- 「清空全部」是**设置与列表空白菜单中的独立命令**，**需要确认**。
- 记录右键菜单：右键或 `Shift+F10` 打开，支持批量操作。

> ⚠️ 其中「**无选择时不执行**」是 **Windows 的交互逻辑**，按用户口径**不同步** —— macOS 保持「无选择时清空全部」，只是现在要先确认（见 §3 与 §4.3）。

### 2.7 断言基线

`Sources/ClipShelfLite/Support/SelfTest.swift`：**prompt-47 交付后为 42 条**。本份的断言块接在 **47 的断言块之后**、`self test defaults suite file count does not grow`（**L671 附近，必须保持整段最后**）**之前**。

## 3. Scope

**受影响**

- `Sources/ClipShelfLite/Views/MainView.swift`
  - `deleteSelectedOrClear()`（L422-429）加确认
  - `ClipRow`（L990 起）加 `.contextMenu`
- `Sources/ClipShelfLite/Views/SettingsView.swift` —— 「清空历史」按钮（L293-295）加确认
- `Sources/ClipShelfLite/App/AppDelegate.swift` —— **只动 `clearHistory()`（L113-115）**
- `Sources/ClipShelfLite/Support/SelfTest.swift` —— **只在 47 的断言块之后、L671 之前追加**

**建议新增**（放 `Support/`，与既有风格一致）

- `Sources/ClipShelfLite/Support/ClearHistoryConfirmation.swift` —— **纯函数**，例如：
  ```swift
  enum ClearHistoryConfirmation {
      /// 空历史不该弹确认（既没意义，也会让「无选择按删除」在空列表上多一步）
      static func shouldConfirm(itemCount: Int) -> Bool
      static func title(itemCount: Int) -> String
      static func message(itemCount: Int) -> String
  }
  ```
- 右键菜单的**结构**也要可断言，例如：
  ```swift
  enum ClipRowMenuAction: CaseIterable { case copy, paste, pin, delete }
  enum ClipRowMenu {
      static let orderedActions: [ClipRowMenuAction] = [.copy, .paste, .pin, .delete]
      static func pinTitle(isPinned: Bool) -> String
  }
  ```

**不得改变的部分**

- **`Services/RuntimeControlServer.swift` 的 `--ctl clear` 一个字都不许动。**
- **`ClipStore.clearHistory()` 的语义与撤销栈行为**（`Stores/ClipStore.swift:116-125`）。
- **macOS 现有的选择 / 键盘 / 拖选逻辑**：`handleRowClick`、`handleSingleSelectionClick`、`handleMultiSelectionClick`、`handleKey`、`handleCommandKey`、`installCommandKeyMonitorIfNeeded`、`DragSelectionCaptureView`（`MainView.swift:1425`）、`KeyboardCaptureView`（`:1374`）**全部不动**。
- **`--ctl status` 的 JSON 字段名与结构。**
- `Package.swift`、`tests/parity/`、`docs/`、`README.md`、`Assets/` 一律不动。
- **prompt-47 的断言与其余 41 条断言一条都不许改、不许删。**
- **不改任何配色、间距、圆角、图标**（本份**不碰美术风格**）。

## 4. Constraints

1. **确认只在 UI 层**（§2.1 的第 1/2/3 号入口）。**绝不放 `ClipStore.clearHistory()`。**
2. **判定逻辑必须是纯函数**（`itemCount` → `Bool` / `String`），以便断言覆盖 **0 条 / 1 条 / 多条** 三条分支。
3. **按 macOS 习惯实现，不照搬 Windows**：
   - 确认用 SwiftUI 原生 **`confirmationDialog`** 或 **`alert`**；**不要**自绘对话框。
   - 右键菜单用 SwiftUI 原生 **`.contextMenu`**；**不要**照搬 Windows 的菜单分组 / 图标 / 自绘样式；**不要**加 `Shift+F10` 键位绑定（macOS 上右键即触发）。
   - 文案用 macOS 惯例措辞（`role: .destructive` 的「清空」+「取消」），不要抄 Windows 的措辞。
4. **右键菜单的作用域必须与同一行的行内按钮完全一致**（§2.4 那张表），**不得引入新规则**：
   - 复制 / 粘贴 → 走既有的 `actionItems(for:)`（属于当前选择则作用于全部选中）
   - 置顶 / 删除 → **只作用本行**
5. **不得**把 `Delete` 键在「无选择」时的行为改成「不执行」—— macOS 保持「清空全部」，只是先确认。
6. **空历史时不该弹确认**（`shouldConfirm(itemCount: 0) == false`），否则空列表按删除会多一步无意义交互。
7. **新增断言 ≥2 条**：一条覆盖清空确认的纯函数，一条覆盖右键菜单的结构（项集合与顺序、置顶标题随 `isPinned` 变化）。
8. **不引入第三方依赖。**

## 5. Acceptance

- **A** `swift build --disable-sandbox` 通过；`swift test --disable-sandbox --scratch-path /tmp/cs-batch-e` 通过（**不要**用仓库内的 `.build`）。贴关键输出。
- **B（人工实测，必须做，不许跳过）** 三个 UI 入口逐个验证：
  1. 菜单栏 →「清空历史」
  2. 设置页 →「清空历史」
  3. 工具栏垃圾桶按钮（**先清空选择**）与 `Delete` 键（**先清空选择**）
  - 每个入口都要验：**弹出确认**；点「取消」→ 历史**一条不少**；点「确认」→ 清空且 `Cmd+Z` 能撤销回来。
  - **贴出每个入口的实测记录（做了什么、看到什么）。** 弹窗无法自动化，**必须人工做，如实记录**，不许写「应该会弹」。
  - 另测：**历史为空时**按 `Delete`，**不应**弹确认。
- **C（回归，必须做）** `--ctl clear` **不弹任何对话框**且行为不变：跑 `tests/parity/runtime_control.sh`，贴输出。
- **D（人工实测）** 右键菜单：
  - 在记录上右键 → 出现菜单，四个菜单项（复制 / 粘贴 / 置顶或取消置顶 / 删除）行为正确。
  - **多选时**：右键**已选中**的项 → 复制/粘贴作用于全部选中、置顶/删除只作用该行（与行内按钮一致）；右键**未选中**的项 → 复制/粘贴只作用该行。
  - **拖选不受影响**：右键菜单出现后，鼠标拖选仍然正常（这是本份最大的回归风险，必须实测）。
- **E** 回归：
  - 既有 42 条断言**零改动、零删除**；`Result: 44/44 checks passed.`（或你实际新增条数对应的数字），退出码 0，**连跑 3 次**。
  - `tests/parity/run.sh` 隔离模式 **0 FAIL**（命令见 `tests/parity/README.md`）。
- **F** 报告：`prompts/prompt-49-clear-history-confirmation-and-row-context-menu-report.md`，含：改动清单、A–E 的原始输出、**B 与 D 的人工实测记录**、以及一节**「本份有没有越过『只同步功能』这条线」的自查**（逐条回答：有没有抄 Windows 的菜单样式/键位/对话框样式/交互规则）。

## 6. Handoff

- **实现**：Codex。**独立测试与复核**：WorkBuddy（只读，不改源码）。
- **授权（未勾选即未授权）**：`[ ]` 本地检查 · `[ ]` Release 构建 · `[ ]` 覆盖安装 · `[ ]` `git commit` · `[ ]` `push` · `[ ]` GitHub 发布。
  **本轮建议**：仅本地检查。
- **提交信息建议**：`Confirm clearing history and add a record context menu`
