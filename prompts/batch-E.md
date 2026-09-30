# 批次 E：47 → 49（只同步功能，不同步 UI / 美术风格 / 交互逻辑）

本文件是**批次指令**。它**不改变**任何单份提示词的目标与验收，只规定执行顺序、同批分工与全局口径。
单份提示词：`prompts/prompt-47-version-single-source-and-window-title.md`、`prompts/prompt-49-clear-history-confirmation-and-row-context-menu.md`。

> **编号说明**：**48 已被用户显式跳过**（它是纯美术风格改动 —— 把 `SettingsView.swift:468` 的边框色统一到 `AppTheme.subtleBorder`，属「美术风格」），故本批从 47 直接跳到 49。**不要顺手做 48。**

---

## 1. 全局口径（本批第一原则，用户 2026-09-30 明确）

> **「只同步功能，不同步 UI、美术风格、交互逻辑。」**

含义（逐条落实）：

1. **只搬能力，不搬样子**：Windows 端怎么做的不重要，**实现方式一律按 macOS 自己的习惯**（原生控件、原生对话框、原生菜单、系统配色）。
2. **不照搬 Windows 的视觉**：菜单分组、图标、自绘模板、圆角/阴影数值、颜色常量，一律不用。
3. **不照搬 Windows 的交互逻辑**：键位绑定（如 `Shift+F10`）、点击行为规则、分支条件，一律不改。macOS 现有的选择/键盘/拖选逻辑**一个字都不许动**。
4. 判定标准：**如果一个改动只能让 macOS「看起来更像 Windows」而拿不出对应的功能差异，就不该做。**

---

## 2. 执行顺序与理由

**47 → 49**（逐份串行，一份通过自测再开始下一份）。

- **47 先做**：改动面小（4 个文件 + 1 条断言），且它把「版本号单一来源」这件事先定下来 —— 49 的断言块要接在它后面。
- **49 后做**：改动面大（3 个 UI 入口 + 右键菜单 + 2 条断言），且有真正的交互风险（右键菜单可能干扰拖选），放在最后便于单独观察。

---

## 3. 同批硬性分工（两份都会碰到同两个文件，必须照此切）

| 文件 | 47 可以动的 | 49 可以动的 |
| --- | --- | --- |
| `Sources/ClipShelfLite/App/AppDelegate.swift` | **只** `showWindow()` 里的 `window.title` 那一行（L85 附近） | **只** `clearHistory()`（L113-115） |
| `Sources/ClipShelfLite/Support/SelfTest.swift` | **只**在 `built-in text preview extension routing is narrow` 断言（L659-669）之后、`_ = CFPreferencesAppSynchronize(...)`（**L671**）**之前**追加自己的断言块 | **只**接在 **47 的断言块之后**、**仍在 L671 之前**追加自己的断言块 |

> ⚠️ **L671 的 `self test defaults suite file count does not grow` 必须保持整段最后一条** —— 它内部先 `CFPreferencesAppSynchronize` 再 `clearSelfTestDefaults()`，是收尾断言。**两份都不得把它往前挪、不得改它。**

其余文件各自独占，无冲突：47 动 `AppUpdateChecker.swift` / `RuntimeControlServer.swift` / `script/build_app_bundle.sh`；49 动 `MainView.swift` / `SettingsView.swift` / `ClipStore.swift`（若需要）。

**两份都不许碰**：`Package.swift`、`tests/parity/`、`docs/`、`README.md`、`Assets/`。

---

## 4. 本批环境（2026-09-30 实测，与上批不同）

- `swift --version` = **Apple Swift 6.4**（`swiftlang-6.4.0.34.1`），Target `arm64-apple-macosx27.0.0`。**不需要** `DEVELOPER_DIR` 绕过。
- `swift build --disable-sandbox` 正常。
- ⚠️ **`swift test --disable-sandbox` 在仓库目录内会失败**（`ClipShelfLiteTests.xctest: resource fork, Finder information, or similar detritus not allowed`）—— 仓库在 `~/Documents` 下受 iCloud 文件提供器管理。**这是既有环境问题，与本批代码无关。** 一律改用：
  ```bash
  swift test --disable-sandbox --scratch-path /tmp/cs-batch-e
  ```
- **`[PASS]` 行数是 `2 × 断言数`**（套件在同一进程跑两遍：C 桥接 + Swift-Testing 发现）。断言 41 条时是 82 行；47 交付后 42 条 → 84 行；49 交付后 44 条 → 88 行。**别把它当成新增断言。**
- `--self-test` 报告末行的 `Result: N/N checks passed.` 才是权威计数。

---

## 5. 每份的交付要求

1. **产品代码 + 可测试接缝**（判定逻辑抽成**纯函数**，否则断言无法覆盖）。
2. 跑一次 `swift build --disable-sandbox` + `swift test --disable-sandbox --scratch-path /tmp/cs-batch-e` + `ClipShelf --self-test <路径>`，**贴原始输出**。
3. 出一份**单份报告**：`prompts/prompt-<编号>-<主题>-report.md`（报告与正文分离）。
4. 批末出一份**批次小结**：`prompts/batch-E-report.md`，含：两份的完成状态、断言数变化（41 → ?）、**两份合起来的 `SelfTest.swift` diff 是否只落在第 3 节规定的位置**、遇到的阻塞、以及**本批是否有任何一处越过了「只同步功能」这条线**（自查并明确回答）。
5. 授权口径不变：**仅本地检查**；`git commit` / `push` / 发布由**用户本人**执行。

---

## 6. 七条红线

1. **不得**把确认逻辑放进 `ClipStore.clearHistory()` —— `--ctl clear` 是脚本驱动通道，弹窗会让 `tests/parity/` 挂死。
2. **不得**改变 macOS 现有的选择 / 键盘 / 拖选逻辑（`handleRowClick`、`handleKey`、`handleCommandKey`、`DragSelectionCaptureView`（`MainView.swift:1425`）、`KeyboardCaptureView`（`:1374`）全部不动）。
3. **不得**把 Delete 键在「无选择」时的行为改成「不执行」（那是 Windows 的**交互逻辑**，按用户口径不同步；macOS 保持「清空全部」，只是要加确认）。
4. **不得**照搬 Windows 的菜单样式 / 分组 / 图标 / 自绘对话框 / `Shift+F10` 键位。
5. **不得**删除或改写任何既有断言（**47 交付前 41 条，49 交付前 42 条**）。只允许追加。
6. **不得**改 `--ctl status` 的 JSON **字段名与结构**（`tests/parity/` 与 `runtime_control.sh` 依赖它）。
7. **不得**为了让测试通过而弱化断言。若某条断言实测不稳定，**说明原因并给出连续 5 次的实测结果**，不要放宽它。
