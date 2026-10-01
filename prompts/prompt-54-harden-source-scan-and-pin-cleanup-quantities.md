# prompt-54：收口「源码文本检查」的三个盲区 + 把「清理旧历史」的量与可撤销性钉进断言

**编号**：54
**性质**：**纯测试侧加固**。产品行为**一个字都不许变**（唯一例外见 §2.4 的 `isDeclaredMenuInstalled` 内部实现）。
**合并来源**：
- **prompt-52**（prompt-51 验收遗留的 3 × P3：注释盲区 / 诱饵盲区 / 对象错位）—— **本份把它并进来，52 作废，不要再单独出。**
- **prompt-53 验收遗留的 3 × P3**（自证式断言的量不可测 / 常量不可测 / 「必须走 `remove(ids:)`」这条规格约束没有任何断言）
**基线**：本机 `main` = **`8a8d31c`**（prompt-53 已验收，`prompts/prompt-53-verification-report.md`）

---

## 1. Goal

一句话：**把两条已经写好的断言从「看起来在守」升级成「真的守得住」，并让这个升级本身也被断言守住。**

拆成 4 件事：

1. **G1 —— 让 `isDeclaredMenuInstalled` 不再被「注释 / 诱饵 / 角色错位」骗过。** 现状实测：把整个真实 `.contextMenu` 块注释掉、或在它前面插一个诱饵菜单、或把 `role: .destructive` 从「删除」挪到「置顶」，**三种情况下断言都仍然全绿**（prompt-51 验收 V7b / V8 / V5）。
2. **G2 —— 把「清理旧历史」的窗口长度钉死。** 现状实测：把 `cutoffDate` 的 `* 86_400` 改成 `* 86_400 * 2`、`* 3_600`（天当小时）、甚至 `* 0`（= 一键删光全部未置顶），**47 条断言全部全绿**。根因是第 44 条断言**用被测函数自己算出的 cutoff 去构造样本**，样本与实现同步漂移。
3. **G3 —— 把默认值与选项集合的字面值钉死。** 现状实测：`defaultRetentionDays` 由 `30` 改成 `15`，**仍然全绿**（断言全用符号比较）。
4. **G4 —— 给「清理必须可撤销」这条规格约束补上断言。** 现状实测：把 `ClipStore.removeOldUnpinnedItems` 改成**绕过撤销栈直改** `items.removeAll { … }; save()`，**47 条断言仍然全绿**。prompt-53 §4.6 明确要求走 `remove(ids:)`，但**没有任何断言在守它**。

> **同时**：G1 的加固产物必须是一个**可复用**的源码扫描器，G4 复用它 —— 这是本份把 52 并进来的**唯一理由**：两个消费者共用一份加固过的扫描逻辑，而不是各自糊一个正则。

---

## 2. Evidence（2026-10-01 实测，HEAD `8a8d31c`）

> ⚠️ 行号已随 prompt-53 漂移，**下列为 2026-10-01 实测值，仅供参考**。若与你的 checkout 不符，**一律以「符号名」为准**，不要按固定偏移量换算。

### 2.1 要加固的那个检查

`Sources/ClipShelfLite/Support/ClearHistoryConfirmation.swift:41-80`：

```swift
static func isDeclaredMenuInstalled(in source: String) -> Bool {
    guard let menuBlock = contextMenuBlock(in: source) else { return false }
    return sourceContains(#"ForEach\(\s*ClipRowMenu\.orderedActions"#, in: menuBlock)
        && sourceContains(#"\brole:\s*\.destructive"#, in: menuBlock)
}

private static func sourceContains(_ pattern: String, in source: String) -> Bool {
    source.range(of: pattern, options: .regularExpression) != nil
}

private static func contextMenuBlock(in source: String) -> String? {
    guard let menuStart = source.range(of: #"\.contextMenu\s*\{"#, options: .regularExpression),
          let openingBrace = source[menuStart].lastIndex(of: "{") else { return nil }
    // …按花括号深度配对，返回从 .contextMenu 到配平的那个 } 为止的整块…
}
```

**三个盲区的机理**：

