# prompt-57：交互缺陷修正与手感微调（4 项）

> **本份的性质**：不是新功能，是**修一个真 bug + 补三个真缺口**。
> 来源是 `docs/parity/windows-optimization-log-study.md` —— 那份把上游 `origin/main`（`1be0ec6` / `windows-v1.4.1`）
> 的**真实改动**逐条读过，判定出「哪些是 Mac 真缺口、哪些是平台差异、哪些是两端相反」。
> 本份**只包含判定为真缺口/真 bug 的 4 项**；上游其余全部条目**明确不做**（见 §7）。
>
> **四项各自的判定标准**：① 目标行为必须精确成立（有断言）；② 既有 51 条断言一条不得改坏；③ 视觉项不得引入任何行为变化。

---

## 1. 背景：这 4 项从哪来

| # | 项目 | 性质 | 上游对应 | 实测证据 |
| --- | --- | --- | --- | --- |
| **G1** | 有置顶记录时，新记录**不会**滚动到顶 | **真 bug**（我方读日志时发现，与 win 无关） | — | `MainView.swift:215` + `ClipStore.swift:350/371-379` |
| **G2** | 记录类型筛选**不持久化** | 真缺口 | `930c15e` 的 `AppSettings.HistoryTypeFilter` | 全仓 `kindFilter` 只有 4 处，无一处读写 `userDefaults` |
| **G3** | 图标按钮**完全没有 hover 反馈** | 真缺口（手感） | `App.xaml` 的 `SoftButton` → `IsMouseOver → HoverBrush` | 全仓 `onHover`/`isHovered` **零命中**；`AppTheme` 无 hover 色 token |
| **G4** | 选中行背景**硬切**，无过渡 | 真缺口（手感） | `5e0e089` 的 `SelectionRowMotion.cs`（110 ms `CubicEase.EaseOut`，只动平面 opacity） | `MainView.swift:1033/1067-1077` 无任何 `animation` |

**为什么是这 4 项而不是别的**：上游另外 14 条性能/体验改动里，10 条是平台差异（WPF 滚轮物理、自绘控件、STA 渲染调度器、缓存清理、应用内更新…），
4 条是「Windows 主动偏离 Mac」的交互差异（行内按钮作用域、单击唯一已选记录、记录操作同排、紧凑默认窗口尺寸）。
逐条理由见研究文档 §2.3 / §2.4，**本份不得顺手做那些**。

---

## 2. 现状代码（Evidence）

> ⚠️ **行号会漂，以符号名为准。** 下列行号均为 2026-10-01 复测值（HEAD = `c640700`，51 条断言，自 `c640700` 起 0 个新提交）。
> 漂移量**不统一**（`MainView.swift` 自 `30871be` 起被改过 **7** 次、`ClipStore.swift` **4** 次、`AppTheme.swift` **0** 次），**不要按固定偏移量换算**。

### 2.1 G1 —— 滚动到顶的观察值错了

```swift
// Views/MainView.swift:215-220
.onChange(of: store.items.first?.id) { newID in
    guard let newID, !isDragSelecting else { return }
    withAnimation(.easeOut(duration: 0.18)) {
        proxy.scrollTo(newID, anchor: .top)
    }
}
```

```swift
// Stores/ClipStore.swift:344-356
private func add(_ item: ClipItem) {
    if isDuplicate(items.first, item) { return }
    items.removeAll { isDuplicate($0, item) }
    items.insert(item, at: 0)      // :350
    sortItems()                    // :351
    HistoryTrimmer.trim(&items, maxItems: maxItems)
    save()
}

// Stores/ClipStore.swift:371-379
private func sortItems() {
    items.sort { first, second in
        if first.isPinned != second.isPinned {
            return first.isPinned && !second.isPinned   // :374 置顶项排到最前
        }
        return first.createdAt > second.createdAt
    }
}
```

**故障链**：只要有 **≥ 1 条置顶记录**，`items.first` 恒为那条置顶记录 →
`onChange(of: store.items.first?.id)` 的观察值**根本没变** → 回调**不触发** → 新记录到达时列表不滚到顶。

