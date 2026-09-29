# 批次 C（最后 4 份）：40 → 41 → 36 → 42

依据 `AGENTS.md` 的 Prompt and collaboration guidelines。协作约定见 `prompts/README.md`。

---

## 0. 本文件的性质（先读这段）

本文件**不是新的功能规格**，而是一份**批次执行指令**。

功能规格是下列 **4 份既有提示词文件**，必须逐份**完整读取**并执行：

| 顺序 | 文件                                          | 主题                              |
| -- | ------------------------------------------- | ------------------------------- |
| 1  | `prompts/prompt-40-status-menu-pause-settings.md` | 菜单栏「暂停记录 ↔ 继续记录」与「设置」           |
| 2  | `prompts/prompt-41-image-preview-zoom.md`   | 图片预览缩放（50%–400%）                |
| 3  | `prompts/prompt-36-image-ocr.md`            | 图片记录 OCR（按需触发）                  |
| 4  | `prompts/prompt-42-text-preview-enhancement.md` | 文本预览增强（行号 / 换行 / 查找 / 编码）       |

**执行顺序：40 → 41 → 36 → 42。** 顺序不可调换，理由见 §1.1。

> **本批是执行清单的最后一批。** 已交付并独立验收通过的 **8 份**为：31、32、43、34、35、33、38、37。**不要重做**。  
> 39（缓存清理）**已拍板不做**。本批做完，`prompts/` 的执行清单即全部完成。

---

## 1. 为什么合并成一批

用户 2026-09-28 明确要求提速：「一次让 codex 做多几个功能，一个一个来太慢了」。

批次**只减少往返次数**，**不改变**任何一份提示词的目标、约束或验收标准。

### 1.1 顺序依据（41 必须在 36 与 42 之前）

- **41 → 36 有依赖**：41 把图片面板从「自适应缩放」改成「可缩放 + 可滚动」，36 的**拖框选择**必须在**图片坐标**下换算选区。若先做 36 再做 41，41 会改掉 `NSImageView` 的尺寸语义，36 的选区数学随即失效。
- **41 → 42 有依赖**：41 占用 `Cmd+=` / `Cmd+-` / `Cmd+0`，42 要往**同一个** `startKeyMonitor()` 里加 `Cmd+F` / `F3` / `Shift+F3`。先做 41 定下快捷键，42 只需「不覆盖」即可；反过来则要回头改 41。
- **40 与另外三份无交集**：40 只动 `App/AppDelegate.swift` + `Views/MainView.swift`；41 / 36 / 42 全部只动 `Services/PreviewController.swift`（加各自新增文件）。40 最小、最独立，**先做**。
- **36 成本最高**（引入 Vision、后台识别、降采样、进行状态），**42 优先级最低**（`prompts/README.md` §1 表格），故 36 第三、42 最后。

### 1.2 分工（**硬性，避免同批互相打架**）

**（a）图片面板的控件位置 —— 41 与 36 都改同一个面板，必须分区**

| 提示词 | 新增控件                        | 位置（本批定死）                |
| --- | --------------------------- | ----------------------- |
| 41  | 缩放按钮 `+` / `−` / `复位` + 当前百分比 | 面板**右下角**浮层             |
| 36  | 「识别文字」入口（工具栏）               | 面板**顶部**工具条             |

> 两份提示词的 §4 都只说「窗口角落」「预览工具栏」，未定死。**本批为它们分配不同的边**：41 占**右下**、36 占**顶部**。任何一份都**不得**把控件放到对方的区域。

**（b）键盘事件 —— 41 与 42 都改 `startKeyMonitor()`，必须共存**

| 提示词 | 新增按键                                        | 说明                          |
| --- | ------------------------------------------- | --------------------------- |
| 41  | `Cmd+=` / `Cmd+-` / `Cmd+0`（+ `Cmd+滚轮`）    | `Cmd+滚轮` 需**额外**注册 `scrollWheel` 监听 |
| 42  | `Cmd+F` / `F3` / `Shift+F3` / `Esc`（查找条优先） | `Esc` 只在**查找条打开时**拦截        |
| 36  | 无                                            | 不得占用任何快捷键                   |

- **42 必须保留 41 的 `Cmd+=` / `Cmd+-` / `Cmd+0`**（42 的 §3 已写明，此处再强调一次）。
- **41 引入的 `scrollWheel` 监听必须在 `stopKeyMonitor()` 里一并移除**，否则关闭预览后仍会响应滚轮。现有的 `stopKeyMonitor()` 只移除一个 `keyMonitor`（`PreviewController.swift:364-369`）。