| 盲区 | 机理 | prompt-51 验收实测 |
| --- | --- | --- |
| 注释 | `range(of:)` 匹配的是**原始文本**，注释里的代码照样命中 | 把整块真实菜单 `/* */` 注释掉 → **44/44 全绿** |
| 诱饵 | `range(of:)` **只取第一个匹配** | 在真实菜单**之前**插一个同样合规的诱饵，同时破坏真实菜单 → **44/44 全绿** |
| 对象错位 | 判据是「块内**存在**某个 `role: .destructive`」，不是「**删除**那一项是破坏性的」 | 把 `role: .destructive` 从 `.delete` 挪到 `.pin`（仍在块内）→ **44/44 全绿** |

**当前无实害**：`MainView.swift` 全文**只有 1 处** `.contextMenu`、**0 处**注释、**0 处** `/*`（2026-10-01 实测）。所以这三条是**将来的坑**，不是现在的 bug —— 本份做的是**把坑填掉**，不是修故障。

### 2.2 现有代码的关键事实（2026-10-01 实测，供你设计扫描器时参考）

| 事实 | 值 |
| --- | --- |
| `MainView.swift` 总行数 | **1955** |
| `private struct ClipRow: View {` 起始行 | **973** |
| `.contextMenu {` 出现处 | **仅 1 处，行 1047**（紧跟 `:1044 .onTapGesture {` → `:1045 handleClick(NSApp.currentEvent)` → `:1046 }`） |
| `case .delete:` / `role: .destructive` | **1058 / 1059** |
| 全文 `role: .destructive` 出现次数 | **4 处**（`:86` 清空按钮、`:348` 行内删除按钮、`:1021` 行内删除按钮、`:1059` 右键菜单删除项）→ **这就是「不限定作用域就会误命中」的原因** |
| `MainView.swift` 里的注释 | **0 处**（既无 `//` 行注释，也无 `/* */`） |
| 字符串字面量里含 `://` 的行 | **0 处** |
| `ClipStore.swift` 总行数 | **435** |
| `ClipStore.swift` 里的注释 / `.contextMenu` | **0 / 0** |

> ⚠️ **别把「现在没有注释」当成可以不剥注释的理由** —— 恰恰相反：**正因为现在没有，加了剥注释才不会有任何行为变化**，是零风险的一次性加固。

### 2.3 第 44 条断言为什么测不出窗口长度（G2 的根因）

`Sources/ClipShelfLite/Support/SelfTest.swift:712-745`：

```swift
let cleanupNow = Date(timeIntervalSince1970: 2_000_000_000)
let cleanupCutoff = OldHistoryCleanup.cutoffDate(now: cleanupNow, retentionDays: 30)   // ← 用被测函数算边界
let pinnedOldItem       = ClipItem(… createdAt: cleanupCutoff.addingTimeInterval(-1), isPinned: true)
let unpinnedNewItem     = ClipItem(… createdAt: cleanupCutoff.addingTimeInterval(1))
let unpinnedOldItem     = ClipItem(… createdAt: cleanupCutoff.addingTimeInterval(-1))
let unpinnedAtCutoffItem = ClipItem(… createdAt: cleanupCutoff)
let cleanupEligibleIDs = OldHistoryCleanup.eligibleIDs(in: […], now: cleanupNow, retentionDays: 30)
```

`eligibleIDs` 内部**又调一次** `cutoffDate`（`OldHistoryCleanup.swift:40`）→ **样本边界与实现边界同步漂移**。
所以这条断言**能**测出「置顶保护 / 严格早于 / 边界不入选」（prompt-53 验收 M1/M2 已证明），**但完全测不出「窗口有多长」**。

`Sources/ClipShelfLite/Support/OldHistoryCleanup.swift`（全 50 行）关键部分：

```swift
static let retentionOptions = [7, 30, 90]           // :4
static let defaultRetentionDays = 30                // :5
static func normalized(_ days: Int) -> Int {        // :27
    retentionOptions.contains(days) ? days : defaultRetentionDays
}
static func cutoffDate(now: Date, retentionDays: Int) -> Date {   // :31
    now.addingTimeInterval(-TimeInterval(normalized(retentionDays)) * 86_400)
}
static func eligibleIDs(in items: [ClipItem], now: Date, retentionDays: Int) -> Set<ClipItem.ID> { … }  // :35
static func summaryText(eligibleCount: Int, retentionDays: Int) -> String { … }  // :44
```