`addFilePaths(_:)`（`:358-369`）也走 `sortItems()`，所以**截图文件夹新增文件同样不滚**。

> ⚠️ **`ClipStore` 不能在断言里构造**：`Stores/ClipStore.swift:25` 是 `private init()`，只有 `:6 static let shared`。
> `Support/SelfTest.swift` 里**没有**任何 `ClipStore(` 构造（已 Grep 确认）。
> → **G1 的接缝必须是「对 `[ClipItem]` 的纯函数」**，不得依赖构造 store。

### 2.2 G2 —— 筛选状态只活在 View 里

```swift
// Views/MainView.swift:8
@State private var kindFilter: ClipKindFilter = .all
```

全仓 `kindFilter` 只有 4 处命中：`MainView.swift:8`（声明）/`:39`（用于 `ClipHistoryFilter.items`）/`:224`（`.onChange` 里 `clearSelection()`）/`:391`（`Picker` 绑定）。
**没有任何一处**读写 `AppEnvironment.userDefaults`；`Support/` 下也没有 `*FilterPreferences` 类型。

`ClipKindFilter`（`Support/ClipKindFilter.swift:3-28`）是 `enum ClipKindFilter: String, CaseIterable, Identifiable`，**已有合成的 `rawValue`**（`"all"` / `"text"` / `"file"` / `"image"`）。

**要照抄的既有惯用法**（`Support/HistoryLimitPreferences.swift`，全 **38** 行）：

```swift
enum HistoryLimitPreferences {
    static let key = "history.maxItems"          // :4
    static let defaultValue = 100                // :5
    static var value: Int {                      // :8-11
        get { load(from: AppEnvironment.userDefaults) }
        set { save(newValue, to: AppEnvironment.userDefaults) }
    }
    static func load(from defaults: UserDefaults) -> Int {   // :13-18
        guard defaults.object(forKey: key) != nil else { return defaultValue }
        return normalized(defaults.integer(forKey: key))
    }
    @discardableResult
    static func save(_ value: Int, to defaults: UserDefaults) -> Int {   // :21-25
        let normalizedValue = normalized(value)
        defaults.set(normalizedValue, forKey: key)
        return normalizedValue
    }
}
```

断言要照抄 `Support/SelfTest.swift:148-173` 的隔离写法（**不要**用 `UserDefaults.standard`）：

```swift
let defaults = AppEnvironment.userDefaults(environment: environment)   // :148
let reloadedDefaults = UserDefaults(suiteName: suiteName)              // :167
```

### 2.3 G3 —— 图标按钮只认「按下」，不认「悬停」

```swift
// Views/MainView.swift:1148-1162
private struct ChatGPTIconButtonStyle: ButtonStyle {
    @Environment(\.colorScheme) private var colorScheme
    let isSelected: Bool

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 15, weight: .medium))
            .foregroundStyle(isSelected ? (colorScheme == .dark ? Color.white : Color.black) : Color.secondary)
            .frame(width: 36, height: 32)
            .background(
                RoundedRectangle(cornerRadius: 9)
                    .fill(isSelected ? AppTheme.selectedActionBackground.opacity(configuration.isPressed ? 1.5 : 1)
                                     : AppTheme.actionButtonBackground.opacity(configuration.isPressed ? 1.2 : 1))
            )
    }
}
```

用在 `ClipRow` 的三个按钮上：`MainView.swift:1010`（置顶）/`:1018`（复制）/`:1026`（删除）。

**注意**：`SwiftUI.ButtonStyle` 拿不到 hover 状态，惯用法是在 `makeBody` 里套一层带 `@State private var isHovered` 的内部 `View` + `.onHover { … }`。
`AppTheme` 现有色板在 `Support/AppTheme.swift:12-83`，与本次相关的是 `:72 actionButtonBackground`、`:76 selectedActionBackground`；
`adaptive(light:dark:)` 的惯用法见 `:12-83`，**必须用它，不得硬编码 `Color(red:…)`**。

### 2.4 G4 —— 选中背景是「换颜色」而不是「改透明度」

