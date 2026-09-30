# prompt-50：让右键菜单断言连上真实菜单（「镜像常量」收口）

依据 `AGENTS.md` 的 Prompt and collaboration guidelines。协作约定与编号见 `prompts/README.md`。

> **来源**：`prompts/batch-E-verification-report.md` **§7 P2-1**（主）+ **§7 P3-1**（附带）。
> **依赖**：批次 E 已交付（`999e66f`）。**基线断言数 = 44**。
> **归属说明**：这个缺陷**是 WorkBuddy 写 prompt-49 时给的「建议结构」造成的**，不是上一批实现方的偏差。本份把它修好。

---

## 1. 问题（一句话）

**有一条断言看着在守右键菜单，其实守的是一个「影子常量」，跟真实菜单之间没有任何引用边。**

`prompt-49` 让实现方加了一个常量：

```swift
// Sources/ClipShelfLite/Support/ClearHistoryConfirmation.swift:24-25
enum ClipRowMenu {
    static let orderedActions: [ClipRowMenuAction] = [.copy, .paste, .pin, .delete]
    ...
}
```

但**真实的右键菜单是另一处手写的**，**完全不引用这个常量**：

```swift
// Sources/ClipShelfLite/Views/MainView.swift:1095-1108
.contextMenu {
    Button("复制") { handleCopy() }
    Button("粘贴") { handlePaste() }
    Button(ClipRowMenu.pinTitle(isPinned: item.isPinned)) { store.togglePinned(item) }
    Button("删除", role: .destructive) { store.remove(item) }
}
```

于是断言 `SelfTest.swift:695` 变成**常量与同一个字面量自比较**：

```swift
condition: ClipRowMenu.orderedActions == [.copy, .paste, .pin, .delete] && ...
```

### 1.1 实测铁证（本份必须复现的反向验证）

把**真实的 `.contextMenu { … }` 块整块删掉**，其余不动 → **44/44 仍然全绿**。

> 也就是说：**将来有人把菜单项删掉、改顺序、换成别的作用域，都不会有任何红灯。**

### 1.2 附带问题（P3-1，顺手改）

断言名 `record context menu preserves existing action order` **措辞夸大**：真实菜单顺序是「复制 / 粘贴 / 置顶 / 删除」，而行内**按钮**顺序是「置顶 / 复制 / 粘贴 / 删除」（`MainView.swift:1045-1075`）—— **两者并不相同**。「preserves existing action order」会让人误以为它们一致。

---

## 2. Goal

1. **给真实菜单建立引用边**：让 `ClipRow` 的 `.contextMenu` **由 `ClipRowMenu.orderedActions` 派生**（遍历 + 分支），而不是手写四个 `Button`。
2. **断言改名**，不再声称「保持了既有顺序」。

> 只动这三件事，**不改任何行为、外观与作用域**。

---

## 3. Scope

**受影响**

- `Sources/ClipShelfLite/Views/MainView.swift` —— **只动 `ClipRow` 里的 `.contextMenu` 块**（L1095-1108）
- `Sources/ClipShelfLite/Support/ClearHistoryConfirmation.swift` —— 给 `ClipRowMenu` 补一个纯函数（见下）
- `Sources/ClipShelfLite/Support/SelfTest.swift` —— **只改 `record context menu ...` 那一条断言**（改名 + 判据），**其余 43 条一个字都不许动**

**建议做法（供参考，实现方式你定）**

给 `ClipRowMenu` 补一个「菜单项标题」纯函数，把四个标题也收进单一来源：

```swift
static func title(for action: ClipRowMenuAction) -> String {
    switch action {
    case .copy:   return "复制"
    case .paste:  return "粘贴"
    case .pin:    return pinTitle(isPinned: false)   // 注意：置顶项的实际标题依赖 isPinned
    case .delete: return "删除"
    }
}
```

菜单改为**由 `orderedActions` 遍历生成**：

```swift
.contextMenu {
    ForEach(ClipRowMenu.orderedActions, id: \.self) { action in
        switch action {
        case .copy:
            Button(ClipRowMenu.title(for: .copy)) { handleCopy() }
        case .paste:
            Button(ClipRowMenu.title(for: .paste)) { handlePaste() }
        case .pin:
            Button(ClipRowMenu.pinTitle(isPinned: item.isPinned)) { store.togglePinned(item) }
        case .delete:
            Button(ClipRowMenu.title(for: .delete), role: .destructive) { store.remove(item) }
        }
    }
}
```

**不得改变的部分**

- **四个菜单项的行为与作用域必须与现在逐项完全一致**（这是 prompt-49 §4.4 的硬要求，本份只是换写法）：
  - 复制 / 粘贴 → `handleCopy()` / `handlePaste()`（走既有的 `actionItems(for:)`）
  - 置顶 → `store.togglePinned(item)`（**只本行**）
  - 删除 → `store.remove(item)`（**只本行**，`role: .destructive`）