**（c）`showBuiltInPreviewIfPossible(_:)` 的三个分支各归各家（`PreviewController.swift:116-140`）**

| 分支         | 归属  | 允许的改动                             |
| ---------- | --- | --------------------------------- |
| `.image`   | 41  | 允许（缩放入口）                          |
| `.text`    | 42  | 允许（行号 / 换行 / 查找 / 编码）              |
| `.file`    | 42  | **只允许**改「回落判定」：文本类扩展名走内置文本预览      |
| `.file` 的**图片判定** | 谁都不许改 | `.file` 记录只要 `NSImage(contentsOf:)` 成功就仍走 `showImagePreview`，**必须排在文本扩展名判定之前** |

> **36 不得改这个函数**：36 的 §3 已写明「图片预览的默认呈现不得因本项改变」。OCR 入口加在 `showImagePreview` 内部。

**（d）`README.md` 功能列表 —— 4 份都要追加，是唯一的文件级争用点**

`README.md` 结构（实测）：中文 `### 功能` 在 **L7**（条目到 L28），英文 `### Features` 在 **L114**（条目到 L135）。

→ **只追加条目，不得重排、改写、删除任何既有条目**；中英两处都要加。
→ 串行执行时按 40 → 41 → 36 → 42 依次追加，每份追加在**该列表末尾**。

---

## 2. 每份提示词仍然各自独立成立

对 4 份中的**每一份**，下列内容继续完全生效：

- 它的 §3「**不得改变的部分**」；
- 它的 §4 Constraints；
- 它的 §5 Acceptance（**必须逐条满足**，不得因为「批次」而降低标准）；
- 它的 §6 Handoff。

**统一授权口径**：4 份的授权项**均未勾选**，即**仅授权本地检查**。  
→ **不要**执行 Release 构建、覆盖安装、`git commit`、`push`、GitHub 发布。  
→ `git commit` / `push` / 上传 / 发布**一律由用户本人执行**。

**统一环境约束**（见 `prompts/README.md`「环境约束」，对 4 份都生效）：

1. **不得引入 XCTest / Swift Testing，不得要求安装 Xcode**（本机 `import XCTest` 与 `import Testing` 均 `no such module`）。
2. 测试断言**一律加进 prompt-31 已建立的底座**：`ClipShelfSelfTest.run()` 为唯一实现，`--self-test` 与 `MacTests/ClipShelfLiteTests/SelfTestTests.m` 桥接为两个入口。**断言逻辑只实现一次。**
3. 只用 Apple 随系统提供的框架（AppKit / SwiftUI / **Vision** / QuickLookUI 等）；不新增第三方依赖。
4. 不做代码签名证书、不做公证；ad-hoc 签名保持不变。

---

## 3. 证据锚点的时效性（**本批已复测**）

**2026-09-28 复测**，HEAD `0860ab3`（`Add history filters and file type indicators`）。

### 3.1 `Services/PreviewController.swift` —— ✅ **完全未漂移**

`git log 355acec..HEAD -- Sources/ClipShelfLite/Services/PreviewController.swift` → **0 次改动**（文件 395 行）。

因此 **36 / 41 / 42 三份引用的行号全部仍然准确**：

| 符号                                            | 提示词写的     | **实测**     |
| --------------------------------------------- | --------- | ---------- |
| `showImagePreview(_:title:)`                  | 154-172   | **154-172** |
| `showBuiltInPreviewIfPossible(_:)`            | 116-140   | **116-140** |
| `showTextPreview(_:title:)`                   | 174-191   | **174-191** |
| `previewURL(for:)`                            | 70-92     | **70-92**   |
| 窗口尺寸策略（`recommendedImageWindowSize` 等）        | 205-248   | **205-248** |
| `previewPanel(title:size:)`                   | 299-317   | **299-317** |
| `startKeyMonitor()`                           | 328-347   | **328-347** |
| `previewNavigationDirection(for:)`            | 353-362   | **353-362** |
| `cleanupTemporaryFiles()`                     | 371-376   | **371-376** |
| `scaleProportionallyUpOrDown`                 | （36 §3 引用） | **L164**    |

