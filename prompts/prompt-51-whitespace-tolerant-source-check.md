# prompt-51：把「源码文本检查」做扎实（容忍空白 + 限定作用域 + 收紧静默放行）

依据 `AGENTS.md` 的 Prompt and collaboration guidelines。协作约定与编号见 `prompts/README.md`。

> **来源**：`prompts/prompt-50-verification-report.md` **§7 P3-1 / P3-2 / P3-3**（三条都是同一处代码引出的）。
> **依赖**：prompt-50 已交付（`0c10b11`）。**基线断言数 = 44**。
> **性质**：**测试侧加固**，不是新功能。**不改任何产品行为、不改 UI、不改真实菜单。**

---

## 1. 背景（一句话）

prompt-50 为了让「右键菜单确实引用声明清单」这件事可断言，在断言里**扫 `MainView.swift` 的源码文本**。方向是对的（验收已用反向探针证明引用边成立），但实现有三个毛病：

```swift
// Sources/ClipShelfLite/Support/ClearHistoryConfirmation.swift:44-47
static func isDeclaredMenuInstalled(in source: String) -> Bool {
    source.contains(".contextMenu {")
        && source.contains("ForEach(ClipRowMenu.orderedActions")
}
```

```swift
// Sources/ClipShelfLite/Support/SelfTest.swift:707
&& (mainViewSource.map(ClipRowMenu.isDeclaredMenuInstalled(in:)) ?? true),
```

### 1.1 毛病一：**对格式敏感 → 假红**（P3-1，最要紧）

`source.contains(...)` 是**逐字符**匹配。验收时实测：**只把 `ForEach` 换行写成多行、行为完全不变，断言就 43/44 变红**：

```swift
ForEach(
    ClipRowMenu.orderedActions,
    id: \.self
) { action in
```

多一个空格（`ForEach( ClipRowMenu...`）同理。失败信息还是
`The record context menu is missing, detached from its declaration, or has divergent titles`
—— **误导性很强**：菜单明明好好的。将来任何人（或任何格式化工具，如 `swift-format`）重排这一行，都会踩到。

### 1.2 毛病二：**`role: .destructive` 没有覆盖**（P3-2）

实测：把菜单里删除按钮的 `role: .destructive` 去掉，**44/44 仍然全绿**。
规格（prompt-50 §3）要求它必须保留，但当时的判据没管它 —— 这是**规格的缺口**，本份补上。

> ⚠️ **补的时候有个坑**：`MainView.swift` 里**不止一处** `role: .destructive` —— 工具栏垃圾桶按钮也是
> （`Button(role: .destructive) { deleteSelectedOrClear() }`）。所以**不能全局搜** `role: .destructive`，
> 那样即使菜单里的删掉了也照样通过。**必须把检查限定在 `.contextMenu` 块内部。**

### 1.3 毛病三：源码读不到时**静默放行**（P3-3）

`?? true` 是 fail-open：源码不可读时这条判据**直接算通过**。
交付方是**有意为之**（避免分发后的 `.app` 里 `--self-test` 永久失败），也已在报告里披露 —— 可以理解。
但它意味着：**在开发环境里，这条判据也可能悄悄不干活**。本份把它收紧。

---

## 2. Evidence（2026-09-30 21:29 实测，HEAD `0c10b11`）

| 位置 | 现状 |
| --- | --- |
| `Sources/ClipShelfLite/Support/ClearHistoryConfirmation.swift:44-47` | `isDeclaredMenuInstalled(in:)`，两个 `contains` |
| `Sources/ClipShelfLite/Support/SelfTest.swift:693-697` | 读取 `MainView.swift` 源码的 5 行（`#filePath` → 上两级 → `Views/MainView.swift`） |
| `Sources/ClipShelfLite/Support/SelfTest.swift:698-710` | 目标断言 `record context menu exposes the declared actions in order`（8 个子句） |
| `Sources/ClipShelfLite/Support/SelfTest.swift:712` | 收尾断言 `_ = CFPreferencesAppSynchronize(...)`，**必须保持整段最后一条** |
| `Sources/ClipShelfLite/Views/MainView.swift:1095-1116` | 真实菜单：`ForEach(ClipRowMenu.orderedActions, id: \.self)` + `switch`，删除项 `role: .destructive` |
| `Sources/ClipShelfLite/Views/MainView.swift` 的工具栏垃圾桶 | **另一处** `Button(role: .destructive)` —— §1.2 的坑就在这 |