- **菜单项顺序仍是** 复制 → 粘贴 → 置顶/取消置顶 → 删除。
- **`role: .destructive` 必须仍在「删除」上。**
- **不改任何配色、间距、圆角、图标**（本份**不碰美术风格**）。
- **不改** `handleRowClick` / `handleKey` / `handleCommandKey` / `DragSelectionCaptureView` / `KeyboardCaptureView` / `deleteSelectedOrClear` / `ClearHistoryConfirmation` 的既有函数。
- **不改** `Package.swift`、`tests/parity/`、`docs/`、`README.md`、`Assets/`、`script/`。
- **其余 43 条断言一条都不许改、不许删。**

---

## 4. Constraints

1. **引用边是硬要求**：改完后，`ClipRow` 的菜单**必须**通过 `ClipRowMenu.orderedActions` 生成 —— 不允许再出现「手写四个 `Button`」与「常量」两份清单并存的情况。
2. **断言必须变得可被证伪**（本份的核心验收）：删掉真实菜单应当**让断言变红**（见 §5 B）。
3. **断言改名**为：`record context menu exposes the declared actions in order`
   > 新名字**必须包含 `context menu` 这个子串** —— `tests/parity/lib.sh` 的 T11 覆盖度检查依赖它。
4. **断言判据**至少覆盖：
   - `orderedActions == [.copy, .paste, .pin, .delete]`（顺序）
   - 四个标题正确：`title(for: .copy) == "复制"`、`.paste == "粘贴"`、`.delete == "删除"`
   - `pinTitle(isPinned: false) == "置顶"`、`pinTitle(isPinned: true) == "取消置顶"`
   - **`title(for: .pin)` 必须与 `pinTitle(isPinned: false)` 一致**（防止两处标题漂移）
5. **断言总数保持 44**（本份是改写，不是新增）。
6. **不引入第三方依赖。**

---

## 5. Acceptance

- **A** `swift build --disable-sandbox` 通过；`swift test --disable-sandbox --scratch-path /tmp/cs-p50` 通过（**不要**用仓库内的 `.build`，会因 iCloud 签名失败）。贴关键输出。
- **B（本份的关键验收，必须做）反向验证 —— 证明断言不再是影子：**
  1. 先跑一次基线：`ClipShelf --self-test <路径>` → **44/44，exit 0**。
  2. 然后**临时**把 `ClipRow` 里真实的 `.contextMenu { … }` 块**整块注释掉或删掉**（其余不动），重新构建，再跑 `--self-test`。
  3. **必须看到 `record context menu ...` 变红（43/44，exit 1）。**
     - 如果**仍然 44/44 全绿** → **说明引用边没建起来，本份没做到**，请如实报告并说明原因，**不要**把这一步写成通过。
  4. **把临时改动完整还原**，重跑基线确认回到 **44/44**，并用 `git diff` 证明 `MainView.swift` 已还原干净。
  - 贴出第 1/2/3/4 步的**原始输出**。
- **C（回归）** 连续 3 次 `--self-test` 都是 **44/44、exit 0**；其余 43 条断言零改动（贴 `git diff` 的 `SelfTest.swift` 段，证明只动了那一条）。
- **D（回归）** `CLIPSHELF_PARITY_ISOLATED=1 CLIPSHELF_PARITY_DELIVERED=31,32,43,33,38,37,34,35,40,41,36,42,44,45,46,47,49 tests/parity/run.sh` → **T11 覆盖度检查通过**、除 T16 外 0 FAIL。
  > 说明：**T16（大上限累积）的偶发失败与本份无关**，它已在测试侧修好（改成「写一条→确认入库→再写下一批」）；若你仍偶发遇到它变红，如实贴出，**不要去改它**（那是 WorkBuddy 的文件）。
- **E（人工实测）** 右键菜单在真实 UI 里**看起来与改之前完全一样**：四个项、顺序一致、作用域一致、删除仍是破坏性样式。贴出菜单树。
- **F** 报告：`prompts/prompt-50-row-menu-mirror-constant-report.md`，含：改动清单、A–E 的原始输出、**B 的四步原始输出**（这是核心证据）、以及一节说明「为什么改完后这条断言不再是影子」。

---

## 6. Handoff

- **实现**：Codex。**独立测试与复核**：WorkBuddy（只读，不改源码）。
- **授权（未勾选即未授权）**：`[ ]` 本地检查 · `[ ]` Release 构建 · `[ ]` 覆盖安装 · `[ ]` `git commit` · `[ ]` `push` · `[ ]` GitHub 发布。
  **本轮建议**：仅本地检查。
- **提交信息建议**：`Derive the record context menu from the declared action list`