### 2.4 要补断言的那条规格约束（G4）

`Sources/ClipShelfLite/Stores/ClipStore.swift:90-97`：

```swift
@discardableResult
func removeOldUnpinnedItems(retentionDays: Int, now: Date = Date()) -> Int {
    let ids = OldHistoryCleanup.eligibleIDs(in: items, now: now, retentionDays: retentionDays)
    remove(ids: ids)          // ← 必须经由它（它会 deletionUndoStack.record(...) → 可撤销）
    return ids.count
}
```

`remove(ids:)` 在 `ClipStore.swift:73-86`，里面是 `deletionUndoStack.record(entries)` → `items.removeAll { … }` → `save()`。

**为什么不能用纯逻辑断言**：`ClipStore` 是 `private init()` 的单例，`ClipStore.shared` 会写**用户真实数据目录**（自测里禁止触碰）。**所以这一条只能扫源码文本** —— 因此它**必须**复用 G1 加固过的扫描器，否则等于把三个已知盲区原样搬过来。

### 2.5 现有断言锚点（`Sources/ClipShelfLite/Support/SelfTest.swift`）

| 行 | 内容 | 本份要不要动 |
| --- | --- | --- |
| `:148` | `let defaults = AppEnvironment.userDefaults(environment: environment)` | 不动。新断言若要用 defaults，**必须用这个局部变量** |
| `:693-697` | `mainViewSourceURL` / `mainViewSource`（`#filePath` 往上两级 → `Views/MainView.swift`） | 不动。**G4 用同一套写法读 `Stores/ClipStore.swift`** |
| `:698` | `let runningFromAppBundle = Bundle.main.bundlePath.hasSuffix(".app")` | 不动（fail-open 用） |
| **`:699-710`** | **目标断言 43** `record context menu exposes the declared actions in order` | **断言名与 condition 文本都不动**（T11 依赖名里的 `context menu` 子串） |
| `:712-745` | 断言 44 `old history cleanup selects only unpinned records older than the window` | **不动**（它守的是置顶保护与边界，这两条已被证明有效） |
| **`:747-758`** | **断言 45** `old history cleanup retention window only accepts the offered values` | **就地加强**（见 §3 G3），**断言名一个字不许改** |
| `:760-769` | 断言 46 `old history cleanup summary reports the eligible count` | 不动 |
| **`:771-780`** | 最后一条断言 `self test defaults suite file count does not grow` | **新增的 4 条断言全部插在它之前**（即在 `:770` 的空行处插入） |

---

## 3. Scope

### 3.1 新增文件：`Sources/ClipShelfLite/Support/SourceScan.swift`

`Package.swift` 用的是 `path: "Sources/ClipShelfLite"` **目录通配**，**新增 Swift 文件不需要改包清单** —— 别去动 `Package.swift`。

实现一个**代码专用**的扫描器（只认代码，不认注释、不认字符串字面量里的文本）：

```swift
import Foundation

/// 只服务于 `--self-test` 的源码文本检查。
/// 目标：把「看起来在守」变成「真的守得住」。
enum SourceScan {
    /// 单趟词法扫描，返回「只剩代码」的文本：
    /// - 字符串字面量（含 `"` 与 `\"` 转义）**整段替换成 `""`** —— 字面量里的文本不参与匹配，也不参与花括号配对
    /// - `//` 到行尾 整段删除
    /// - `/* … */`（可跨行）整段删除
    /// 其余字符**原样保留**（包括换行，保证后续作用域切片的行结构可用）。
    static func codeOnly(_ source: String) -> String

    /// 取类型体：定位 `(struct|class|enum|extension)\s+<name>\b`，返回其后**最外层**花括号内的内容（不含声明行）。
    /// 花括号配对在 `codeOnly` 之后的文本上做。
    static func body(ofTypeNamed name: String, in source: String) -> String?

    /// 取函数体：定位 `func\s+<name>\s*\(`，返回其后**最外层**花括号内的内容。
    static func body(ofFunctionNamed name: String, in source: String) -> String?

    /// 正则包含（**容忍空白**，沿用 prompt-51 的既有约定）。
    static func contains(_ pattern: String, in source: String) -> Bool
}
```

**硬性要求**：

1. **`codeOnly` 必须对字符串字面量安全**：`"https://example.com"` 里的 `//` **不得**被当成行注释（否则整行被截断，后续匹配静默失效）。实测当前 `MainView.swift` / `ClipStore.swift` 里**没有** `://`，所以这条是**为将来兜底**；但**必须做到**，不许用「逐行 `split(separator: "//")[0]`」这种偷懒写法。
2. **`body(ofTypeNamed:)` 要能正确处理 `private struct ClipRow: View {` 这种带修饰符与冒号继承的写法**，并且**不许**把 `struct ClipRowMenu` 误当成 `ClipRow`（用词边界 `\b`）。
3. **`body(ofFunctionNamed:)` 用于 `removeOldUnpinnedItems`**，同样要词边界（别让 `removeOldUnpinnedItemsBackup` 之类误命中）。
4. **找不到时返回 `nil`**，**不许**返回空字符串（调用方要靠 `nil` 区分「没找到」与「找到了但是空的」）。