- `Sources/ClipShelfLite/Support/SelfTest.swift` 当前共 **44 条**断言。

---

## 3. Scope

**受影响（只有这两个文件）**

- `Sources/ClipShelfLite/Support/ClearHistoryConfirmation.swift` —— 只改 `isDeclaredMenuInstalled`（及其新增的私有辅助）
- `Sources/ClipShelfLite/Support/SelfTest.swift` —— 只改**目标断言那一条**（`:693-710` 区间）

**不得改变的部分**

- **`Sources/ClipShelfLite/Views/MainView.swift` 一个字都不许改**（真实菜单保持现状；反向验证时的临时改动必须**完整还原**）。
- **其余 43 条断言**一条都不许改、不许删。
- **断言总数必须仍是 44**（本份是加固，不新增断言）。
- **`--ctl clear` / `ClipStore.clearHistory()` / `ClearHistoryConfirmation` 的三个既有纯函数**不许动。
- 不改 `Package.swift`、`tests/parity/`、`docs/`、`README.md`、`Assets/`、`script/`。
- **不要顺手做**：把 `ClipRowMenu` 从 `ClearHistoryConfirmation.swift` 挪到别的文件（虽然文件名确实不贴切）—— 那是额外改动，本份不做。

---

## 4. Constraints

### 4.1 检查必须**容忍空白与换行**

把两个 `contains` 换成**正则**，例如：

```swift
private static func sourceContains(_ pattern: String, in source: String) -> Bool {
    source.range(of: pattern, options: .regularExpression) != nil
}
```

- 菜单存在性：`#"\.contextMenu\s*\{"#`
- 引用声明清单：`#"ForEach\(\s*ClipRowMenu\.orderedActions"#`

> 注意 Swift 里写正则用**原始字符串** `#"..."#`，反斜杠不用双写。

### 4.2 `role: .destructive` 的检查必须**限定在 `.contextMenu` 块内部**

- **做法建议**：先写一个「取出 `.contextMenu { … }` 配对花括号块」的私有辅助（从 `.contextMenu` 之后第一个 `{` 开始数括号深度），再**只在这个块里**检查 `#"\brole:\s*\.destructive"#`。
- **不接受**：在整个 `MainView.swift` 上搜 `role: .destructive`（工具栏垃圾桶会假通过，见 §1.2）。
- 取块失败（找不到 `.contextMenu`）时，该子句应判 **false**。

### 4.3 收紧 fail-open（源码不可读时）

规则：**只有「从 `.app` 包内运行」时才允许静默放行；其它情况（开发二进制 / `swift test`）源码不可读即判失败。**

最小改法示意（实现方式你定）：

```swift
let runningFromAppBundle = Bundle.main.bundlePath.hasSuffix(".app")
...
&& (mainViewSource.map(ClipRowMenu.isDeclaredMenuInstalled(in:)) ?? runningFromAppBundle),
```

> 理由：`swift test` 的 `Bundle.main` 是 `.xctest` 包，仓库里的裸二进制 `Bundle.main.bundlePath` 是 `.build/debug` —— 两者都**不以 `.app` 结尾**，因此在开发环境会走**严格**分支；只有 `dist/ClipShelf.app` 才放行。
> **必须在报告里写清这个判定依据**，并说明它是否影响 `tests/parity/`。

### 4.4 其它