```swift
// Views/MainView.swift:1033
.background(rowBackground)

// Views/MainView.swift:1067-1077
private var rowBackground: Color {
    if isSelected {
        return selectionColor.opacity(colorScheme == .dark && !selectionColorIsPreset ? 0.68 : 1)
    }
    if isFocused { return Color.clear }
    return Color.clear
}
```

- 两个 `isFocused` 分支都返回 `Color.clear` → 该属性**实际只依赖 `isSelected`**（外加 `colorScheme` / `selectionColor` / `selectionColorIsPreset`）。
- `:1033` 外层**没有任何** `animation` / `withAnimation` → 选中与取消都是硬切。
- ⚠️ **不得改变既有呈现**：深色 + 非预设色时的 `0.68` 是**既有行为**，必须原样保留（见 §5 的 G4-b 断言）。
- ⚠️ **注意两个同名概念，别混**：`ClipRow` 里那个 `private var rowBackground: Color`（`:1067`）是**本行**的；
  `AppTheme` 里另有一个 `AppTheme.rowBackground`（`Support/AppTheme.swift:20`，是列表底色 token），**两者无关**。
  G4 要改的是**前者**（`ClipRow` 的），**不得动 `AppTheme.rowBackground`**。

---

## 3. Scope

### 3.1 G1 —— 修滚动锚点（新增纯函数接缝 + 改观察值）

新增一个**纯函数**（建议放 `Support/HistoryScrollTarget.swift`，也可放进既有 `Support/ClipKindFilter.swift` 或 `Support/SourceScan.swift` 同级目录；**文件名不限，但类型名与语义要一致**）：

```swift
enum HistoryScrollTarget {
    /// 列表里「最新记录」的 id：取 `createdAt` **最大**的那条。
    /// 必须用 `max(by: createdAt)`，**不得**用 `items.first`
    /// —— `ClipStore.sortItems()` 会把置顶项排到最前（ClipStore.swift:371-379）。
    static func newestAnchorID(in items: [ClipItem]) -> ClipItem.ID?

    /// 本次应当滚动到的锚点；返回 `nil` = **不滚动**。
    /// 仅当「最新记录的时间戳比上次记录的更新」时才返回锚点：
    ///   - 新增一条记录（含截图文件夹新文件）→ 返回新记录 id
    ///   - 删除最新记录 → `nil`（不许滚）
    ///   - 置顶 / 取消置顶（顺序变、`createdAt` 不变）→ `nil`
    ///   - 切换筛选 / 搜索（`items` 引用变但内容同）→ `nil`
    static func anchorToReveal(in items: [ClipItem], lastRevealedNewest: Date?) -> ClipItem.ID?
}
```

`MainView` 侧：
- 把 `:215` 的 `.onChange(of: store.items.first?.id)` 改成观察 `anchorToReveal(...)`（或 `newestAnchorID(...)` + 一个 `@State private var lastRevealedNewest: Date?`）。
- **保留** `guard let newID, !isDragSelecting else { return }` 与 `withAnimation(.easeOut(duration: 0.18))`（既有手感，不得改）。
- 滚动后更新 `lastRevealedNewest`。

### 3.2 G2 —— 筛选持久化（新增偏好类型 + View 接上）

新增 `Support/HistoryFilterPreferences.swift`，**形状照抄 `HistoryLimitPreferences`**：

```swift
enum HistoryFilterPreferences {
    static let key = "history.kindFilter"
    static let defaultValue: ClipKindFilter = .all
    static var value: ClipKindFilter {
        get { load(from: AppEnvironment.userDefaults) }
        set { save(newValue, to: AppEnvironment.userDefaults) }
    }
    static func load(from defaults: UserDefaults) -> ClipKindFilter
    @discardableResult
    static func save(_ value: ClipKindFilter, to defaults: UserDefaults) -> ClipKindFilter
}
```

- `load`：无该键 → `defaultValue`；**有该键但 `rawValue` 无法解析（脏数据/旧版本）→ 回落 `defaultValue`**（不得崩溃、不得返回 `nil`）。
- `MainView` 侧：`@State private var kindFilter: ClipKindFilter = HistoryFilterPreferences.value`；
  在既有 `.onChange(of: kindFilter)`（`:224`）里**追加**写入（`clearSelection()` 与 `proxy.scrollTo` 的行为**不得变**）。