### 3.2 改 `ClearHistoryConfirmation.swift` 的 `isDeclaredMenuInstalled`

**签名一个字都不许改**（调用点 `SelfTest.swift:707` 不许动）：

```swift
static func isDeclaredMenuInstalled(in source: String) -> Bool
```

**内部改成**（三个盲区一起堵）：

1. **先剥注释与字符串字面量**：`let code = SourceScan.codeOnly(source)`，后续一切匹配都在 `code` 上做。
2. **限定作用域**：只扫 **`ClipRow` 类型体**（`SourceScan.body(ofTypeNamed: "ClipRow", in: code)`）。取不到 → **返回 `false`**（这是**收紧**，不是放宽：原实现「找不到菜单块返回 false」的语义要保住）。
3. **要求恰好一个菜单块**：在 `ClipRow` 体内找出**全部** `.contextMenu { … }` 配对块。
   - **0 个 → `false`**；**≥2 个 → `false`**（诱饵即被此条挡住）；**恰好 1 个 → 继续查**。
   - 实现方式建议：`body(ofTypeNamed:)` 之后再写一个「按花括号配对切出全部 `.contextMenu` 块」的小工具（可在 `ClipRowMenu` 内 private，也可放进 `SourceScan`，由你定）。
4. **该块内必须同时满足两条**：
   - `#"ForEach\(\s*ClipRowMenu\.orderedActions"#`（**保留**，这是 prompt-50 建立的「引用边」，不许删）
   - `role: .destructive` **必须落在 `case .delete:` 这一个分支内** —— 即：从 `case\s+\.delete:` 之后，到**下一个 `case\s+\.` 或该 switch 结束**之前的那一段里，必须匹配到 `#"\brole:\s*\.destructive"#`。
     - 推荐写法（ICU 正则，Swift 的 `.regularExpression` 支持前瞻）：`#"case\s+\.delete:(?:(?!\n\s*case\s+\.)[\s\S])*?\brole:\s*\.destructive"#`
     - **判据是行为，不是写法**：只要「把 role 从 `.delete` 挪到 `.pin` 会返回 `false`」成立即可。

**保留**原有的 `sourceContains` 风格（容忍空白）。**不要**放宽：`\brole:` 里的词边界、`ForEach` 的空白容忍，都保持。

> **本轮「行为不变」的例外就这一处**：`isDeclaredMenuInstalled` 是**检查器**，不是产品行为。产品界面、菜单项、快捷键**一个字都不许变**。

### 3.3 改 `SelfTest.swift`：断言 45 就地加强（G3）

**断言名一个字不许改**：`old history cleanup retention window only accepts the offered values`

在现有 `condition` 里**追加**绝对字面值判据（**不许删掉原有的任何一条**）：

```swift
&& OldHistoryCleanup.defaultRetentionDays == 30
&& OldHistoryCleanup.retentionOptions == [7, 30, 90]
&& OldHistoryCleanup.normalized(15) == 30
```

（`normalized(15) == 30` 是**字面值**，不是 `== defaultRetentionDays` —— 这一条才是真正钉死默认值的那个。）

`success` / `failure` 文案可以微调（要提到默认值 30 与选项集合），但**名字不许动**。

