# prompt-53：删除自动粘贴与全部「粘贴」入口 + 删除行内「已置顶」文字 + 新增「按天数清理旧历史」

**编号**：53
**来源**：`docs/parity/windows-1.4.1-removals-vs-macos.md`（2026-10-01 实测）
**性质**：**两份删除（R1 / R4）+ 一份新增（G1）**。删除项是「Windows 已删、macOS 仍保留」，新增项是「Windows 有、macOS 没有」。
**基线**：本机 `main` = **`f55782f`**（prompt-51 已验收）

---

## 1. Goal

1. **R1 —— 删除自动粘贴，以及全部 4 个「粘贴」入口。** Windows 在 1.0.12 彻底移除了自动粘贴（删掉顶部 / 行内 / 右键菜单入口、Enter 与 Ctrl+V 记录命令、外部窗口追踪与模拟按键），理由是**冗余**：复制到剪贴板后用户本来就要在目标处自己按 Cmd+V，应用再模拟一次按键既多余又脆弱。macOS 侧要跟上。
2. **R4 —— 删除记录行里的「已置顶」文字标签。** Windows 1.0.11 把文字去掉了，置顶状态只由大头针变强调色表达（文字确实与图标重复）。macOS 侧要跟上。
3. **G1 —— 新增「按天数清理旧历史」。** Windows 有「保留最近 7 / 30 / 90 天，先查看待清理数量、再确认执行，只清未置顶、可撤销」。macOS 目前**只有按条数上限淘汰**，没有按时间这条轴。这是本次唯一的**新增功能**。

> **不做**（明确排除，别顺手做）：
> - 保留 macOS 的**四种图标可选**（`AppIconPreferences`）—— 纯外观偏好，不构成冗余。
> - 保留 macOS 的**菜单栏「最近 8 条记录」子菜单**（`AppDelegate.rebuildMenu`）—— Windows 删它是因为任务栏菜单有尺寸限制，macOS 菜单栏没有这个问题。**本份只删掉该子菜单里的「粘贴」一项，「复制」保留。**
> - 保留 macOS 设置里的「多选后点击已选/未选记录」「点击恢复期」—— 触控板三指拖移特有的补偿设置。
> - 保留 `Delete` 无选择时「清空全部」的语义（Windows 是「不执行」，macOS 不动）。
> - 保留「列表空白区右键菜单」与右键菜单里的「预览 / 撤销删除」项（属交互便利，现行口径不对齐交互）。

---

## 2. Evidence（2026-10-01 实测，HEAD `f55782f`）

> ⚠️ **行号已随 prompt-50 / 51 漂移，且漂移量不统一。下列为 2026-10-01 实测值，仅供参考；若与你的 checkout 不符，一律以「符号名」为准，不要按固定偏移量换算。**

### 2.1 「粘贴」在 macOS 侧一共有 4 个入口 + 2 条键盘路径（这就是要删干净的全集）

| # | 入口 | 位置（实测行号，仅供参考） |
| --- | --- | --- |
| 1 | **工具栏**「粘贴」按钮 | `Views/MainView.swift:330-338`（`Button { pasteActionItems() }` + `.floatingTooltip(pasteActionHelp)`） |
| 2 | **行内**「粘贴」按钮 | `Views/MainView.swift` 的 `ClipRow` 内 `:1059-1067`（`Button { handlePaste() }`） |
| 3 | **菜单栏最近记录子菜单**里的「粘贴」 | `App/AppDelegate.swift:213-215` |
| 4 | **行右键菜单**里的「粘贴」 | `Views/MainView.swift:1102-1105`（`case .paste:` 分支） |
| 5 | **键盘：Return / 小键盘 Enter** | `Views/MainView.swift` 的 `handleKey` → `case 36, 76:`（`:481-484`） |
| 6 | **键盘：Cmd+V** | `Views/MainView.swift` 的 `handleCommandKey` → `case "v":`（`:563-564`） |

**自动粘贴的本体**：`Services/PasteController.swift`（整份 20 行）—— `NSApp.hide(nil)` + 0.12 s 后 `CGEvent` 发 Cmd+V。

**调用链**：`ClipStore.paste(_ item:)` / `paste(_ items:)`（`Stores/ClipStore.swift:56-64`）→ `writeToPasteboard` + `PasteController.paste()`。
删掉 6 个入口后，`ClipStore.paste` 的**全部调用点**（`MainView.swift:482 / :564 / :625 / :630`、`AppDelegate.swift:233`）都会消失，于是 `ClipStore.paste` 两方法、`PasteController` 都可以安全删除。