### 3.3 G3 —— 图标按钮 hover 反馈

把「按钮底色怎么算」抽成**纯函数**（建议放 `Support/IconButtonAppearance.swift`），`ChatGPTIconButtonStyle` 改为调用它：

```swift
enum IconButtonAppearance {
    /// 优先级：按下 > 悬停 > 选中/常态。
    static func background(isSelected: Bool, isPressed: Bool, isHovered: Bool) -> Color
}
```

- 新增 `AppTheme` token（放在 `Support/AppTheme.swift:72-79` 那一组附近，**必须走 `adaptive(light:dark:)`**）。
- `ChatGPTIconButtonStyle` 里加 `@State private var isHovered` + `.onHover`，把 `isHovered` 传给 `IconButtonAppearance.background`。
- **视觉要求（Mac 习惯）**：hover 反馈要**克制** —— 只改底色，不改图标颜色、不加边框、不加缩放。
- 既有 `configuration.isPressed` 的 `1.2` / `1.5` 透明度倍数**可以**被这套新逻辑取代（这是内部实现），但**按下必须仍比悬停更明显**。

### 3.4 G4 —— 选中行过渡

新增（建议 `Support/SelectionRowMotion.swift`）：

```swift
enum SelectionRowMotion {
    static let duration: TimeInterval = 0.11
    static let animation: Animation = .easeOut(duration: duration)

    /// 选中背景**层**的不透明度。0 = 完全透明（未选中）。
    /// 过渡只作用于这一层；文字与图标不参与动画（对齐 win 的 SelectionRowMotion.cs）。
    static func surfaceOpacity(isSelected: Bool, isDark: Bool, isPreset: Bool) -> Double
}
```

- `surfaceOpacity` 必须**精确保留既有呈现**：未选中 → `0`；选中且 `isDark && !isPreset` → `0.68`；其余选中 → `1`。
- `ClipRow` 的 `.background(rowBackground)`（`:1033`）改为「**颜色固定 + 层 opacity 过渡**」：
  底色用**固定颜色**（不再随选中态变色），选中与否由 `surfaceOpacity` 驱动；
  在**背景层上**加 `.animation(SelectionRowMotion.animation, value: isSelected)`。
- **只动背景层**：`HStack` 里的文字、图标、分隔线**不得**参与动画（不得给整个 row 套 `.animation`）。

---

## 4. Constraints

1. **行为零变化**（G1 除外，G1 是修 bug）：
   - G2 只新增「记住上次选择」这一条可观测行为；`clearSelection()`、`proxy.scrollTo(firstID, anchor: .top)`（`:226-233`）不得改。
   - G3 / G4 是纯视觉：**不得**改变任何点击语义、选择语义、快捷键、键盘路径。
2. **既有 51 条断言一条不得删改**，本份只能**新增**。改完 `--self-test` 报告必须是 `Result: 60/60`。
3. **不得新增依赖**，不得改 `Package.swift`。
   `Package.swift` 用 `path: "Sources/ClipShelfLite"`（目录通配）→ **新增源文件不需要改 manifest**。
4. **不得改**：`tests/parity/**`（WorkBuddy 所有）、`MacTests/**`、`script/**`、`Assets/**`、`README.md`。
5. **G1 的接缝必须是纯函数** —— `ClipStore` 是 `private init()`，断言里构造不了，**不得**把逻辑放进 `ClipStore` 然后要求断言覆盖。
6. **G3 / G4 不得用「扫源码文本」做主要判据** —— 本份把逻辑抽成了纯函数，断言必须打在纯函数上（源码扫描有三个固有盲区，见 `prompts/README.md` §7-43）。
7. **不得顺手做** §7 列出的任何一项。
8. 断言一律加进 `Support/SelfTest.swift` 的 `ClipShelfSelfTest.run()`（**单一实现**，`swift test` 与 `--self-test` 双入口共用）。

---

## 5. Acceptance

### A. 构建与测试