### 3.4 新增 4 条断言（全部插在 `SelfTest.swift:770` 的空行处，即最后一条断言之前）

> ⚠️ **断言名是「载荷」，必须逐字照抄，不要改写、不要意译、不要调换语序。** 原因：黑盒套件 T11 的覆盖度检查会拿这 4 个**关键词**去 `--self-test` 报告里做大小写不敏感的子串匹配，而这 4 个词**各自只出现在下面那一条断言名里**：
>
> | 关键词（T11 用） | 来自哪条 | 该词在「改之前」的 47 条报告里 |
> | --- | --- | --- |
> | `reflow` | A48 | **不存在** ✅ |
> | `decoys` | A49 | **不存在** ✅ |
> | `matches` | A50 | **不存在** ✅ |
> | `removal` | A51 | **不存在** ✅ |
>
> 四个词都已用脚本对着改之前的 47 条自测报告核过 —— **每一个都只在新增的那一条断言名里出现**，所以 T11 能真正区分「新增断言进了报告」与「没进」。改名 = T11 覆盖度检查失败。

#### A48 —— 扫描器的**正向**判据（纯逻辑，无文件 I/O，**不要** fail-open）

```
name: "row menu source check accepts the installed menu and tolerates reflow"
```

喂**合成字符串**给 `ClipRowMenu.isDeclaredMenuInstalled(in:)`，至少覆盖：

| 用例 | 期望 |
| --- | --- |
| 一段合规的 `struct ClipRow`（含 `.contextMenu { ForEach(ClipRowMenu.orderedActions …) { switch … case .delete: Button(…, role: .destructive) … } }`） | `true` |
| **把 `ForEach` 与 `orderedActions` 换行拆开、并加多余空格**（行为完全不变） | `true`（prompt-51 的空白容忍不许退化） |
| `struct ClipRowMenu { … }` 里塞一个合规菜单，但**真正的 `ClipRow` 里没有菜单** | `false`（词边界 / 作用域必须正确） |
| 把菜单文本放进一个**字符串字面量**里（例如 `let decoy = ".contextMenu { ForEach(ClipRowMenu.orderedActions …"`） | `false`（`codeOnly` 必须屏蔽字符串字面量） |

#### A49 —— 扫描器的**反向**判据（纯逻辑，无文件 I/O，**不要** fail-open）

```
name: "row menu source check rejects comments decoys and misplaced roles"
```

| 用例 | 期望 |
| --- | --- |
| 把合规菜单**整块用 `/* … */` 注释掉** | `false`（注释盲区） |
| 把合规菜单**用 `//` 逐行注释掉** | `false`（同上，两种注释都要管） |
| 在真正的菜单**之前**再放一个合规菜单（同一个 `ClipRow` 体内，共 2 个） | `false`（诱饵盲区） |
| `role: .destructive` 从 `case .delete:` **挪到 `case .pin:`**（仍在同一菜单块内） | `false`（对象错位） |
| 菜单块里**完全没有** `role: .destructive` | `false` |
| 菜单块里**没有** `ForEach(ClipRowMenu.orderedActions`（手写死 3 个 Button） | `false` |

**`failure` 文案必须报出「是哪一条子用例挂了」**（例如把子用例名拼进 `failure:` 字符串）。理由：这两条是复合判据，**失败时必须能一眼定位**，不许只给一句泛泛的失败信息。

#### A50 —— 把窗口长度钉死（G2）

```
name: "old history cleanup cutoff matches the declared retention window exactly"
```

```swift
let absoluteNow = Date(timeIntervalSince1970: 2_000_000_000)
condition:
    OldHistoryCleanup.cutoffDate(now: absoluteNow, retentionDays: 30)
        == Date(timeIntervalSince1970: 2_000_000_000 - 30 * 86_400)
    && OldHistoryCleanup.cutoffDate(now: absoluteNow, retentionDays: 7)
        == Date(timeIntervalSince1970: 2_000_000_000 - 7 * 86_400)
    && OldHistoryCleanup.cutoffDate(now: absoluteNow, retentionDays: 90)
        == Date(timeIntervalSince1970: 2_000_000_000 - 90 * 86_400)
    && OldHistoryCleanup.cutoffDate(now: absoluteNow, retentionDays: 0)
        == OldHistoryCleanup.cutoffDate(now: absoluteNow, retentionDays: 30)   // 非法值回落 30 天
```