### 2.2 ⚠️ 关键接缝：Cmd+V 是被「吞键列表」吞掉的，**只删 `case "v"` 会造成新故障**

`MainView.swift` 的 `installCommandKeyMonitorIfNeeded()` 里有一段：

```swift
if event.modifierFlags.contains(.command),
   ["a", "c", "v", "z"].contains(event.charactersIgnoringModifiers?.lowercased()) {
    handleCommandKey(event)
    return nil          // ← 吞掉事件，不再往下传
}
```

`shouldHandleMainWindowKeyEvent`（同文件）**不排除搜索框聚焦**（只有 Cmd+Z 那一条特判了 `isSearchFocused`）。

**后果**：如果只删 `handleCommandKey` 里的 `case "v":`、却把 `"v"` 留在上面这个列表里，那么 Cmd+V 会走进 `handleCommandKey` → `default: break` → 仍然 `return nil` → **Cmd+V 变成完全没反应，连在搜索框里粘贴文字都不行**。这是必须避免的回归。

同理，`KeyCaptureNSView.keyDown`（`MainView.swift:1441-1454`）里也有一份 `["a", "c", "v"]` 与 `case 36, 49, 51, 76, 117, 125, 126:`，需要一并去掉 `"v"` 与 `36, 76`，否则列表获得焦点时按键会被静默吃掉。

> **本份要求的行为**：Cmd+V 交还给系统/响应链（在搜索框里就是普通粘贴文字）。**这是有意的顺带修正**，不要把它「修回去」。
> 现状（改之前）：搜索框里按 Cmd+V 会触发「粘贴记录并隐藏窗口」，而不是粘贴文字 —— 与同一段代码里 Cmd+Z 专门特判搜索框的做法不一致。

### 2.3 行内「已置顶」文字

`Views/MainView.swift` 的 `ClipRow` 元数据行（`:1031-1035`）：

```swift
HStack(spacing: 8) {
    if item.isPinned {
        Label("已置顶", systemImage: "pin.fill")
            .labelStyle(.titleAndIcon)
    }
    Text(kindText)
    Text(DateText.formatter.string(from: item.createdAt))
}
```

置顶状态的**另一个**表达在同一个 `ClipRow` 的行内大头针按钮（`:1043-1051`）：`Image(systemName: item.isPinned ? "pin.fill" : "pin")` + `.floatingTooltip(item.isPinned ? "取消置顶" : "置顶")`。**这一块不许动。**

### 2.4 「按天数清理旧历史」可以复用的既有能力（**不要重新发明**）

| 能力 | 位置 | 说明 |
| --- | --- | --- |
| **删除 + 撤销** | `Stores/ClipStore.swift` 的 `remove(ids: Set<ClipItem.ID>)`（`:83-95`） | 内部会 `deletionUndoStack.record(entries)` → 自动进撤销栈，**一次 Cmd+Z 整体恢复**。`canUndoDeletion` / `undoLastDeletion()` 已存在 |
| **撤销栈** | `Support/HistoryDeletionUndo.swift` 的 `HistoryDeletionUndoStack` | 最多 10 批；`record(_:)` 收 `[HistoryDeletionEntry]` |
| **记录时间** | `Models/ClipItem.swift:17` `var createdAt: Date` | 判据用它 |
| **置顶标记** | `Models/ClipItem.swift:18` `var isPinned: Bool` | 只清未置顶 |
| **偏好读写的既有范式** | `Support/HistoryLimitPreferences.swift`（`load(from:)` / `save(_:to:)` / `normalized`）、`Support/SelectionClickBehaviorPreferences.swift` 的 `DragSelectionPreferences`（`static var` + `changedNotification`） | **照这个写**，不要自己发明新的存储方式 |
| **设置区块的写法** | `Views/SettingsView.swift` 的 `private func settingsSection<Content: View>(_ title: String, ...)`（`:366`）与 `private var historySection`（`:176`） | 新区块照抄这个结构 |
| **测试隔离用的 defaults** | `Support/SelfTest.swift` 里的 `let defaults = AppEnvironment.userDefaults(environment: environment)`（`:151`）与 `HistoryLimitPreferences.load(from: defaults)`（`:162`） | 新断言必须用这个**局部 `defaults`**，不要用 `AppEnvironment.userDefaults` |

### 2.5 断言基线

`Sources/ClipShelfLite/Support/SelfTest.swift`：**当前 44 条**（`grep -c 'results.append(check('` 实测）。