```bash
swift build --disable-sandbox
swift test --disable-sandbox --scratch-path /tmp/cs-p57
swift build --disable-sandbox -c release
.build/debug/ClipShelf --self-test /tmp/p57-report.txt
```

- 全部退出码 **0**；`swift test` **0 FAIL**；`--self-test` 报告 `Result: 60/60 checks passed.`
- **报告里贴原始输出**（`Build complete!`、`PASS/FAIL` 计数、`Result:` 行）。

### B. 新增断言（**恰好 9 条**，`--self-test` 报告必须命中 §4 的全部 T11 关键词）

> **T11 关键词（WorkBuddy 已写进 `tests/parity/lib.sh`）：`newest`、`anchor`、`hover`、`transition`、`preference`。**
> 这 5 个词在**改之前的 51 条报告里都不存在**（已用脚本逐词 `\b…\b` 验证）。
> **所以：断言名里必须保留这些词，改名 = T11 覆盖度检查失败。**

| 断言名（**必须逐字使用**） | 判据 | 删掉它之后哪个变异会变全绿 |
| --- | --- | --- |
| `newest record anchor ignores pin order` | 夹具含 **1 条置顶（`createdAt` 较旧）+ 1 条新的**：`HistoryScrollTarget.newestAnchorID(in:)` 必须等于**新记录**的 id，且**不等于** `items.first!.id`（用 `!=` 显式钉住） | 把 `newestAnchorID` 换成 `items.first?.id` |
| `scroll anchor stays nil unless the newest record is newer` | `anchorToReveal(in:lastRevealedNewest:)`：① 新增记录（新 `createdAt`）→ 返回新 id；② 传 `lastRevealedNewest == 当前最新` → `nil`；③ 把最新那条删掉后 → `nil`；④ 只改 `isPinned` 使顺序变、`createdAt` 不变 → `nil` | 去掉「时间戳必须更新」这一判定，永远返回锚点 |
| `history filter preference round trips through defaults` | 照抄 `SelfTest.swift:148-173`：`HistoryFilterPreferences.save(.image, to: defaults)` 后，**新建** `UserDefaults(suiteName: suiteName)` 再 `load` → 必须 `== .image` | `save` 里不写 `defaults.set` |
| `unknown history filter value falls back to all` | ① 空 defaults → `load` 必须 `== .all`（**字面值**，且 `HistoryFilterPreferences.defaultValue == .all`）；② 手工 `defaults.set("bogus-not-a-kind", forKey: "history.kindFilter")` → `load` 必须 `== .all`（不崩、不返回其它 case） | `load` 去掉 `?? defaultValue`（改成 `force`/返回第一个 case） |
| `icon button hover surface differs from the rest state` | `IconButtonAppearance.background(isSelected: false, isPressed: false, isHovered: true)` **必须 ≠** `…isHovered: false`（常态）；且 `isSelected: true` 时同样必须 ≠（选中态也要有 hover 反馈） | hover 分支返回与常态同一个色 |
| `icon button press state outranks hover` | `background(isSelected: false, isPressed: true, isHovered: true)` **必须 ==** `background(isSelected: false, isPressed: true, isHovered: false)`（按下时 hover 不叠加） | 把 hover 判定提到按下之前（优先级反转） |
| `selection row transition duration is a pinned literal` | `SelectionRowMotion.duration == 0.11`（**绝对字面值**，不经由任何计算） | 把 `0.11` 改成 `0` 或 `2.0` |
| `selection row surface opacity keeps the existing presentation` | `surfaceOpacity(isSelected: false, isDark: true, isPreset: false) == 0`、`(false, true, true) == 0`、`(false, false, false) == 0`；`(true, true, false) == 0.68`；`(true, true, true) == 1`；`(true, false, false) == 1`（**全是既有行为的字面值**） | 把 `0.68` 改成 `1.0`（深色非预设的既有呈现被改坏） |
| `selection row animation only targets the surface layer` | `SelectionRowMotion.animation` 的时长必须 `== duration`；**并且** `ClipRow` 的 `body` 里 `.animation` 只出现在背景层那一处 —— 用 `SourceScan.body(ofTypeNamed: "ClipRow", in:)` 断言 `animation` 出现次数 **== 1**，且 `HStack` 之后（`SourceScan.codeOnly` 之后统计） | 把 `.animation` 套到整个 `HStack` 上（文字也会渐变） |