**关键**：这三个期望值必须是**写死的字面量算式**（`2_000_000_000 - 30 * 86_400`），**绝对不许**再写成 `absoluteNow.addingTimeInterval(-30 * 86_400)` 之外的任何「调用被测函数」的形式。**这一条的价值全在「不经过被测函数」。**

#### A51 —— 把「清理必须可撤销」钉死（G4）

```
name: "old history cleanup routes through the shared removal path so it stays undoable"
```

沿用 `:693-697` 的 `#filePath` 写法，读 `Stores/ClipStore.swift`：

```swift
let clipStoreSourceURL = URL(fileURLWithPath: #filePath)
    .deletingLastPathComponent()
    .deletingLastPathComponent()
    .appendingPathComponent("Stores/ClipStore.swift")
let clipStoreSource = try? String(contentsOf: clipStoreSourceURL, encoding: .utf8)
```

然后**把作用域限定在 `removeOldUnpinnedItems` 的函数体内**，判据：

```swift
guard let body = clipStoreSource.map({ SourceScan.body(ofFunctionNamed: "removeOldUnpinnedItems", in: SourceScan.codeOnly($0)) }) ?? nil
    else { return runningFromAppBundle }      // 与既有 fail-open 一致（见 §4.5）
condition:
    SourceScan.contains(#"remove\(ids:"#, in: body)
    && !SourceScan.contains(#"items\.removeAll"#, in: body)
    && !SourceScan.contains(#"deletionUndoStack"#, in: body)
    && !SourceScan.contains(#"save\(\)"#, in: body)
```

（上面是**语义要求**，不是逐字模板；写法可以调整，但**四条子判据一条都不许少**，且**必须用 `SourceScan` 而不是自己再写一套正则**。）

### 3.5 断言总数

**47 → 51**（改 1 条 + 新增 4 条）。**既有 47 条一条都不许删、不许改名。**

---

## 4. Constraints

1. **产品行为零变化。** 除 §3.2 的 `isDeclaredMenuInstalled`（它是自测用的检查器）外，**不许改任何产品代码**：`Views/`、`App/`、`Stores/ClipStore.swift`、`Support/OldHistoryCleanup.swift`、`Support/ClearHistoryConfirmation.swift` 里的 `ClipRowMenuAction` / `ClipRowMenu.title` / `pinTitle`、`Support/HistoryDeletionUndo.swift` —— **全部只读**。
2. **`Package.swift` 不许改**（目录通配，新增文件不需要动它）。
3. **`tests/parity/` 不许改**（归 WorkBuddy 所有）。包括 `manual-checklist.md` 与 `lib.sh`。**跑黑盒失败时不要改脚本，如实报告。**
4. **目标断言名 `record context menu exposes the declared actions in order` 与断言 45 的名字 `old history cleanup retention window only accepts the offered values`，一个字都不许改**（T11 靠名字里的子串做覆盖度检查）。
5. **新增断言的 fail-open 口径**：
   - **A48 / A49 / A50 是纯逻辑**（喂合成字符串 / 算日期），**不要** fail-open，**不要**读文件。
   - **A51 读文件**，沿用既有的 `?? runningFromAppBundle` fail-open（与 `:707` 一致）。**并在报告里写明**：打包分发场景下该断言等于不存在 —— 这是既有口径，不要改。
   - **好处**：A48/A49 把扫描器的加固**独立于「源码可读」**地守住了，正好补上 fail-open 的弱点。
6. **不许把断言改成恒红或恒绿来「过」反向验证。** 反向验证的正确姿势是：**在未变异的当前代码上 51/51 全绿**，然后注入缺陷才变红。
7. **新断言一律不依赖 `ClipStore.shared`**（它会写用户真实数据目录）。
8. **不引入第三方依赖。**

---

## 5. Acceptance

### A —— 构建与测试

```bash
swift build --disable-sandbox
swift test --disable-sandbox --scratch-path /tmp/cs-p54      # 必须用 /tmp，仓库在 ~/Documents 下受 iCloud 管理，就地跑会签名失败
.build/debug/ClipShelf --self-test /tmp/p54-report.txt       # 隔离数据目录与 defaults suite
```