**36 的两条前置结论也复核通过**：全仓检索 `Vision` / `VNRecognize` → **仍为零命中**；图片面板确实**没有工具栏、没有缩放状态**。

**42 的一条前置结论复核通过**：检索 `isHorizontallyResizable` / `widthTracksTextView` → **零命中**，文本预览确实**没有行号栏、没有换行开关、没有查找条**。

### 3.2 `App/AppDelegate.swift`、`Stores/ClipStore.swift`、`Views/SettingsView.swift`、`Views/MainView.swift` —— ⚠️ **已漂移**

漂移来自 3 个提交：`dd9ce81`（测试底座）、`0417d7c`（历史上限 + 控制通道）、`e1362a2`（删除撤销 + 多文件拆分）。

**`prompts/prompt-40-status-menu-pause-settings.md` 的 Evidence 段已按实测更新**，对照如下：

| 符号 / 位置 | 提示词原写的 | **实测** | 备注 |
| --- | --- | --- | --- |
| `AppDelegate.swift` `rebuildMenu(_:)` | 141-170 | **149-178** | 菜单项：显示 ClipShelf(151) / 选择截图文件夹(152) / 分隔(153) / 最近 8 条(155-173) / 分隔(175) / 清空历史(176) / 退出(177) |
| `AppDelegate.swift` `menuWillOpen(_:)` | 120-122 | **128-130** | 菜单每次打开都重建 → 标题可动态切换 |
| `AppDelegate.swift` 状态栏图标机制 | 124-139 | **132-147** | §3「不得改变」引用 |
| `ClipStore.swift` `isClipboardHistoryEnabled` | 9-13 | **11-15** | 持久化键定义在 **L19** |
| `ClipStore.swift` 轮询守卫 | 115-117 | **163-169** | **语义已变**：不再是内联 `guard`，改为委托 `ClipboardHistoryPolicy.shouldCapture(historyEnabled:...)` |
| `SettingsView.swift` 同一个开关 | 159 | **165** | `Toggle("记录文字、文件和图片复制历史", isOn: $store.isClipboardHistoryEnabled)` |
| `MainView.swift` `showingSettings` | 8 | **9** | `@State private` |
| `MainView.swift` `openSettings()` | 351-355 | **407-411** | `private` |
| `MainView.swift` `closeSettings()` | — | **413-417** | `private` |
| `MainView.swift` `settingsOverlay` | 99-127 | **98-126** | — |

> **漂移量不统一**（`AppDelegate` +8、`ClipStore` +48、`SettingsView` +6、`MainView` 头部 +1 / 中段 +56）。  
> 规则：**行号不符时以符号名为准，并在报告中指出。**

**40 的一处额外提醒（不是错误，是缺口）**：`MainView.showingSettings` 是 `@State private`，`openSettings()` / `closeSettings()` 是 `private`，**`AppDelegate` 无法直接访问它们**。既有做法是走通知：

```
Support/ 下的偏好类型里：static let changedNotification = Notification.Name("ClipShelf...")
AppDelegate：NotificationCenter.default.post(...)
MainView：.onReceive(NotificationCenter.default.publisher(for: X.changedNotification)) { ... }
```

（现成例子：`Support/AppearancePreferences.swift:29-30` + `App/AppDelegate.swift:40-45` + `Views/MainView.swift:89-95`。）

→ 40 的 §4.4 已允许「可通过 Notification 通知 `MainView` 置 `showingSettings = true`；实现方式自定」。**建议照上述既有惯用法做**，不要为此新增全局单例或改写 `MainView` 的既有状态。

### 3.3 其它前置

`prompt-31` / `32` / `43` / `34` / `35` / `33` / `38` / `37` 均已交付，测试底座（`--self-test` + `swift test` 双入口）与隔离接缝（`CLIPSHELF_DATA_DIR` / `CLIPSHELF_DEFAULTS_SUITE` / `CLIPSHELF_PASTEBOARD_NAME` / `CLIPSHELF_CONTROL_SOCKET`）均已可用，4 份提示词可正常沿用。

---

## 4. 本批必须落地的自动断言（各份的「可自动化部分」）

`Sources/ClipShelfLite/Support/SelfTest.swift` 目前有 **32 条**断言（`results.append(check(...))` 计数实测 = 32）。**这 32 条一条都不得删改，本批只能新增。**