**本份断言总数：51 → 60**（G1 2 条 + G2 2 条 + G3 2 条 + G4 3 条）。
若你认为需要拆成更多条，可以，但**必须 ≥ 9 条**且报告能命中上面 5 个关键词。

### C. **反向验证（必做，逐条贴原始输出）** —— 证明断言不是空转

对**每一条**新断言，临时注入一个**只破坏它的那一个机制**的改动，**必须看到该断言变红**，然后完整还原：

| 变异 | 做法 | **必须变红的断言** |
| --- | --- | --- |
| M1 | `newestAnchorID` 改成 `items.first?.id` | **`newest record anchor ignores pin order`** |
| M2 | `anchorToReveal` 去掉「时间戳更新」判定 | **`scroll anchor stays nil unless the newest record is newer`** |
| M3 | `HistoryFilterPreferences.save` 不写 defaults | **`history filter preference round trips through defaults`** |
| M4 | `load` 把 `?? defaultValue` 换成直接返回第一个 case | **`unknown history filter value falls back to all`** |
| M5 | hover 分支返回与常态相同的色 | **`icon button hover surface differs from the rest state`** |
| M6 | hover 判定提到按下之前 | **`icon button press state outranks hover`** |
| M7 | `duration` 改成 `2.0` | **`selection row transition duration is a pinned literal`** |
| M8 | `surfaceOpacity` 的 `0.68` 改成 `1.0` | **`selection row surface opacity keeps the existing presentation`** |
| M9 | 把 `.animation(SelectionRowMotion.animation, value: isSelected)` 从背景层挪到 `HStack` 上 | **`selection row animation only targets the surface layer`** |

要求：
1. **每一轮变异前先完整还原**（`git checkout -- <被改文件>` 或重新 `git archive` 一份干净副本），否则下一轮会读到上一轮注入的缺陷。
2. **每一轮都要重新构建并检查退出码**（增量重建会读到陈旧二进制 → 假红）。**还原之后必须再跑一次基线**，确认回到 `60/60`。
3. 贴出**每一轮的原始输出**（变异后的 `[FAIL]` 行 + 还原后的 `Result: 60/60`）。
4. 若某个变异**没能杀掉**预期断言 → **如实报告，不要改断言去迁就**，也不要把它写成通过。

### D. 隔离黑盒回归

```bash
CLIPSHELF_PARITY_ISOLATED=1 CLIPSHELF_PARITY_DELIVERED=31,32,43,34,35,53,54,55,56,57 tests/parity/run.sh
```

- **除已知的 T16 偶发红之外**，0 FAIL；T11 两条必须 PASS。
- 报告里贴「汇总」三行。

### E. 手工确认清单（视觉项无法自动化，**必须实际打开 App 看一眼**，逐条打勾并写一句实感）

1. **G1**：先置顶任意一条记录（让它排到最前），再复制一段新文本 → **列表必须滚到顶**，新记录可见。
2. **G1 反例**：删除当前最新的那条记录 → 列表**不得**跳动。
3. **G2**：把筛选切到「图片」→ 退出 App → 重新打开 → 筛选**仍是「图片」**。
4. **G3**：鼠标悬停在某一行的置顶/复制/删除图标上 → 底色**有轻微变化**；移开后恢复。
5. **G3 反例**：按住不松 → 按下态比悬停态**更明显**。
6. **G4**：用 `↑`/`↓` 连续移动选中 → 选中块是**短促淡入**而不是硬跳；**文字与图标不得跟着渐变或抖动**。
7. **G4 深色**：切到深色模式 + 非预设选中色 → 选中块仍是**原来那个 0.68 的观感**（不得变得不透明）。

### F. 用户数据未被触碰

- 跑完 `shasum -a 256 ~/Library/Application\ Support/ClipShelf/history.json`，与**开始前**一致。
- 不得 `pkill` 用户正在运行的实例。
- **不得**往用户真实偏好域（`local.codex.ClipShelf`）写 `history.kindFilter` —— 断言一律用隔离 suite。