预期：`swift test` **exit 0**、`[PASS]` **102 = 2 × 51**、`[FAIL]` **0**；`--self-test` **51/51 checks passed**、**exit 0**。
贴关键原始输出（含新增 4 条的 `[x]` 行）。

### B —— 未变异状态下必须全绿（**先做这一步，再做 C**）

把**当前未改动**的 `MainView.swift` / `ClipStore.swift` 喂给新断言 → **必须 51/51 全绿**。
**若某条新断言在未变异代码上就是红的，说明判据写错了，不许靠改断言文案糊过去 —— 要改实现。**

### C —— 三个盲区的**真实文件**反向验证（必须逐条贴原始输出 + 完整还原）

对**真实的 `Sources/ClipShelfLite/Views/MainView.swift`** 做下面三次注入，每次改完**重跑 `--self-test`**，**必须变红（≤50/51）**，然后**逐字节还原**：

| # | 注入 | 期望 |
| --- | --- | --- |
| **C1 注释盲区** | 把**整个真实 `.contextMenu { … }` 块**（`:1047` 起）用 `/* … */` 包起来 | **必须红**（改前是 51/51 假绿） |
| **C2 诱饵盲区** | 在真实菜单**之前**（同一个 `ClipRow` 体内）插入一个**同样合规**的 `.contextMenu { … }`，同时把真实菜单里的 `role: .destructive` 删掉 | **必须红**（改前是 44/44 假绿） |
| **C3 对象错位** | 把 `role: .destructive` 从 `:1059` 的 `.delete` 分支**挪到** `.pin` 分支 | **必须红**（改前是 44/44 假绿） |

> ⚠️ **注入必须是合法 Swift，且每轮都要检查构建退出码。** 注释整块时用 `/* … */`，**不要**用 `// ` 逐行加前缀 —— 后者会打断 SwiftUI 链式调用导致构建失败，**构建失败的一轮不得计入结论**（prompt-51 验收已踩过这个坑）。
> ⚠️ **每轮注入前先还原上一轮**，别把上一轮的缺陷带进下一轮（会得到「每条断言都挂」的假象）。

### D —— 变异测试（证明新增 4 条断言不是空转，必须逐条贴原始输出）

在 `/tmp` 的干净副本上做。**每一轮先还原全部被改文件、再注入、构建退出码必须为 0 才计有效轮次**。

| # | 注入的缺陷 | 期望恰好击落 |
| --- | --- | --- |
| M1 | `SourceScan.codeOnly` 改成直接 `return source`（不剥注释） | **A49** |
| M2 | `codeOnly` 里**去掉字符串字面量处理**（保留剥注释） | **A48**（字面量诱饵那条） |
| M3 | `isDeclaredMenuInstalled` 改回 `source.range(of:)` 只取**第一个**菜单块（去掉「恰好一个」） | **A49** |
| M4 | `isDeclaredMenuInstalled` 里 `role: .destructive` 改回「块内存在即可」（去掉 `.delete` 分支绑定） | **A49** |
| M5 | `isDeclaredMenuInstalled` 里**去掉** `ForEach(ClipRowMenu.orderedActions` 那一条 | **A48 与 A49 都应变红**（引用边是两边的共同前提） |
| M6 | `OldHistoryCleanup.cutoffDate` 的 `* 86_400` 改成 `* 3_600` | **A50** |
| M7 | `OldHistoryCleanup.defaultRetentionDays` 由 `30` 改成 `15` | **断言 45（改后）与 A50** |
| M8 | `ClipStore.removeOldUnpinnedItems` 改成绕过撤销栈（`items.removeAll { ids.contains($0.id) }; save()`） | **A51** |
| M9 | `body(ofTypeNamed:)` 的 `\b` 词边界去掉，使 `struct ClipRowMenu` 被误当成 `ClipRow` | **A48** |

**判据**：每轮**失败断言名（去重后）恰好等于上表所列**。若某条新断言在对应变异下**依然通过** → **该断言空转**，必须修，并如实报告。

### E —— 断言完整性

- `grep -c 'results\.append(check('` == **51**。
- 与 `8a8d31c` 对比断言名集合：**消失 0 条**，新增 **4** 条，且**两条被改动的断言（43 / 45）名字逐字未变**。
- 贴出对比用的脚本与原始输出。