- 目标断言 `record context menu exposes the declared actions in order` 在 **`:699-711`**。
- **整段最后一条**是 `self test defaults suite file count does not grow`（**`:717-718`**），**必须保持最后**。
- 新断言要追加在**目标断言收尾 `))`（`:711`）之后**、**`_ = CFPreferencesAppSynchronize(suiteName as CFString)`（`:712`）之前**。

### 2.6 Windows 的做法（**只作功能参照，不照搬**）

- 自动粘贴：1.0.12 彻底移除，并有硬断言「程序集里不存在任何名字带 `Paste` 的类型」（`ClipShelf/CopyOnlyTests.cs:58`）。
- 「已置顶」文字：1.0.11 移除，只留大头针变色（`ClipShelf/MainWindow.xaml:103`）。
- 清理旧历史：`ClipShelf/SettingsPanel.cs:145-160` —— 保留最近 **7 / 30 / 90 天**，先「查看待清理记录数量」→ 再「确认清理这些记录」，只清未置顶，可 Ctrl+Z 撤销，**不随缓存清理自动执行**。

---

## 3. Scope

**受影响**

- `Sources/ClipShelfLite/Services/PasteController.swift` —— **整份删除**
- `Sources/ClipShelfLite/Stores/ClipStore.swift` —— 删 `paste` 两方法；**新增** `removeOldUnpinnedItems`
- `Sources/ClipShelfLite/Views/MainView.swift` —— §2.1 的入口 1/2/4/5/6 + §2.2 的两个吞键列表 + §2.3 的「已置顶」文字
- `Sources/ClipShelfLite/App/AppDelegate.swift` —— 只动最近记录子菜单（去掉「粘贴」项）与 `pasteMenuItem`
- `Sources/ClipShelfLite/Support/ClearHistoryConfirmation.swift` —— `ClipRowMenuAction` 去掉 `.paste`
- `Sources/ClipShelfLite/Views/SettingsView.swift` —— 新增一个设置区块
- `Sources/ClipShelfLite/Support/SelfTest.swift` —— 改 1 条断言 + 新增 3 条

**建议新增**（放 `Support/`，与既有风格一致）

- `Sources/ClipShelfLite/Support/OldHistoryCleanup.swift` —— **纯逻辑，全部可断言**：

  ```swift
  enum OldHistoryCleanup {
      static let retentionOptions = [7, 30, 90]      // 与 Windows 一致
      static let defaultRetentionDays = 30
      static let key = "history.oldRecordRetentionDays"

      static var retentionDays: Int { get set }       // 经 AppEnvironment.userDefaults
      static func load(from defaults: UserDefaults) -> Int
      @discardableResult static func save(_ days: Int, to defaults: UserDefaults) -> Int

      /// 只接受 retentionOptions 里的值；其它一律回落 defaultRetentionDays
      static func normalized(_ days: Int) -> Int

      /// cutoff = now - retentionDays 天（86400 秒/天）
      static func cutoffDate(now: Date, retentionDays: Int) -> Date

      /// 只挑「未置顶」且 createdAt 早于 cutoff 的记录
      static func eligibleIDs(in items: [ClipItem], now: Date, retentionDays: Int) -> Set<ClipItem.ID>

      /// 「将清理 30 天前的 12 条未置顶记录。」/「没有符合条件的旧记录。」
      static func summaryText(eligibleCount: Int, retentionDays: Int) -> String
  }
  ```

**不得改变的部分**

- **`Package.swift` 一个字都不许动** —— 它用 `path: "Sources/ClipShelfLite"` 目录通配，删文件不需要改它。
- **复制路径一个字都不许动**：`ClipStore.copy(_:)` 两个重载、`writeToPasteboard`、`writeSingleItemToPasteboard`、`pasteboardObject(for:)`、`fileURLsForMultiCopy`、`combinedText`。
- **`ClipStore.remove(ids:)` / `clearHistory()` / `undoLastDeletion()` / `HistoryDeletionUndoStack` 的语义与撤销栈行为**（新功能只能**调用**它们，不许改）。
- **`Services/RuntimeControlServer.swift` 与 `Support/RuntimeControlProtocol.swift` 一个字都不许动**（`--ctl` 没有 paste 命令，本份也不新增）。
- **`ClipRow` 的行内大头针按钮与它的 tooltip/无障碍名**（`MainView.swift:1043-1051`）不许动 —— 「已置顶」的语义要由它继续承担。
- **`AppDelegate.rebuildMenu` 的菜单栏「最近 8 条记录」子菜单本身要保留**（只去掉里面的「粘贴」一项）。
- **`tests/parity/`、`docs/`、`prompts/README.md`、`README.md`、`Assets/`、`script/`、`MacTests/` 一律不动。**
- **除目标断言那一条外，其余 43 条断言一条都不许改、不许删。**
- **不改任何配色、间距、圆角、图标**（本份**不碰美术风格**）。