| 提示词 | 必须新增的断言（把判定逻辑做成**纯函数**再断言） | 备注 |
| --- | --- | --- |
| **40** | `ClipboardHistoryPolicy.shouldCapture(historyEnabled: false, ...) == false` | **这是真实缺口**：既有 3 处调用全部传 `historyEnabled: true`（`SelfTest.swift:27-28 / 50-51 / 64-65`），**暂停路径从未被断言过**。 |
| **40** | 菜单标题映射（若实现为纯函数，如 `enabled ? "暂停记录" : "继续记录"`） | 标题文案属 UI，但**映射规则**可断言。 |
| **41** | 缩放比例**夹紧**到 0.5–4.0（含越界输入）与**复位**规则 | §4.7 已要求。 |
| **36** | 识别结果的**文本聚合 / 去重 / 语言配置**（`recognitionLanguages` / `usesLanguageCorrection`） | §4.7 已要求；**拖框选择**属手工验收。 |
| **42** | **编码检测**（BOM / UTF-8 / UTF-16 / GB18030） | §4.7 已要求；查找条 / 行号 / 换行属手工验收。 |

> **本批 4 份的核心行为多为图形交互**（菜单标题、缩放手感、OCR 拖框、查找条），黑盒层覆盖不到。  
> **自动化以 `swift test` 的纯函数断言为主，UI 部分走 `tests/parity/manual-checklist.md` 人工验收。**

---

## 5. 交付节奏（硬性）

1. **逐份串行**：一份**完成并通过自测**后，再开始下一份。不要并行铺开。
2. **每份完成后立即自测**：
   ```
   swift build --disable-sandbox
   swift test  --disable-sandbox
   ClipShelf --self-test <报告路径>
   ```
3. **若某一份卡住**：**不要**为了让整批通过而弱化任何断言或降低标准。  
   → 停下那一份，在报告中写清「未完成 + 卡在哪里 + 已尝试什么 + 建议」，然后**继续做后面的份**。  
   → **注意本批有真实依赖**：若 **41 未完成**，则 36 的「拖框选择」与 42 的「`Cmd+=/-/0` 不被覆盖」**无法验证**，请在 36 / 42 的报告中明确标注为「未验证」。
4. **不得为了让结果通过而弱化既有断言。** 若确需改动既有断言，必须在报告中说明其与目标行为的关系。

---

## 6. 报告（每份分开写）

- **每份各出一份报告**，命名沿用既有结构：  
  `prompts/prompt-40-status-menu-report.md`、`prompts/prompt-41-image-zoom-report.md`、`prompts/prompt-36-image-ocr-report.md`、`prompts/prompt-42-text-preview-report.md`。  
  每份须含：结果 / 改动文件 / 自动测试输出 / 手工验证步骤 / 范围说明。
- **另出一份批次小结** `prompts/batch-C-report.md`，含：
  - 4 份的完成状态一览（完成 / 未完成）；
  - 每份跑了哪些命令、结果如何（`swift test` 断言总数从 32 变成多少）；
  - 任何未完成项与原因；
  - **与各份提示词 Evidence 段不符之处**（若有）。

> 上一批（B2）的教训：**批次小结没交**（`prompts/batch-B2-report.md` 至今缺失）。本批请务必补上。

---

## 7. 范围红线（重复强调）

- `tests/parity/` 归 WorkBuddy 所有，**不得修改**。
- 不得改动 `history.json` 的字段与结构（缩放比例、识别结果、编码检测结果**都不落盘**）。
- 不得改动既有设置入口、文案与布局风格（各份 §3 已逐条列明）。
- 新增文件请放在各份 §3 指定的位置（如 `Services/ImageTextRecognizer.swift`、`Support/TextEncodingDetector.swift`）。
- **不得重做已交付的 8 份**：31、32、43、34、35、33、38、37。

---

## 8. 交付后

WorkBuddy 将**逐份独立复核**（代码审查 + 变异测试 + 黑盒端到端），并以

```
CLIPSHELF_PARITY_DELIVERED=31,32,43,33,38,37,34,35,40,41,36,42
```

运行 `tests/parity/run.sh`（**推荐加 `CLIPSHELF_PARITY_ISOLATED=1`**，这样无需用户退出日常在用的 ClipShelf）。

**手工验收**部分由 WorkBuddy 按 `tests/parity/manual-checklist.md` 逐条复现；其中「未点击不识别」「缩放复位」「查找条 Esc 不关预览」三条是本批**最容易被糊过去**的点，会重点复测。