- **断言名保持不变**：`record context menu exposes the declared actions in order`
  （必须仍含 `context menu` 子串 —— `tests/parity/lib.sh` 的 T11 覆盖度检查依赖它）。
- `success` / `failure` 文案**建议**更新为能反映「含破坏性角色」的说法；改文案不违反本节，但**名字不许改**。
- **不引入第三方依赖。**

---

## 5. Acceptance

- **A** `swift build --disable-sandbox` 通过；`swift test --disable-sandbox --scratch-path /tmp/cs-p51` 通过（**不要**用仓库内的 `.build`，会因 iCloud 签名失败）。贴关键输出。

- **B（核心）三条反向验证 —— 每一条都要贴原始输出，并完整还原。**

  > 这是本份最重要的部分。**「跑一遍全绿」没有任何证明力**，因为修复前后都是全绿。

  1. **证明「容忍空白」真的生效**：把 `MainView.swift:1096` 的
     `ForEach(ClipRowMenu.orderedActions, id: \.self) { action in`
     **临时**改写成跨多行的等价写法（见 §1.1），**行为完全不变**。
     → **断言必须仍然全绿（44/44）**。若变红 ⇒ 本项没做到。
     （对照：修复前这种写法会 43/44 变红。）
  2. **证明 `role: .destructive` 的检查真的生效**：把菜单里删除按钮的 `, role: .destructive` **临时**去掉。
     → **断言必须变红（43/44）**。若仍全绿 ⇒ 检查没连上真实菜单项（很可能是全局搜索假通过了）。
  3. **证明 fail-open 已收紧**：把 `SelfTest.swift:696` 的
     `.appendingPathComponent("Views/MainView.swift")`
     **临时**改成 `.appendingPathComponent("Views/NoSuchFile.swift")`。
     → 用**仓库里的裸二进制**跑 `--self-test`，**断言必须变红**。若仍全绿 ⇒ 还是 fail-open。
     （对照：修复前这种写法是 44/44 全绿。）

  4. **每一条临时改动都要完整还原**，最后用 `git diff` 证明 `MainView.swift` **零改动**、`SelfTest.swift` 只剩本份该有的改动。

- **C（回归）** 连续 3 次 `--self-test` 都是 **44/44、exit 0**；断言总数仍是 **44**；其余 43 条零改动（贴 `git diff` 的 `SelfTest.swift` 段）。

- **D（回归）** `CLIPSHELF_PARITY_ISOLATED=1 CLIPSHELF_PARITY_DELIVERED=31,32,43,33,38,37,34,35,40,41,36,42,44,45,46,47,49 tests/parity/run.sh`
  → **0 FAIL**（T12 打包冒烟按脚本默认跳过是正常的）。**T11 覆盖度检查必须通过**。
  > T16 偶发红是**测试套件自身**的问题，**已由 WorkBuddy 修好**（预热 + 增量断言）。若你仍遇到它变红，如实贴出，**不要改它**（`tests/parity/` 不归你）。

- **E** 报告：`prompts/prompt-51-whitespace-tolerant-source-check-report.md`，含：
  - 改动清单（文件 + 行数）；
  - A–D 的原始输出；
  - **B 的三条反向验证的完整四步记录（含还原证据）**；
  - 一节说明 **fail-open 的判定依据**（`Bundle.main.bundlePath` 在三种运行方式下分别是什么，是否影响 `tests/parity/`）。

---

## 6. Handoff

- **实现**：Codex。**独立测试与复核**：WorkBuddy（只读，不改源码）。
- **授权（未勾选即未授权）**：`[ ]` 本地检查 · `[ ]` Release 构建 · `[ ]` 覆盖安装 · `[ ]` `git commit` · `[ ]` `push` · `[ ]` GitHub 发布。
  **本轮建议**：仅本地检查。
- **提交信息建议**：`Make the row menu source check whitespace tolerant and strict in development`