---

## 4. Constraints

1. **「粘贴」必须删干净**：删完之后全仓 `grep -rn '粘贴' Sources/` 与 `grep -rn 'PasteController\|store\.paste' Sources/` **都必须是零命中**（`writeToPasteboard` / `NSPasteboard` / `pasteboardName` 这些**剪贴板读写**不算，它们不是「粘贴入口」，必须保留）。
2. **§2.2 的吞键列表必须同步去掉 `"v"`**。这是本份**最容易漏、且漏了会造成新故障**的一处。两个列表都要改：`installCommandKeyMonitorIfNeeded` 的 `["a", "c", "v", "z"]` → `["a", "c", "z"]`；`KeyCaptureNSView.keyDown` 的 `["a", "c", "v"]` → `["a", "c"]`。`KeyCaptureNSView.keyDown` 的 `case 36, 49, 51, 76, 117, 125, 126:` → `case 49, 51, 117, 125, 126:`。
3. **`ClipRowMenuAction` 去掉 `.paste` 后**，`orderedActions` 必须是 `[.copy, .pin, .delete]`（顺序不变，只是少了中间那个）。`ClipRow` 的 `.contextMenu` 里 `case .paste:` 分支同步删除。
4. **目标断言要改、但断言名一个字不许改**：`record context menu exposes the declared actions in order`。T11 的覆盖度关键字依赖其中的 `context menu` 子串。`success` / `failure` 文案可以更新（去掉「粘贴」相关措辞），但**必须继续覆盖「破坏性删除角色」这层含义**，且 `ClipRowMenu.isDeclaredMenuInstalled` 的作用域检查**不许放宽**。
5. **新增断言 ≥3 条**，全部是**纯逻辑**，不许依赖真实 `ClipStore.shared`（它是单例，会写到用户真实数据目录）：
   - `old history cleanup selects only unpinned records older than the window` —— 覆盖 4 种情况：**已置顶但很旧 → 不选**；**未置顶但很新 → 不选**；**未置顶且够旧 → 选**；**恰好等于 cutoff → 不选**（严格早于）。
   - `old history cleanup retention window only accepts the offered values` —— `normalized(7/30/90)` 各自不变；`normalized(0/15/999/-1)` 一律回落 `30`；`load(from:)` 在键缺失时返回 `30`。
   - `old history cleanup summary reports the eligible count` —— `eligibleCount == 0` 时给「没有符合条件的旧记录。」；`> 0` 时文案里**同时含天数与条数**。
   - 断言里用 `SelfTest.swift` 既有的**局部 `defaults`**（`AppEnvironment.userDefaults(environment: environment)`），**不要**用 `AppEnvironment.userDefaults`，否则会污染「plist 数量不增长」那条断言。
6. **`removeOldUnpinnedItems` 必须走既有的 `remove(ids:)`**，不得直接改 `items`、不得直接碰 `deletionUndoStack`。签名：
   ```swift
   @discardableResult
   func removeOldUnpinnedItems(retentionDays: Int, now: Date = Date()) -> Int
   ```
   `now` 要有默认值（方便将来测试），返回实际删除条数。
7. **设置区块的写法与位置**：新增 `private var oldHistorySection: some View { settingsSection("清理旧历史") { … } }`，在 `SettingsView.body` 的 `VStack` 里插在 **`historySection` 之后、`hotKeySection` 之前**。区块内容：
   - `HStack { Text("保留最近"); Spacer(); Picker { ForEach(OldHistoryCleanup.retentionOptions, id: \.self) { Text("\($0) 天").tag($0) } }.labelsHidden().pickerStyle(.menu) }`
   - 一行状态文字（初始为空）
   - `Button("查看待清理记录数量")`
   - `Button("确认清理这些记录")` —— **只在有可清理项时可用**
   - 说明文字：「只移除所选天数之前的未置顶记录，不删除原文件。清理后仍可使用 Command-Z 撤销。」
   - **不做**「定时自动清理」，**不做**「随其它操作顺带清理」。只有用户点按钮才执行。
8. **按 macOS 习惯实现，不照搬 Windows**：用原生 `Picker` / `Button` / `Text`，**不要**自绘对话框、**不要**加确认弹窗之外的额外步骤、**不要**照抄 Windows 的措辞。
9. **不引入第三方依赖。**

---

## 5. Acceptance

