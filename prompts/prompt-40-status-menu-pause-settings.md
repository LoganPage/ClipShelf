# prompt-40：菜单栏补齐「暂停记录 / 继续记录」与「设置」

依据 `AGENTS.md` 的 Prompt and collaboration guidelines。协作约定见 `prompts/README.md`。
前置：prompt-31 已落地（测试底座可用）。本提示词只交付**菜单栏这两项**。

> 来源说明：本项由 Mac 端上一轮勘察识别（对应 `docs/parity/codex-prompt.md` 的 P1-6），**未列入 Windows 交接说明的六项缺口清单**。它是**功能**而非交互规则，故按用户口径（只对齐功能、不改交互规则）纳入本轮。

## 1. Goal

菜单栏增加「暂停记录 ↔ 继续记录」（标题随状态切换）与「设置」（打开主窗口并直达设置面板）。

功能基准：Windows 端 ClipShelf 1.4.0 的通知区域菜单提供暂停 / 继续记录与直达设置。

## 2. Evidence

**初测环境**：2026-09-27，仓库 `/Users/Zhuanz/Documents/Codex/2026-05-16/macos-windows`，HEAD `355acec`。
**复测（行号已更新）**：2026-09-28，HEAD `0860ab3`。下列行号均为**复测值**。

- `Sources/ClipShelfLite/App/AppDelegate.swift:149-178` `rebuildMenu(_:)`：当前菜单项依次为「显示 ClipShelf」（L151）、「选择截图文件夹」（L152）、分隔线（L153）、「最近 8 条」（L155-173）、分隔线（L175）、「清空历史」（L176）、「退出」（L177）。**没有暂停 / 继续，也没有设置入口**。
- `App/AppDelegate.swift:128-130` `menuWillOpen(_:)` → `rebuildMenu(menu)` —— **菜单每次打开都会重建**，因此标题可以随状态动态切换，不需要额外刷新机制。
- `Stores/ClipStore.swift:11-15`：`@Published var isClipboardHistoryEnabled: Bool`，`didSet` 写入 `UserDefaults` 的 `clipboardHistory.enabled`（键定义在 **L19**）。**已有持久化**。
- `Stores/ClipStore.swift:163-169` `pollPasteboard()`：守卫已改为**委托** `ClipboardHistoryPolicy.shouldCapture(historyEnabled: isClipboardHistoryEnabled, ...)`（`Support/ClipboardHistoryPolicy.swift:16-27`，其中 `guard historyEnabled else { return false }` 在 L22）—— 暂停后不再采集。**本项只改菜单与入口，不要动这个策略函数。**
- `Views/SettingsView.swift:165`：设置面板「历史」区已有 `Toggle("记录文字、文件和图片复制历史", isOn: $store.isClipboardHistoryEnabled)` —— **同一个开关**，菜单项必须与它双向同步。
- `Views/MainView.swift:9` `@State private var showingSettings`；`:407-411` `openSettings()`；`:413-417` `closeSettings()`；`:98-126` `settingsOverlay`。

> ⚠ **接缝提醒（实现前必读）**：`showingSettings` 是 `@State private`，`openSettings()` / `closeSettings()` 都是 `private` —— **`AppDelegate` 无法直接访问**。既有惯用法是走通知：偏好类型里定义 `static let changedNotification = Notification.Name("ClipShelf...")`，`AppDelegate` 用 `NotificationCenter.default.post(...)` 发出，`MainView` 用 `.onReceive(NotificationCenter.default.publisher(for: X.changedNotification))` 接收。现成例子见 `Support/AppearancePreferences.swift:29-30` + `App/AppDelegate.swift:40-45` + `Views/MainView.swift:89-95`。**请照此惯用法做，不要新增全局单例，也不要改写 `MainView` 的既有状态。**

## 3. Scope

**受影响**：`App/AppDelegate.swift`、`Views/MainView.swift`（接收「打开设置」通知）、`README.md` 功能列表（中英双语）。

**不得改变的部分**
- 既有菜单项的**顺序、文案与行为**：显示 ClipShelf / 选择截图文件夹 / 最近 8 条（含「复制」「粘贴」子菜单）/ 清空历史 / 退出。
- 状态栏图标与 `NSStatusItem` 机制（`AppDelegate.swift:132-147`）。
- `ClipStore.isClipboardHistoryEnabled` 的语义与持久化键名 `clipboardHistory.enabled`。
- 设置面板的既有布局与开关文案。

## 4. Constraints

1. 「暂停记录」与「继续记录」是**同一个菜单项**，标题随 `store.isClipboardHistoryEnabled` 切换；状态来源**必须**是 `store.isClipboardHistoryEnabled`，**不要新建状态变量**。
2. 暂停后复制新内容**不再新增记录**；继续后恢复采集。
3. 状态**重启后保持**（复用既有 `UserDefaults` 持久化，不要另写一份）。
4. 「设置」菜单项：打开主窗口**并显示设置面板**（可通过 Notification 通知 `MainView` 置 `showingSettings = true`；实现方式自定）。
5. 新增项放在**合适的既有分隔线之间**，不打断「最近 8 条」子菜单的既有结构；具体位置由实现判断，但不得让「退出」不再位于末位。
6. 沿用 prompt-31 的测试底座：可自动断言的部分（开关切换后采集行为）进 `swift test` / `--self-test`；菜单标题与面板弹出属**手工验收**，不得为了自动化而弱化断言。
   - **注意**：`Support/SelfTest.swift` 目前 32 条断言里，`ClipboardHistoryPolicy.shouldCapture` 的三处调用**全部传 `historyEnabled: true`**（L27-28 / L50-51 / L64-65）—— **暂停路径从未被断言过**。本项必须**新增**一条 `historyEnabled: false` 的断言（这是本项唯一真正可自动化的缺口）。**不得删改既有 32 条中的任何一条。**
7. 不得为了让测试通过而弱化断言。

## 5. Acceptance

- **A** 菜单栏出现「暂停记录」；点击后标题变为「继续记录」。
- **B** 暂停状态下复制新文本 / 文件 / 图片 → 等 2 秒 → `history.json` **不新增**记录。
- **C** 点击「继续记录」后复制 → 正常新增。
- **D** 重启应用后，暂停 / 继续状态保持。
- **E** 菜单项与设置面板里的同一个开关**双向同步**（任一处切换，另一处显示一致）。
- **F** 「设置」菜单项 → 主窗口出现**并显示设置面板**。
- **G** 回归：既有菜单项（显示 / 选择截图文件夹 / 最近 8 条 / 清空历史 / 退出）顺序与行为不变；「退出」仍在末位。
- **H** `swift build` 与 `swift test` 通过；`--self-test` 退出码 0。
- **I** `README.md` 中英双语功能列表补充本项。
- **J** 报告：改动文件清单、测试输出、手工验证步骤。

## 6. Handoff

- **实现**：Codex。**独立测试与复核**：WorkBuddy（只读，不改源码）。
- **授权（未勾选即未授权）**：`[ ]` 本地检查 · `[ ]` Release 构建 · `[ ]` 覆盖安装 · `[ ]` `git commit` · `[ ]` `push` · `[ ]` GitHub 发布。
  **本轮建议**：仅本地检查。
- **提交信息建议**：`Add pause/resume recording and settings to the status menu`