### F —— 隔离黑盒回归

```bash
CLIPSHELF_PARITY_ISOLATED=1 \
CLIPSHELF_PARITY_DELIVERED=31,32,43,33,38,37,34,35,40,41,36,42,44,45,46,47,49 \
tests/parity/run.sh
```

- 预期 **0 失败**，T11 两条通过。
- ⚠️ **`T16 上限 10000 时 8 条全部累积` 是已知的偶发脆弱用例**（prompt-53 验收实测：全量上下文下约 12 次红 2 次；只跑它自己 6/6 绿）。**它红了就重跑一次并如实标注，不要去改 `tests/parity/`**。
- ⚠️ **跑黑盒期间不要并发跑 `swift build`**（变异脚本、其它编译都会干扰它的计时）—— prompt-53 验收时我自己踩过这个坑。
- **上面这条命令不带 `54`**（保持与 prompt-53 验收时完全一致的口径）。跑完之后**再跑一次带上 `54` 的版本**，用来确认 T11 的关键词命中：
  ```bash
  CLIPSHELF_PARITY_ISOLATED=1 \
  CLIPSHELF_PARITY_DELIVERED=31,32,43,33,38,37,34,35,40,41,36,42,44,45,46,47,49,54 \
  tests/parity/run.sh
  ```
  预期 T11 的覆盖度那条**通过**（`reflow` / `decoys` / `matches` / `removal` 四个词都能在报告里命中）。
  **若未命中 —— 先检查是不是你把断言名改写了（§3.4 的警告），不要改 `tests/parity/lib.sh`（那是我的文件），如实把现象贴出来。**

---

## 6. Handoff

1. **只改 2 个文件 + 新增 1 个文件**：

```
A  Sources/ClipShelfLite/Support/SourceScan.swift                    （新增）
M  Sources/ClipShelfLite/Support/ClearHistoryConfirmation.swift      （只改 isDeclaredMenuInstalled 及其 private 辅助）
M  Sources/ClipShelfLite/Support/SelfTest.swift                       （改 1 条 + 新增 4 条）
```

**改动集里出现任何第 4 个文件（`prompts/` 下的报告除外），请停下来说明原因。**

2. **交付后出一份 `prompts/prompt-54-harden-source-scan-and-pin-cleanup-quantities-report.md`**，内容至少包含：
   - 改动清单（新增 / 修改 / 删除，带行数）
   - Acceptance A 的原始输出（`swift build` / `swift test` / `--self-test` 三条）
   - Acceptance B 的结论（未变异 51/51）
   - Acceptance C 的**三次注入的原始输出**（含注入前后计数）+ 还原证明
   - Acceptance D 的**变异矩阵原始输出**（每轮：注入 → 构建退出码 → 失败断言名）
   - Acceptance E 的断言名对比原始输出
   - Acceptance F 的黑盒汇总行
   - 一句话自评：**新增 4 条断言里，有没有哪一条你自己觉得「其实测不出什么」**

3. **不许**把这份提示词文件本身提交进 git 之外的地方；`prompts/` 下我的原稿你可以一并提交（前几份都是这么做的），**但不要改动它的内容**。

---

## 7. 附：这份规格「故意没做什么」

- **没做**「把 `removeOldUnpinnedItems` 改成可注入实例以便纯逻辑断言」—— 那要动 `ClipStore` 的单例结构，风险远大于收益，且会碰到用户真实数据目录的隔离保证。**本份明确选择扫源码 + 复用加固过的扫描器。**
- **没做**「花括号配对跳过字符串字面量里的花括号」的**独立**处理 —— 因为 §3.1 的 `codeOnly` 已经把字符串字面量整段换成 `""`，花括号配对在 `codeOnly` 之后做就自动安全了。**如果你发现实现上做不到，如实报告，不要偷偷降级。**
- **没做**「断言 44 改成不经过 `cutoffDate`」—— 它守的是置顶保护与边界（`<` vs `<=`），那两条**已经被证明有效**（prompt-53 验收 M1/M2）；窗口长度由新增的 A50 单独负责。**两条各守一段，不要互相吞掉。**