---

## 6. 报告要求

`prompts/prompt-57-interaction-defects-and-handfeel-report.md`，必须包含：

1. 改动清单（文件 / 行数增减 / 内容）。
2. Acceptance A 的**原始输出**。
3. Acceptance B 的 9 条断言名与**原始通过行**。
4. Acceptance C 的 **9 行变异矩阵**：变异 → 预期断言名 → 实际变红的断言名 → 原始输出；以及**还原后基线**的输出。
5. Acceptance D 的汇总行。
6. Acceptance E 的 7 项打勾 + 每项一句实感（**没做的项直接写「未做」**）。
7. Acceptance F 的哈希前后对比。
8. 一句话自评：**「你觉得这次改动的风险点在哪里？」** —— 直说，不要客套。

---

## 7. 附：这份规格**故意没做什么**

> 这一节是**硬约束**，不是建议。做了任何一项都会让 §5 的判定失去意义。

**上游有、但本份明确不做（理由见 `docs/parity/windows-optimization-log-study.md` §2.3 / §2.4）**：

- **不做**「滚轮物理」（win `af7d188` 的 `WheelScrollMotion` / `ScrollRenderingProbe`）：macOS 由 `NSScrollView` + 系统惯性滚动承担，自研一层会与系统打架。
- **不做**「滚动条宽度 / 自绘 pin 图标 / 灰细线统一 / 类型筛选分组表面 / 选中悬停稳定」：平台差异（macOS 用系统 overlay scroller、SF Symbols、单一 `AppTheme.subtleBorder` token、`Picker(.segmented)`）。
- **不做**「应用内更新」：macOS 无此机制；且**用户不是 Apple 开发者**，签名/公证不可行。**不得提出需要开发者账号的方案。**
- **不做**「预览页磁盘缓存 + 手动清理」：`docs/parity/windows-1.4.1-vs-macos.md:85` 已拍板不做（prompt-39 关闭）。
- **不做**「相邻预览预热」：同文档 `:84` 已判不适用。
- **不做**「预览渲染调度器 / 缓存行 / 回收式虚拟化」：WPF 特有；SwiftUI `LazyVStack` 自带惰性化，行内图片解码已由 **prompt-56 G3（缩略图缓存）**覆盖。
- **不做**「平滑度探针」：win 自己也在 `ad5c104` 把它整块删了（*Keep smoothness diagnostics local-only*）。本仓库的既有口径是**计时只进报告、不进 `--self-test` 断言**。
- **不做**「紧凑默认窗口尺寸 / 启动忽略已保存尺寸」：与 macOS `setFrameAutosaveName` 的既有习惯相反。
- **不做**「行内按钮作用域改成只作用本行」、「单击唯一已选记录改成保持选中」、「记录操作与类型筛选同排」：
  这三条是 **Windows 主动偏离 Mac** 的交互差异，按用户口径「只对齐功能、不对齐交互」保持 macOS 现状。
- **不做**「拖拽选择改增量更新」（win `779a7cd` 的 `ReplaceSelection`）：
  macOS 侧 `MainView.swift:42-44` 已有 `dragSnapshotItems` 快照（`:886-887` 建立 / `:444`、`:906` 清除），拖拽期间**不重算筛选**；
  `selectedIDs` 是 `Set`，赋值没有 WPF「重建 item container」的代价。照搬属于**无收益的复杂度**。

**与 prompt-55 / prompt-56 的边界（同批交付时不得互相越界）**：

- **不得**碰搜索路径（`SearchMatcher` / `ClipHistoryFilter.items` / 索引缓存）→ 归 **prompt-55**。
- **不得**碰「TIFF→PNG 移出主线程 / `HistoryWriter` 合并写盘 / 截图文件夹 500ms 轮询 / 缩略图缓存 / 打包改 `-c release`」→ 归 **prompt-56**。
- 本份只碰：`MainView` 的**滚动观察值**、**`kindFilter` 的读写**、**`ChatGPTIconButtonStyle`**、**`ClipRow` 的背景层**，以及 4 个**新增**的 `Support/` 文件与 `AppTheme` 的一个新 token。