- **A** `swift build --disable-sandbox` 通过；`swift test --disable-sandbox --scratch-path /tmp/cs-p53` 通过（**不要**用仓库内的 `.build`；仓库在 `~/Documents` 下受 iCloud 管理，就地跑会签名失败）。贴关键输出。
  - 预期：`--self-test` 报 **47/47**；`swift test` 的 `[PASS]` 是 **94 = 2 × 47**、0 FAIL。
- **B（删除的完整性，必须贴原始输出）** 在仓库根目录跑：

  ```bash
  grep -rn '粘贴' Sources/ClipShelfLite/ || echo "零命中 ✅"
  grep -rn 'PasteController' Sources/ClipShelfLite/ || echo "零命中 ✅"
  grep -rn 'store\.paste' Sources/ClipShelfLite/ || echo "零命中 ✅"
  grep -rn 'suppressPasteUntil' Sources/ClipShelfLite/ || echo "零命中 ✅"
  grep -rn 'handlePaste' Sources/ClipShelfLite/ || echo "零命中 ✅"
  ls Sources/ClipShelfLite/Services/PasteController.swift 2>&1 || echo "文件已删除 ✅"
  ```

  **六条必须全部零命中 / 文件不存在。** 任何一条有输出 = 本份没做完，**如实报告，不要含糊过去**。

- **C（§2.2 的接缝，必须实测并贴输出）** 证明 Cmd+V **没有**变成死键：
  1. 启动应用（或用 `--self-test` 跑一遍确保无崩溃）。
  2. 聚焦搜索框 → 在别处复制一段文字 → 按 **Cmd+V** → **搜索框里出现那段文字**（而不是「历史记录被粘贴 / 窗口隐藏」）。
  3. 列表获得焦点时按 **Cmd+V** → **不执行任何记录操作**（不粘贴、不隐藏窗口）。
  4. 列表获得焦点时按 **Return** → **不执行粘贴**。
  - 第 2 步是**本份最关键的一条人工验收**，因为它是「删除」顺带修出来的行为。**做不到就如实说，不要写成通过。**

- **D（新功能的人工验收，必须做）** 打开设置 →「清理旧历史」：
  1. 默认显示「保留最近 30 天」。
  2. 点「查看待清理记录数量」→ 状态文字给出**条数**（0 条时给「没有符合条件的旧记录。」且「确认清理」按钮不可用）。
  3. 挑一条**已置顶**的旧记录：它**不得**出现在待清理集合里。
  4. 点「确认清理这些记录」→ 记录消失，状态文字更新为「已清理 N 条记录。」
  5. 按 **Cmd+Z** → 被清理的记录**全部回来**，且**置顶状态与时间不变**（一次撤销恢复整批）。
  6. 原始文件**未被删除**（文件类记录的原文件仍在原处）。

- **E（回归）** 跑一次隔离模式黑盒，**必须 0 失败**：

  ```bash
  CLIPSHELF_PARITY_ISOLATED=1 \
  CLIPSHELF_PARITY_DELIVERED=31,32,43,33,38,37,34,35,40,41,36,42,44,45,46,47,49 \
  tests/parity/run.sh
  ```
  - 预期：`通过 29 失败 0 跳过 1`（T12 打包冒烟默认跳过）。**T11 两条必须通过** —— 目标断言名未变，覆盖度不受影响。
  - 注意：**`tests/parity/` 归 WorkBuddy 所有，你不许改**（包括 `manual-checklist.md` 里那三条已经过期的粘贴条目，由 WorkBuddy 自己更新）。跑失败时**不要改脚本**，如实报告。

---

## 6. Handoff

- **报告文件**：`prompts/prompt-53-drop-paste-and-old-history-cleanup-report.md`，含：
  - 改动清单（新增 / 修改 / **删除** 三类分开列，删除的文件要写出来）
  - A / B / C / D / E 五段的**原始输出**
  - 断言计数：改前 **44** → 改后 **47**（`grep -c 'results.append(check('` 的前后两次实测值）
  - **断言名清单的前后对照**（证明只有目标断言那一条被改，其余 43 条一字未动）
  - 一节「**只删功能、不碰美术风格**」的自查：确认没有动任何配色 / 间距 / 圆角 / 图标
- **本份会同时删除一个源文件**（`PasteController.swift`）。报告里要写明「`Package.swift` 用目录通配，因此无需改动」。
- **不授权**：`git commit` / `push` / 打 tag / 发布，一律由用户本人执行。
- 交付后 WorkBuddy 会跑独立验收：断言完整性核对 → 变异测试（证明新增的 3 条断言不是空转）→ 黑盒回归 → 用户数据未被触碰检查。
